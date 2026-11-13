
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reactive.Linq;
using CoreLink.Client.Configuration;
using CoreLink.Client.Lifecycle;
using CoreLink.Client.Results;
using CoreLink.Contracts.Core;
using CoreLink.Contracts.Maps;
using CoreLink.Contracts.Registers;
using CoreLink.Maps;
using CoreLink.Rx.Runtime;

namespace CoreLink.Client;

/// <summary>
/// Публичная точка управления CoreLink.
/// Владеет конфигурацией сессии, буферами
/// и жизненным циклом Core и Rx.
/// Polling выполняется внутри Core.
/// </summary>
public sealed class CoreLinkClient
{
    private readonly ICore _core;
    private readonly object _lifecycleLock = new();

    private string? _lastError;
    private CoreMap? _coreMap;

    private float[]? _coreBuffer;
    private float[]? _rxBuffer;

    private CoreLinkRx? _rx;

    private Task<CoreLinkResult>? _stopTask;
    private bool _coreStarted;

    private CoreLinkState _state = CoreLinkState.Created;

    private int _lastSingleReadStatus =
        (int)CoreReadStatus.Stopped;

    private int _lastWriteStatus =
        (int)CoreWriteStatus.Stopped;

    public CoreLinkClient(ICore core)
    {
        _core = core;
    }

    public string? LastError
    {
        get
        {
            lock (_lifecycleLock)
                return _lastError;
        }
    }

    public CoreLinkState State
    {
        get
        {
            lock (_lifecycleLock)
                return _state;
        }
    }

    public CoreMap? CoreMap
    {
        get
        {
            lock (_lifecycleLock)
                return _coreMap;
        }
    }

    /// <summary>
    /// Поток изменений значений Rx.
    /// Доступен после успешного Start.
    /// </summary>
    public IObservable<RxValue>? Values
    {
        get
        {
            lock (_lifecycleLock)
                return _rx?.Values;
        }
    }

    /// <summary>
    /// Поток статусов получения snapshot.
    /// </summary>
    public IObservable<SnapshotStatus>? Statuses
    {
        get
        {
            lock (_lifecycleLock)
                return _rx?.Statuses;
        }
    }

    /// <summary>
    /// Последний статус одиночного чтения.
    /// </summary>
    public CoreReadStatus LastSingleReadStatus =>
        (CoreReadStatus)Volatile.Read(
            ref _lastSingleReadStatus);

    /// <summary>
    /// Последний статус операции записи.
    /// </summary>
    public CoreWriteStatus LastWriteStatus =>
        (CoreWriteStatus)Volatile.Read(
            ref _lastWriteStatus);

