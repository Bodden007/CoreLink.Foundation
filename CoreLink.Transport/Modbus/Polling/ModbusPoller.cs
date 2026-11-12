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

    /// <summary>
    /// Выполняет последовательный polling Modbus-регистров.
    ///
    /// Каждый запрос полностью завершается перед следующим циклом.
    /// Ошибки одного цикла не останавливают polling и не препятствуют
    /// последующему восстановлению соединения.
    ///
    /// Reconnect остаётся ответственностью ModbusConnectionManager.
    /// </summary>
    private async Task PollAsync(
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                ModbusReadResult result =
                    await _dispatcher.ReadPollingAsync(
                        _config.SlaveId,
                        _config.InputStartAddress,
                        _config.InputRegisterCount,
                        _config.PollFunctionCode);

                if (result.Status ==
                    ModbusTransportStatus.Cancelled)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;
                }
                else if (result.Ok)
                {
                    ushort[]? registers = result.Data;

                    if (registers is not null &&
                        registers.Length > 0)
                    {
                        RegistersReceived?.Invoke(registers);
                    }
                }
                else
                {
                    // Ошибка обмена не завершает polling.
                    // Следующий цикл повторно обратится к Transport.
                    PollingStatusChanged?.Invoke(result.Status);
                }
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                // Штатное завершение polling.
                break;
            }
            catch (Exception ex)
            {
                // TODO: Подключить централизованное логирование.
                // Неожиданная ошибка должна быть диагностирована,
                // но не должна завершать постоянный polling.

                System.Diagnostics.Trace.TraceError(
                    $"Modbus polling failed: {ex}");

                try
                {
                    PollingStatusChanged?.Invoke(
                        ModbusTransportStatus.Faulted);
                }
                catch (Exception notificationEx)
                {
                    // TODO: Подключить централизованное логирование.
                    // Ошибка подписчика не должна завершать polling.

                    System.Diagnostics.Trace.TraceError(
                        $"Modbus polling notification failed: {notificationEx}");
                }
            }

            try
            {
                // Интервал отсчитывается после завершения запроса.
                // Новые polling-запросы не накапливаются.
                await Task.Delay(
                    _config.PollIntervalMs,
                    cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
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