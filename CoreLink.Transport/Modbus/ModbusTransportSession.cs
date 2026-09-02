    using CoreLink.Transport.Modbus.Configuration;
using CoreLink.Transport.Modbus.Connection;
using CoreLink.Transport.Modbus.Dispatching;
using CoreLink.Transport.Modbus.Polling;

namespace CoreLink.Transport.Modbus;

/// <summary>
/// Представляет одну законченную Modbus TCP-сессию CoreLink.
///
/// Сессия владеет TCP-соединением, диспетчером запросов
/// и постоянным циклом polling.
///
/// Наружу не раскрываются внутренние transport-компоненты:
/// все операции проходят через единый диспетчер приоритетов.
/// </summary>
public sealed class ModbusTransportSession : IDisposable
{
    private readonly ModbusConnectionManager _connectionManager;
    private readonly ModbusRequestDispatcher _dispatcher;
    private readonly ModbusPoller _poller;
    private readonly ModbusConnectionConfig _config;

    private bool _disposed;

    /// <summary>
    /// Вызывается после успешного штатного polling-чтения.
    ///
    /// Массив содержит Input Registers в порядке,
    /// полученном от Modbus-устройства.
    /// </summary>
    public event Action<ushort[]>? RegistersReceived;

    /// <summary>
    /// Показывает, запущен ли постоянный polling.
    /// </summary>
    public bool IsRunning => _poller.IsRunning;

    /// <summary>
    /// Создаёт одну Modbus TCP-сессию.
    ///
    /// Конструктор создаёт внутренние transport-компоненты,
    /// но не устанавливает TCP-соединение.
    /// Подключение выполняется лениво при первом запросе.
    /// </summary>
    public ModbusTransportSession(
        ModbusConnectionConfig config)
    {
        _config = config;
        ArgumentNullException.ThrowIfNull(config);

        _connectionManager =
            new ModbusConnectionManager(config);

        _dispatcher =
            new ModbusRequestDispatcher(
                _connectionManager);

        _poller =
            new ModbusPoller(
                _dispatcher,
                config);

        _poller.RegistersReceived +=
            OnRegistersReceived;
    }

    /// <summary>
    /// Запускает диспетчер запросов и постоянный polling.
    ///
    /// Dispatcher запускается первым, чтобы первый polling-запрос
    /// сразу имел работающего исполнителя.
    /// </summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(
            _disposed,
            this);

        _dispatcher.Start();
        _poller.Start();
    }

    /// <summary>
    /// Останавливает создание новых polling-запросов,
    /// затем завершает диспетчер.
    ///
    /// Такой порядок не позволяет poller поставить новый запрос
    /// после начала остановки transport-сессии.
    /// </summary>
    public void Stop()
    {
        if (_disposed)
            return;

        _poller.Stop();
        _dispatcher.Stop();
    }

    /// <summary>
    /// Ставит запись одного Holding Register
    /// в высокоприоритетную очередь.
    ///
    /// Write обслуживается раньше polling и single read
    /// при выборе следующей Modbus-операции.
    /// </summary>
    public Task WriteSingleRegisterAsync(
        ushort address,
        ushort value)
    {
        ThrowIfNotAvailable();

        return _dispatcher.WriteSingleRegisterAsync(
            GetSlaveId(),
            address,
            value);
    }

    /// <summary>
    /// Ставит запись блока Holding Registers
    /// в высокоприоритетную очередь.
    /// </summary>
    public Task WriteMultipleRegistersAsync(
        ushort startAddress,
        ushort[] values)
    {
        ThrowIfNotAvailable();

        return _dispatcher.WriteMultipleRegistersAsync(
            GetSlaveId(),
            startAddress,
            values);
    }

    /// <summary>
    /// Выполняет одиночное чтение Input Registers.
    ///
    /// Single read имеет самый низкий приоритет и выполняется
    /// только после ожидающих write и штатного polling.
    /// </summary>
    public Task<ushort[]> ReadSingleAsync(
        ushort startAddress,
        ushort count)
    {
        ThrowIfNotAvailable();

        return _dispatcher.ReadSingleAsync(
            GetSlaveId(),
            startAddress,
            count);
    }

    /// <summary>
    /// Передаёт штатные polling-данные владельцу сессии.
    /// </summary>
    private void OnRegistersReceived(
        ushort[] registers)
    {
        RegistersReceived?.Invoke(
            registers);
    }

    /// <summary>
    /// Проверяет, что сессия может принимать новые операции.
    /// </summary>
    private void ThrowIfNotAvailable()
    {
        ObjectDisposedException.ThrowIf(
            _disposed,
            this);

        if (!_dispatcher.IsRunning)
        {
            throw new InvalidOperationException(
                "Modbus transport session is not running.");
        }
    }

    /// <summary>
    /// Возвращает Unit Identifier текущей сессии.
    ///
    /// FIXME:
    /// SlaveId временно извлекается через отдельное поле сессии.
    /// После окончательной фиксации transport API убрать дублирование
    /// параметра slaveId во внутренних методах.
    /// </summary>
    private byte GetSlaveId()
    {
        return _config.SlaveId;
    }

    /// <summary>
    /// Освобождает всю Modbus TCP-сессию сверху вниз.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _poller.RegistersReceived -=
            OnRegistersReceived;

        _poller.Dispose();
        _dispatcher.Dispose();
        _connectionManager.Dispose();

        _disposed = true;
    }
}