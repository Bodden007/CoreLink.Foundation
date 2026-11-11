
using System.Diagnostics;
using CoreLink.Client.Configuration;
using CoreLink.Client.Lifecycle;
using CoreLink.Client.Results;
using CoreLink.Contracts.Core;
using CoreLink.Contracts.Maps;
using CoreLink.Rx.Runtime;

namespace CoreLink.Client;

/// <summary>
/// Публичная точка управления CoreLink.
///
/// Client владеет конфигурацией сессии, буферами
/// и жизненным циклом Core и Rx.
///
/// Rx получает snapshot только через Client.
/// Client не выполняет самостоятельный polling.
/// </summary>
public sealed class CoreLinkClient
{
    private readonly IMapProvider _mapProvider;
    private readonly ICore _core;

    private readonly object _lifecycleLock = new();

    private CoreMap? _coreMap;

    private float[]? _coreBuffer;
    private float[]? _rxBuffer;

    private CoreLinkRx? _rx;

    private Task<CoreLinkResult>? _stopTask;
    private bool _coreStarted;

    private CoreLinkState _state = CoreLinkState.Created;

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

    public CoreLinkClient(
        IMapProvider mapProvider,
        ICore core)
    {
        _mapProvider = mapProvider;
        _core = core;
    }

    /// <summary>
    /// Загружает обе проекции карты, запускает Core,
    /// затем создаёт и запускает Rx.
    ///
    /// Повторный Start после Stop не поддерживается
    /// существующим контрактом экземпляра ICore.
    /// </summary>
    public CoreLinkResult Start(string mapId)
    {
        lock (_lifecycleLock)
        {
            if (_state == CoreLinkState.Running)
                return CoreLinkResult.Ok;

            if (_state != CoreLinkState.Created)
                return CoreLinkResult.SessionError;

            _state = CoreLinkState.Starting;

            try
            {
                if (_mapProvider is null ||
                    _core is null ||
                    string.IsNullOrWhiteSpace(mapId))
                {
                    return StartFailed(CoreLinkResult.BadConfig);
                }

                if (!_mapProvider.TryLoad(
                        mapId,
                        out CoreMap? coreMap) ||
                    coreMap is null)
                {
                    return StartFailed(CoreLinkResult.BadConfig);
                }

                if (!_mapProvider.TryLoadRxMap(
                        mapId,
                        out RxMapConfiguration? rxMap,
                        out string? mapError) ||
                    rxMap is null)
                {
                    ReportError(
                        mapError ?? "Rx map loading failed.");

                    return StartFailed(CoreLinkResult.BadConfig);
                }

                if (!RxDefinitionMapper.TryCreate(
                        rxMap,
                        out var definitions,
                        out string? definitionError))
                {
                    ReportError(
                        definitionError ??
                        "Rx definitions are invalid.");

                    return StartFailed(CoreLinkResult.BadConfig);
                }

                if (coreMap.BufferLength <= 0 ||
                    coreMap.BufferLength != definitions.Length)
                {
                    ReportError(
                        "Core/Rx value count mismatch.");

                    return StartFailed(CoreLinkResult.BadConfig);
                }

                var coreBuffer =
                    new float[coreMap.BufferLength];

                var rxBuffer =
                    new float[coreMap.BufferLength];

                CoreStatus coreStatus = _core.Start(coreMap);

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
                    return StartFailed(result);
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
                    ReportError(
                        rx.LastError ?? "Rx start failed.");

                    _state = CoreLinkState.Faulted;
                    return CoreLinkResult.SessionError;
                }

                return CoreLinkResult.Ok;
            }
            catch (Exception ex)
            {
                ReportError("CoreLink start failed.", ex);
                _state = CoreLinkState.Faulted;
                return CoreLinkResult.SessionError;
            }
        }
    }

    private CoreLinkResult StartFailed(CoreLinkResult result)
    {
        _state = CoreLinkState.Faulted;
        return result;
    }

    /// <summary>
    /// Публичное получение snapshot через Core.
    ///
    /// Использует собственный буфер Client.
    /// Повторный вызов может изменить ранее
    /// возвращённый ReadOnlyMemory.
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
                ReportError("Snapshot read failed.", ex);
                return SnapshotStatus.BadBuffer;
            }
        }
    }

    /// <summary>
    /// Асинхронно читает значения PLC по ValueId.
    ///
    /// Не использует snapshot и не изменяет Rx.
    /// При любой ошибке возвращает общий статус
    /// без частично прочитанных значений.
    ///
    /// Исключения не выходят за границу Client.
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
            // TODO: Заменить временный Trace
            // централизованным логированием CoreLink.
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
    ///
    /// Использует отдельный постоянный буфер.
    /// Публичный GetSnapshot не изменяет этот буфер.
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
                ReportError("Rx snapshot read failed.", ex);
                return SnapshotStatus.BadBuffer;
            }
        }
    }

    /// <summary>
    /// Основной механизм остановки.
    ///
    /// Конкурентные вызовы получают одну задачу.
    /// Сначала останавливается Rx, затем Core.
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
                    ReportError("Core stop failed.", ex);
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
            // Ошибки диагностики не должны нарушать Client.
        }
    }
}
