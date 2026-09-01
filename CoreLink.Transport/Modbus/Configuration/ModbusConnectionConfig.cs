namespace CoreLink.Transport.Modbus.Configuration;

/// <summary>
/// Содержит неизменяемые параметры Modbus TCP-соединения.
///
/// Конфигурация создаётся при запуске транспортной сессии
/// и не должна изменяться во время её работы.
/// </summary>
internal sealed class ModbusConnectionConfig
{
    /// <summary>
    /// IPv4-адрес или DNS-имя ПЛК.
    /// </summary>
    public required string Host { get; init; }

    /// <summary>
    /// TCP-порт Modbus TCP.
    /// Обычно используется порт 502.
    /// </summary>
    public required int Port { get; init; }

    /// <summary>
    /// Modbus Unit Identifier.
    /// </summary>
    public required byte SlaveId { get; init; }

    /// <summary>
    /// Максимальное время установки TCP-соединения.
    /// </summary>
    public required int ConnectTimeoutMs { get; init; }

    /// <summary>
    /// Максимальное время выполнения одного Modbus-запроса.
    /// </summary>
    public required int RequestTimeoutMs { get; init; }

    /// <summary>
    /// Минимальная пауза между попытками повторного подключения.
    ///
    /// Ограничение защищает сеть и ПЛК от непрерывного
    /// цикла reconnect при физически недоступном устройстве.
    /// </summary>
    public required int ReconnectDelayMs { get; init; }
}   