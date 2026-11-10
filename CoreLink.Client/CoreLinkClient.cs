
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
            {
                return _state;
            }
        }
    }

    public CoreMap? CoreMap
    {
        get
        {
            lock (_lifecycleLock)
            {
                return _coreMap;
            }
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
            {
                return _rx?.Values;
            }
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
            {
                return _rx?.Statuses;
            }
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

                // Буферы выделяются однократно.
                // Публичные запросы и Rx не используют
                // общий изменяемый массив.
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
                    // TODO: Уточнить гарантии освобождения
                    // ресурсов после неуспешного Core.Start.
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
                    rxMap.RxIntervalMs);

                _rx = rx;
                _state = CoreLinkState.Running;

                rx.Start();

                if (rx.IsFaulted)
                {
                    ReportError(
                        rx.LastError ?? "Rx start failed.");

                    // TODO: Согласовать восстановление
                    // после отказа запуска Rx.
                    _state = CoreLinkState.Faulted;
                    return CoreLinkResult.SessionError;
                }

                return CoreLinkResult.Ok;
            }
            catch (Exception ex)
            {
                ReportError("CoreLink start failed.", ex);
                _state = CoreLinkState.Faulted;

                // Ресурсы запущенной сессии
                // освобождаются через StopAsync.
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
    ///
    /// Core не сохраняет указатель.
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

                // TODO: Добавить отдельный SnapshotStatus
                // для внутренних ошибок чтения.
                return SnapshotStatus.BadBuffer;
            }
        }
    }

    /// <summary>
    /// SnapshotReader, принадлежащий Rx.
    ///
    /// Использует отдельный постоянный буфер.
    /// Новый массив на каждом цикле не создаётся.
    ///
    /// Rx выполняет запросы последовательно:
    /// следующая запись в буфер произойдёт только
    /// после синхронной обработки текущего snapshot.
    ///
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

                // TODO: Уточнить статус внутренних
                // ошибок получения snapshot.
                return SnapshotStatus.BadBuffer;
            }
        }
    }

    /// <summary>
    /// Основной механизм остановки.
    ///
    /// Запрещает новые операции, завершает Rx
    /// без удержания lifecycle lock,
    /// затем останавливает Core.
    ///
    /// Конкурентные вызовы получают одну задачу.
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
            {
                _state = CoreLinkState.Faulted;
            }

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

            // Освобождение ссылок только после
            // подтверждённого завершения Rx и Core.
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
    ///
    /// Не вызывать непосредственно из обработчика Rx.
    /// Для такого сценария использовать StopAsync.
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
    /// Временная диагностика Client.
    ///
    /// TODO: Заменить Trace централизованным логгером.
    /// </summary>
    private static void ReportError(
        string message,
        Exception? exception = null)
    {
        try
        {
            Trace.TraceError(
                exception is null
                    ? message
                    : $"{message} {exception}");
        }
        catch (Exception)
        {
            // Ошибка диагностики не должна
            // нарушать работу Client.
        }
    }
}
