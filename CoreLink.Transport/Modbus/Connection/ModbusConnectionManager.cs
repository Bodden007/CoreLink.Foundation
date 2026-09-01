using System.Net.Sockets;
using NModbus;
using CoreLink.Transport.Modbus.Configuration;

namespace CoreLink.Transport.Modbus.Connection;

/// <summary>
/// Владеет одной Modbus TCP-сессией.
///
/// Класс отвечает только за жизненный цикл TCP-соединения
/// и экземпляра IModbusMaster.
/// </summary>
internal sealed class ModbusConnectionManager : IDisposable
{
    private readonly ModbusConnectionConfig _config;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);

    private DateTime _lastConnectAttemptUtc = DateTime.MinValue;

    private TcpClient? _tcpClient;
    private IModbusMaster? _master;

    /// <summary>
    /// Создаёт менеджер одной Modbus TCP-сессии.
    /// Само соединение в конструкторе не открывается.
    /// </summary>
    public ModbusConnectionManager(ModbusConnectionConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// Гарантирует наличие активной Modbus TCP-сессии.
    ///
    /// Повторные попытки подключения ограничиваются ReconnectDelayMs,
    /// чтобы при недоступном ПЛК не создавать непрерывный цикл connect.
    /// </summary>
    private async Task<bool> EnsureConnectedAsync(
        CancellationToken cancellationToken)
    {
        if (_master is not null && _tcpClient?.Connected == true)
            return true;

        await _connectionLock.WaitAsync(cancellationToken);

        try
        {
            if (_master is not null && _tcpClient?.Connected == true)
                return true;

            TimeSpan reconnectDelay =
                TimeSpan.FromMilliseconds(_config.ReconnectDelayMs);

            TimeSpan elapsed =
                DateTime.UtcNow - _lastConnectAttemptUtc;

            if (_lastConnectAttemptUtc != DateTime.MinValue &&
                elapsed < reconnectDelay)
            {
                return false;
            }

            _lastConnectAttemptUtc = DateTime.UtcNow;

            CloseConnection();

            TcpClient tcpClient = new();

            using CancellationTokenSource timeoutCts =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);

            timeoutCts.CancelAfter(_config.ConnectTimeoutMs);

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
            _master = factory.CreateMaster(tcpClient);

            return true;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <summary>
    /// Закрывает текущую Modbus TCP-сессию.
    ///
    /// После вызова следующий запрос сможет инициировать reconnect.
    /// </summary>
    private void CloseConnection()
    {
        _master?.Dispose();
        _master = null;

        _tcpClient?.Dispose();
        _tcpClient = null;
    }

    /// <summary>
    /// Освобождает сетевые ресурсы менеджера.
    /// </summary>
    public void Dispose()
    {
        CloseConnection();

        _connectionLock.Dispose();
    }
}