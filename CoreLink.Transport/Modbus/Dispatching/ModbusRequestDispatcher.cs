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

    /// <summary>
    /// Синхронизирует изменение состояния Dispatcher
    /// с постановкой новых запросов в очереди.
    /// </summary>
    private readonly object _lifecycleLock = new();

    /// <summary>
    /// Разрешена ли постановка запросов.
    /// При Stop устанавливается false до отмены worker.
    /// </summary>
    private bool _acceptingRequests;


    /// <summary>
    /// Общая задача остановки Dispatcher.
    /// Конкурентные StopAsync() ожидают один и тот же worker,
    /// не выполняя повторную отмену и освобождение CTS.
    /// </summary>
    private Task? _stopTask;

    /// <summary>
    /// Общая задача освобождения ресурсов Dispatcher.
    /// Гарантирует однократное освобождение _requestSignal.
    /// </summary>
    private Task? _disposeTask;

    /// <summary>
    /// Признак начала окончательного освобождения Dispatcher.
    /// После DisposeAsync() повторный Start запрещён.
    /// </summary>
    private bool _disposed;

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
    /// Повторный Start работающего Dispatcher ничего не делает.
    /// После штатного Stop допускается повторный запуск.
    /// После Dispose запуск запрещён.
    /// </summary>
    public void Start()
    {
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // Запрещаем новый запуск, пока предыдущий worker
            // ещё проходит процедуру остановки.
            if (_stopTask is { IsCompleted: false })
            {
                throw new InvalidOperationException(
                    "Modbus dispatcher is stopping.");
            }

            if (_workerTask is not null)
                return;

            // Предыдущий Stop полностью завершён.
            // Новая сессия получает собственный CTS и worker.
            _stopTask = null;

            _workerCts = new CancellationTokenSource();

            _workerTask = WorkerAsync(
                _workerCts.Token);

            _acceptingRequests = true;
        }
    }

    /// <summary>
    /// Добавляет команду записи одного Holding Register
    /// в высокоприоритетную очередь Dispatcher.
    ///
    /// Проверка состояния и постановка запроса выполняются
    /// под общим lifecycle-lock. Это исключает ситуацию,
    /// когда worker уже остановлен, а запрос остаётся
    /// в очереди с незавершённым TaskCompletionSource.
    ///
    /// Если Dispatcher не принимает запросы, возвращается
    /// результат со статусом Cancelled.
    /// </summary>
    public Task<ModbusWriteResult> WriteSingleRegisterAsync(
        byte slaveId,
        ushort address,
        ushort value)
    {
        lock (_lifecycleLock)
        {
            // StopAsync() закрывает приём запросов под тем же lock.
            // После остановки создавать новую операцию запрещено.
            if (!_acceptingRequests)
                return Task.FromResult(CreateCancelledWriteResult());

            // Каждый запрос имеет собственный CompletionSource.
            // RunContinuationsAsynchronously предотвращает выполнение
            // пользовательского продолжения внутри worker.
            TaskCompletionSource<ModbusWriteResult> completion =
                CreateCompletionSource<ModbusWriteResult>();

            _writeRequests.Enqueue(
                WriteRequest.CreateSingle(
                    slaveId,
                    address,
                    value,
                    completion));

            // Сигнал и постановка в очередь находятся под одним lock.
            // Stop не сможет вклиниться между ними.
            _requestSignal.Release();

            return completion.Task;
        }
    }

    /// <summary>
    /// Добавляет команду записи нескольких Holding Registers
    /// в высокоприоритетную очередь Dispatcher.
    ///
    /// Исходный массив копируется до постановки запроса,
    /// поскольку вызывающая сторона сохраняет владение
    /// переданным массивом и может изменить его содержимое.
    ///
    /// Проверка состояния, копирование и постановка запроса
    /// выполняются под lifecycle-lock.
    ///
    /// При остановленном Dispatcher возвращается Cancelled.
    /// </summary>
    public Task<ModbusWriteResult> WriteMultipleRegistersAsync(
        byte slaveId,
        ushort startAddress,
        ushort[] values)
    {
        ArgumentNullException.ThrowIfNull(values);

        lock (_lifecycleLock)
        {
            // Не создаём запрос, который некому выполнить.
            if (!_acceptingRequests)
                return Task.FromResult(CreateCancelledWriteResult());

            TaskCompletionSource<ModbusWriteResult> completion =
                CreateCompletionSource<ModbusWriteResult>();

            // Снимок данных на момент постановки команды.
            // Последующие изменения исходного массива
            // не повлияют на отправляемые значения.
            ushort[] valuesCopy = (ushort[])values.Clone();

            _writeRequests.Enqueue(
                WriteRequest.CreateMultiple(
                    slaveId,
                    startAddress,
                    valuesCopy,
                    completion));

            // Будим единственный worker.
            // Приоритет записи определяется TryExecuteNextAsync().
            _requestSignal.Release();

            return completion.Task;
        }
    }

    /// <summary>
    /// Добавляет штатный polling-запрос в очередь Dispatcher.
    ///
    /// Поддерживаются:
    /// FC03 — Read Holding Registers;
    /// FC04 — Read Input Registers.
    ///
    /// Polling имеет приоритет ниже write, но выше single read.
    /// Существующий алгоритм распределения приоритетов
    /// не изменяется.
    ///
    /// При остановке Dispatcher возвращается Cancelled.
    /// </summary>
    public Task<ModbusReadResult> ReadPollingAsync(
        byte slaveId,
        ushort startAddress,
        ushort count,
        byte functionCode = 4)
    {
        // Ошибка конфигурации должна выявляться сразу.
        // Lifecycle-проверка не заменяет валидацию аргументов.
        if (functionCode is not (3 or 4))
            throw new ArgumentOutOfRangeException(nameof(functionCode));

        lock (_lifecycleLock)
        {
            // Исключаем enqueue после завершения worker.
            // Это гарантирует, что polling не останется
            // в бесконечном ожидании CompletionSource.
            if (!_acceptingRequests)
                return Task.FromResult(CreateCancelledReadResult());

            TaskCompletionSource<ModbusReadResult> completion =
                CreateCompletionSource<ModbusReadResult>();

            _pollRequests.Enqueue(
                new ReadRequest(
                    slaveId,
                    startAddress,
                    count,
                    functionCode,
                    completion));

            // Worker самостоятельно выбирает момент выполнения
            // с учётом приоритетов остальных очередей.
            _requestSignal.Release();

            return completion.Task;
        }
    }

    /// <summary>
    /// Добавляет одиночное чтение регистров
    /// в низкоприоритетную очередь Dispatcher.
    ///
    /// Поддерживаются FC03 и FC04.
    ///
    /// Одиночное чтение не прерывает текущий запрос
    /// и не изменяет период штатного polling.
    ///
    /// Состояние Dispatcher проверяется атомарно
    /// относительно постановки запроса в очередь.
    ///
    /// При остановке возвращается статус Cancelled.
    /// </summary>
    public Task<ModbusReadResult> ReadSingleAsync(
        byte slaveId,
        ushort startAddress,
        ushort count,
        byte functionCode = 4)
    {
        if (functionCode is not (3 or 4))
            throw new ArgumentOutOfRangeException(nameof(functionCode));

        lock (_lifecycleLock)
        {
            // После StopAsync() очередь больше не обслуживается.
            // Поэтому возвращаем готовый результат Cancelled,
            // не создавая зависший TaskCompletionSource.
            if (!_acceptingRequests)
                return Task.FromResult(CreateCancelledReadResult());

            TaskCompletionSource<ModbusReadResult> completion =
                CreateCompletionSource<ModbusReadResult>();

            _singleReadRequests.Enqueue(
                new ReadRequest(
                    slaveId,
                    startAddress,
                    count,
                    functionCode,
                    completion));

            // Сигнализация выполняется до освобождения lock,
            // чтобы StopAsync() не мог закрыть приём запросов
            // посередине операции постановки.
            _requestSignal.Release();

            return completion.Task;
        }
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
            lock (_lifecycleLock)
            {
                // Закрываем приём запросов независимо от причины
                // завершения worker: Stop, отмена или исключение.
                //
                // Проверка _acceptingRequests и Enqueue используют
                // тот же lock, поэтому после его установки в false
                // ни один новый запрос не попадёт в очереди.
                _acceptingRequests = false;

                // Завершаем все необработанные запросы.
                // Очистка выполняется под тем же lock, исключая
                // появление новой записи после последнего Dequeue.
                CompletePendingRequestsAsCancelled();
            }
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
    /// Штатные результаты Transport передаются без изменений.
    /// Неожиданное исключение преобразуется в Faulted, чтобы
    /// не завершать единственный worker диспетчера.
    ///
    /// Completion завершается при любом обработанном результате.
    /// </summary>
    private async Task ExecuteWriteAsync(
        WriteRequest request,
        CancellationToken cancellationToken)
    {
        ModbusWriteResult result;

        try
        {
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
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            // Штатная отмена операции владельцем lifecycle.
            result = CreateCancelledWriteResult();
        }
        catch (Exception ex)
        {
            // TODO: Подключить централизованное логирование.
            // Неожиданное исключение не должно завершать worker.

            System.Diagnostics.Trace.TraceError(
                $"Modbus dispatcher write failed: {ex}");

            result = new ModbusWriteResult
            {
                Status = ModbusTransportStatus.Faulted
            };
        }

        // Завершаем ожидающий Task без проброса исключений.
        request.Completion.TrySetResult(result);
    }



    /// <summary>
    /// Выполняет одну операцию чтения через ModbusConnectionManager.
    ///
    /// Результат содержит статус и данные одной операции.
    /// При неожиданной ошибке возвращается Faulted без данных.
    ///
    /// Ошибка одного запроса не завершает dispatcher worker.
    /// </summary>
    private async Task ExecuteReadAsync(
        ReadRequest request,
        CancellationToken cancellationToken)
    {
        ModbusReadResult result;

        try
        {
            if (cancellationToken.IsCancellationRequested)
            {
                result = CreateCancelledReadResult();
            }
            else
            {
                result = request.FunctionCode switch
                {
                    3 => await _connectionManager.ReadHoldingRegistersAsync(
                        request.SlaveId,
                        request.StartAddress,
                        request.Count,
                        cancellationToken),

                    4 => await _connectionManager.ReadInputRegistersAsync(
                        request.SlaveId,
                        request.StartAddress,
                        request.Count,
                        cancellationToken),

                    _ => throw new InvalidOperationException(
                        "Unsupported Modbus read function code.")
                };
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            // Штатная отмена при остановке Dispatcher.
            result = CreateCancelledReadResult();
        }
        catch (Exception ex)
        {
            // TODO: Подключить централизованное логирование.
            // Неожиданная ошибка не должна завершать worker.

            System.Diagnostics.Trace.TraceError(
                $"Modbus dispatcher read failed: {ex}");

            result = new ModbusReadResult
            {
                Status = ModbusTransportStatus.Faulted,
                Data = null
            };
        }

        // Любой обработанный запрос получает конечный результат.
        request.Completion.TrySetResult(result);
    }


    /// <summary>
    /// Останавливает Dispatcher и запрещает новые запросы.
    ///
    /// Конкурентные вызовы используют общую задачу остановки.
    /// Повторное освобождение CancellationTokenSource исключено.
    /// </summary>
    public Task StopAsync()
    {
        lock (_lifecycleLock)
        {
            // Запрет постановки устанавливается до отмены worker.
            // Все новые запросы получат статус Cancelled.
            _acceptingRequests = false;

            // Если остановка уже началась, ожидаем её результат.
            if (_stopTask is not null)
                return _stopTask;

            if (_workerCts is null)
                return Task.CompletedTask;

            // Создаём одну задачу для всех конкурентных StopAsync().
            _stopTask = StopCoreAsync(
                _workerCts,
                _workerTask);

            return _stopTask;
        }
    }

    /// <summary>
    /// Выполняет фактическую остановку единственного worker.
    ///
    /// CancellationTokenSource освобождается только после
    /// завершения worker. Состояние Dispatcher очищается
    /// под общим lifecycle-lock.
    /// </summary>
    private async Task StopCoreAsync(
        CancellationTokenSource workerCts,
        Task? workerTask)
    {
        try
        {
            // Отменяем текущую операцию через существующий
            // CancellationToken. Новых запросов уже не принимаем.
            workerCts.Cancel();

            if (workerTask is not null)
            {
                try
                {
                    await workerTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                    when (workerCts.IsCancellationRequested)
                {
                    // Штатная отмена lifecycle.
                }
            }
        }
        finally
        {
            lock (_lifecycleLock)
            {
                // Защита от изменения состояния другой сессии.
                if (ReferenceEquals(_workerCts, workerCts))
                {
                    _workerCts = null;
                    _workerTask = null;
                    _writeGrantedBeforePendingPoll = false;
                }

                // Освобождение принадлежит только этой процедуре.
                workerCts.Dispose();
            }
        }
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
    /// Окончательно освобождает Dispatcher.
    ///
    /// Одновременные вызовы DisposeAsync() используют одну
    /// задачу завершения. Повторное освобождение SemaphoreSlim
    /// и создание нового worker после Dispose исключены.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        lock (_lifecycleLock)
        {
            if (_disposeTask is null)
            {
                // Запрещаем Start ещё до начала асинхронной
                // процедуры освобождения ресурсов.
                _disposed = true;
                _acceptingRequests = false;

                _disposeTask = DisposeCoreAsync();
            }

            return new ValueTask(_disposeTask);
        }
    }

    /// <summary>
    /// Останавливает worker перед освобождением сигнализации.
    ///
    /// Dispatcher не освобождает ModbusConnectionManager:
    /// его lifetime принадлежит ModbusTransportSession.
    /// </summary>
    private async Task DisposeCoreAsync()
    {
        try
        {
            // Используем существующую общую процедуру Stop.
            // Она ожидает завершения worker и очистки очередей.
            await StopAsync().ConfigureAwait(false);
        }
        finally
        {
            // Новые запросы запрещены флагом _disposed.
            // Worker уже завершён после StopAsync().
            _requestSignal.Dispose();
        }
    }


    /// <summary>
    /// Внутреннее представление операции чтения.
    /// </summary>

    private sealed record ReadRequest(
        byte SlaveId,
        ushort StartAddress,
        ushort Count,
        byte FunctionCode,
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
