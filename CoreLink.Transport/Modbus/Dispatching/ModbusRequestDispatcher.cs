using System.Collections.Concurrent;
using CoreLink.Transport.Modbus.Connection;
using CoreLink.Transport.Modbus.Results;

namespace CoreLink.Transport.Modbus.Dispatching;

/// <summary>
/// Последовательно выполняет Modbus-запросы одной TCP-сессии
/// с учётом приоритета операций.
///
/// Порядок обслуживания:
/// 1. Write.
/// 2. Штатный polling read.
/// 3. Одиночный read.
///
/// Уже выполняющийся Modbus-запрос никогда не прерывается.
/// Приоритет применяется только при выборе следующей операции.
///
/// Dispatcher не преобразует transport-status обратно в исключения:
/// результат ModbusConnectionManager передаётся вызывающей стороне
/// в детерминированном виде.
/// </summary>
internal sealed class ModbusRequestDispatcher : IAsyncDisposable
{
    private readonly ModbusConnectionManager _connectionManager;

    /// <summary>
    /// Очередь команд записи.
    ///
    /// Запись имеет высший пользовательский приоритет,
    /// поскольку обычно является командой оператора или автоматики.
    /// </summary>
    private readonly ConcurrentQueue<WriteRequest> _writeRequests = new();

    /// <summary>
    /// Очередь штатных polling-запросов.
    ///
    /// Poller ожидает завершения каждого запроса перед созданием
    /// следующего, поэтому эта очередь не должна бесконтрольно расти.
    /// </summary>
    private readonly ConcurrentQueue<ReadRequest> _pollRequests = new();

    /// <summary>
    /// Очередь одиночных чтений.
    ///
    /// Такие запросы являются вспомогательными и выполняются
    /// только после write и штатного polling.
    /// </summary>
    private readonly ConcurrentQueue<ReadRequest> _singleReadRequests = new();

    /// <summary>
    /// Будит единственный worker при появлении новой операции.
    /// </summary>
    private readonly SemaphoreSlim _requestSignal = new(0);

    private CancellationTokenSource? _workerCts;
    private Task? _workerTask;

    /// <summary>
    /// Если polling уже ожидает выполнения, write получает один
    /// приоритетный слот перед ним.
    ///
    /// После этого polling обязательно обслуживается, чтобы
    /// непрерывный поток записей не остановил телеметрию.
    /// </summary>
    private bool _writeGrantedBeforePendingPoll;

    /// <summary>
    /// Показывает, работает ли диспетчер запросов.
    /// </summary>
    public bool IsRunning => _workerTask is not null;

    /// <summary>
    /// Создаёт диспетчер для существующей Modbus TCP-сессии.
    ///
    /// Диспетчер не владеет ModbusConnectionManager и не уничтожает
    /// его при собственном завершении.
    /// </summary>
    public ModbusRequestDispatcher(
        ModbusConnectionManager connectionManager)
    {
        _connectionManager = connectionManager;
    }

    /// <summary>
    /// Запускает единственный worker обработки Modbus-запросов.
    ///
    /// Повторный вызов Start ничего не делает.
    /// </summary>
    public void Start()
    {
        if (IsRunning)
            return;

        _workerCts = new CancellationTokenSource();

        _workerTask = WorkerAsync(
            _workerCts.Token);
    }

    /// <summary>
    /// Добавляет запись одного Holding Register
    /// в высокоприоритетную очередь.
    /// </summary>
    public Task<ModbusWriteResult> WriteSingleRegisterAsync(
        byte slaveId,
        ushort address,
        ushort value)
    {
        TaskCompletionSource<ModbusWriteResult> completion =
            CreateCompletionSource<ModbusWriteResult>();

        _writeRequests.Enqueue(
            WriteRequest.CreateSingle(
                slaveId,
                address,
                value,
                completion));

        _requestSignal.Release();

        return completion.Task;
    }

    /// <summary>
    /// Добавляет запись блока Holding Registers
    /// в высокоприоритетную очередь.
    ///
    /// Массив копируется при постановке в очередь, чтобы вызывающая
    /// сторона не могла изменить данные до фактической отправки.
    /// </summary>
    public Task<ModbusWriteResult> WriteMultipleRegistersAsync(
        byte slaveId,
        ushort startAddress,
        ushort[] values)
    {
        ArgumentNullException.ThrowIfNull(values);

        TaskCompletionSource<ModbusWriteResult> completion =
            CreateCompletionSource<ModbusWriteResult>();

        ushort[] valuesCopy =
            (ushort[])values.Clone();

        _writeRequests.Enqueue(
            WriteRequest.CreateMultiple(
                slaveId,
                startAddress,
                valuesCopy,
                completion));

        _requestSignal.Release();

        return completion.Task;
    }

