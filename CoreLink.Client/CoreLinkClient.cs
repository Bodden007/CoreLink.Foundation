
using CoreLink.Client.Lifecycle;
using CoreLink.Client.Results;
using CoreLink.Contracts.Core;
using CoreLink.Contracts.Maps;

namespace CoreLink.Client;

/// <summary>
/// Публичная точка управления CoreLink.
///
/// Client владеет конфигурацией сессии и клиентским буфером.
/// Управляет жизненным циклом предоставленного ICore.
///
/// Синхронизация защищает lifecycle и операции
/// с клиентским буфером от конкурентного Stop.
/// </summary>
public sealed class CoreLinkClient
{
    private readonly IMapProvider _mapProvider;
    private readonly ICore _core;

    private readonly object _lifecycleLock = new();

    private CoreMap? _coreMap;
    private float[]? _coreBuffer;

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

    public CoreLinkState State { get; private set; } =
        CoreLinkState.Created;

    public CoreLinkClient(
        IMapProvider mapProvider,
        ICore core)
    {
        ArgumentNullException.ThrowIfNull(mapProvider);
        ArgumentNullException.ThrowIfNull(core);

        _mapProvider = mapProvider;
        _core = core;
    }

    public CoreLinkResult Start(string mapId)
    {
        lock (_lifecycleLock)
        {
            if (State is
                CoreLinkState.Starting or
                CoreLinkState.Running)
            {
                return CoreLinkResult.Ok;
            }

            State = CoreLinkState.Starting;

            if (!_mapProvider.TryLoad(
                    mapId,
                    out _coreMap) ||
                _coreMap is null)
            {
                State = CoreLinkState.Faulted;
                return CoreLinkResult.BadConfig;
            }

            _coreBuffer =
                new float[_coreMap.BufferLength];

            var coreStatus =
                _core.Start(_coreMap);

            var result = coreStatus switch
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
                _coreBuffer = null;
                _coreMap = null;
                State = CoreLinkState.Faulted;
                return result;
            }

            State = CoreLinkState.Running;
            return CoreLinkResult.Ok;
        }
    }

    /// <summary>
    /// Получает последний согласованный snapshot через ICore.
    ///
    /// Клиентский буфер временно закрепляется через fixed.
    /// Core не сохраняет указатель.
    ///
    /// При Ok возвращается представление буфера Client.
    /// Rx должен синхронно скопировать данные в свой буфер.
    ///
    /// Lock сериализует GetSnapshot относительно Start и Stop.
    /// Lifetime возвращённого ReadOnlyMemory за пределами
    /// метода этим lock не защищается.
    /// </summary>
    public unsafe SnapshotStatus GetSnapshot(
        out ReadOnlyMemory<float> values)
    {
        lock (_lifecycleLock)
        {
            values = ReadOnlyMemory<float>.Empty;

            if (State != CoreLinkState.Running ||
                _coreBuffer is null)
            {
                return SnapshotStatus.Stopped;
            }

            float[] buffer = _coreBuffer;

            SnapshotStatus status;

            fixed (float* pointer = buffer)
            {
                status = _core.GetSnapshot(
                    pointer,
                    buffer.Length);
            }

            if (status == SnapshotStatus.Ok)
            {
                values = buffer;
            }

            return status;
        }
    }

    /// <summary>
    /// Останавливает Core и освобождает клиентские ресурсы.
    ///
    /// Ожидает завершения активного GetSnapshot,
    /// после чего новые запросы не допускаются.
    ///
    /// FIXME: При подключении Rx сначала завершать
    /// его цикл и операции копирования, затем Core.
    ///
    /// FIXME: Согласовать обработку результата Core.Stop,
    /// прежде чем считать остановку гарантированно успешной.
    /// </summary>
    public void Stop()
    {
        lock (_lifecycleLock)
        {
            if (State is
                CoreLinkState.Stopped or
                CoreLinkState.Created)
            {
                return;
            }

            State = CoreLinkState.Stopping;

            _core.Stop();

            _coreBuffer = null;
            _coreMap = null;

            State = CoreLinkState.Stopped;
        }
    }
}
