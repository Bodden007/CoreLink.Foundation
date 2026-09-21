using CoreLink.Contracts.Maps;

namespace CoreLink.Contracts.Maps;

public interface IMapProvider
{
    bool TryLoad(
        string mapId,
        out CoreMap? coreMap);
}