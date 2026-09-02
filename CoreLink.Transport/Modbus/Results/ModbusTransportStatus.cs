namespace CoreLink.Transport.Modbus.Results;

/// <summary>
/// Детерминированный результат transport-операции.
///
/// Штатные ошибки TCP/Modbus не должны требовать
/// анализа цепочки исключений на уровне Client или Rx.
/// </summary>
public enum ModbusTransportStatus
{
    Ok = 0,

    /// <summary>
    /// TCP-соединение отсутствует или было потеряно.
    /// </summary>
    Disconnected = 1,

    /// <summary>
    /// Не удалось установить TCP-соединение
    /// за ConnectTimeoutMs.
    /// </summary>
    ConnectTimeout = 2,

    /// <summary>
    /// Modbus-запрос не завершился
    /// за RequestTimeoutMs.
    /// </summary>
    RequestTimeout = 3,

    /// <summary>
    /// Ошибка выполнения Modbus-запроса
    /// при существующей transport-сессии.
    /// </summary>
    RequestFailed = 4,

    /// <summary>
    /// Операция была штатно отменена владельцем lifecycle.
    /// </summary>
    Cancelled = 5,

    /// <summary>
    /// Внутренняя ошибка самого Transport,
    /// не относящаяся к штатной потере связи с PLC.
    /// </summary>
    Faulted = 6
}