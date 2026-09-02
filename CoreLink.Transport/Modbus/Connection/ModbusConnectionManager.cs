using System.Diagnostics;
using System.Net.Sockets;
using NModbus;
using CoreLink.Transport.Modbus.Configuration;

namespace CoreLink.Transport.Modbus.Connection;

/// <summary>
/// Владеет одной Modbus TCP-сессией.
///
/// Менеджер управляет одним TcpClient и одним IModbusMaster,
/// используемыми всеми операциями чтения и записи текущей сессии.
///
/// Подключение восстанавливается по необходимости.
/// Все Modbus-запросы выполняются последовательно через request lock.
/// </summary>
internal sealed class ModbusConnectionManager : IDisposable
{
    private readonly ModbusConnectionConfig _config;

    /// <summary>
    /// Сериализует создание и восстановление TCP-сессии.
    ///
    /// Если несколько операций одновременно обнаружили отсутствие
    /// соединения, реально выполнять connect должен только один поток.
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
    ///
    /// Создаётся при успешном подключении и уничтожается
    /// при сетевой ошибке, timeout или закрытии менеджера.
    /// </summary>
    private TcpClient? _tcpClient;

    /// <summary>
    /// Текущий NModbus master.
    ///
    /// Его lifetime совпадает с lifetime текущего TcpClient.
    /// </summary>
    private IModbusMaster? _master;

    // FIXME: временная диагностика перенесена из Nitrogen.
    // После стабилизации CoreLink.Transport определить постоянный
    // диагностический контракт и удалить временные счётчики.

    public long RequestLockWaitCount { get; private set; }
    public long RequestLockEnterCount { get; private set; }
    public long RequestStartedCount { get; private set; }
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
    /// Конструктор только сохраняет конфигурацию.
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
    /// Запрос выполняется через общую TCP-сессию и сериализуется
    /// относительно остальных операций чтения и записи.
    ///
    /// Ошибка соединения или Modbus-запроса передаётся
    /// вызывающей стороне и не маскируется пустым массивом.
    /// </summary>
    public async Task<ushort[]> ReadInputRegistersAsync(
        byte slaveId,
        ushort startAddress,
        ushort count,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteRequestAsync(
            master => master.ReadInputRegistersAsync(
                slaveId,
                startAddress,
                count),
            cancellationToken);
    }

    /// <summary>
    /// Записывает один Holding Register.
    ///
    /// Использует ту же TCP-сессию и тот же IModbusMaster,
    /// что и операции polling/read.
    ///
    /// Ошибка соединения или записи передаётся вызывающей стороне.
    /// </summary>
    public async Task WriteSingleRegisterAsync(
        byte slaveId,
        ushort address,
        ushort value,
        CancellationToken cancellationToken = default)
    {
        await ExecuteRequestAsync(
            master => master.WriteSingleRegisterAsync(
                slaveId,
                address,
                value),
            cancellationToken);
    }

    /// <summary>
    /// Записывает последовательный блок Holding Registers.
    ///
    /// Весь Modbus-запрос выполняется атомарно относительно
    /// остальных операций текущей TCP-сессии через request lock.
    ///
    /// Ошибка соединения или записи передаётся вызывающей стороне.
    /// </summary>
    public async Task WriteMultipleRegistersAsync(
        byte slaveId,
        ushort startAddress,
        ushort[] values,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);

