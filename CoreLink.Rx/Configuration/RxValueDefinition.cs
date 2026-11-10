namespace CoreLink.Rx.Configuration;

/// <summary>
/// Описание одного значения для реактивного слоя.
///
/// ValueId определяется позицией описания в Map
/// и совпадает с индексом значения в snapshot.
///
/// Содержит имя параметра и обозначения единиц
/// измерения для поддерживаемых систем PLC.
///
/// Числовые преобразования здесь не выполняются.
/// </summary>
public sealed class RxValueDefinition
{
    public string Name { get; }

    public IReadOnlyDictionary<int, string> Units { get; }

    public RxValueDefinition(
        string name,
        IReadOnlyDictionary<int, string> units)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(units);

        Name = name;

        Units = new System.Collections.ObjectModel
            .ReadOnlyDictionary<int, string>(
                new Dictionary<int, string>(units));
    }
}
