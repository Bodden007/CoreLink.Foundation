using CoreLink.Contracts.Maps;
using CoreLink.Maps.Configuration;
using CoreLink.Maps.Maps;

namespace CoreLink.Maps;

public sealed class MapProvider : IMapProvider
{
    public bool TryLoad(
        string mapId,
        out CoreMap? coreMap)
    {
        coreMap = null;

        var registers =
            RegisterMap.Registers;

        if (registers.Length == 0)
        {
            return false;
        }

        var map = new CoreLinkMap
        {
            IpAddress = ConnectionMap.IpAddress,
            Port = ConnectionMap.Port,
            UnitId = ConnectionMap.UnitId,
            PollIntervalMs = ConnectionMap.PollIntervalMs,
            RegisterOrder = ConnectionMap.Order,
            Registers = registers
        };

        coreMap =
            CoreMapEncoder.Encode(map);

        return true;
    }
}