using CoreLink.Maps.Maps;
using CoreLink.Maps.Models;

namespace CoreLink.Maps;

/// <summary>
/// Предоставляет конфигурацию CoreLink.
/// </summary>
public static class MapManager
{
    public static bool TryLoad(string mapId, out CoreLinkMap? map)
    {
        map = mapId switch
        {
            "test" => TestMap.Create(),
            _ => null
        };

        return map is not null;
    }
}