using CoreLink.Transport.Modbus;
using CoreLink.Transport.Modbus.Configuration;
using CoreLink.Transport.Modbus.Results;

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
        // Отдельная локальная конфигурация намеренно хранится прямо в smoke test:
        // здесь проверяется фактический runtime-path транспорта, а не загрузка карты.
        ModbusConnectionConfig config = new()
        {
            Host = "127.0.0.1",
            Port = 1502,
            SlaveId = 255,

            // Таймауты оставлены короткими, чтобы ручная проверка быстро показывала
            // отсутствие PLC/эмулятора и не создавала впечатление зависшего процесса.
            ConnectTimeoutMs = 3000,
            RequestTimeoutMs = 1000,
            ReconnectDelayMs = 1000,

            // 500 ms соответствует текущему базовому циклу polling проекта.
            PollIntervalMs = 500,

            // Читаем минимальный диапазон: для smoke test важнее жизненный цикл
            // сессии и диспетчеризация запросов, чем полнота технологической карты.
            InputStartAddress = 8,
            InputRegisterCount = 2
        };

        await using ModbusTransportSession transport =
            new(config);

        // Счетчик нужен не для логики транспорта, а для визуальной проверки:
        // polling продолжает работать до и после ручных read/write команд.
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

        // Статус выводится отдельно от данных, чтобы при обрыве связи было видно:
        // прекратились ли кадры из-за PLC или из-за остановки самого теста.
        transport.TransportStatusChanged += status =>
        {
            Console.WriteLine(
                $"POLL STATUS  " +
                $"{DateTime.Now:HH:mm:ss.fff} " +
                $"{status}");
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

        // Start вызывается до чтения клавиатуры: ручные команды должны попадать
        // в уже работающую сессию и конкурировать именно со штатным polling.
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
            // Управление намеренно синхронное с консоли: так легче воспроизводить
            // последовательность действий при диагностике приоритетов dispatcher.
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

        // Явно проверяем штатный Stop, хотя await using дополнительно страхует Dispose.
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

        // Время постановки команды позволяет глазами оценить задержку write
        // относительно фонового polling без отдельного профилировщика.
        DateTime started =
            DateTime.Now;

        Console.WriteLine(
            $"WRITE QUEUED " +
            $"{started:HH:mm:ss.fff} " +
            $"address={address} value={value}");

        // Важна именно запись через публичную сессию, а не прямой вызов manager:
        // smoke test должен проходить тот же путь, что и будущий Client.
        ModbusWriteResult result =
            await transport.WriteSingleRegisterAsync(
                address,
                value);

        if (result.Ok)
        {
            Console.WriteLine(
                $"WRITE DONE   " +
                $"{DateTime.Now:HH:mm:ss.fff}");
        }
        else
        {
            Console.WriteLine(
                $"WRITE FAILED " +
                $"{DateTime.Now:HH:mm:ss.fff} " +
                $"status={result.Status}");
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

        // Метка времени нужна для ручной проверки: single read не должен
        // нарушать последовательность polling и не должен зависать бесконечно.
        DateTime started =
            DateTime.Now;

        Console.WriteLine(
            $"SINGLE QUEUED " +
            $"{started:HH:mm:ss.fff} " +
            $"start={startAddress} count={count}");

        // Запрос выполняется через ту же transport session, чтобы проверить
        // реальную arbitration policy dispatcher, а не isolated read API.
        ModbusReadResult result =
            await transport.ReadSingleAsync(
                startAddress,
                count);

        if (result.Ok &&
            result.Data is not null)
        {
            Console.WriteLine(
                $"SINGLE DONE   " +
                $"{DateTime.Now:HH:mm:ss.fff} " +
                $"[{string.Join(", ", result.Data)}]");
        }
        else
        {
            Console.WriteLine(
                $"SINGLE FAILED " +
                $"{DateTime.Now:HH:mm:ss.fff} " +
                $"status={result.Status}");
        }
    }
}
