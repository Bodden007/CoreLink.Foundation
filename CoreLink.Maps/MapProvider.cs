
using CoreLink.Contracts.Maps;
using CoreLink.Maps.Configuration;

namespace CoreLink.Maps;

/// <summary>
/// Преобразует публичную карту конфигурации
/// в представления Core и Rx.
///
/// Не управляет lifecycle, транспортом и polling.
/// </summary>
public static class MapProvider
{
    /// <summary>
    /// Формирует бинарную карту Core.
    ///
    /// Ошибки преобразования обрабатываются
    /// вызывающим CoreLink.Client.
    /// </summary>
    public static CoreMap BuildCore(CoreLinkMap map)
    {
        return CoreMapEncoder.Encode(map);
    }

    /// <summary>
    /// Формирует managed-карту Rx.
    /// Порядок регистров определяет ValueId.
    /// </summary>
    public static bool TryBuildRx(
        CoreLinkMap map,
        out RxMapConfiguration? rxMap,
        out string? error)
    {
        return RxMapBuilder.TryBuild(
            map.Registers,
            map.SupportedUnitSystemIds,
            map.RxIntervalMs,
            out rxMap,
            out error);
    }
}
