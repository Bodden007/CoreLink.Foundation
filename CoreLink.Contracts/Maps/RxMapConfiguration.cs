
namespace CoreLink.Contracts.Maps;

/// <summary>
/// Описание технологического значения для Rx.
///
/// ValueId определяется позицией элемента
/// в массиве Values и совпадает с индексом snapshot.
///
/// Обозначения единиц не зависят от языка UI.
/// Числовые преобразования не выполняются.
/// </summary>
public readonly record struct RxMapValue(
    string Name,
    string AmericanUnit,
    string MetricUnit);

/// <summary>
/// Managed-конфигурация реактивного слоя.
///
/// Формируется CoreLink.Maps и передаётся через
/// CoreLink.Client в CoreLink.Rx.
///
/// Не содержит физических Modbus-адресов,
/// параметров TCP и бинарного представления карты.
/// </summary>
public sealed class RxMapConfiguration
{
    /// <summary>
    /// Описания значений в порядке RegisterMap.
    /// ValueId == index.
    /// </summary>
    public required RxMapValue[] Values { get; init; }

    /// <summary>
    /// Индекс регистра Measurme в snapshot.
    ///
    /// Null означает отсутствие регистра в карте.
    /// В этом случае используется система UA (1).
    ///
    /// Если регистр присутствует, но значение
    /// некорректно, система по умолчанию не применяется.
    /// </summary>
    public int? UnitSystemValueId { get; init; }

    /// <summary>
    /// Задержка между последовательными циклами Rx
    /// в миллисекундах.
    ///
    /// Не зависит от PollIntervalMs транспорта.
    /// </summary>
    public int RxIntervalMs { get; init; }
}
