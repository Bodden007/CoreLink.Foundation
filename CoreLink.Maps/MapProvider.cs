
using CoreLink.Contracts.Maps;
using CoreLink.Maps.Configuration;
using CoreLink.Maps.Maps;

namespace CoreLink.Maps;

/// <summary>
/// Предоставляет конфигурации Core и Rx из единой
/// физической карты регистров.
///
/// Core получает бинарную карту v2.
/// Rx получает managed-описания значений.
///
/// TODO: Согласовать общий контракт загрузки обеих
/// проекций через IMapProvider перед интеграцией Client.
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
        {
            return false;
        }

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
            // TODO: Передать ошибку кодирования карты
            // в централизованный логгер.
            System.Diagnostics.Trace.TraceError(
                $"Core map encoding failed: {ex}");

            return false;
        }
    }

    /// <summary>
    /// Формирует managed-карту для реактивного слоя.
    ///
    /// Порядок значений соответствует RegisterMap.
    /// ValueId равен индексу элемента.
    ///
    /// Не создаёт Rx и не управляет его lifecycle.
    /// </summary>
    public bool TryLoadRxMap(
        string mapId,
        out RxMapConfiguration? rxMap,
        out string? error)
    {
        return RxMapBuilder.TryBuild(
            RegisterMap.Registers,
            500,
            out rxMap,
            out error);
    }
}
