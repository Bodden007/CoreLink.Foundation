namespace CoreLink.Maps.Definitions;

/// <summary>
/// Американская система единиц измерения.
/// Значение None означает отсутствие единицы.
/// </summary>
public enum UA : byte
{
    None = 0,
    PSI = 1
}

/// <summary>
/// Метрическая система единиц измерения.
/// Значение None означает отсутствие единицы.
/// </summary>
public enum EU : byte
{
    None = 0,
    Bar = 1,
    MPa = 2,
    Atm = 3
}

/// <summary>
/// Единицы измерения одного технологического параметра.
///
/// American соответствует Measurme = 1.
/// Metric соответствует Measurme = 2.
///
/// Отсутствующая структура или значение enum 0
/// означает, что единица не задана.
/// Числовые преобразования не выполняются.
/// </summary>
public readonly record struct Units(
    UA American,
    EU Metric);
