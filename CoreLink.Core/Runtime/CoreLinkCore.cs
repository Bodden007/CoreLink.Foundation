
using CoreLink.Contracts.Core;
using CoreLink.Contracts.Maps;
using CoreLink.Contracts.Registers;
using CoreLink.Core.Configuration;
using CoreLink.Core.Configuration.Decoding;
using CoreLink.Core.Configuration.Models;
using CoreLink.Transport.Modbus;
using CoreLink.Transport.Modbus.Results;
using NModbus.Utility;

namespace CoreLink.Core.Runtime;

/// <summary>
/// C# реализация ядра CoreLink.
///
/// Владеет транспортной сессией, декодирует результаты polling
/// и предоставляет последний согласованный snapshot.
///
/// Не зависит от Client, Rx и Avalonia.
/// </summary>
public sealed class CoreLinkCore
{
    private readonly object _lifecycleLock = new();
    private readonly object _snapshotLock = new();

    private ModbusTransportSession? _transport;
    private DecodedCoreMap? _map;

    private float[]? _snapshot;
    private float[]? _decodeBuffer;

    private bool _hasData;
    private bool _fresh;
    private bool _started;
    private bool _stopping;

    private CoreStatus _state = CoreStatus.Stopped;

    public event Action<CoreStatus>? StatusChanged;

    /// <summary>
    /// Декодирует карту и запускает непрерывный polling.
    ///
    /// Доступность PLC не является условием успешного запуска.
    /// Reconnect выполняется внутри Transport.
    ///
    /// Повторный Start на одном экземпляре запрещён.
    /// </summary>
    public CoreStatus Start(CoreMap coreMap)
    {
        CoreStatus result;

        lock (_lifecycleLock)
        {
            if (_started)
                return CoreStatus.Error;

            if (coreMap is null)
                return CoreStatus.BadConfig;

            DecodedCoreMap map;

            try
            {
                map = CoreMapDecoder.Decode(
                    coreMap.Buffer.Span);

                if (!ValidateMap(
                        map,
                        coreMap.BufferLength))
                {
                    return CoreStatus.BadConfig;
                }
            }
            catch (InvalidOperationException)
            {
                return CoreStatus.BadConfig;
            }
            catch (ArgumentException)
            {
                return CoreStatus.BadConfig;
            }

            ModbusTransportSession? session = null;

            try
            {
                var config =
                    ModbusConfigFactory.Create(map);

                session = new ModbusTransportSession(config);

                session.RegistersReceived +=
                    OnRegistersReceived;

                session.TransportStatusChanged +=
                    OnTransportStatusChanged;

                lock (_snapshotLock)
                {
                    _map = map;

                    _snapshot =
                        new float[map.Registers.Length];

                    _decodeBuffer =
                        new float[map.Registers.Length];

                    _hasData = false;
                    _fresh = false;
                    _stopping = false;

                    _transport = session;
                    _state = CoreStatus.Running;
                }

                session.Start();

                _started = true;
                result = CoreStatus.Running;
            }
            catch (Exception)
            {
                if (session is not null)
                {
                    session.RegistersReceived -=
                        OnRegistersReceived;

                    session.TransportStatusChanged -=
                        OnTransportStatusChanged;

                    try
                    {
                        Task.Run(async () =>
                            await session.DisposeAsync())
                            .GetAwaiter()
                            .GetResult();
                    }
                    catch (Exception)
                    {
                        ResetState(CoreStatus.Error);
                        return CoreStatus.Error;
                    }
                }

                ResetState(CoreStatus.Error);
                return CoreStatus.Error;
            }
        }

        StatusChanged?.Invoke(result);

        return result;
    }

