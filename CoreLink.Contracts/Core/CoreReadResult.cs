namespace CoreLink.Contracts.Core;

/// <summary>
/// Итоговый статус прямого чтения регистров PLC.
/// </summary>
public enum CoreReadStatus
{
    Ok = 0,
    Disconnected = 1,
    Timeout = 2,
    Failed = 3,
    BadConfig = 4,
    Stopped = 5
}

/// <summary>
/// Результат прямого чтения по ValueId.
///
/// Значения доступны только при успешном завершении
/// всей операции. Порядок значений соответствует
/// порядку запрошенных ValueId.
/// </summary>
public sealed class CoreReadResult
{
    public required CoreReadStatus Status { get; init; }

    public float[]? Values { get; init; }

    public bool Ok => Status == CoreReadStatus.Ok;
}
