namespace CoreLink.Transport.Modbus.Results;

/// <summary>
/// Результат операции записи Modbus.
///
/// Client получает однозначный статус выполнения команды
/// и не обязан анализировать внутренние TCP/NModbus исключения.
/// </summary>
public sealed class ModbusWriteResult
{
    public required ModbusTransportStatus Status { get; init; }

    /// <summary>
    /// Успешно ли завершилась операция записи.
    /// </summary>
    public bool Ok =>
        Status == ModbusTransportStatus.Ok;
}