        await ExecuteRequestAsync(
            master => master.WriteMultipleRegistersAsync(
                slaveId,
                startAddress,
                values),
            cancellationToken);
    }

    /// <summary>
    /// Гарантирует наличие активной Modbus TCP-сессии.
    ///
    /// Если соединения нет, выполняется попытка подключения.
    /// Параллельные попытки connect сериализуются через connection lock.
    ///
    /// Частота повторных подключений ограничивается ReconnectDelayMs,
    /// чтобы при недоступном ПЛК не создавать reconnect loop.
    /// </summary>
    private async Task<bool> EnsureConnectedAsync(
        CancellationToken cancellationToken)
    {
        if (_master is not null &&
            _tcpClient?.Connected == true)
        {
            return true;
        }

        await _connectionLock.WaitAsync(
            cancellationToken);

        try
        {
            // Повторная проверка обязательна после входа в lock:
            // другой поток мог уже восстановить соединение.
            if (_master is not null &&
                _tcpClient?.Connected == true)
            {
                return true;
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
                return false;
            }

            _lastConnectAttemptUtc =
                DateTime.UtcNow;

            // Старая сессия перед новым connect больше
            // не должна использоваться.
            CloseConnection();

            TcpClient tcpClient = new();

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
            catch
            {
                tcpClient.Dispose();

                return false;
            }

            ModbusFactory factory = new();

            _tcpClient = tcpClient;
            _master =
                factory.CreateMaster(
                    tcpClient);

            return true;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <summary>
    /// Выполняет Modbus-запрос, не возвращающий значение.
    ///
    /// Перед запросом гарантирует наличие соединения,
    /// затем получает exclusive-доступ к текущему IModbusMaster.
    ///
    /// Ошибка не маскируется успешным завершением Task.
    /// При ошибке текущая TCP-сессия закрывается, а исключение
    /// передаётся вызывающей стороне.
    /// </summary>
    private async Task ExecuteRequestAsync(
        Func<IModbusMaster, Task> request,
        CancellationToken cancellationToken)
    {
        bool connected =
            await EnsureConnectedAsync(
                cancellationToken);

        if (!connected || _master is null)
        {
            throw new IOException(
                $"Modbus TCP connection unavailable: " +
                $"{_config.Host}:{_config.Port}.");
        }

        // FIXME: временная диагностика перенесена из Nitrogen.
        RequestLockWaitCount++;

        await _requestLock.WaitAsync(
            cancellationToken);

        // FIXME: временная диагностика перенесена из Nitrogen.
        RequestLockEnterCount++;

        try
        {
            await ExecuteRequestWithDiagnosticsAsync(
                () => request(_master));
        }
        catch
        {
            // После ошибки текущая TCP-сессия больше
            // не считается пригодной для следующих запросов.
            CloseConnection();

            throw;
        }
        finally
        {
            _requestLock.Release();
        }
    }

    /// <summary>
    /// Выполняет Modbus-запрос, возвращающий значение.
    ///
    /// Перед запросом гарантирует наличие соединения.
    /// Все запросы текущей сессии сериализуются через request lock.
    ///
    /// Ошибка не преобразуется в null/default.
    /// При ошибке текущая TCP-сессия закрывается, а исключение
    /// передаётся вызывающей стороне.
    /// </summary>
    private async Task<TResult> ExecuteRequestAsync<TResult>(
        Func<IModbusMaster, Task<TResult>> request,
        CancellationToken cancellationToken)
    {
        bool connected =
            await EnsureConnectedAsync(
                cancellationToken);

        if (!connected || _master is null)
        {
            throw new IOException(
                $"Modbus TCP connection unavailable: " +
                $"{_config.Host}:{_config.Port}.");
        }

        // FIXME: временная диагностика перенесена из Nitrogen.
        RequestLockWaitCount++;

        await _requestLock.WaitAsync(
            cancellationToken);

        // FIXME: временная диагностика перенесена из Nitrogen.
        RequestLockEnterCount++;

        try
        {
            return await ExecuteRequestWithDiagnosticsAsync(
                () => request(_master));
        }
        catch
        {
            // После ошибки текущая TCP-сессия больше
            // не считается пригодной для следующих запросов.
            CloseConnection();

            throw;
        }
        finally
        {
            _requestLock.Release();
        }
    }

    /// <summary>
    /// Выполняет Modbus-запрос без результата через временный
    /// диагностический механизм контроля длительности операции.
    /// </summary>
    private async Task ExecuteRequestWithDiagnosticsAsync(
        Func<Task> request)
    {
        await ExecuteRequestWithDiagnosticsAsync(
            async () =>
            {
                await request();

                return true;
            });
    }

    /// <summary>
    /// Выполняет Modbus-запрос и контролирует его максимальную
    /// продолжительность через RequestTimeoutMs.
    ///
    /// Механизм перенесён из Nitrogen для сохранения проверенного
    /// поведения транспорта на первом этапе миграции.
    ///
    /// При превышении RequestTimeoutMs запрос считается зависшим
    /// и вызывающая сторона получает TimeoutException.
    ///
    /// FIXME: после завершения переноса отдельно пересмотреть
    /// диагностику и постоянный timeout-контракт CoreLink.Transport.
    /// </summary>
    private async Task<TResult>
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
            Task<TResult> requestTask =
                request();

            Task timeoutTask =
                Task.Delay(
                    _config.RequestTimeoutMs);

            Task completedTask =
                await Task.WhenAny(
                    requestTask,
                    timeoutTask);

            if (completedTask != requestTask)
            {
                CurrentRequestMs =
                    stopwatch.ElapsedMilliseconds;

                LastRequestMs =
                    stopwatch.ElapsedMilliseconds;

                MaxRequestMs =
                    Math.Max(
                        MaxRequestMs,
                        LastRequestMs);

                HungRequestCount++;
                RequestIsHung = true;

                throw new TimeoutException(
                    $"Modbus request timeout after " +
                    $"{_config.RequestTimeoutMs} ms.");
            }

            TResult result =
                await requestTask;

            stopwatch.Stop();

            CurrentRequestMs =
                stopwatch.ElapsedMilliseconds;

            LastRequestMs =
                stopwatch.ElapsedMilliseconds;

            MaxRequestMs =
                Math.Max(
                    MaxRequestMs,
                    LastRequestMs);

            LastSuccessTime =
                DateTime.Now;

            RequestCompletedCount++;

            return result;
        }
        finally
        {
            RequestInProgress = false;
            RequestIsHung = false;
            CurrentRequestStartedAt = null;
        }
    }

    /// <summary>
    /// Закрывает текущую Modbus TCP-сессию.
    ///
    /// IModbusMaster и TcpClient уничтожаются вместе,
    /// поскольку относятся к одной физической TCP-сессии.
    ///
    /// После закрытия следующий запрос сможет инициировать reconnect.
    /// </summary>
    private void CloseConnection()
    {
        _master?.Dispose();
        _master = null;

        _tcpClient?.Dispose();
        _tcpClient = null;
    }

    /// <summary>
    /// Освобождает сетевые ресурсы и примитивы
    /// синхронизации текущего менеджера.
    ///
    /// После Dispose экземпляр больше не должен использоваться.
    /// </summary>
    public void Dispose()
    {
        CloseConnection();

        _connectionLock.Dispose();
        _requestLock.Dispose();
    }
}