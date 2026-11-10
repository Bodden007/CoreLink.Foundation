namespace CoreLink.Maps.Definitions;

/// <summary>
/// Преобразует типизированные единицы измерения
/// в технические обозначения для CoreLink.Rx.
///
/// Не выполняет локализацию и числовые преобразования.
/// Значение enum 0 означает отсутствие единицы.
///
/// Неизвестные значения не подменяются пустой строкой.
/// </summary>
internal static class UnitSymbols
{
    public static bool TryGet(
        UA unit,
        out string symbol)
    {
        switch (unit)
        {
            case UA.None:
                symbol = string.Empty;
                return true;

            case UA.PSI:
                symbol = "psi";
                return true;

            default:
                symbol = string.Empty;
                return false;
        }
    }

    public static bool TryGet(
        EU unit,
        out string symbol)
    {
        switch (unit)
        {
            case EU.None:
                symbol = string.Empty;
                return true;

            case EU.Bar:
                symbol = "bar";
                return true;

            case EU.MPa:
                symbol = "MPa";
                return true;

            case EU.Atm:
                symbol = "atm";
                return true;

            default:
                symbol = string.Empty;
                return false;
        }
    }
}
