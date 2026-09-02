namespace CoreLink.Transport.Modbus.Configuration;

/// <summary>
/// Содержит неизменяемые параметры одной Modbus TCP-сессии.
///
/// Конфигурация создаётся до запуска транспорта и используется
/// менеджером соединения на протяжении всего времени жизни сессии.
///
/// Класс не владеет сетевыми ресурсами, синхронизацией или lifecycle
/// соединения и содержит только параметры конфигурации.
/// </summary>
public sealed class ModbusConnectionConfig
{

    /// <summary>
    /// Интервал между последовательными циклами чтения Input Registers
    /// в миллисекундах.
    ///
    /// Следующий цикл polling начинается только после завершения
    /// предыдущего чтения, поэтому запросы не накапливаются в очереди.
    /// </summary>
    public required int PollIntervalMs { get; init; }

    /// <summary>
    /// Начальный адрес блока Input Registers,
    /// читаемого основным циклом polling.
    /// </summary>
    public required ushort InputStartAddress { get; init; }

    /// <summary>
    /// Количество Input Registers,
    /// читаемых одним Modbus-запросом polling.
    /// </summary>
    public required ushort InputRegisterCount { get; init; }

    /// <summary>
    /// IPv4-адрес или DNS-имя Modbus TCP-устройства.
    ///
    /// Значение используется только при установке TCP-соединения.
    /// </summary>
    public required string Host { get; init; }

    /// <summary>
    /// TCP-порт Modbus TCP-устройства.
    ///
    /// Стандартный порт Modbus TCP — 502, однако конкретная
    /// установка может использовать другой порт.
    /// </summary>
    public required int Port { get; init; }

    /// <summary>
    /// Modbus Unit Identifier, используемый при обращении к устройству.
    ///
    /// Значение передаётся в Modbus-запросах и не относится
    /// к управлению TCP-соединением.
    /// </summary>
    public required byte SlaveId { get; init; }

    /// <summary>
    /// Максимальное время ожидания установки TCP-соединения
    /// в миллисекундах.
    ///
    /// Ограничение не позволяет операции подключения зависнуть
    /// на неопределённое время при недоступном ПЛК или сети.
    /// </summary>
    public required int ConnectTimeoutMs { get; init; }

    /// <summary>
    /// Максимальное время выполнения одного Modbus-запроса
    /// в миллисекундах.
    ///
    /// Таймаут запроса отделён от таймаута подключения, потому что
    /// уже установленное TCP-соединение также может перестать
    /// корректно отвечать.
    /// </summary>
    public required int RequestTimeoutMs { get; init; }

    /// <summary>
    /// Минимальный интервал между повторными попытками подключения
    /// в миллисекундах.
    ///
    /// Ограничение предотвращает непрерывный reconnect-loop
    /// при физически недоступном устройстве или разрыве сети.
    /// </summary>
    public required int ReconnectDelayMs { get; init; }
}