namespace CoreLink.Maps.Registers;

/// <summary>
/// Порядок байтов и слов при преобразовании многорегистрового значения.
/// </summary>
public enum RegisterOrder
{
    ABCD,
    BADC,
    CDAB,
    DCBA
}