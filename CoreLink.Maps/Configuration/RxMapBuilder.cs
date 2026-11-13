using CoreLink.Contracts.Maps;


namespace CoreLink.Maps.Configuration;

/// <summary>
/// Формирует managed-карту Rx.
///
/// Проверки конфигурации выполняются однократно.
/// В рабочем цикле Rx строкового поиска имён нет.
/// </summary>
internal static class RxMapBuilder
{
    public static bool TryBuild(
        PhysicalRegister[] registers,
        IReadOnlyCollection<int> supportedUnitSystemIds,
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

        if (supportedUnitSystemIds is null ||
            supportedUnitSystemIds.Count == 0)
        {
            error = "Supported unit systems are empty.";
            return false;
        }

        var supported = new HashSet<int>();

        foreach (int id in supportedUnitSystemIds)
        {
            if (id <= 0)
            {
                error = $"Invalid unit system ID: {id}.";
                return false;
            }

            if (!supported.Add(id))
            {
                error = $"Duplicate unit system ID: {id}.";
                return false;
            }
        }

        var values = new RxMapValue[registers.Length];
        var names = new HashSet<string>(StringComparer.Ordinal);

        int? unitSystemValueId = null;

        for (int i = 0; i < registers.Length; i++)
        {
            PhysicalRegister register = registers[i];

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
                unitSystemValueId = i;

            var units = new Dictionary<int, string>();

            IReadOnlyList<UnitEntry>? entries =
                register.Units.Entries;

            if (entries is not null)
            {
                foreach (UnitEntry entry in entries)
                {
                    if (!supported.Contains(entry.SystemId))
                    {
                        error =
                            $"Unregistered unit system " +
                            $"{entry.SystemId}: {register.Name}.";
                        return false;
                    }

                    if (string.IsNullOrWhiteSpace(entry.Symbol))
                    {
                        error =
                            $"Empty unit symbol: " +
                            $"{register.Name}, system " +
                            $"{entry.SystemId}.";
                        return false;
                    }

                    if (!units.TryAdd(
                            entry.SystemId,
                            entry.Symbol))
                    {
                        error =
                            $"Duplicate unit system " +
                            $"{entry.SystemId}: {register.Name}.";
                        return false;
                    }
                }
            }

            values[i] = new RxMapValue(
                register.Name,
                new System.Collections.ObjectModel
                    .ReadOnlyDictionary<int, string>(units));
        }

        if (unitSystemValueId is null &&
            !supported.Contains(1))
        {
            error =
                "Default unit system 1 is not registered.";
            return false;
        }

        result = new RxMapConfiguration
        {
            Values = values,
            SupportedUnitSystemIds =
                supported.OrderBy(id => id).ToArray(),
            UnitSystemValueId = unitSystemValueId,
            RxIntervalMs = rxIntervalMs
        };

        return true;
    }
}
