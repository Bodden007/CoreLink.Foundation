
using CoreLink.Contracts.Registers;

namespace CoreLink.Contracts.Core;

/// <summary>
/// Результат операции записи PLC.
///
/// Не является состоянием соединения
/// или жизненного цикла Core.
/// </summary>
public enum CoreWriteStatus
{
    Ok = 0,
    Disconnected = 1,
    Timeout = 2,
    Failed = 3,
    BadConfig = 4,
    Stopped = 5
}

/// <summary>
/// Одно типизированное значение Batch Write.
///
/// Word хранится как ushort.
/// Float32 хранится как float.
///
/// Физическое представление Modbus формирует Core.
/// </summary>
public readonly record struct CoreWriteValue
{
    public RegisterType Type { get; }
    public ushort WordValue { get; }
    public float FloatValue { get; }

    private CoreWriteValue(
        RegisterType type,
        ushort wordValue,
        float floatValue)
    {
        Type = type;
        WordValue = wordValue;
        FloatValue = floatValue;
    }

    public static CoreWriteValue Word(ushort value)
    {
        return new CoreWriteValue(
            RegisterType.Word,
            value,
            default);
    }

    public static CoreWriteValue Float(float value)
    {
        return new CoreWriteValue(
            RegisterType.Float,
            default,
            value);
    }
}
