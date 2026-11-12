
namespace CoreLink.Contracts.Core;

/// <summary>
/// Функция Modbus для адресного чтения.
/// Выбирается независимо от штатного polling.
/// </summary>
public enum ModbusReadFunction : byte
{
    HoldingRegisters = 3,
    InputRegisters = 4
}

/// <summary>
/// Результат одиночного чтения внутри CoreLink.
///
/// Status определяет успешность операции.
/// Value используется Client только при Ok.
/// При ошибке Value не должен передаваться
/// потребителю как новое значение.
/// </summary>
public readonly record struct CoreSingleReadResult(
    CoreReadStatus Status,
    float Value);