    /// <summary>
    /// Проверяет карту, формирует конфигурации
    /// Core и Rx, затем запускает систему.
    ///
    /// При невалидной карте Core не запускается.
    /// Повторный Start после Stop не поддерживается.
    /// </summary>
    public CoreLinkResult Start(CoreLinkMap? map)
    {
        lock (_lifecycleLock)
        {
            if (_state == CoreLinkState.Running)
                return CoreLinkResult.Ok;

            if (_state != CoreLinkState.Created)
                return CoreLinkResult.SessionError;

            _state = CoreLinkState.Starting;
            _lastError = null;

            try
            {
                if (!TryValidateMap(
                        map,
                        out string? error))
                {
                    return StartFailed(
                        CoreLinkResult.BadConfig,
                        error ?? "Invalid CoreLink map.");
                }

                if (_core is null)
                {
                    return StartFailed(
                        CoreLinkResult.SessionError,
                        "Core implementation is null.");
                }

                CoreMap coreMap;

                try
                {
                    coreMap = MapProvider.BuildCore(map!);
                }
                catch (Exception ex) when (
                    ex is ArgumentException or
                    InvalidOperationException or
                    OverflowException)
                {
                    // TODO: Подключить централизованное
                    // логирование ошибок конфигурации.
                    return StartFailed(
                        CoreLinkResult.BadConfig,
                        $"Core map encoding failed: {ex}");
                }

                if (!MapProvider.TryBuildRx(
                        map!,
                        out RxMapConfiguration? rxMap,
                        out string? mapError) ||
                    rxMap is null)
                {
                    return StartFailed(
                        CoreLinkResult.BadConfig,
                        mapError ?? "Rx map building failed.");
                }

                if (!RxDefinitionMapper.TryCreate(
                        rxMap,
                        out var definitions,
                        out string? definitionError))
                {
                    return StartFailed(
                        CoreLinkResult.BadConfig,
                        definitionError ??
                        "Rx definitions are invalid.");
                }

                if (coreMap.BufferLength <= 0 ||
                    coreMap.BufferLength != definitions.Length)
                {
                    return StartFailed(
                        CoreLinkResult.BadConfig,
                        "Core/Rx value count mismatch.");
                }

                var coreBuffer =
                    new float[coreMap.BufferLength];

                var rxBuffer =
                    new float[coreMap.BufferLength];

                CoreStatus coreStatus =
                    _core.Start(coreMap);

                CoreLinkResult result = coreStatus switch
                {
                    CoreStatus.Running =>
                        CoreLinkResult.Ok,

                    CoreStatus.Disconnected =>
                        CoreLinkResult.Disconnected,

                    CoreStatus.BadConfig =>
                        CoreLinkResult.BadConfig,

                    _ =>
                        CoreLinkResult.SessionError
                };

                if (result != CoreLinkResult.Ok)
                {
                    return StartFailed(
                        result,
                        $"Core start failed: {coreStatus}.");
                }

                _coreStarted = true;
                _coreMap = coreMap;

                _coreBuffer = coreBuffer;
                _rxBuffer = rxBuffer;

                var rx = new CoreLinkRx(
                    ReadRxSnapshot,
                    definitions,
                    rxMap.UnitSystemValueId,
                    rxMap.SupportedUnitSystemIds,
                    rxMap.RxIntervalMs);

                _rx = rx;
                _state = CoreLinkState.Running;

                rx.Start();

                if (rx.IsFaulted)
                {
                    return StartFailed(
                        CoreLinkResult.SessionError,
                        rx.LastError ?? "Rx start failed.");
                }

                return CoreLinkResult.Ok;
            }
            catch (Exception ex)
            {
                // TODO: Подключить централизованное
                // логирование исключений Client.Start.
                _lastError = ex.ToString();

                ReportError(
                    "CoreLink start failed.",
                    ex);

                _state = CoreLinkState.Faulted;

                return CoreLinkResult.SessionError;
            }
        }
    }

    /// <summary>
    /// Сохраняет причину отказа запуска.
    /// </summary>
    private CoreLinkResult StartFailed(
        CoreLinkResult result,
        string message)
    {
        _lastError = message;
        _state = CoreLinkState.Faulted;

        ReportError(
            $"CoreLink.Client.Start: {result}. {message}");

        return result;
    }

    /// <summary>
    /// Получает snapshot через Core.
    /// Возвращаемый ReadOnlyMemory использует
    /// повторно применяемый буфер Client.
    /// </summary>
    public unsafe SnapshotStatus GetSnapshot(
        out ReadOnlyMemory<float> values)
    {
        lock (_lifecycleLock)
        {
            values = ReadOnlyMemory<float>.Empty;

            if (_state != CoreLinkState.Running ||
                _coreBuffer is null)
            {
                return SnapshotStatus.Stopped;
            }

            float[] buffer = _coreBuffer;

            try
            {
                SnapshotStatus status;

                fixed (float* pointer = buffer)
                {
                    status = _core.GetSnapshot(
                        pointer,
                        buffer.Length);
                }

                if (status == SnapshotStatus.Ok)
                    values = buffer;

                return status;
            }
            catch (Exception ex)
            {
                // TODO: Централизованное логирование.
                ReportError(
                    "Snapshot read failed.",
                    ex);

                return SnapshotStatus.BadBuffer;
            }
        }
    }

    /// <summary>
    /// Читает значения PLC по ValueId.
    /// Не изменяет snapshot или Rx.
    /// </summary>
    public async Task<CoreReadResult> ReadAsync(
        IReadOnlyList<int> valueIds)
    {
        lock (_lifecycleLock)
        {
            if (_state != CoreLinkState.Running)
            {
                return new CoreReadResult
                {
                    Status = CoreReadStatus.Stopped,
                    Values = null
                };
            }
        }

        if (valueIds is null ||
            valueIds.Count == 0)
        {
            return new CoreReadResult
            {
                Status = CoreReadStatus.BadConfig,
                Values = null
            };
        }

        try
        {
            CoreReadResult result =
                await _core.ReadAsync(valueIds)
                    .ConfigureAwait(false);

            return result;
        }
        catch (Exception ex)
        {
            // TODO: Подключить централизованное
            // логирование ошибок Read.
            ReportError(
                "Direct register read failed.",
                ex);

            return new CoreReadResult
            {
                Status = CoreReadStatus.Failed,
                Values = null
            };
        }
    }

