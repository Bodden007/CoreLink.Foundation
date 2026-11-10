
using CoreLink.Maps.Definitions;

namespace CoreLink.Maps.Configuration;

/// <summary>
/// Описание значения для managed-карты.
/// Позиция в массиве определяет ValueId.
/// </summary>
public readonly record struct RxMapValue(
    string Name,
    string AmericanUnit,
    string MetricUnit);

/// <summary>
/// Подготовленная конфигурация реактивного слоя.
/// Не содержит физических адресов регистров.
/// </summary>
public sealed class RxMapConfiguration
{
    public required RxMapValue[] Values { get; init; }

    /// <summary>
    /// Индекс регистра Measurme.
    /// Null означает отсутствие регистра и систему UA по умолчанию.
    /// </summary>
    public int? UnitSystemValueId { get; init; }

    public int RxIntervalMs { get; init; }
}

/// <summary>
/// Формирует managed-карту Rx из физических определений.
///
/// Поиск Measurme и преобразование единиц выполняются
/// только при подготовке конфигурации.
/// В рабочем цикле Rx строкового поиска нет.
/// </summary>
internal static class RxMapBuilder
{
    public static bool TryBuild(
        PhysicalRegister[] registers,
        int rxIntervalMs,
        out RxMapConfiguration? result,
        out string? error)
    {
        result = null;
        error = null;

        if (registers is null || registers.Length == 0)
        {
            error = "Register map is empty.";
            return false;
        }

        if (rxIntervalMs <= 0)
        {
            error = "Invalid Rx interval.";
            return false;
        }

        var values = new RxMapValue[registers.Length];
        var names = new HashSet<string>(StringComparer.Ordinal);

        int? unitSystemValueId = null;

        for (int i = 0; i < registers.Length; i++)
        {
            var register = registers[i];

            if (string.IsNullOrWhiteSpace(register.Name))
            {
                error = $"Empty register name at index {i}.";
                return false;
            }

            if (!names.Add(register.Name))
            {
                error = $"Duplicate register name: {register.Name}.";
                return false;
            }

            if (register.Name == "Measurme")
            {
                unitSystemValueId = i;
            }

            if (!UnitSymbols.TryGet(
                    register.Units.American,
                    out string americanUnit))
            {
                error = $"Invalid UA unit: {register.Name}.";
                return false;
            }

            if (!UnitSymbols.TryGet(
                    register.Units.Metric,
                    out string metricUnit))
            {
                error = $"Invalid EU unit: {register.Name}.";
                return false;
            }

            values[i] = new RxMapValue(
                register.Name,
                americanUnit,
                metricUnit);
        }

        result = new RxMapConfiguration
        {
            Values = values,
            UnitSystemValueId = unitSystemValueId,
            RxIntervalMs = rxIntervalMs
        };

        return true;
    }
}
