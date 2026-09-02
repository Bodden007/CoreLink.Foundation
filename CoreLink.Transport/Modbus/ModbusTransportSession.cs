using CoreLink.Transport.Modbus.Configuration;
using CoreLink.Transport.Modbus.Connection;
using CoreLink.Transport.Modbus.Dispatching;
using CoreLink.Transport.Modbus.Polling;
using CoreLink.Transport.Modbus.Results;

namespace CoreLink.Transport.Modbus;

/// <summary>
/// Представляет одну законченную Modbus TCP-сессию CoreLink.
///
/// Сессия владеет TCP-соединением, диспетчером запросов
/// и постоянным циклом polling.
///
/// Штатные transport-ошибки возвращаются через
/// ModbusTransportStatus и не требуют анализа исключений.
/// </summary>
public sealed class ModbusTransportSession : IAsyncDisposable
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
    /// Публикует неуспешный статус штатного polling.
    ///
    /// Ошибка одного запроса не останавливает transport-сессию.
    /// Следующая попытка снова проходит через Transport,
    /// который при необходимости выполняет reconnect.
    /// </summary>
    public event Action<ModbusTransportStatus>? TransportStatusChanged;

    /// <summary>
    /// Показывает, запущен ли постоянный polling.
    /// </summary>
    public bool IsRunning =>
        _poller.IsRunning;

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
        ArgumentNullException.ThrowIfNull(config);

        _config = config;

        _connectionManager =
            new ModbusConnectionManager(
                config);

        _dispatcher =
            new ModbusRequestDispatcher(
                _connectionManager);

        _poller =
            new ModbusPoller(
                _dispatcher,
                config);

        _poller.RegistersReceived +=
            OnRegistersReceived;

        _poller.PollingStatusChanged +=
            OnPollingStatusChanged;
    }

    /// <summary>
    /// Запускает dispatcher и постоянный polling.
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
    /// Асинхронно останавливает transport-сессию.
    ///
    /// Сначала прекращается polling, затем завершается
    /// единственный dispatcher worker.
    /// </summary>
    public async Task StopAsync()
    {
        if (_disposed)
            return;

        await _poller.StopAsync();
        await _dispatcher.StopAsync();
    }

    /// <summary>
    /// Ставит запись одного Holding Register
    /// в высокоприоритетную очередь.
    ///
    /// Результат содержит детерминированный transport status.
    /// </summary>
    public Task<ModbusWriteResult> WriteSingleRegisterAsync(
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
    ///
    /// Результат содержит детерминированный transport status.
    /// </summary>
    public Task<ModbusWriteResult> WriteMultipleRegistersAsync(
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
    /// Single read имеет самый низкий приоритет.
    /// При ошибке Data == null, а причина содержится в Status.
    /// </summary>
    public Task<ModbusReadResult> ReadSingleAsync(
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
    /// Передаёт transport-status polling владельцу сессии.
    /// </summary>
    private void OnPollingStatusChanged(
        ModbusTransportStatus status)
    {
        TransportStatusChanged?.Invoke(
            status);
    }

    /// <summary>
    /// Проверяет lifecycle сессии перед постановкой новой операции.
    ///
    /// Ошибка использования API не является сетевым состоянием
    /// и поэтому не преобразуется в ModbusTransportStatus.
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
    /// После окончательной фиксации transport API убрать дублирование
    /// slaveId во внутренних вызовах.
    /// </summary>
    private byte GetSlaveId()
    {
        return _config.SlaveId;
    }

    /// <summary>
    /// Асинхронно завершает Modbus TCP-сессию сверху вниз.
    ///
    /// Сначала прекращается производство запросов,
    /// затем dispatcher, после чего уничтожается TCP-сессия.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _poller.RegistersReceived -=
            OnRegistersReceived;

        _poller.PollingStatusChanged -=
            OnPollingStatusChanged;

        await _poller.DisposeAsync();
        await _dispatcher.DisposeAsync();

        _connectionManager.Dispose();

        _disposed = true;
    }
}
