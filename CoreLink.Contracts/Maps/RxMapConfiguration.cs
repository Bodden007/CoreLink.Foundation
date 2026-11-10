
namespace CoreLink.Contracts.Maps;

/// <summary>
/// Значение Rx с обозначениями единиц
/// по идентификаторам систем PLC.
/// ValueId соответствует индексу в Values.
/// </summary>
public readonly record struct RxMapValue(
    string Name,
    IReadOnlyDictionary<int, string> Units);

/// <summary>
/// Managed-конфигурация Rx.
/// Не содержит физических адресов Modbus.
/// </summary>
public sealed class RxMapConfiguration
{
    public required RxMapValue[] Values { get; init; }

    /// <summary>
    /// Зарегистрированные системы единиц.
    /// Только положительные идентификаторы.
    /// ID 0 зарезервирован для отсутствия системы.
    /// </summary>
    public required int[] SupportedUnitSystemIds { get; init; }

    /// <summary>
    /// Индекс Measurme в snapshot.
    /// Null: регистра нет, применяется система 1.
    /// </summary>
    public int? UnitSystemValueId { get; init; }

    public int RxIntervalMs { get; init; }
}
