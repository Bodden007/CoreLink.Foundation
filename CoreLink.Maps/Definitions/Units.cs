
namespace CoreLink.Maps.Definitions;

/// <summary>
/// Единица одного параметра в заданной системе PLC.
/// </summary>
public readonly record struct UnitEntry(
    int SystemId,
    string Symbol);

/// <summary>
/// Набор единиц одного технологического параметра.
///
/// Отсутствие записи для зарегистрированной системы
/// означает отсутствие обозначения, а не ошибку.
///
/// Числовых преобразований не выполняет.
/// </summary>
public readonly struct Units
{
    public IReadOnlyList<UnitEntry> Entries { get; }

    public Units(params UnitEntry[] entries)
    {
        Entries = Array.AsReadOnly(
            (UnitEntry[])(
                entries ??
                throw new ArgumentNullException(nameof(entries))
            ).Clone());
    }
}