    /// <summary>
    /// Добавляет штатное чтение polling.
    ///
    /// Polling имеет приоритет ниже write, но выше одиночного чтения.
    /// </summary>
    public Task<ModbusReadResult> ReadPollingAsync(
        byte slaveId,
        ushort startAddress,
        ushort count)
    {
        TaskCompletionSource<ModbusReadResult> completion =
            CreateCompletionSource<ModbusReadResult>();

        _pollRequests.Enqueue(
            new ReadRequest(
                slaveId,
                startAddress,
                count,
                completion));

        _requestSignal.Release();

        return completion.Task;
    }

    /// <summary>
    /// Добавляет одиночное чтение с самым низким приоритетом.
    ///
    /// Используется для операций, которые не должны задерживать
    /// штатный поток данных.
    /// </summary>
    public Task<ModbusReadResult> ReadSingleAsync(
        byte slaveId,
        ushort startAddress,
        ushort count)
    {
        TaskCompletionSource<ModbusReadResult> completion =
            CreateCompletionSource<ModbusReadResult>();

        _singleReadRequests.Enqueue(
            new ReadRequest(
                slaveId,
                startAddress,
                count,
                completion));

        _requestSignal.Release();

        return completion.Task;
    }

    /// <summary>
    /// Единственная точка исполнения запросов текущей Modbus-сессии.
    ///
    /// Наличие одного worker гарантирует, что два запроса
    /// физически не выполняются одновременно.
    /// </summary>
    private async Task WorkerAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _requestSignal.WaitAsync(
                    cancellationToken);

