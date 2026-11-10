
using CoreLink.Contracts.Registers;
using CoreLink.Maps.Definitions;

namespace CoreLink.Maps.Maps;

/// <summary>
/// Физическая карта конкретной сборки.
///
/// Порядок записей определяет ValueId.
/// Адреса регистров не являются ValueId.
///
/// SupportedUnitSystemIds задаёт известные системы.
/// 0 зарезервирован для отсутствия системы.
///
/// Единицы указываются через UnitEntry(SystemId, Symbol).
/// Отсутствие записи означает пустое обозначение.
///
/// Регистр с именем "Measurme" выбирает систему.
/// Если регистра нет, применяется система 1.
///
/// TODO: Уточнить реальные единицы параметров
/// по документации PLC перед эксплуатацией.
/// </summary>
internal static class RegisterMap
{
    public static readonly int[] SupportedUnitSystemIds =
    [
        1, // UA
        2  // EU
    ];

    public static readonly PhysicalRegister[] Registers =
    [
        new("Status", 1, RegisterType.Word),

        new("Cutoff", 3, RegisterType.Word),

        new("Speed", 5, RegisterType.Float),

        new(
            "Pressure_1",
            7,
            RegisterType.Float,
            new Units(
                new UnitEntry(1, UnitSymbols.Psi),
                new UnitEntry(2, UnitSymbols.Bar))),

        new(
            "Pressure_2",
            9,
            RegisterType.Float,
            new Units(
                new UnitEntry(1, UnitSymbols.Psi),
                new UnitEntry(2, UnitSymbols.MPa))),

        new("Pressure_3", 11, RegisterType.Float),

        new("Pressure_4", 13, RegisterType.Float),

        new("Pressure_5", 15, RegisterType.Float),

        // Выбор системы единиц PLC.
        // 0: без единицы.
        // Зарегистрированный ID: штатная система.
        // Неизвестный ID: ошибка конфигурации.
        new("Measurme", 0, RegisterType.Word)
    ];
}
