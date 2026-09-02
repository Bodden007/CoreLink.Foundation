namespace CoreLink.Transport.Modbus.Results;

/// <summary>
/// Результат операции чтения Modbus.
///
/// При Status == Ok содержит полученные регистры.
/// При ошибке Data остаётся null:
/// пустой массив не используется как признак отказа.
/// </summary>
public sealed class ModbusReadResult
{
    public required ModbusTransportStatus Status { get; init; }

    public ushort[]? Data { get; init; }

    /// <summary>
    /// Успешно ли завершилась операция чтения.
    /// </summary>
    public bool Ok =>
        Status == ModbusTransportStatus.Ok;
}