using System.Diagnostics;
using System.Net.Sockets;
using NModbus;
using CoreLink.Transport.Modbus.Configuration;
using CoreLink.Transport.Modbus.Results;

namespace CoreLink.Transport.Modbus.Connection;

/// <summary>
/// Владеет одной Modbus TCP-сессией.
///
/// Менеджер управляет одним TcpClient и одним IModbusMaster,
/// используемыми всеми операциями чтения и записи текущей сессии.
///
/// Подключение восстанавливается по необходимости.
/// Все Modbus-запросы выполняются последовательно через request lock.
///
/// Штатные сетевые и Modbus-ошибки не пробрасываются наружу
/// исключениями: вызывающая сторона получает детерминированный
/// ModbusTransportStatus.
/// </summary>
internal sealed class ModbusConnectionManager : IDisposable
{
    private readonly ModbusConnectionConfig _config;

    /// <summary>
    /// Сериализует создание и восстановление TCP-сессии.
    /// </summary>
    private readonly SemaphoreSlim _connectionLock = new(1, 1);

    /// <summary>
    /// Сериализует все Modbus-запросы через один IModbusMaster.
    ///
    /// Polling и команды записи используют одну TCP-сессию,
    /// поэтому параллельное выполнение запросов не допускается.
    /// </summary>
    private readonly SemaphoreSlim _requestLock = new(1, 1);

    /// <summary>
    /// Время последней попытки подключения.
    ///
    /// Используется для ограничения частоты reconnect
    /// при физически недоступном устройстве.
    /// </summary>
    private DateTime _lastConnectAttemptUtc = DateTime.MinValue;

    /// <summary>
    /// Текущий TCP-клиент Modbus-сессии.
    /// </summary>
    private TcpClient? _tcpClient;

    /// <summary>
    /// Текущий NModbus master.
    ///
    /// Его lifetime совпадает с lifetime текущего TcpClient.
    /// </summary>
    private IModbusMaster? _master;

    /// <summary>
    /// Признак завершения жизненного цикла ModbusConnectionManager.
    /// Обеспечивает однократное освобождение ресурсов
    /// при повторных или конкурентных вызовах Dispose().
    /// 0 — активен, 1 — освобождение инициировано.
    /// </summary>
    private int _disposed;

    // FIXME: временная диагностика перенесена из Nitrogen.
    // После стабилизации CoreLink.Transport определить постоянный
    // диагностический контракт и удалить временные счётчики.

    public long RequestLockWaitCount { get; private set; }
    public long RequestLockEnterCount { get; private set; }
    public long RequestStartedCount { get; private set; }
    // Любой запрос, прошедший диагностическую обёртку до конца,
    // включая ошибку, timeout и отмену.
    public long RequestCompletedCount { get; private set; }
    public long HungRequestCount { get; private set; }
    public long RecoveredRequestCount { get; private set; }

    public bool RequestInProgress { get; private set; }
    public bool RequestIsHung { get; private set; }

    public DateTime? CurrentRequestStartedAt { get; private set; }

    public long CurrentRequestMs { get; private set; }
    public long LastRequestMs { get; private set; }
    public long MaxRequestMs { get; private set; }

    public DateTime? LastSuccessTime { get; private set; }

