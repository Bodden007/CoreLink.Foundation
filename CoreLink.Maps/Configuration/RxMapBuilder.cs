
using CoreLink.Contracts.Maps;
using CoreLink.Maps.Definitions;

namespace CoreLink.Maps.Configuration;

/// <summary>
/// Формирует managed-карту Rx из физических определений.
///
/// Поиск Measurme и преобразование единиц выполняются
/// только при подготовке конфигурации.
/// В рабочем цикле Rx строкового поиска нет.
///
/// Контракты RxMapConfiguration и RxMapValue
/// принадлежат CoreLink.Contracts.
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
