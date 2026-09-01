using System.Net.Sockets;
using NModbus;
using NModbus.Utility;

internal sealed class NModbusAotSmokeTest : IDisposable
{
    private readonly string _host;
    private readonly int _port;

    private TcpClient? _tcpClient;
    private IModbusMaster? _master;

    public NModbusAotSmokeTest(string host, int port)
    {
        _host = host;
        _port = port;
    }

    /// <summary>
    /// Создаёт TCP-соединение и Modbus master.
    ///
    /// Этот метод нужен для AOT smoke test:
    /// он заставляет NativeAOT включить реальный путь
    /// TcpClient -> ModbusFactory -> IModbusMaster.
    ///
    /// Таймаут ограничен, потому что недоступный ПЛК
    /// не должен зависать бесконечно.
    /// </summary>
    public async Task<bool> ConnectAsync(
        int timeoutMs,
        CancellationToken cancellationToken = default)
    {
        DisposeConnection();

        try
        {
            TcpClient tcpClient = new();

            using CancellationTokenSource timeoutCts =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);

            timeoutCts.CancelAfter(timeoutMs);

            await tcpClient.ConnectAsync(
                _host,
                _port,
                timeoutCts.Token);

            ModbusFactory factory = new();

            _tcpClient = tcpClient;
            _master = factory.CreateMaster(tcpClient);

            return true;
        }
        catch (OperationCanceledException)
        {
            DisposeConnection();
            return false;
        }
        catch (SocketException)
        {
            DisposeConnection();
            return false;
        }
    }

    /// <summary>
    /// Проверяет FC04 — чтение Input Registers.
    ///
    /// Именно этот вызов используется polling-слоем Nitrogen,
    /// поэтому он обязан реально попасть в NativeAOT executable.
    /// </summary>
    public async Task<ushort[]> ReadInputRegistersAsync(
        byte slaveId,
        ushort startAddress,
        ushort count)
    {
        if (_master is null)
            return Array.Empty<ushort>();

        try
        {
            return await _master.ReadInputRegistersAsync(
                slaveId,
                startAddress,
                count);
        }
        catch
        {
            // FIXME: общий catch нужен только для AOT smoke test.
            // Удалить после завершения проверки NModbus.
            DisposeConnection();

            return Array.Empty<ushort>();
        }
    }

    /// <summary>
    /// Проверяет FC06 — запись одного Holding Register.
    ///
    /// Используется для одиночных команд ПЛК:
    /// reset, transmission command и подобных управляющих значений.
    /// </summary>
    public async Task<bool> WriteSingleRegisterAsync(
        byte slaveId,
        ushort address,
        ushort value)
    {
        if (_master is null)
            return false;

        try
        {
            await _master.WriteSingleRegisterAsync(
                slaveId,
                address,
                value);

            return true;
        }
        catch
        {
            // FIXME: общий catch нужен только для AOT smoke test.
            // Удалить после завершения проверки NModbus.
            DisposeConnection();

            return false;
        }
    }

    /// <summary>
    /// Проверяет FC16 — запись нескольких Holding Registers.
    ///
    /// Используется, когда одно логическое значение занимает
    /// несколько Modbus WORD, например IEEE-754 Float32.
    /// </summary>
    public async Task<bool> WriteMultipleRegistersAsync(
        byte slaveId,
        ushort startAddress,
        ushort[] values)
    {
        if (_master is null)
            return false;

        try
        {
            await _master.WriteMultipleRegistersAsync(
                slaveId,
                startAddress,
                values);

            return true;
        }
        catch
        {
            // FIXME: общий catch нужен только для AOT smoke test.
            // Удалить после завершения проверки NModbus.
            DisposeConnection();

            return false;
        }
    }

    /// <summary>
    /// Преобразует два Modbus WORD в IEEE-754 Float32.
    ///
    /// В нашем текущем порядке регистров:
    /// register[1] = high word,
    /// register[0] = low word.
    ///
    /// Используем встроенный NModbus ModbusUtility.GetSingle,
    /// чтобы не держать собственную реализацию IEEE-754.
    /// </summary>
    public static float RegistersToSingle(
        ushort lowWord,
        ushort highWord)
    {
        return ModbusUtility.GetSingle(
            highWord,
            lowWord);
    }

    private void DisposeConnection()
    {
        _master?.Dispose();
        _master = null;

        _tcpClient?.Dispose();
        _tcpClient = null;
    }

    public void Dispose()
    {
        DisposeConnection();
    }
}