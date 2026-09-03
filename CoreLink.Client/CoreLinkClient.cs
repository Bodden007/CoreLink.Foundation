using CoreLink.Client.Lifecycle;
using CoreLink.Client.Results;
using CoreLink.Contracts.Maps;

namespace CoreLink.Client;

/// <summary>
/// Главная публичная точка управления CoreLink-стеком.
/// </summary>
public sealed class CoreLinkClient
{
    private readonly IMapProvider _mapProvider;

    private MapConfiguration? _configuration;

    public CoreLinkState State { get; private set; } =
        CoreLinkState.Created;

    public CoreLinkClient(IMapProvider mapProvider)
    {
        _mapProvider = mapProvider;
    }

    public CoreLinkResult Start(string mapId)
    {
        if (State is CoreLinkState.Starting or CoreLinkState.Running)
        {
            return CoreLinkResult.Ok;
        }

        State = CoreLinkState.Starting;

        if (!_mapProvider.TryLoad(
                mapId,
                out _configuration))
        {
            State = CoreLinkState.Faulted;

            return CoreLinkResult.BadConfig;
        }

        State = CoreLinkState.Running;

        return CoreLinkResult.Ok;
    }

    public void Stop()
    {
        if (State is CoreLinkState.Stopped or CoreLinkState.Created)
        {
            return;
        }

        State = CoreLinkState.Stopping;

        _configuration = null;

        State = CoreLinkState.Stopped;
    }
}