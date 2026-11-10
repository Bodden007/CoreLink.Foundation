
using CoreLink.Contracts.Maps;
using CoreLink.Maps.Configuration;
using CoreLink.Maps.Maps;

namespace CoreLink.Maps;

/// <summary>
/// Предоставляет Core и Rx проекции одной карты.
///
/// Core получает бинарную карту v2.
/// Rx получает managed-описания значений
/// и зарегистрированных систем единиц.
/// </summary>
public sealed class MapProvider : IMapProvider
{
    public bool TryLoad(
        string mapId,
        out CoreMap? coreMap)
    {
        coreMap = null;

        var registers = RegisterMap.Registers;

        if (registers.Length == 0)
            return false;

        var map = new CoreLinkMap
        {
            IpAddress = ConnectionMap.IpAddress,
            Port = ConnectionMap.Port,
            UnitId = ConnectionMap.UnitId,

            PollIntervalMs = ConnectionMap.PollIntervalMs,

            ConnectTimeoutMs = ConnectionMap.ConnectTimeoutMs,
            RequestTimeoutMs = ConnectionMap.RequestTimeoutMs,
            ReconnectDelayMs = ConnectionMap.ReconnectDelayMs,

            RegisterOrder = ConnectionMap.Order,
            Registers = registers
        };

        try
        {
            coreMap = CoreMapEncoder.Encode(map);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError(
                $"Core map encoding failed: {ex}");

            return false;
        }
    }

    /// <summary>
    /// Формирует Rx-карту.
    /// ValueId соответствует индексу RegisterMap.
    /// </summary>
    public bool TryLoadRxMap(
        string mapId,
        out RxMapConfiguration? rxMap,
        out string? error)
    {
        return RxMapBuilder.TryBuild(
            RegisterMap.Registers,
            RegisterMap.SupportedUnitSystemIds,
            ConnectionMap.RxIntervalMs,
            out rxMap,
            out error);
    }
}
