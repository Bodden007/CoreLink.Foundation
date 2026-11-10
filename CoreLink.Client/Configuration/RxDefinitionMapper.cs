
using CoreLink.Contracts.Maps;
using CoreLink.Rx.Configuration;

namespace CoreLink.Client.Configuration;

/// <summary>
/// Преобразует managed-карту в определения Rx.
///
/// Порядок значений сохраняется: ValueId == index.
/// Сопоставление выполняется один раз перед запуском.
///
/// Не выполняет локализацию или пересчёт значений.
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

        if (map.SupportedUnitSystemIds is null ||
            map.SupportedUnitSystemIds.Length == 0)
        {
            error = "Supported unit systems are empty.";
            return false;
        }

        var supported = new HashSet<int>();

        foreach (int id in map.SupportedUnitSystemIds)
        {
            if (id <= 0 || !supported.Add(id))
            {
                error = $"Invalid unit system ID: {id}.";
                return false;
            }
        }

        if (map.UnitSystemValueId is null &&
            !supported.Contains(1))
        {
            error = "Default unit system 1 is not registered.";
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

            if (value.Units is null)
            {
                error = $"Units are null: {value.Name}.";
                return false;
            }

            var units = new Dictionary<int, string>(
                value.Units.Count);

            foreach (var pair in value.Units)
            {
                if (!supported.Contains(pair.Key))
                {
                    error =
                        $"Unregistered unit system " +
                        $"{pair.Key}: {value.Name}.";
                    return false;
                }

                if (string.IsNullOrWhiteSpace(pair.Value))
                {
                    error =
                        $"Invalid unit symbol: " +
                        $"{value.Name}, system {pair.Key}.";
                    return false;
                }

                units.Add(pair.Key, pair.Value);
            }

            result[i] = new RxValueDefinition(
                value.Name,
                units);
        }

        definitions = result;
        return true;
    }
}
