
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
/// </summary>
public sealed class CoreLinkClient
{
    private readonly IMapProvider _mapProvider;
    private readonly ICore _core;

    private CoreMap? _coreMap;
    private float[]? _coreBuffer;

    public CoreMap? CoreMap =>
        _coreMap;

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

    public void Stop()
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
