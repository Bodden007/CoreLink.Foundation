
namespace CoreLink.Contracts.Maps;

/// <summary>
/// Единый контракт загрузки конфигураций CoreLink.
///
/// Предоставляет бинарную карту для Core
/// и managed-карту для Rx.
///
/// Обе карты формируются из одного RegisterMap.
/// Не выполняет polling и не управляет lifecycle.
/// </summary>
public interface IMapProvider
{
    /// <summary>
    /// Загружает бинарную карту Core v2.
    /// </summary>
    bool TryLoad(
        string mapId,
        out CoreMap? coreMap);

    /// <summary>
    /// Загружает managed-карту Rx.
    ///
    /// Порядок значений соответствует бинарной карте.
    /// ValueId == index.
    ///
    /// При ошибке возвращает false и описание причины.
    /// Исключения вызывающему компоненту не передаются.
    /// </summary>
    bool TryLoadRxMap(
        string mapId,
        out RxMapConfiguration? rxMap,
        out string? error);
}
