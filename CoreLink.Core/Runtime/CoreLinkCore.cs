
using System.Diagnostics;
using CoreLink.Contracts.Core;
using CoreLink.Contracts.Maps;
using CoreLink.Contracts.Registers;
using CoreLink.Core.Configuration;
using CoreLink.Core.Configuration.Decoding;
using CoreLink.Core.Configuration.Models;
using CoreLink.Transport.Modbus;
using CoreLink.Transport.Modbus.Results;
using NModbus.Utility;
using System.Buffers.Binary;

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
    /// Асинхронно читает PLC по логическим ValueId.
    ///
    /// Соседние физические регистры читаются одним
    /// Modbus-запросом, остальные — последовательно.
    ///
    /// Любая ошибка прекращает чтение всего набора.
    /// Snapshot и Rx не изменяются.
    /// </summary>
    public async Task<CoreReadResult> ReadAsync(
        IReadOnlyList<int> valueIds)
    {
        if (valueIds is null ||
            valueIds.Count == 0)
        {
            return ReadError(CoreReadStatus.BadConfig);
        }

        ModbusTransportSession session;
        DecodedCoreMap map;

        lock (_snapshotLock)
        {
            if (_stopping ||
                _transport is null ||
                _map is null)
            {
                return ReadError(CoreReadStatus.Stopped);
            }

            session = _transport;
            map = _map;
        }

        try
        {
            int valueCount = valueIds.Count;
            var values = new float[valueCount];

            // Сначала проверяем весь запрос.
            // Ошибочный ValueId не должен приводить
            // к частично выполненному чтению PLC.
            for (int i = 0; i < valueCount; i++)
            {
                int valueId = valueIds[i];

                if ((uint)valueId >=
                    (uint)map.Registers.Length)
                {
                    return ReadError(CoreReadStatus.BadConfig);
                }
            }

            int position = 0;

            while (position < valueCount)
            {
                if (IsStopping())
                    return ReadError(CoreReadStatus.Stopped);

                int startPosition = position;

                RegisterDefinition first =
                    map.Registers[valueIds[position]];

                int startAddress = first.Address;
                int endAddress =
                    startAddress + GetRegisterLength(first.Type);

                if (endAddress == startAddress)
                    return ReadError(CoreReadStatus.BadConfig);

                position++;

                // Объединяем только непосредственно соседние
                // физические диапазоны в порядке вызова.
                // Повторные ValueId остаются отдельными чтениями.
                while (position < valueCount)
                {
                    RegisterDefinition next =
                        map.Registers[valueIds[position]];

                    int nextLength =
                        GetRegisterLength(next.Type);

                    if (nextLength == 0 ||
                        next.Address != endAddress ||
                        endAddress - startAddress + nextLength > 125)
                    {
                        break;
                    }

                    endAddress += nextLength;
                    position++;
                }

                ushort count =
                    (ushort)(endAddress - startAddress);

                ModbusReadResult result =
                    await session.ReadSingleAsync(
                        (ushort)startAddress,
                        count).ConfigureAwait(false);

                if (IsStopping())
                    return ReadError(CoreReadStatus.Stopped);

                if (result.Status != ModbusTransportStatus.Ok)
                {
                    return ReadError(
                        MapReadStatus(result.Status));
                }

                ushort[]? registers = result.Data;

                if (registers is null ||
                    registers.Length != count)
                {
                    return ReadError(CoreReadStatus.Failed);
                }

                for (int i = startPosition;
                     i < position;
                     i++)
                {
                    RegisterDefinition definition =
                        map.Registers[valueIds[i]];

                    int offset =
                        definition.Address - startAddress;

                    if (!TryDecodeValue(
                            registers,
                            offset,
                            definition.Type,
                            map.RegisterOrder,
                            out float value))
                    {
                        return ReadError(CoreReadStatus.Failed);
                    }

                    values[i] = value;
                }
            }

            if (IsStopping())
                return ReadError(CoreReadStatus.Stopped);

            return new CoreReadResult
            {
                Status = CoreReadStatus.Ok,
                Values = values
            };
        }
        catch (ObjectDisposedException)
        {
            return ReadError(CoreReadStatus.Stopped);
        }
        catch (InvalidOperationException)
        {
            return ReadError(
                IsStopping()
                    ? CoreReadStatus.Stopped
                    : CoreReadStatus.Failed);
        }
        catch (Exception ex)
        {
            // TODO: Заменить Trace централизованным
            // диагностическим логированием CoreLink.
            ReportReadError(ex);

            return ReadError(
                IsStopping()
                    ? CoreReadStatus.Stopped
                    : CoreReadStatus.Failed);
        }
    }

    /// <summary>
    /// Декодирует одно значение внутри прочитанного
    /// физического диапазона.
    /// </summary>
    private static bool TryDecodeValue(
        ushort[] registers,
        int index,
        RegisterType type,
        RegisterOrder order,
        out float value)
    {
        value = default;

        if (index < 0)
            return false;

        switch (type)
        {
            case RegisterType.Word:
                {
                    if (index >= registers.Length)
                        return false;

                    value = registers[index];
                    return true;
                }

            case RegisterType.Float:
                {
                    if (index >= registers.Length - 1)
                        return false;

                    ushort lowWord = registers[index];
                    ushort highWord = registers[index + 1];

                    switch (order)
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

                    value = ModbusUtility.GetSingle(
                        highWord,
                        lowWord);

                    return true;
                }

            default:
                return false;
        }
    }

    private static int GetRegisterLength(
        RegisterType type)
    {
        return type switch
        {
            RegisterType.Word => 1,
            RegisterType.Float => 2,
            _ => 0
        };
    }

    private static CoreReadStatus MapReadStatus(
        ModbusTransportStatus status)
    {
        return status switch
        {
            ModbusTransportStatus.Ok =>
                CoreReadStatus.Ok,

            ModbusTransportStatus.Disconnected or
            ModbusTransportStatus.ConnectTimeout =>
                CoreReadStatus.Disconnected,

            ModbusTransportStatus.RequestTimeout =>
                CoreReadStatus.Timeout,

            ModbusTransportStatus.Cancelled =>
                CoreReadStatus.Stopped,

            _ =>
                CoreReadStatus.Failed
        };
    }

    private static CoreReadResult ReadError(
        CoreReadStatus status)
    {
        return new CoreReadResult
        {
            Status = status,
            Values = null
        };
    }

    private bool IsStopping()
    {
        lock (_snapshotLock)
        {
            return _stopping ||
                   _transport is null;
        }
    }

    private static void ReportReadError(
        Exception exception)
    {
        try
        {
            Trace.TraceError(
                $"CoreLink direct read failed: {exception}");
        }
        catch (Exception)
        {
            // Ошибка диагностики не должна
            // нарушать выполнение Core.
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


    // TODO: Унифицировать декодирование Word/Float для polling
    // и прямого ReadAsync. Исключить дублирование логики
    // RegisterOrder и SwapBytes. Решение требует согласования.
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


    /// <summary>
    /// Выполняет одиночное чтение Word или Float32.
    ///
    /// Использует действующую транспортную сессию
    /// и существующий механизм декодирования Core.
    ///
    /// При ошибке возвращает статус без успешного
    /// значения. Snapshot и Rx не изменяет.
    ///
    /// Штатные ошибки не пробрасываются вызывающему коду.
    /// </summary>
    public async Task<CoreSingleReadResult> ReadSingleAsync(
        ushort address,
        RegisterType type,
        ModbusReadFunction function)
    {
        // Проверяем параметры до обращения к Transport.
        int registerCount = GetRegisterLength(type);

        if (registerCount == 0 ||
            function is not (
                ModbusReadFunction.HoldingRegisters or
                ModbusReadFunction.InputRegisters) ||
            (uint)address + (uint)registerCount > 65536u)
        {
            return new CoreSingleReadResult(
                CoreReadStatus.BadConfig,
                default);
        }

        ModbusTransportSession session;
        RegisterOrder order;

        lock (_snapshotLock)
        {
            if (_stopping ||
                _transport is null ||
                _map is null)
            {
                return new CoreSingleReadResult(
                    CoreReadStatus.Stopped,
                    default);
            }

            session = _transport;
            order = _map.RegisterOrder;
        }

        try
        {
            ModbusReadResult result =
                await session.ReadSingleAsync(
                    address,
                    (ushort)registerCount,
                    (byte)function)
                .ConfigureAwait(false);

            if (IsStopping())
            {
                return new CoreSingleReadResult(
                    CoreReadStatus.Stopped,
                    default);
            }

            if (result.Status != ModbusTransportStatus.Ok)
            {
                return new CoreSingleReadResult(
                    MapReadStatus(result.Status),
                    default);
            }

            ushort[]? registers = result.Data;

            if (registers is null ||
                registers.Length != registerCount ||
                !TryDecodeValue(
                    registers,
                    0,
                    type,
                    order,
                    out float value))
            {
                return new CoreSingleReadResult(
                    CoreReadStatus.Failed,
                    default);
            }

            return new CoreSingleReadResult(
                CoreReadStatus.Ok,
                value);
        }
        catch (ObjectDisposedException ex)
        {
            // TODO: Подключить логирование завершения
            // операции во время остановки Transport.
            ReportReadError(ex);

            return new CoreSingleReadResult(
                IsStopping()
                    ? CoreReadStatus.Stopped
                    : CoreReadStatus.Failed,
                default);
        }
        catch (InvalidOperationException ex)
        {
            // TODO: Подключить логирование
            // некорректного состояния транспортной сессии.
            ReportReadError(ex);

            return new CoreSingleReadResult(
                IsStopping()
                    ? CoreReadStatus.Stopped
                    : CoreReadStatus.Failed,
                default);
        }
        catch (Exception ex)
        {
            // TODO: Подключить централизованное
            // логирование неожиданных ошибок Core.
            ReportReadError(ex);

            return new CoreSingleReadResult(
                CoreReadStatus.Failed,
                default);
        }
    }


    /// <summary>
    /// Записывает один Word через FC06.
    ///
    /// Использует существующую сессию Transport.
    /// Не выполняет повторную отправку команды.
    /// </summary>
    public Task<CoreWriteStatus> WriteWordAsync(
        ushort address,
        ushort value)
    {
        return ExecuteWriteAsync(
            session => session.WriteSingleRegisterAsync(
                address,
                value));
    }


    /// <summary>
    /// Записывает Float32 через FC16.
    ///
    /// Порядок регистров определяется общей
    /// конфигурацией текущей сессии.
    ///
    /// Два регистра отправляются одной командой.
    /// </summary>
    public async Task<CoreWriteStatus> WriteFloatAsync(
        ushort address,
        float value)
    {
        if ((uint)address + 2u > 65536u)
            return CoreWriteStatus.BadConfig;

        RegisterOrder order;

        lock (_snapshotLock)
        {
            if (_stopping || _map is null)
                return CoreWriteStatus.Stopped;

            order = _map.RegisterOrder;
        }

        ushort[] registers = new ushort[2];

        EncodeFloat32(value, order, registers, 0);

        return await ExecuteWriteAsync(
            session => session.WriteMultipleRegistersAsync(
                address,
                registers))
            .ConfigureAwait(false);
    }


    /// <summary>
    /// Выполняет Batch Write через один FC16.
    ///
    /// Дескриптор содержит начальный адрес,
    /// количество и типизированные значения.
    ///
    /// До первой сетевой операции проверяются:
    /// - размер дескриптора;
    /// - типы всех значений;
    /// - число физических регистров;
    /// - границы адресного пространства.
    ///
    /// Разбиение Batch на несколько запросов запрещено.
    /// </summary>
    public async Task<CoreWriteStatus> WriteBatchAsync(
        ReadOnlyMemory<byte> descriptor)
    {
        if (descriptor.Length < 9)
            return CoreWriteStatus.BadConfig;

        ReadOnlySpan<byte> header = descriptor.Span;

        ushort address =
            BinaryPrimitives.ReadUInt16LittleEndian(
                header.Slice(0, 2));

        ushort count =
            BinaryPrimitives.ReadUInt16LittleEndian(
                header.Slice(2, 2));

        if (count == 0 ||
            descriptor.Length != 4 + count * 5)
        {
            return CoreWriteStatus.BadConfig;
        }

        int registerCount = 0;

        // Сначала проверяем весь дескриптор.
        // Некорректный элемент не должен приводить
        // к частичной отправке команды.
        for (int i = 0; i < count; i++)
        {
            byte type = descriptor.Span[4 + i * 5];

            int length = type switch
            {
                (byte)RegisterType.Word => 1,
                (byte)RegisterType.Float => 2,
                _ => 0
            };

            if (length == 0)
                return CoreWriteStatus.BadConfig;

            registerCount += length;

            if (registerCount > 123)
                return CoreWriteStatus.BadConfig;
        }

        if ((uint)address + (uint)registerCount > 65536u)
            return CoreWriteStatus.BadConfig;

        RegisterOrder order;

        lock (_snapshotLock)
        {
            if (_stopping || _map is null)
                return CoreWriteStatus.Stopped;

            order = _map.RegisterOrder;
        }

        ushort[] registers = new ushort[registerCount];
        int position = 0;

        // Формируем собственные регистры Core.
        // Эти данные не связаны с памятью потребителя.
        for (int i = 0; i < count; i++)
        {
            int offset = 4 + i * 5;
            ReadOnlySpan<byte> entry =
                descriptor.Span.Slice(offset + 1, 4);

            RegisterType type =
                (RegisterType)descriptor.Span[offset];

            if (type == RegisterType.Word)
            {
                registers[position++] =
                    BinaryPrimitives.ReadUInt16LittleEndian(
                        entry);
            }
            else
            {
                uint bits =
                    BinaryPrimitives.ReadUInt32LittleEndian(
                        entry);

                float value =
                    BitConverter.UInt32BitsToSingle(bits);

                EncodeFloat32(
                    value,
                    order,
                    registers,
                    position);

                position += 2;
            }
        }

        return await ExecuteWriteAsync(
            session => session.WriteMultipleRegistersAsync(
                address,
                registers))
            .ConfigureAwait(false);
    }


    /// <summary>
    /// Преобразует Float32 в два Modbus-регистра.
    ///
    /// Порядок соответствует действующему
    /// TryDecodeValue в Core.
    ///
    /// Регистры размещаются в готовом массиве Core.
    /// </summary>
    private static void EncodeFloat32(
        float value,
        RegisterOrder order,
        ushort[] destination,
        int offset)
    {
        uint bits = BitConverter.SingleToUInt32Bits(value);

        ushort lowWord = (ushort)bits;
        ushort highWord = (ushort)(bits >> 16);

        switch (order)
        {
            case RegisterOrder.ABCD:
                destination[offset] = lowWord;
                destination[offset + 1] = highWord;
                break;

            case RegisterOrder.BADC:
                destination[offset] = SwapBytes(lowWord);
                destination[offset + 1] = SwapBytes(highWord);
                break;

            case RegisterOrder.CDAB:
                destination[offset] = highWord;
                destination[offset + 1] = lowWord;
                break;

            case RegisterOrder.DCBA:
                destination[offset] = SwapBytes(highWord);
                destination[offset + 1] = SwapBytes(lowWord);
                break;
        }
    }


    /// <summary>
    /// Выполняет одну операцию записи через Transport.
    ///
    /// Состояния Transport преобразуются
    /// в существующую модель статусных операций.
    ///
    /// Повторная отправка запрещена.
    /// Исключения не выходят за границу Core.
    /// </summary>
    private async Task<CoreWriteStatus> ExecuteWriteAsync(
        Func<ModbusTransportSession,
            Task<ModbusWriteResult>> operation)
    {
        ModbusTransportSession session;

        lock (_snapshotLock)
        {
            if (_stopping || _transport is null)
                return CoreWriteStatus.Stopped;

            session = _transport;
        }

        try
        {
            ModbusWriteResult result =
                await operation(session)
                    .ConfigureAwait(false);

            return MapWriteStatus(result.Status);
        }
        catch (ObjectDisposedException ex)
        {
            // TODO: Подключить логирование
            // завершения Transport во время Write.
            ReportWriteError(ex);

            return IsStopping()
                ? CoreWriteStatus.Stopped
                : CoreWriteStatus.Failed;
        }
        catch (InvalidOperationException ex)
        {
            // TODO: Подключить логирование
            // некорректного состояния сессии.
            ReportWriteError(ex);

            return IsStopping()
                ? CoreWriteStatus.Stopped
                : CoreWriteStatus.Failed;
        }
        catch (Exception ex)
        {
            // TODO: Подключить централизованное
            // логирование неожиданных ошибок записи.
            ReportWriteError(ex);

            return CoreWriteStatus.Failed;
        }
    }

    /// <summary>
    /// Преобразует статус Transport в статус Write.
    /// Не подменяет ошибки успешным выполнением.
    /// </summary>
    private static CoreWriteStatus MapWriteStatus(
        ModbusTransportStatus status)
    {
        return status switch
        {
            ModbusTransportStatus.Ok =>
                CoreWriteStatus.Ok,

            ModbusTransportStatus.Disconnected or
            ModbusTransportStatus.ConnectTimeout =>
                CoreWriteStatus.Disconnected,

            ModbusTransportStatus.RequestTimeout =>
                CoreWriteStatus.Timeout,

            ModbusTransportStatus.Cancelled =>
                CoreWriteStatus.Stopped,

            _ => CoreWriteStatus.Failed
        };
    }

    /// <summary>
    /// Временная диагностика ошибок записи.
    /// </summary>
    private static void ReportWriteError(Exception exception)
    {
        // TODO: Заменить Trace на централизованный
        // промышленный механизм логирования CoreLink.
        try
        {
            Trace.TraceError(
                $"CoreLink Write failed: {exception}");
        }
        catch (Exception)
        {
            // Ошибка диагностического вывода
            // не должна нарушать контракт Core.
        }
    }

}