    /// <summary>
    /// Создаёт менеджер одной Modbus TCP-сессии.
    ///
    /// TCP-соединение открывается лениво при первом запросе.
    /// </summary>
    public ModbusConnectionManager(
        ModbusConnectionConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// Читает последовательный блок Input Registers.
    ///
    /// Пустой массив не используется как признак ошибки:
    /// при отказе возвращается ModbusReadResult со статусом ошибки.
    /// </summary>
    public async Task<ModbusReadResult> ReadInputRegistersAsync(
        byte slaveId,
        ushort startAddress,
        ushort count,
        CancellationToken cancellationToken = default)
    {
        (ModbusTransportStatus status, ushort[]? data) =
            await ExecuteRequestAsync(
                master => master.ReadInputRegistersAsync(
                    slaveId,
                    startAddress,
                    count),
                cancellationToken);

        return new ModbusReadResult
        {
            Status = status,
            Data = status == ModbusTransportStatus.Ok
                ? data
                : null
        };
    }


    /// <summary>
    /// Читает последовательный блок Holding Registers (FC03).
    ///
    /// Использует существующий механизм выполнения Modbus-запросов:
    /// сериализацию, timeout, reconnect и обработку ошибок.
    ///
    /// При ошибке Data == null, причина определяется Status.
    /// </summary>
    public async Task<ModbusReadResult> ReadHoldingRegistersAsync(
        byte slaveId,
        ushort startAddress,
        ushort count,
        CancellationToken cancellationToken = default)
    {
        (ModbusTransportStatus status, ushort[]? data) =
            await ExecuteRequestAsync(
                master => master.ReadHoldingRegistersAsync(
                    slaveId,
                    startAddress,
                    count),
                cancellationToken);

        return new ModbusReadResult
        {
            Status = status,
            Data = status == ModbusTransportStatus.Ok
                ? data
                : null
        };
    }


    /// <summary>
    /// Записывает один Holding Register.
    ///
    /// Client получает детерминированный статус выполнения
    /// и не обязан анализировать исключения TcpClient/NModbus.
    /// </summary>
    public async Task<ModbusWriteResult> WriteSingleRegisterAsync(
        byte slaveId,
        ushort address,
        ushort value,
        CancellationToken cancellationToken = default)
    {
        ModbusTransportStatus status =
            await ExecuteRequestAsync(
                master => master.WriteSingleRegisterAsync(
                    slaveId,
                    address,
                    value),
                cancellationToken);

        return new ModbusWriteResult
        {
            Status = status
        };
    }

    /// <summary>
    /// Записывает последовательный блок Holding Registers.
    ///
    /// Весь Modbus-запрос выполняется последовательно относительно
    /// остальных операций текущей TCP-сессии.
    /// </summary>
    public async Task<ModbusWriteResult> WriteMultipleRegistersAsync(
        byte slaveId,
        ushort startAddress,
        ushort[] values,
        CancellationToken cancellationToken = default)
    {
        if (values is null)
        {
            return new ModbusWriteResult
            {
                Status = ModbusTransportStatus.Faulted
            };
        }

        ModbusTransportStatus status =
            await ExecuteRequestAsync(
                master => master.WriteMultipleRegistersAsync(
                    slaveId,
                    startAddress,
                    values),
                cancellationToken);

        return new ModbusWriteResult
        {
            Status = status
        };
    }

    /// <summary>
    /// Гарантирует наличие активной Modbus TCP-сессии.
    ///
    /// Transport самостоятельно ограничивает частоту reconnect.
    /// Любая незавершённая локальная TcpClient-сессия уничтожается
    /// до выхода из метода.
    /// </summary>
    private async Task<ModbusTransportStatus> EnsureConnectedAsync(
        CancellationToken cancellationToken)
    {
        if (_master is not null &&
            _tcpClient?.Connected == true)
        {
            return ModbusTransportStatus.Ok;
        }

        try
        {
            await _connectionLock.WaitAsync(
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return ModbusTransportStatus.Cancelled;
        }
        catch (Exception)
        {
            return ModbusTransportStatus.Faulted;
        }

        try
        {
            // Повторная проверка после lock обязательна:
            // другой запрос мог уже восстановить соединение.
            if (_master is not null &&
                _tcpClient?.Connected == true)
            {
                return ModbusTransportStatus.Ok;
            }

            TimeSpan reconnectDelay =
                TimeSpan.FromMilliseconds(
                    _config.ReconnectDelayMs);

            TimeSpan elapsed =
                DateTime.UtcNow -
                _lastConnectAttemptUtc;

            if (_lastConnectAttemptUtc != DateTime.MinValue &&
                elapsed < reconnectDelay)
            {
                return ModbusTransportStatus.Disconnected;
            }

            _lastConnectAttemptUtc =
                DateTime.UtcNow;

            CloseConnection();

            TcpClient? tcpClient = new();

            try
            {
                using CancellationTokenSource timeoutCts =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken);

                timeoutCts.CancelAfter(
                    _config.ConnectTimeoutMs);

                try
                {
                    await tcpClient.ConnectAsync(
                        _config.Host,
                        _config.Port,
                        timeoutCts.Token);
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    return ModbusTransportStatus.Cancelled;
                }
                catch (OperationCanceledException)
                {
                    return ModbusTransportStatus.ConnectTimeout;
                }
                catch (SocketException)
                {
                    return ModbusTransportStatus.Disconnected;
                }
                catch (IOException)
                {
                    return ModbusTransportStatus.Disconnected;
                }
                catch (Exception)
                {
                    return ModbusTransportStatus.Faulted;
                }

                try
                {
                    ModbusFactory factory = new();

                    IModbusMaster master =
                        factory.CreateMaster(
                            tcpClient);

                    // Передача владения происходит только после того,
                    // как TCP и NModbus master полностью созданы.
                    _tcpClient = tcpClient;
                    _master = master;

                    tcpClient = null;

                    return ModbusTransportStatus.Ok;
                }
                catch (Exception)
                {
                    return ModbusTransportStatus.Faulted;
                }
            }
            finally
            {
                // Если владение TcpClient не было передано полям
                // менеджера, локальный сокет обязательно уничтожается.
                tcpClient?.Dispose();
            }
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <summary>
    /// Выполняет Modbus-запрос без возвращаемых данных.
    ///
    /// Request lock охватывает connect/reconnect и сам запрос,
    /// поэтому состояние текущей TCP-сессии не может измениться
    /// другим запросом между проверкой соединения и выполнением I/O.
    /// </summary>
    private async Task<ModbusTransportStatus> ExecuteRequestAsync(
        Func<IModbusMaster, Task> request,
        CancellationToken cancellationToken)
    {
        // FIXME: временная диагностика перенесена из Nitrogen.
        RequestLockWaitCount++;

        try
        {
            await _requestLock.WaitAsync(
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return ModbusTransportStatus.Cancelled;
        }
        catch (Exception)
        {
            return ModbusTransportStatus.Faulted;
        }

        // FIXME: временная диагностика перенесена из Nitrogen.
        RequestLockEnterCount++;

        try
        {
            ModbusTransportStatus connectionStatus =
                await EnsureConnectedAsync(
                    cancellationToken);

            if (connectionStatus != ModbusTransportStatus.Ok)
            {
                return connectionStatus;
            }

            IModbusMaster? master = _master;

            if (master is null)
            {
                CloseConnection();

                return ModbusTransportStatus.Faulted;
            }

            ModbusTransportStatus requestStatus =
                await ExecuteRequestWithDiagnosticsAsync(
                    () => request(master));

            if (requestStatus != ModbusTransportStatus.Ok)
            {
                // Любой неуспешный Modbus-запрос инвалидирует
                // текущую сессию. Следующий запрос сам выполнит reconnect.
                CloseConnection();
            }

            return requestStatus;
        }
        finally
        {
            _requestLock.Release();
        }
    }

    /// <summary>
    /// Выполняет Modbus-запрос, возвращающий данные.
    ///
    /// При ошибке result всегда default/null, а причина
    /// однозначно задаётся ModbusTransportStatus.
    /// </summary>
    private async Task<(ModbusTransportStatus Status, TResult? Result)>
        ExecuteRequestAsync<TResult>(
            Func<IModbusMaster, Task<TResult>> request,
            CancellationToken cancellationToken)
    {
        // FIXME: временная диагностика перенесена из Nitrogen.
        RequestLockWaitCount++;

        try
        {
            await _requestLock.WaitAsync(
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return (
                ModbusTransportStatus.Cancelled,
                default);
        }
        catch (Exception)
        {
            return (
                ModbusTransportStatus.Faulted,
                default);
        }

        // FIXME: временная диагностика перенесена из Nitrogen.
        RequestLockEnterCount++;

        try
        {
            ModbusTransportStatus connectionStatus =
                await EnsureConnectedAsync(
                    cancellationToken);

            if (connectionStatus != ModbusTransportStatus.Ok)
            {
                return (
                    connectionStatus,
                    default);
            }

            IModbusMaster? master = _master;

            if (master is null)
            {
                CloseConnection();

                return (
                    ModbusTransportStatus.Faulted,
                    default);
            }

            (ModbusTransportStatus status, TResult? result) =
                await ExecuteRequestWithDiagnosticsAsync(
                    () => request(master));

            if (status != ModbusTransportStatus.Ok)
            {
                // Следующий запрос должен работать уже с новой сессией.
                CloseConnection();

                return (
                    status,
                    default);
            }

            return (
                ModbusTransportStatus.Ok,
                result);
        }
        finally
        {
            _requestLock.Release();
        }
    }

    /// <summary>
    /// Выполняет Modbus-запрос без результата через общий
    /// диагностический механизм контроля длительности операции.
    /// </summary>
    private async Task<ModbusTransportStatus>
        ExecuteRequestWithDiagnosticsAsync(
            Func<Task> request)
    {
        (ModbusTransportStatus status, bool? _) =
            await ExecuteRequestWithDiagnosticsAsync(
                async () =>
                {
                    await request();

                    return true;
                });

        return status;
    }


    /// <summary>
    /// Выполняет Modbus-запрос с независимым ограничением
    /// времени выполнения через RequestTimeoutMs.
    ///
    /// При превышении таймаута текущая TCP-сессия закрывается.
    /// Старому NModbus Task предоставляется ограниченное время
    /// для завершения после освобождения сетевых ресурсов.
    ///
    /// Если Task не завершился, ожидание прекращается.
    /// Его поздний результат игнорируется, а исключение наблюдается
    /// без воздействия на новую транспортную сессию.
    ///
    /// Метод не гарантирует физического завершения зависшего Task.
    /// Сетевые ошибки возвращаются через ModbusTransportStatus.
    /// </summary>

    private async Task<(ModbusTransportStatus Status, TResult? Result)>
        ExecuteRequestWithDiagnosticsAsync<TResult>(
            Func<Task<TResult>> request)
    {
        RequestStartedCount++;

        RequestInProgress = true;
        RequestIsHung = false;

        CurrentRequestStartedAt =
            DateTime.Now;

        CurrentRequestMs = 0;

        Stopwatch stopwatch =
            Stopwatch.StartNew();

        try
        {
            Task<TResult> requestTask;

            try
            {
                requestTask = request();
            }
            catch (Exception)
            {
                stopwatch.Stop();
                UpdateRequestDuration(
                    stopwatch.ElapsedMilliseconds);

                return (
                    ModbusTransportStatus.RequestFailed,
                    default);
            }

            Task timeoutTask =
                Task.Delay(
                    _config.RequestTimeoutMs);

            Task completedTask =
                await Task.WhenAny(
                    requestTask,
                    timeoutTask);


            if (completedTask != requestTask)
            {
                stopwatch.Stop();

                UpdateRequestDuration(
                    stopwatch.ElapsedMilliseconds);

                HungRequestCount++;
                RequestIsHung = true;

                // Повреждённая TCP-сессия больше не используется.
                // Следующий запрос создаст новый TcpClient и IModbusMaster
                // через существующий механизм reconnect.
                //
                // Старый NModbus Task использует захваченный ранее master
                // и не получает ссылку на новую сессию.
                CloseConnection();

                // После закрытия сокета даём NModbus короткое время
                // на физическое завершение текущей операции.
                //
                // FIXME: Временное диагностическое ожидание 250 мс.
                // Уточнить по результатам испытаний зависших запросов.
                const int shutdownGraceMs = 250;

                Task shutdownTask = await Task.WhenAny(
                    requestTask,
                    Task.Delay(shutdownGraceMs));

                if (shutdownTask == requestTask)
                {
                    // Наблюдаем возможное исключение старого Task.
                    // Оно не должно пробрасываться выше Transport.
                    await ((Task)requestTask).ConfigureAwait(
                        ConfigureAwaitOptions.SuppressThrowing);

                    RecoveredRequestCount++;
                }
                else
                {
                    // NModbus Task не завершился после закрытия TCP.
                    // Не блокируем requestLock и последующий reconnect.
                    //
                    // Поздний результат не публикуется и не влияет
                    // на состояние новой транспортной сессии.
                    //
                    // Continuation наблюдает исключение без создания
                    // отдельного фонового потока через Task.Run.
                    _ = requestTask.ContinueWith(
                        static task =>
                        {
                            // TODO: Подключить централизованное
                            // логирование поздней ошибки NModbus.

                            _ = task.Exception;
                        },
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted |
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }

                return (
                    ModbusTransportStatus.RequestTimeout,
                    default);
            }


            try
            {
                TResult result =
                    await requestTask;

                stopwatch.Stop();

                UpdateRequestDuration(
                    stopwatch.ElapsedMilliseconds);

                LastSuccessTime =
                    DateTime.Now;

                return (
                    ModbusTransportStatus.Ok,
                    result);
            }
            catch (OperationCanceledException)
            {
                stopwatch.Stop();

                UpdateRequestDuration(
                    stopwatch.ElapsedMilliseconds);

                return (
                    ModbusTransportStatus.Cancelled,
                    default);
            }
            catch (SocketException)
            {
                stopwatch.Stop();

                UpdateRequestDuration(
                    stopwatch.ElapsedMilliseconds);

                return (
                    ModbusTransportStatus.Disconnected,
                    default);
            }
            catch (IOException)
            {
                stopwatch.Stop();

                UpdateRequestDuration(
                    stopwatch.ElapsedMilliseconds);

                return (
                    ModbusTransportStatus.Disconnected,
                    default);
            }
            catch (TimeoutException)
            {
                stopwatch.Stop();

                UpdateRequestDuration(
                    stopwatch.ElapsedMilliseconds);

                return (
                    ModbusTransportStatus.RequestTimeout,
                    default);
            }
            catch (Exception)
            {
                stopwatch.Stop();

                UpdateRequestDuration(
                    stopwatch.ElapsedMilliseconds);

                return (
                    ModbusTransportStatus.RequestFailed,
                    default);
            }
        }
        finally
        {
            // FIXME: Временная диагностика.
            // RequestCompletedCount учитывает завершение
            // управляющей операции, а не обязательно физическое
            // завершение старого NModbus Task.
            //
            // Удалить временные счётчики после испытаний
            // механизма timeout/reconnect.
            RequestCompletedCount++;
            RequestInProgress = false;
            RequestIsHung = false;
            CurrentRequestStartedAt = null;
        }
    }

    /// <summary>
    /// Обновляет временную диагностику длительности запроса
    /// в одном месте для успешных и неуспешных операций.
    /// </summary>
    private void UpdateRequestDuration(
        long elapsedMilliseconds)
    {
        CurrentRequestMs =
            elapsedMilliseconds;

        LastRequestMs =
            elapsedMilliseconds;

        MaxRequestMs =
            Math.Max(
                MaxRequestMs,
                LastRequestMs);
    }

    /// <summary>
    /// Закрывает текущую Modbus TCP-сессию.
    ///
    /// Метод не пробрасывает ошибки Dispose наружу:
    /// после его вызова состояние менеджера всегда считается
    /// Disconnected независимо от поведения уничтожаемых объектов.
    /// </summary>
    private void CloseConnection()
    {
        IModbusMaster? master = _master;
        TcpClient? tcpClient = _tcpClient;

        _master = null;
        _tcpClient = null;

        try
        {
            master?.Dispose();
        }
        catch (Exception)
        {
            // TODO: Подключить централизованное логирование
            // ошибки освобождения ресурса.

            // Cleanup не должен нарушать
            // детерминированное состояние Transport.
        }

        try
        {
            tcpClient?.Dispose();
        }
        catch (Exception)
        {
            // TODO: Подключить централизованное логирование
            // ошибки освобождения ресурса.

            // Cleanup не должен нарушать
            // детерминированное состояние Transport.
        }
    }


    /// <summary>
    /// Однократно освобождает ресурсы Modbus TCP-сессии.
    ///
    /// Вызывается владельцем только после завершения
    /// всех операций Dispatcher.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        CloseConnection();

        _connectionLock.Dispose();
        _requestLock.Dispose();
    }

}
