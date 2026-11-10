using CoreLink.Contracts.Registers;
using CoreLink.Maps.Definitions;

namespace CoreLink.Maps.Maps;

/// <summary>
/// Физическая карта регистров конкретной сборки.
///
/// Порядок записей определяет ValueId.
/// Адреса регистров не являются ValueId.
///
/// Единицы измерения указываются в порядке UA, EU.
/// Если Units отсутствует, обозначение единицы пустое.
///
/// Для выбора системы единиц PLC используется регистр
/// со строго заданным именем "Measurme".
/// Если регистр отсутствует, применяется UA (1).
///
/// TODO: Уточнить реальные единицы каждого параметра
/// по карте PLC перед эксплуатационным применением.
/// </summary>
internal static class RegisterMap
{
    public static readonly PhysicalRegister[] Registers =
    [
        new("Status",      1, RegisterType.Word),
        new("Cutoff",      3, RegisterType.Word),
        new("Speed",       5, RegisterType.Float),

        new("Pressure_1",  7, RegisterType.Float,
            new Units(UA.PSI, EU.Bar)),

        new("Pressure_2",  9, RegisterType.Float,
            new Units(UA.PSI, EU.MPa)),

        new("Pressure_3", 11, RegisterType.Float),
        new("Pressure_4", 13, RegisterType.Float),
        new("Pressure_5", 15, RegisterType.Float),
    ];
}
