
using System.Diagnostics;
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
/// </summary>
public sealed class ModbusTransportSession : IAsyncDisposable
{
    /// <summary>
    /// Защищает запуск процедуры освобождения ресурсов
    /// от одновременных вызовов DisposeAsync().
    /// </summary>
    private readonly object _disposeLock = new();

    /// <summary>
    /// Общая задача освобождения ресурсов сессии.
    /// Повторные вызовы DisposeAsync() ожидают ту же операцию,
    /// не создавая дополнительных процедур остановки.
    /// </summary>

    private Task? _disposeTask;
    private readonly ModbusConnectionManager _connectionManager;
    private readonly ModbusRequestDispatcher _dispatcher;
    private readonly ModbusPoller _poller;
    private readonly ModbusConnectionConfig _config;

    private bool _disposed;

    public event Action<ushort[]>? RegistersReceived;

    public event Action<ModbusTransportStatus>? TransportStatusChanged;

    public bool IsRunning => _poller.IsRunning;

    public ModbusTransportSession(
        ModbusConnectionConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        _config = config;

        _connectionManager =
            new ModbusConnectionManager(config);

        _dispatcher =
            new ModbusRequestDispatcher(_connectionManager);

        _poller =
            new ModbusPoller(_dispatcher, config);

        _poller.RegistersReceived += OnRegistersReceived;
        _poller.PollingStatusChanged += OnPollingStatusChanged;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _dispatcher.Start();
        _poller.Start();
    }

    public async Task StopAsync()
    {
        if (_disposed)
            return;

        await _poller.StopAsync();
        await _dispatcher.StopAsync();
    }

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
    /// Выполняет прямое чтение через общий Dispatcher.
    ///
    /// FC03 — Holding Registers.
    /// FC04 — Input Registers.
    ///
    /// Не создаёт нового TCP-соединения.
    /// Не изменяет штатный polling.
    /// </summary>
    public Task<ModbusReadResult> ReadSingleAsync(
        ushort startAddress,
        ushort count,
        byte functionCode = 4)
    {
        ThrowIfNotAvailable();

        return _dispatcher.ReadSingleAsync(
            GetSlaveId(),
            startAddress,
            count,
            functionCode);
    }


    private void OnRegistersReceived(ushort[] registers)
    {
        RegistersReceived?.Invoke(registers);
    }

    private void OnPollingStatusChanged(
        ModbusTransportStatus status)
    {
        TransportStatusChanged?.Invoke(status);
    }

    private void ThrowIfNotAvailable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_dispatcher.IsRunning)
        {
            throw new InvalidOperationException(
                "Modbus transport session is not running.");
        }
    }

    private byte GetSlaveId()
    {
        return _config.SlaveId;
    }

    // TODO DEBUG: Remove temporary transport stop diagnostics after testing.
    /// <summary>
    /// FIXME: временная диагностика Stop.
    /// Удалить после испытаний зависшего I/O.
    ///
    /// Поля ConnectionManager читаются без единого
    /// диагностического snapshot-lock, поэтому значения
    /// носят наблюдательный характер.
    /// </summary>
    private void PrintStopDiagnostics(string stage)
    {
        DateTime? started =
            _connectionManager.CurrentRequestStartedAt;

        double elapsedMs = started.HasValue
            ? Math.Max(
                0,
                (DateTime.Now - started.Value).TotalMilliseconds)
            : 0;

        Console.WriteLine();
        Console.WriteLine(
            $"[TRANSPORT {stage}] {DateTime.Now:HH:mm:ss.fff}");

        Console.WriteLine(
            $"  RequestInProgress    = {_connectionManager.RequestInProgress}");

        Console.WriteLine(
            $"  RequestIsHung        = {_connectionManager.RequestIsHung}");

        Console.WriteLine(
            $"  CurrentRequestAgeMs  = {elapsedMs:F0}");

        Console.WriteLine(
            $"  LastRequestMs        = {_connectionManager.LastRequestMs}");

        Console.WriteLine(
            $"  MaxRequestMs         = {_connectionManager.MaxRequestMs}");

        Console.WriteLine(
            $"  RequestStartedCount  = {_connectionManager.RequestStartedCount}");

        Console.WriteLine(
            $"  RequestCompletedCount= {_connectionManager.RequestCompletedCount}");

        Console.WriteLine(
            $"  HungRequestCount     = {_connectionManager.HungRequestCount}");

        Console.WriteLine(
            $"  RecoveredRequestCount= {_connectionManager.RecoveredRequestCount}");

        Console.WriteLine(
            $"  PollerRunning        = {_poller.IsRunning}");

        Console.WriteLine(
            $"  DispatcherRunning    = {_dispatcher.IsRunning}");
    }


    /// <summary>
    /// Асинхронно освобождает ресурсы Modbus TCP-сессии.
    ///
    /// Конкурентные вызовы используют одну задачу завершения.
    /// Повторный вызов не запускает освобождение повторно.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        lock (_disposeLock)
        {
            _disposeTask ??= DisposeCoreAsync();

            return new ValueTask(_disposeTask);
        }
    }

    /// <summary>
    /// Последовательно останавливает Poller и Dispatcher,
    /// затем освобождает TCP-сессию.
    ///
    /// Порядок освобождения сохраняется:
    /// Poller -> Dispatcher -> ConnectionManager.
    /// </summary>
    private async Task DisposeCoreAsync()
    {
        try
        {
            // Запрещаем повторный Start сессии.
            _disposed = true;

            // FIXME: Временная диагностика остановки.
            // Удалить после испытаний timeout/reconnect.
            Stopwatch stopwatch = Stopwatch.StartNew();

            PrintStopDiagnostics("STOP BEGIN");

            _poller.RegistersReceived -= OnRegistersReceived;
            _poller.PollingStatusChanged -= OnPollingStatusChanged;

            await _poller.DisposeAsync()
                .ConfigureAwait(false);

            PrintStopDiagnostics("POLLER STOPPED");

            await _dispatcher.DisposeAsync()
                .ConfigureAwait(false);

            PrintStopDiagnostics("DISPATCHER STOPPED");

            _connectionManager.Dispose();

            stopwatch.Stop();

            // FIXME: Временная диагностика длительности Stop.
            // Удалить после испытаний освобождения ресурсов.
            Console.WriteLine(
                $"[TRANSPORT STOP COMPLETE] " +
                $"Elapsed={stopwatch.Elapsed.TotalMilliseconds:F1} ms");
        }
        catch (Exception ex)
        {
            // TODO: Подключить централизованное логирование.
            // Ошибка освобождения не пробрасывается наружу.
            //
            // При незавершённой остановке worker запрещено
            // принудительно уничтожать ConnectionManager:
            // это может вызвать обращение к освобождённым ресурсам.

            Trace.TraceError(
                $"Modbus transport dispose failed: {ex}");
        }
    }

}
