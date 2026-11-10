using CoreLink.Contracts.Maps;
using CoreLink.Rx.Configuration;

namespace CoreLink.Client.Configuration;

/// <summary>
/// Преобразует managed-карту в определения CoreLink.Rx.
///
/// Порядок значений сохраняется: ValueId == index.
/// Сопоставление выполняется один раз при подготовке
/// конфигурации, не в рабочем цикле Rx.
///
/// Не выполняет локализацию и пересчёт значений.
/// </summary>
internal static class RxDefinitionMapper
{
    public static bool TryCreate(
        RxMapConfiguration? map,
        out RxValueDefinition[] definitions,
        out string? error)
    {
        definitions = [];
        error = null;

        if (map is null ||
            map.Values is null ||
            map.Values.Length == 0)
        {
            error = "Rx map is empty.";
            return false;
        }

        if (map.RxIntervalMs <= 0)
        {
            error = "Invalid Rx interval.";
            return false;
        }

        if (map.UnitSystemValueId is int valueId &&
            (uint)valueId >= (uint)map.Values.Length)
        {
            error = "Invalid Measurme ValueId.";
            return false;
        }

        var result =
            new RxValueDefinition[map.Values.Length];

        var names =
            new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < map.Values.Length; i++)
        {
            var value = map.Values[i];

            if (string.IsNullOrWhiteSpace(value.Name))
            {
                error = $"Empty Rx name at index {i}.";
                return false;
            }

            if (!names.Add(value.Name))
            {
                error = $"Duplicate Rx name: {value.Name}.";
                return false;
            }

            if (value.AmericanUnit is null ||
                value.MetricUnit is null)
            {
                error = $"Invalid units: {value.Name}.";
                return false;
            }

            var units = new Dictionary<int, string>(2)
            {
                [1] = value.AmericanUnit,
                [2] = value.MetricUnit
            };

            result[i] = new RxValueDefinition(
                value.Name,
                units);
        }

        definitions = result;
        return true;
    }
}