                while (await TryExecuteNextAsync(
                           cancellationToken))
                {
                    // Продолжаем разбирать уже накопившиеся запросы
                    // без дополнительного ожидания сигнала.
                }
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            // Штатное завершение worker.
        }
        finally
        {
            CompletePendingRequestsAsCancelled();
        }
    }

    /// <summary>
    /// Выбирает и выполняет ровно одну следующую операцию.
    ///
    /// Если polling уже ожидает, одной записи разрешается пройти
    /// перед ним. Затем polling получает гарантированный слот.
    /// Это сохраняет приоритет write и одновременно исключает
    /// starvation основного потока телеметрии.
    /// </summary>
    private async Task<bool> TryExecuteNextAsync(
        CancellationToken cancellationToken)
    {
        bool pollPending =
            !_pollRequests.IsEmpty;

        if (pollPending)
        {
            if (!_writeGrantedBeforePendingPoll &&
                _writeRequests.TryDequeue(
                    out WriteRequest? priorityWrite))
            {
                _writeGrantedBeforePendingPoll = true;

                await ExecuteWriteAsync(
                    priorityWrite,
                    cancellationToken);

                return true;
            }

            if (_pollRequests.TryDequeue(
                    out ReadRequest? pollRequest))
            {
                _writeGrantedBeforePendingPoll = false;

                await ExecuteReadAsync(
                    pollRequest,
                    cancellationToken);

                return true;
            }
        }

        if (_writeRequests.TryDequeue(
                out WriteRequest? writeRequest))
        {
            await ExecuteWriteAsync(
                writeRequest,
                cancellationToken);

            return true;
        }

        if (_pollRequests.TryDequeue(
                out ReadRequest? pollingRequest))
        {
            _writeGrantedBeforePendingPoll = false;

            await ExecuteReadAsync(
                pollingRequest,
                cancellationToken);

            return true;
        }

        if (_singleReadRequests.TryDequeue(
                out ReadRequest? singleReadRequest))
        {
            await ExecuteReadAsync(
                singleReadRequest,
                cancellationToken);

            return true;
        }

        return false;
    }

    /// <summary>
    /// Выполняет одну команду записи через ModbusConnectionManager.
    ///
    /// Результат manager передаётся без преобразования в исключение.
    /// </summary>
    private async Task ExecuteWriteAsync(
        WriteRequest request,
        CancellationToken cancellationToken)
    {
        ModbusWriteResult result;

        if (cancellationToken.IsCancellationRequested)
        {
            result = CreateCancelledWriteResult();
        }
        else if (request.Values is null)
        {
            result =
                await _connectionManager.WriteSingleRegisterAsync(
                    request.SlaveId,
                    request.Address,
                    request.Value,
                    cancellationToken);
        }
        else
        {
            result =
                await _connectionManager.WriteMultipleRegistersAsync(
                    request.SlaveId,
                    request.Address,
                    request.Values,
                    cancellationToken);
        }

        request.Completion.TrySetResult(
            result);
    }

    /// <summary>
    /// Выполняет одну операцию чтения через ModbusConnectionManager.
    ///
    /// Пустой массив не используется как признак отказа:
    /// status и Data передаются вызывающей стороне как единый результат.
    /// </summary>
    private async Task ExecuteReadAsync(
        ReadRequest request,
        CancellationToken cancellationToken)
    {
        ModbusReadResult result;

        if (cancellationToken.IsCancellationRequested)
        {
            result = CreateCancelledReadResult();
        }
        else
        {
            result =
                await _connectionManager.ReadInputRegistersAsync(
                    request.SlaveId,
                    request.StartAddress,
                    request.Count,
                    cancellationToken);
        }

        request.Completion.TrySetResult(
            result);
    }

    /// <summary>
    /// Асинхронно останавливает worker.
    ///
    /// Уже выполняющийся transport-запрос завершается по обычным
    /// правилам cancellation/timeout. Оставшиеся очереди завершаются
    /// детерминированным статусом Cancelled.
    /// </summary>
    public async Task StopAsync()
    {
        if (_workerCts is null)
            return;

        CancellationTokenSource workerCts =
            _workerCts;

        Task? workerTask =
            _workerTask;

        workerCts.Cancel();

        if (workerTask is not null)
        {
            try
            {
                await workerTask;
            }
            catch (OperationCanceledException)
            {
                // Защитный catch для lifecycle worker.
                // Пользовательские transport-запросы при этом
                // завершаются через ModbusTransportStatus.Cancelled.
            }
        }

        workerCts.Dispose();

        _workerCts = null;
        _workerTask = null;

        _writeGrantedBeforePendingPoll = false;
    }

    /// <summary>
    /// Завершает все запросы, оставшиеся в очередях после остановки.
    ///
    /// Отмена lifecycle является штатным transport-состоянием,
    /// поэтому Task не переводятся в Canceled/Exception.
    /// </summary>
    private void CompletePendingRequestsAsCancelled()
    {
        while (_writeRequests.TryDequeue(
                   out WriteRequest? writeRequest))
        {
            writeRequest.Completion.TrySetResult(
                CreateCancelledWriteResult());
        }

        while (_pollRequests.TryDequeue(
                   out ReadRequest? pollRequest))
        {
            pollRequest.Completion.TrySetResult(
                CreateCancelledReadResult());
        }

        while (_singleReadRequests.TryDequeue(
                   out ReadRequest? singleReadRequest))
        {
            singleReadRequest.Completion.TrySetResult(
                CreateCancelledReadResult());
        }
    }

    /// <summary>
    /// Создаёт результат штатной отмены операции чтения.
    /// </summary>
    private static ModbusReadResult CreateCancelledReadResult()
    {
        return new ModbusReadResult
        {
            Status = ModbusTransportStatus.Cancelled,
            Data = null
        };
    }

    /// <summary>
    /// Создаёт результат штатной отмены операции записи.
    /// </summary>
    private static ModbusWriteResult CreateCancelledWriteResult()
    {
        return new ModbusWriteResult
        {
            Status = ModbusTransportStatus.Cancelled
        };
    }

    /// <summary>
    /// Создаёт continuation source так, чтобы пользовательский код
    /// не продолжал выполнение внутри transport worker.
    /// </summary>
    private static TaskCompletionSource<TResult>
        CreateCompletionSource<TResult>()
    {
        return new TaskCompletionSource<TResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>
    /// Асинхронно завершает dispatcher и освобождает
    /// принадлежащий ему примитив сигнализации.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await StopAsync();

        _requestSignal.Dispose();
    }

    /// <summary>
    /// Внутреннее представление операции чтения.
    /// </summary>
    private sealed record ReadRequest(
        byte SlaveId,
        ushort StartAddress,
        ushort Count,
        TaskCompletionSource<ModbusReadResult> Completion);

    /// <summary>
    /// Внутреннее представление команды записи.
    ///
    /// Values == null означает запись одного регистра.
    /// Непустой Values означает запись блока регистров.
    /// </summary>
    private sealed record WriteRequest(
        byte SlaveId,
        ushort Address,
        ushort Value,
        ushort[]? Values,
        TaskCompletionSource<ModbusWriteResult> Completion)
    {
        public static WriteRequest CreateSingle(
            byte slaveId,
            ushort address,
            ushort value,
            TaskCompletionSource<ModbusWriteResult> completion)
        {
            return new WriteRequest(
                slaveId,
                address,
                value,
                null,
                completion);
        }

        public static WriteRequest CreateMultiple(
            byte slaveId,
            ushort startAddress,
            ushort[] values,
            TaskCompletionSource<ModbusWriteResult> completion)
        {
            return new WriteRequest(
                slaveId,
                startAddress,
                0,
                values,
                completion);
        }
    }
}
