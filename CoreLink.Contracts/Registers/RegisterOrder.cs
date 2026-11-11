namespace CoreLink.Contracts.Registers;

// TODO: Унифицировать декодирование Word/Float для polling
// и прямого ReadAsync. Исключить дублирование логики
// RegisterOrder и SwapBytes. Решение требует согласования.
public enum RegisterOrder : byte
{
    ABCD = 0,
    BADC = 1,
    CDAB = 2,
    DCBA = 3
}