    /// <summary>
    /// SnapshotReader, принадлежащий Rx.
    /// Использует отдельный постоянный буфер.
    /// </summary>
    private unsafe SnapshotStatus ReadRxSnapshot(
        out ReadOnlyMemory<float> values)
    {
        lock (_lifecycleLock)
        {
            values = ReadOnlyMemory<float>.Empty;

            if (_state != CoreLinkState.Running ||
                _rxBuffer is null)
            {
                return SnapshotStatus.Stopped;
            }

            float[] buffer = _rxBuffer;

            try
            {
                SnapshotStatus status;

                fixed (float* pointer = buffer)
                {
                    status = _core.GetSnapshot(
                        pointer,
                        buffer.Length);
                }

                if (status == SnapshotStatus.Ok)
                    values = buffer;

                return status;
            }
            catch (Exception ex)
            {
                // TODO: Централизованное логирование.
                ReportError(
                    "Rx snapshot read failed.",
                    ex);

                return SnapshotStatus.BadBuffer;
            }
        }
    }

    /// <summary>
    /// Остановка Rx, затем Core.
    /// Конкурентные Stop получают одну Task.
    /// </summary>
    public Task<CoreLinkResult> StopAsync()
    {
        lock (_lifecycleLock)
        {
            if (_stopTask is not null)
                return _stopTask;

            if (_state is
                CoreLinkState.Created or
                CoreLinkState.Stopped)
            {
                return Task.FromResult(CoreLinkResult.Ok);
            }

            _state = CoreLinkState.Stopping;

            CoreLinkRx? rx = _rx;

            _stopTask = Task.Run(
                () => FinishStopAsync(rx));

            return _stopTask;
        }
    }

    private async Task<CoreLinkResult> FinishStopAsync(
        CoreLinkRx? rx)
    {
        bool rxStopped = rx is null;

        try
        {
            if (rx is not null)
            {
                await rx.DisposeAsync()
                    .ConfigureAwait(false);

                rxStopped = true;
            }
        }
        catch (Exception ex)
        {
            // TODO: Централизованное логирование.
            ReportError("Rx stop failed.", ex);
        }

        if (!rxStopped)
        {
            lock (_lifecycleLock)
                _state = CoreLinkState.Faulted;

            return CoreLinkResult.SessionError;
        }

        lock (_lifecycleLock)
        {
            if (_coreStarted)
            {
                CoreStatus status;

                try
                {
                    status = _core.Stop();
                }
                catch (Exception ex)
                {
                    // TODO: Централизованное логирование.
                    ReportError(
                        "Core stop failed.",
                        ex);

                    _state = CoreLinkState.Faulted;

                    return CoreLinkResult.SessionError;
                }

                if (status != CoreStatus.Stopped)
                {
                    ReportError(
                        $"Unexpected Core stop status: {status}");

                    _state = CoreLinkState.Faulted;

                    return CoreLinkResult.SessionError;
                }

                _coreStarted = false;
            }

            _rx = null;
            _rxBuffer = null;
            _coreBuffer = null;
            _coreMap = null;

            _state = CoreLinkState.Stopped;

            return CoreLinkResult.Ok;
        }
    }

    /// <summary>
    /// Синхронная обёртка для совместимости.
    /// Не вызывать из обработчика Rx.
    /// </summary>
    public void Stop()
    {
        CoreLinkResult result =
            StopAsync().GetAwaiter().GetResult();

        if (result != CoreLinkResult.Ok)
        {
            ReportError(
                $"CoreLink stop returned {result}.");
        }
    }

    /// <summary>
    /// Временный механизм диагностики.
    /// </summary>
    private static void ReportError(
        string message,
        Exception? exception = null)
    {
        // TODO: Внедрить централизованное
        // диагностическое логирование CoreLink.
        try
        {
            Trace.TraceError(
                exception is null
                    ? message
                    : $"{message} {exception}");
        }
        catch (Exception)
        {
            // TODO: Диагностика отказа логгера.
            // Ошибка диагностики не должна
            // нарушать работу Client.
        }
    }

