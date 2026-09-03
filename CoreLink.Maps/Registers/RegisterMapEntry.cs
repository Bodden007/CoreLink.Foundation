namespace CoreLink.Maps.Registers;

/// <summary>
/// Описывает одно физическое значение в карте PLC.
/// Имя существует только для читаемости карты и не участвует в runtime-парсинге.
/// </summary>
public readonly record struct RegisterMapEntry(
    string Name,
    ushort Address,
    RegisterType Type);