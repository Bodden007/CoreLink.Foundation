using CoreLink.Transport.Modbus.Configuration;
using CoreLink.Transport.Modbus.Dispatching;
using CoreLink.Transport.Modbus.Results;

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
    /// Публикует неуспешный результат polling-запроса.
    ///
    /// Ошибка одного запроса не останавливает постоянный polling.
    /// Следующий цикл снова передаст запрос Transport,
    /// который при необходимости самостоятельно выполнит reconnect.
    /// </summary>
    public event Action<ModbusTransportStatus>? PollingStatusChanged;

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
    /// Неуспешный запрос публикуется как ModbusTransportStatus
    /// и не завершает основной цикл.
    /// </summary>
    private async Task PollAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                ModbusReadResult result =
                    await _dispatcher.ReadPollingAsync(
                        _config.SlaveId,
                        _config.InputStartAddress,
                        _config.InputRegisterCount);

                if (result.Status ==
                    ModbusTransportStatus.Cancelled)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;
                }
                else if (result.Ok)
                {
                    ushort[]? registers =
                        result.Data;

                    if (registers is not null &&
                        registers.Length > 0)
                    {
                        RegistersReceived?.Invoke(
                            registers);
                    }
                }
                else
                {
                    // Потеря связи или ошибка Modbus-запроса
                    // является обычным состоянием Transport.
                    //
                    // Poller не выполняет reconnect самостоятельно:
                    // следующий запрос снова проходит через Transport.
                    PollingStatusChanged?.Invoke(
                        result.Status);
                }

                await Task.Delay(
                    _config.PollIntervalMs,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            // Штатное завершение polling lifecycle.
        }
    }

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
                // Защитный catch lifecycle.
            }
        }

        pollingCts.Dispose();

        _pollingCts = null;
        _pollingTask = null;
    }

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