    /// <summary>
    /// Выполняет одиночное чтение PLC.
    /// Значение обновляется только при успехе.
    /// Snapshot и Rx не затрагиваются.
    /// </summary>
    public async Task ReadSingleAsync(
        ushort address,
        RegisterType type,
        ModbusReadFunction function,
        Action<float> updateValue)
    {
        lock (_lifecycleLock)
        {
            if (_state != CoreLinkState.Running)
            {
                SetSingleReadStatus(CoreReadStatus.Stopped);
                return;
            }
        }

        if (updateValue is null)
        {
            SetSingleReadStatus(CoreReadStatus.BadConfig);
            return;
        }

        try
        {
            CoreSingleReadResult result =
                await _core.ReadSingleAsync(
                    address,
                    type,
                    function)
                .ConfigureAwait(false);

            SetSingleReadStatus(result.Status);

            if (result.Status != CoreReadStatus.Ok)
                return;

            updateValue(result.Value);
        }
        catch (Exception ex)
        {
            // TODO: Подключить централизованное
            // логирование ошибок Single Read.
            ReportError(
                "Single Read failed.",
                ex);

            SetSingleReadStatus(CoreReadStatus.Failed);
        }
    }

    private void SetSingleReadStatus(CoreReadStatus status)
    {
        Volatile.Write(
            ref _lastSingleReadStatus,
            (int)status);
    }

    private void SetWriteStatus(CoreWriteStatus status)
    {
        Volatile.Write(
            ref _lastWriteStatus,
            (int)status);
    }

    /// <summary>
    /// Записывает Word через FC06.
    /// Не изменяет snapshot.
    /// </summary>
    public Task WriteWordAsync(
        ushort address,
        ushort value)
    {
        return ExecuteClientWriteAsync(
            () => _core.WriteWordAsync(address, value));
    }

    /// <summary>
    /// Записывает Float32 через FC16.
    /// Порядок слов берётся из текущей сессии.
    /// </summary>
    public Task WriteFloatAsync(
        ushort address,
        float value)
    {
        return ExecuteClientWriteAsync(
            () => _core.WriteFloatAsync(address, value));
    }

