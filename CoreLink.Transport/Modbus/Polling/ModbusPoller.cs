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
internal sealed class ModbusPoller : IDisposable
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

        _pollingCts = new CancellationTokenSource();

        _pollingTask = PollAsync(
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
    /// </summary>
    private async Task PollAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
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
    public void Stop()
    {
        if (_pollingCts is null)
            return;

        _pollingCts.Cancel();

        _pollingCts.Dispose();
        _pollingCts = null;

        _pollingTask = null;
    }

    /// <summary>
    /// Останавливает polling.
    ///
    /// Диспетчер здесь не уничтожается, поскольку его lifetime
    /// принадлежит владельцу всей Modbus-сессии.
    /// </summary>
    public void Dispose()
    {
        Stop();
    }
}