    /// <summary>
    /// Синхронно завершает Core и освобождает Transport.
    ///
    /// Ожидает завершения polling и dispatcher.
    /// После успешного Stop snapshot уничтожен.
    /// </summary>
    public CoreStatus Stop()
    {
        lock (_lifecycleLock)
        {
            ModbusTransportSession? session;

            lock (_snapshotLock)
            {
                if (_state == CoreStatus.Stopped)
                    return CoreStatus.Stopped;

                _stopping = true;
                _fresh = false;

                session = _transport;
            }

            if (session is not null)
            {
                try
                {
                    // Асинхронный Transport завершается
                    // без удержания блокировки snapshot.
                    Task.Run(async () =>
                        await session.DisposeAsync())
                        .GetAwaiter()
                        .GetResult();
                }
                catch (Exception)
                {
                    lock (_snapshotLock)
                    {
                        _state = CoreStatus.Error;
                    }

                    return CoreStatus.Error;
                }

                session.RegistersReceived -=
                    OnRegistersReceived;

                session.TransportStatusChanged -=
                    OnTransportStatusChanged;
            }

            ResetState(CoreStatus.Stopped);
        }

        StatusChanged?.Invoke(CoreStatus.Stopped);

        return CoreStatus.Stopped;
    }

    /// <summary>
    /// Синхронно копирует готовый snapshot в буфер Client.
    ///
    /// Не выполняет Modbus-запросов.
    /// Не сохраняет указатель.
    /// При неуспешном результате буфер не изменяется.
    /// </summary>
    public unsafe SnapshotStatus GetSnapshot(
        float* buffer,
        int length)
    {
        lock (_snapshotLock)
        {
            if (_stopping ||
                _state == CoreStatus.Stopped)
            {
                return SnapshotStatus.Stopped;
            }

            if (buffer == null ||
                _snapshot is null ||
                length < _snapshot.Length)
            {
                return SnapshotStatus.BadBuffer;
            }

            if (!_hasData)
                return SnapshotStatus.NoData;

            if (!_fresh)
                return SnapshotStatus.Disconnected;

            for (int i = 0; i < _snapshot.Length; i++)
            {
                buffer[i] = _snapshot[i];
            }

            return SnapshotStatus.Ok;
        }
    }

    /// <summary>
    /// Получает завершённый блок Input Registers.
    ///
    /// Декодирует весь блок и только после успешного
    /// преобразования публикует новый snapshot.
    /// </summary>
    private void OnRegistersReceived(
        ushort[] registers)
    {
        CoreStatus? changedStatus = null;

        lock (_snapshotLock)
        {
            if (_stopping ||
                _map is null ||
                _snapshot is null ||
                _decodeBuffer is null)
            {
                return;
            }

            bool decoded = TryDecodeRegisters(
                registers,
                _map,
                _decodeBuffer);

            if (!decoded)
            {
                _fresh = false;

                if (_state != CoreStatus.Error)
                {
                    _state = CoreStatus.Error;
                    changedStatus = CoreStatus.Error;
                }
            }
            else
            {
                Array.Copy(
                    _decodeBuffer,
                    _snapshot,
                    _snapshot.Length);

                _hasData = true;
                _fresh = true;

                if (_state != CoreStatus.Running)
                {
                    _state = CoreStatus.Running;
                    changedStatus = CoreStatus.Running;
                }
            }
        }

        if (changedStatus.HasValue)
        {
            StatusChanged?.Invoke(
                changedStatus.Value);
        }
    }

    /// <summary>
    /// Обрабатывает состояние polling Transport.
    ///
    /// При потере связи последнее значение сохраняется,
    /// но перестаёт считаться актуальным.
    /// </summary>
    private void OnTransportStatusChanged(
        ModbusTransportStatus status)
    {
        CoreStatus nextStatus = status switch
        {
            ModbusTransportStatus.Disconnected or
            ModbusTransportStatus.ConnectTimeout or
            ModbusTransportStatus.RequestTimeout or
            ModbusTransportStatus.RequestFailed =>
                CoreStatus.Disconnected,

            ModbusTransportStatus.Faulted =>
                CoreStatus.Error,

            ModbusTransportStatus.Cancelled =>
                CoreStatus.Stopped,

            _ => CoreStatus.Running
        };

        if (nextStatus == CoreStatus.Stopped)
            return;

        bool changed;

        lock (_snapshotLock)
        {
            if (_stopping)
                return;

            changed = _state != nextStatus;

            _state = nextStatus;
            _fresh = false;
        }

        if (changed)
        {
            StatusChanged?.Invoke(nextStatus);
        }
    }