    /// <summary>
    /// Записывает последовательность Word/Float32.
    /// Один Batch соответствует одному FC16.
    /// Максимум 123 физических регистра.
    /// </summary>
    public async Task WriteBatchAsync(
        ushort startAddress,
        IReadOnlyList<CoreWriteValue> values)
    {
        if (values is null ||
            values.Count == 0 ||
            values.Count > 123)
        {
            SetWriteStatus(CoreWriteStatus.BadConfig);
            return;
        }

        try
        {
            int registerCount = 0;

            foreach (CoreWriteValue value in values)
            {
                int length = value.Type switch
                {
                    RegisterType.Word => 1,
                    RegisterType.Float => 2,
                    _ => 0
                };

                if (length == 0)
                {
                    SetWriteStatus(CoreWriteStatus.BadConfig);
                    return;
                }

                registerCount += length;

                if (registerCount > 123)
                {
                    SetWriteStatus(CoreWriteStatus.BadConfig);
                    return;
                }
            }

            if ((uint)startAddress +
                (uint)registerCount > 65536u)
            {
                SetWriteStatus(CoreWriteStatus.BadConfig);
                return;
            }

            byte[] descriptor =
                new byte[4 + values.Count * 5];

            BinaryPrimitives.WriteUInt16LittleEndian(
                descriptor.AsSpan(0, 2),
                startAddress);

            BinaryPrimitives.WriteUInt16LittleEndian(
                descriptor.AsSpan(2, 2),
                (ushort)values.Count);

            for (int i = 0; i < values.Count; i++)
            {
                CoreWriteValue value = values[i];
                int offset = 4 + i * 5;

                descriptor[offset] = (byte)value.Type;

                if (value.Type == RegisterType.Word)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(
                        descriptor.AsSpan(offset + 1, 2),
                        value.WordValue);
                }
                else
                {
                    uint bits =
                        BitConverter.SingleToUInt32Bits(
                            value.FloatValue);

                    BinaryPrimitives.WriteUInt32LittleEndian(
                        descriptor.AsSpan(offset + 1, 4),
                        bits);
                }
            }

            await ExecuteClientWriteAsync(
                () => _core.WriteBatchAsync(descriptor))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // TODO: Подключить централизованное
            // логирование формирования Batch.
            ReportError(
                "Batch Write failed.",
                ex);

            SetWriteStatus(CoreWriteStatus.Failed);
        }
    }

    /// <summary>
    /// Выполняет пользовательскую запись.
    /// Сохраняет результат для диагностики.
    /// </summary>
    private async Task ExecuteClientWriteAsync(
        Func<Task<CoreWriteStatus>> operation)
    {
        lock (_lifecycleLock)
        {
            if (_state != CoreLinkState.Running)
            {
                SetWriteStatus(CoreWriteStatus.Stopped);
                return;
            }
        }

        try
        {
            CoreWriteStatus status =
                await operation().ConfigureAwait(false);

            SetWriteStatus(status);
        }
        catch (Exception ex)
        {
            // TODO: Подключить централизованное
            // логирование ошибок Write.
            ReportError(
                "Client Write failed.",
                ex);

            SetWriteStatus(CoreWriteStatus.Failed);
        }
    }

    /// <summary>
    /// Возвращает поток изменений по ValueId.
    /// Последнее значение не воспроизводится.
    /// Дополнительных запросов к Core нет.
    /// </summary>
    public IObservable<RxValue> ObserveValue(int valueId)
    {
        try
        {
            lock (_lifecycleLock)
            {
                if (_state != CoreLinkState.Running ||
                    _rx is null ||
                    _coreMap is null)
                {
                    // TODO: Централизованное логирование.
                    return Observable.Empty<RxValue>();
                }

                if ((uint)valueId >=
                    (uint)_coreMap.BufferLength)
                {
                    // TODO: Централизованное логирование.
                    return Observable.Empty<RxValue>();
                }

                return _rx.Values.Where(
                    value => value.ValueId == valueId);
            }
        }
        catch (Exception ex)
        {
            // TODO: Централизованное логирование.
            ReportError(
                "ObserveValue failed.",
                ex);

            return Observable.Empty<RxValue>();
        }
    }

    /// <summary>
    /// Проверяет карту до запуска Core.
    /// Невалидная конфигурация блокирует polling.
    /// </summary>
    private static bool TryValidateMap(
        CoreLinkMap? map,
        out string? error)
    {
        error = null;

        if (map is null)
        {
            error = "CoreLink map is null.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(map.IpAddress) ||
            !IPAddress.TryParse(
                map.IpAddress,
                out IPAddress? address) ||
            address.AddressFamily != AddressFamily.InterNetwork)
        {
            error = "Invalid IPv4 address.";
            return false;
        }

        if (map.Port == 0)
        {
            error = "Invalid Modbus port.";
            return false;
        }

        if (map.UnitId == 0)
        {
            error = "Invalid Modbus UnitId.";
            return false;
        }

        if (map.PollFunctionCode is not (3 or 4))
        {
            error = "Unsupported polling function code.";
            return false;
        }

        if (map.PollIntervalMs <= 0 ||
            map.RxIntervalMs <= 0)
        {
            error = "Invalid polling or Rx interval.";
            return false;
        }

        if (map.ConnectTimeoutMs <= 0 ||
            map.RequestTimeoutMs <= 0 ||
            map.ReconnectDelayMs <= 0)
        {
            error = "Invalid connection timeout configuration.";
            return false;
        }

        if (!Enum.IsDefined(map.RegisterOrder))
        {
            error = "Invalid register order.";
            return false;
        }

        if (map.Registers is null ||
            map.Registers.Length == 0)
        {
            error = "Register map is empty.";
            return false;
        }

        if (map.SupportedUnitSystemIds is null ||
            map.SupportedUnitSystemIds.Length == 0)
        {
            error = "Supported unit systems are empty.";
            return false;
        }

        int minAddress = int.MaxValue;
        int maxAddress = 0;

        var occupied = new HashSet<int>();

        foreach (var register in map.Registers)
        {
            if (string.IsNullOrWhiteSpace(register.Name))
            {
                error = "Register name is empty.";
                return false;
            }

            int length = register.Type switch
            {
                RegisterType.Word => 1,
                RegisterType.Float => 2,
                _ => 0
            };

            if (length == 0)
            {
                error =
                    $"Invalid register type: {register.Name}.";
                return false;
            }

            int start = register.Address;
            int end = start + length - 1;

            if (end > ushort.MaxValue)
            {
                error =
                    $"Register address overflow: {register.Name}.";
                return false;
            }

            for (int addressIndex = start;
                 addressIndex <= end;
                 addressIndex++)
            {
                if (!occupied.Add(addressIndex))
                {
                    error =
                        $"Overlapping register: {register.Name}, " +
                        $"address {addressIndex}.";
                    return false;
                }
            }

            minAddress = Math.Min(minAddress, start);
            maxAddress = Math.Max(maxAddress, end);
        }

        if (maxAddress - minAddress + 1 > 125)
        {
            error =
                "Modbus polling range exceeds 125 registers.";
            return false;
        }

        return true;
    }
}
