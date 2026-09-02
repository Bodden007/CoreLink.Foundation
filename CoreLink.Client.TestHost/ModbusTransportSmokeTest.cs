using CoreLink.Transport.Modbus;
using CoreLink.Transport.Modbus.Configuration;

namespace CoreLink.Client.TestHost;

/// <summary>
/// Выполняет ручную smoke-проверку Modbus transport.
///
/// Тест проверяет реальное соединение с PLC:
/// постоянный polling, high-priority write и low-priority single read.
///
/// FIXME:
/// Временный ручной тест. После стабилизации transport слоя
/// заменить автоматизированными integration tests.
/// </summary>
internal static class ModbusTransportSmokeTest
{
    /// <summary>
    /// Запускает transport и позволяет вручную вызвать
    /// write и single read во время постоянного polling.
    /// </summary>
    public static async Task RunAsync()
    {
        ModbusConnectionConfig config = new()
        {
            Host = "192.168.0.10",
            Port = 502,
            SlaveId = 255,

            ConnectTimeoutMs = 3000,
            RequestTimeoutMs = 1000,
            ReconnectDelayMs = 1000,

            PollIntervalMs = 500,

            InputStartAddress = 8,
            InputRegisterCount = 2
        };

        await using ModbusTransportSession transport =
       new(config);

        long pollCount = 0;

        transport.RegistersReceived += registers =>
        {
            long currentPoll =
                Interlocked.Increment(
                    ref pollCount);

            Console.WriteLine(
                $"POLL #{currentPoll} " +
                $"{DateTime.Now:HH:mm:ss.fff} " +
                $"[{string.Join(", ", registers)}]");
        };

        transport.TransportError += exception =>
        {
            Console.WriteLine(
                $"POLL ERROR   " +
                $"{DateTime.Now:HH:mm:ss.fff} " +
                $"{exception.GetType().Name}: " +
                $"{exception.Message}");
        };

        Console.WriteLine();
        Console.WriteLine("=== MODBUS TRANSPORT SMOKE TEST ===");
        Console.WriteLine(
            $"{config.Host}:{config.Port} " +
            $"Slave={config.SlaveId}");

        Console.WriteLine(
            $"Polling: start={config.InputStartAddress}, " +
            $"count={config.InputRegisterCount}, " +
            $"interval={config.PollIntervalMs} ms");

        transport.Start();

        Console.WriteLine();
        Console.WriteLine("Transport started.");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  W - WriteSingleRegister");
        Console.WriteLine("  R - SingleRead");
        Console.WriteLine("  Q - Stop");
        Console.WriteLine();

        bool running = true;

        while (running)
        {
            ConsoleKeyInfo key =
                Console.ReadKey(
                    intercept: true);

            switch (key.Key)
            {
                case ConsoleKey.W:
                    await ExecuteWriteAsync(
                        transport);
                    break;

                case ConsoleKey.R:
                    await ExecuteSingleReadAsync(
                        transport);
                    break;

                case ConsoleKey.Q:
                    running = false;
                    break;
            }
        }

        await transport.StopAsync();

        Console.WriteLine();
        Console.WriteLine("Transport stopped.");
        Console.WriteLine(
            $"Polling frames received: {pollCount}");
    }

    /// <summary>
    /// Выполняет тестовую запись одного Holding Register.
    ///
    /// ВАЖНО:
    /// Адрес и значение должны быть заменены на безопасный
    /// тестовый регистр конкретного PLC перед запуском команды W.
    /// </summary>
    private static async Task ExecuteWriteAsync(
        ModbusTransportSession transport)
    {
        // FIXME:
        // Установить безопасный Holding Register для текущего PLC.
        const ushort address = 2;
        const ushort value = 1;

        DateTime started =
            DateTime.Now;

        Console.WriteLine(
            $"WRITE QUEUED " +
            $"{started:HH:mm:ss.fff} " +
            $"address={address} value={value}");

        try
        {
            await transport.WriteSingleRegisterAsync(
                address,
                value);

            Console.WriteLine(
                $"WRITE DONE   " +
                $"{DateTime.Now:HH:mm:ss.fff}");
        }
        catch (Exception exception)
        {
            Console.WriteLine(
                $"WRITE FAILED " +
                $"{DateTime.Now:HH:mm:ss.fff} " +
                $"{exception.GetType().Name}: " +
                $"{exception.Message}");
        }
    }

    /// <summary>
    /// Выполняет одиночное чтение Input Registers.
    ///
    /// Single read проходит через тот же dispatcher,
    /// но имеет меньший приоритет, чем штатный polling.
    /// </summary>
    private static async Task ExecuteSingleReadAsync(
    ModbusTransportSession transport)
    {
        const ushort startAddress = 8;
        const ushort count = 2;

        DateTime started =
            DateTime.Now;

        Console.WriteLine(
            $"SINGLE QUEUED " +
            $"{started:HH:mm:ss.fff} " +
            $"start={startAddress} count={count}");

        try
        {
            ushort[] registers =
                await transport.ReadSingleAsync(
                    startAddress,
                    count);

            Console.WriteLine(
                $"SINGLE DONE   " +
                $"{DateTime.Now:HH:mm:ss.fff} " +
                $"[{string.Join(", ", registers)}]");
        }
        catch (Exception exception)
        {
            Console.WriteLine(
                $"SINGLE FAILED " +
                $"{DateTime.Now:HH:mm:ss.fff} " +
                $"{exception.GetType().Name}: " +
                $"{exception.Message}");
        }
    }
}