    /// <summary>
    /// Проверяет параметры карты до запуска Transport.
    ///
    /// BufferLength в CoreMap содержит количество значений,
    /// а не размер бинарного буфера.
    /// </summary>
    private static bool ValidateMap(
        DecodedCoreMap map,
        int valueCount)
    {
        if (map.Port == 0 ||
            map.PollIntervalMs <= 0 ||
            map.ConnectTimeoutMs <= 0 ||
            map.RequestTimeoutMs <= 0 ||
            map.ReconnectDelayMs < 0 ||
            map.RegisterLength == 0 ||
            map.RegisterLength > 125 ||
            map.Registers.Length == 0 ||
            map.Registers.Length != valueCount)
        {
            return false;
        }

        int startAddress = map.StartAddress;

        int endAddress =
            startAddress + map.RegisterLength;

        if (endAddress > 65536)
            return false;

        foreach (RegisterDefinition register in map.Registers)
        {
            int registerLength = register.Type switch
            {
                RegisterType.Word => 1,
                RegisterType.Float => 2,
                _ => 0
            };

            if (registerLength == 0)
                return false;

            if (register.Address < startAddress ||
                (int)register.Address + registerLength >
                endAddress)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Преобразует физические Modbus-регистры в float[].
    ///
    /// Индекс результата соответствует индексу
    /// RegisterDefinition в декодированной карте.
    ///
    /// Float32 преобразуется через NModbus.
    /// </summary>
    private static bool TryDecodeRegisters(
        ushort[] registers,
        DecodedCoreMap map,
        float[] destination)
    {
        if (registers.Length != map.RegisterLength ||
            destination.Length != map.Registers.Length)
        {
            return false;
        }

        for (int i = 0; i < map.Registers.Length; i++)
        {
            RegisterDefinition definition =
                map.Registers[i];

            int index =
                definition.Address - map.StartAddress;

            switch (definition.Type)
            {
                case RegisterType.Word:
                {
                    if ((uint)index >=
                        (uint)registers.Length)
                    {
                        return false;
                    }

                    destination[i] = registers[index];
                    break;
                }

                case RegisterType.Float:
                {
                    if (index < 0 ||
                        index >= registers.Length - 1)
                    {
                        return false;
                    }

                    ushort lowWord = registers[index];
                    ushort highWord = registers[index + 1];

                    switch (map.RegisterOrder)
                    {
                        case RegisterOrder.ABCD:
                            break;

                        case RegisterOrder.BADC:
                            lowWord = SwapBytes(lowWord);
                            highWord = SwapBytes(highWord);
                            break;

                        case RegisterOrder.CDAB:
                            (lowWord, highWord) =
                                (highWord, lowWord);
                            break;

                        case RegisterOrder.DCBA:
                            (lowWord, highWord) =
                                (SwapBytes(highWord),
                                 SwapBytes(lowWord));
                            break;

                        default:
                            return false;
                    }

                    destination[i] =
                        ModbusUtility.GetSingle(
                            highWord,
                            lowWord);

                    break;
                }

                default:
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Меняет порядок байтов одного Modbus WORD.
    /// </summary>
    private static ushort SwapBytes(ushort value)
    {
        return (ushort)(
            (value >> 8) |
            (value << 8));
    }

    /// <summary>
    /// Сбрасывает состояние и принадлежащие Core данные.
    ///
    /// Вызывать только при контролируемом lifecycle.
    /// </summary>
    private void ResetState(CoreStatus status)
    {
        lock (_snapshotLock)
        {
            _transport = null;
            _map = null;

            _snapshot = null;
            _decodeBuffer = null;

            _hasData = false;
            _fresh = false;
            _stopping = false;

            _state = status;
        }
    }
}
