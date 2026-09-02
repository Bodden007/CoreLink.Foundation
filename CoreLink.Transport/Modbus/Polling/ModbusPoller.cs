using CoreLink.Transport.Modbus.Configuration;
using CoreLink.Transport.Modbus.Dispatching;

namespace CoreLink.Transport.Modbus.Polling;

/// <summary>
/// Выполняет постоянное последовательное чтение штатного
/// блока Modbus Input Registers.
///
/// Poller не обращается к ModbusConnectionManager напрямую.
/// Все операции передаются диспетчеру, который определяет
/// порядок выполнения относительно write и single read.
/// </summary>
internal sealed class ModbusPoller : IAsyncDisposable
{
    private readonly ModbusRequestDispatcher _dispatcher;
    private readonly ModbusConnectionConfig _config;

    private CancellationTokenSource? _pollingCts;
    private Task? _pollingTask;

    /// <summary>
    /// Вызывается после успешного чтения непустого
    /// блока Input Registers.
    ///
    /// Полученный массив сохраняет порядок регистров,
    /// возвращённый Modbus-устройством.
    /// </summary>
    public event Action<ushort[]>? RegistersReceived;

    /// <summary>
    /// Вызывается при ошибке очередного polling-запроса.
    ///
    /// Ошибка одного запроса не останавливает постоянный polling.
    /// После штатной задержки будет выполнена следующая попытка.
    /// </summary>
    public event Action<Exception>? PollingError;

    /// <summary>
    /// Показывает, запущен ли основной цикл polling.
    /// </summary>
    public bool IsRunning =>
        _pollingTask is not null;

    /// <summary>
    /// Создаёт poller для существующего диспетчера
    /// Modbus-запросов.
    ///
    /// Конструктор не запускает polling автоматически.
    /// </summary>
    public ModbusPoller(
        ModbusRequestDispatcher dispatcher,
        ModbusConnectionConfig config)
    {
        _dispatcher = dispatcher;
        _config = config;
    }

    /// <summary>
    /// Запускает постоянный последовательный polling.
    ///
    /// Первое чтение выполняется сразу.
    /// Повторный вызов Start при работающем polling
    /// ничего не делает.
    /// </summary>
    public void Start()
    {
        if (IsRunning)
            return;

        _pollingCts =
            new CancellationTokenSource();

        _pollingTask =
            PollAsync(
                _pollingCts.Token);
    }

    /// <summary>
    /// Основной цикл polling.
    ///
    /// Каждый цикл создаёт ровно один polling-запрос
    /// и ожидает его завершения.
    ///
    /// Поэтому очередь polling не может накапливаться,
    /// даже если PLC отвечает медленнее заданного интервала.
    ///
    /// Ошибка одного запроса публикуется через PollingError,
    /// но не завершает основной цикл.
    /// </summary>
    private async Task PollAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    ushort[] registers =
                        await _dispatcher.ReadPollingAsync(
                            _config.SlaveId,
                            _config.InputStartAddress,
                            _config.InputRegisterCount);

                    if (registers.Length > 0)
                    {
                        RegistersReceived?.Invoke(
                            registers);
                    }
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    // Потеря связи или ошибка одного Modbus-запроса
                    // не должна останавливать постоянный polling.
                    PollingError?.Invoke(
                        exception);
                }

                await Task.Delay(
                    _config.PollIntervalMs,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            // Штатное завершение polling.
        }
    }

    /// <summary>
    /// Останавливает создание новых polling-запросов.
    ///
    /// Уже переданный диспетчеру Modbus-запрос не прерывается:
    /// он завершается по обычным правилам transport слоя.
    /// </summary>
    /// <summary>
    /// Асинхронно останавливает создание новых polling-запросов.
    ///
    /// Если polling уже ожидает переданный dispatcher запрос,
    /// метод дожидается завершения текущего polling-цикла.
    /// Сам вызывающий поток при этом не блокируется.
    /// </summary>
    public async Task StopAsync()
    {
        if (_pollingCts is null)
            return;

        CancellationTokenSource pollingCts =
            _pollingCts;

        Task? pollingTask =
            _pollingTask;

        pollingCts.Cancel();

        if (pollingTask is not null)
        {
            try
            {
                await pollingTask;
            }
            catch (OperationCanceledException)
            {
                // Штатное завершение polling.
            }
        }

        pollingCts.Dispose();

        _pollingCts = null;
        _pollingTask = null;
    }

    /// <summary>
    /// Останавливает polling.
    ///
    /// Диспетчер здесь не уничтожается, поскольку его lifetime
    /// принадлежит владельцу всей Modbus-сессии.
    /// </summary>
    /// <summary>
    /// Асинхронно завершает polling.
    ///
    /// Dispatcher здесь не уничтожается:
    /// его lifetime принадлежит ModbusTransportSession.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}