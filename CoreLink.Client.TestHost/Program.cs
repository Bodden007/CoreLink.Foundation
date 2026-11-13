
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reactive.Linq;
using CoreLink.Adapter.CSharp;
using CoreLink.Client;
using CoreLink.Client.Results;
using CoreLink.Contracts.Core;
using CoreLink.Contracts.Maps;
using CoreLink.Contracts.Registers;
using NModbus;

// Integration stress test: CoreLink polling + periodic CoreLink ReadAsync
// + independent test-only Modbus FC03/FC04/FC06/FC16 client.
// WARNING: write operations are intentionally restricted to loopback.

const int durationSeconds = 60;
const int snapshotPeriodMs = 500;
const int readPeriodMs = 700;
const int fc04ReadPeriodMs = 1100;
const int fc03ReadPeriodMs = 1300;
const int writePeriodMs = 2100;
const int ioTimeoutMs = 1500;

Trace.Listeners.Add(new TextWriterTraceListener(Console.Error));
Trace.AutoFlush = true;

// TestHost owns its configuration.
// CoreLink receives the completed map through Start.
var map = new CoreLinkMap
{
    IpAddress = "127.0.0.1",
    Port = 1502,
    UnitId = 1,

    PollFunctionCode = 3,
    PollIntervalMs = 500,
    RxIntervalMs = 500,

    ConnectTimeoutMs = 3000,
    RequestTimeoutMs = 1000,
    ReconnectDelayMs = 1000,

    RegisterOrder = RegisterOrder.ABCD,

    SupportedUnitSystemIds = [1, 2],

    Registers =
    [
        new("Status", 1, RegisterType.Word),
        new("Cutoff", 3, RegisterType.Word),
        new("Speed", 5, RegisterType.Float),

        new(
            "Pressure_1",
            7,
            RegisterType.Float,
            new Units(
                new UnitEntry(1, "psi"),
                new UnitEntry(2, "bar"))),

        new(
            "Pressure_2",
            9,
            RegisterType.Float,
            new Units(
                new UnitEntry(1, "psi"),
                new UnitEntry(2, "MPa"))),

        new("Pressure_3", 11, RegisterType.Float),
        new("Pressure_4", 13, RegisterType.Float),
        new("Pressure_5", 15, RegisterType.Float),

        new("Measurme", 0, RegisterType.Word)
    ]
};


if (args.Contains("--invalid-map"))
{
    CoreLinkMap CreateMap(
        ushort port,
        byte functionCode,
        PhysicalRegister[] registers)
    {
        return new CoreLinkMap
        {
            IpAddress = map.IpAddress,
            Port = port,
            UnitId = map.UnitId,

            PollFunctionCode = functionCode,
            PollIntervalMs = map.PollIntervalMs,
            RxIntervalMs = map.RxIntervalMs,

            ConnectTimeoutMs = map.ConnectTimeoutMs,
            RequestTimeoutMs = map.RequestTimeoutMs,
            ReconnectDelayMs = map.ReconnectDelayMs,

            RegisterOrder = map.RegisterOrder,
            Registers = registers,
            SupportedUnitSystemIds = map.SupportedUnitSystemIds
        };
    }

    bool RunInvalidCase(
        string name,
        CoreLinkMap invalidMap,
        string expectedError)
    {
        var testClient = new CoreLinkClient(
            new CSharpCoreAdapter());

        CoreLinkResult result = testClient.Start(invalidMap);

        bool passed =
            result == CoreLinkResult.BadConfig &&
            testClient.State ==
                CoreLink.Client.Lifecycle.CoreLinkState.Faulted &&
            testClient.LastError == expectedError &&
            testClient.CoreMap is null &&
            testClient.Values is null &&
            testClient.Statuses is null;

        Console.WriteLine();
        Console.WriteLine($"TEST: {name}");
        Console.WriteLine($"Result    : {result}");
        Console.WriteLine($"State     : {testClient.State}");
        Console.WriteLine($"LastError : {testClient.LastError}");
        Console.WriteLine(
            passed ? "PASSED" : "FAILED");

        return passed;
    }

    int passedCount = 0;
    const int totalTests = 4;

    // 1. Недопустимый порт.
    if (RunInvalidCase(
        "Invalid Port",
        CreateMap(0, 3, map.Registers),
        "Invalid Modbus port."))
    {
        passedCount++;
    }

    // 2. Перекрытие Float и Word.
    if (RunInvalidCase(
        "Overlapping Registers",
        CreateMap(
            1502,
            3,
            [
                new("Speed", 10, RegisterType.Float),
                new("Status", 11, RegisterType.Word)
            ]),
        "Overlapping register: Status, address 11."))
    {
        passedCount++;
    }

    // 3. Недопустимая Modbus-функция.
    if (RunInvalidCase(
        "Invalid Function Code",
        CreateMap(1502, 5, map.Registers),
        "Unsupported polling function code."))
    {
        passedCount++;
    }

    // 4. Диапазон 126 регистров вместо 125.
    if (RunInvalidCase(
        "Polling Range Overflow",
        CreateMap(
            1502,
            3,
            [
                new("First", 0, RegisterType.Word),
                new("Last", 125, RegisterType.Word)
            ]),
        "Modbus polling range exceeds 125 registers."))
    {
        passedCount++;
    }

    Console.WriteLine();
    Console.WriteLine(
        $"INVALID MAP TESTS: {passedCount}/{totalTests}");

    bool allPassed = passedCount == totalTests;

    Console.WriteLine(
        allPassed
            ? "ALL INVALID MAP TESTS PASSED"
            : "INVALID MAP TESTS FAILED");

    Environment.ExitCode = allPassed ? 0 : 1;
    return;
}


// No accidental writes to production PLCs.
if (!IPAddress.TryParse(map.IpAddress, out var ip) ||
    !IPAddress.IsLoopback(ip) ||
    map.IpAddress != "127.0.0.1" ||
    map.Port != 1502 ||
    map.UnitId != 1)
{
    Console.WriteLine(
        "SAFETY STOP: stress writer permits only " +
        "127.0.0.1:1502, UnitId=1.");

    return;
}

ushort port = map.Port;
byte unitId = map.UnitId;
int pollingMs = map.PollIntervalMs;
byte pollingFunctionCode = map.PollFunctionCode;

if (pollingFunctionCode is not (3 or 4))
{
    Console.WriteLine(
        $"INVALID POLLING FUNCTION CODE: {pollingFunctionCode}");

    return;
}


if (args.Contains("--client-write"))
{
    // Public CoreLink write API integration test.
    // Writes are permitted only to emulator HR20..23.
    // The endpoint safety check has already passed.

    var writeClient = new CoreLinkClient(
        new CSharpCoreAdapter());

    int passed = 0;
    const int total = 3;

    async Task<bool> VerifyAsync(
        string testName,
        ushort address,
        ushort[] expected,
        IModbusMaster master)
    {
        CoreWriteStatus status = writeClient.LastWriteStatus;

        ushort[] actual = await master.ReadHoldingRegistersAsync(
            unitId,
            address,
            (ushort)expected.Length);

        bool valuesMatch = actual.SequenceEqual(expected);

        bool ok =
            status == CoreWriteStatus.Ok &&
            valuesMatch;

        Console.WriteLine();
        Console.WriteLine($"TEST: {testName}");
        Console.WriteLine($"Status  : {status}");
        Console.WriteLine(
            $"Expected: [{string.Join(", ", expected)}]");
        Console.WriteLine(
            $"Actual  : [{string.Join(", ", actual)}]");
        Console.WriteLine(ok ? "PASSED" : "FAILED");

        return ok;
    }

    try
    {
        CoreLinkResult start = writeClient.Start(map);

        if (start != CoreLinkResult.Ok)
        {
            Console.WriteLine(
                $"CORE START FAILED: {start}");

            Console.WriteLine(writeClient.LastError);
            Environment.ExitCode = 1;
            return;
        }

        using var tcp = new TcpClient();

        using var connectCts =
            new CancellationTokenSource(
                TimeSpan.FromMilliseconds(ioTimeoutMs));

        await tcp.ConnectAsync(
            ip,
            port,
            connectCts.Token);

        using var master =
            new ModbusFactory().CreateMaster(tcp);

        // 1. Word -> FC06.
        await writeClient.WriteWordAsync(20, 1234);

        if (await VerifyAsync(
            "WriteWordAsync FC06",
            20,
            [1234],
            master))
        {
            passed++;
        }

        // 2. Float32 -> FC16.
        // ABCD: low word followed by high word.
        const float floatValue = 25.25f;

        await writeClient.WriteFloatAsync(
            21,
            floatValue);

        uint floatBits =
            BitConverter.SingleToUInt32Bits(floatValue);

        ushort[] floatRegisters =
        [
            (ushort)floatBits,
            (ushort)(floatBits >> 16)
        ];

        if (await VerifyAsync(
            "WriteFloatAsync FC16",
            21,
            floatRegisters,
            master))
        {
            passed++;
        }

        // 3. Batch: Float32 + Word -> one FC16.
        const float batchFloat = 34.5f;
        const ushort batchWord = 4321;

        await writeClient.WriteBatchAsync(
            21,
            [
                CoreWriteValue.Float(batchFloat),
                CoreWriteValue.Word(batchWord)
            ]);

        uint batchBits =
            BitConverter.SingleToUInt32Bits(batchFloat);

        ushort[] batchRegisters =
        [
            (ushort)batchBits,
            (ushort)(batchBits >> 16),
            batchWord
        ];

        if (await VerifyAsync(
            "WriteBatchAsync FC16",
            21,
            batchRegisters,
            master))
        {
            passed++;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine(
            $"CLIENT WRITE TEST EXCEPTION: {ex}");
    }
    finally
    {
        CoreLinkResult stop = await writeClient.StopAsync();

        Console.WriteLine();
        Console.WriteLine($"CORE STOP: {stop}");

        if (stop != CoreLinkResult.Ok)
            passed = -1;
    }

    Console.WriteLine();
    Console.WriteLine(
        $"CLIENT WRITE TESTS: {passed}/{total}");

    bool success = passed == total;

    Console.WriteLine(
        success
            ? "ALL CLIENT WRITE TESTS PASSED"
            : "CLIENT WRITE TESTS FAILED");

    Environment.ExitCode = success ? 0 : 1;
    return;
}


if (args.Contains("--client-write-cycle"))
{
    // Stress test: public write API during PLC failures.
    // Endpoint safety check has already passed.
    // Writes only Holding Registers 20..23.

    const int durationSecondsCycle = 110;
    const int delayMs = 700;

    var testClient = new CoreLinkClient(
        new CSharpCoreAdapter());

    int verifiedMask = 0;
    int cycleWriteOk = 0;
    int unavailable = 0;
    int unexpected = 0;
    int verified = 0;
    int verificationUnavailable = 0;

    bool failureObserved = false;
    bool recoveredAfterFailure = false;

    CoreLinkResult start = testClient.Start(map);

    if (start != CoreLinkResult.Ok)
    {
        Console.WriteLine(
            $"START FAILED: {start}, {testClient.LastError}");

        Environment.ExitCode = 1;
        return;
    }

    async Task<bool?> VerifyAsync(
        ushort address,
        ushort[] expected)
    {
        try
        {
            using var tcp = new TcpClient();

            using var deadline =
                new CancellationTokenSource(
                    TimeSpan.FromMilliseconds(ioTimeoutMs));

            await tcp.ConnectAsync(
                ip, port, deadline.Token);

            using var master =
                new ModbusFactory().CreateMaster(tcp);

            ushort[] actual =
                await master.ReadHoldingRegistersAsync(
                    unitId,
                    address,
                    (ushort)expected.Length)
                .WaitAsync(
                    TimeSpan.FromMilliseconds(ioTimeoutMs));

            bool matched = actual.SequenceEqual(expected);

            if (!matched)
            {
                Console.WriteLine(
                    $"VERIFY MISMATCH: expected " +
                    $"[{string.Join(',', expected)}], " +
                    $"actual [{string.Join(',', actual)}]");
            }

            return matched;
        }
        catch (Exception ex) when (
            ex is SocketException or
            IOException or
            TimeoutException or
            OperationCanceledException or
            ObjectDisposedException ||
            ex.GetType().Name.Contains(
                "SlaveException",
                StringComparison.Ordinal))
        {
            return null;
        }
    }

    CoreLinkResult cycleStopResult =
        CoreLinkResult.SessionError;

    var watch = Stopwatch.StartNew();

    try
    {
        int iteration = 0;

        while (watch.Elapsed.TotalSeconds <
               durationSecondsCycle)
        {
            iteration++;

            int operation = (iteration - 1) % 3;

            ushort word =
                (ushort)(1000 + iteration);

            float floatValue =
                25.25f + iteration;

            ushort[] expected;
            ushort address;
            string name;

            if (operation == 0)
            {
                name = "FC06 WORD";
                address = 20;
                expected = [word];

                await testClient.WriteWordAsync(
                    address, word);
            }
            else if (operation == 1)
            {
                name = "FC16 FLOAT";
                address = 21;

                uint bits =
                    BitConverter.SingleToUInt32Bits(
                        floatValue);

                expected =
                [
                    (ushort)bits,
                    (ushort)(bits >> 16)
                ];

                await testClient.WriteFloatAsync(
                    address, floatValue);
            }
            else
            {
                name = "FC16 BATCH";
                address = 21;

                uint bits =
                    BitConverter.SingleToUInt32Bits(
                        floatValue);

                expected =
                [
                    (ushort)bits,
                    (ushort)(bits >> 16),
                    word
                ];

                await testClient.WriteBatchAsync(
                    address,
                    [
                        CoreWriteValue.Float(floatValue),
                        CoreWriteValue.Word(word)
                    ]);
            }

            CoreWriteStatus status =
                testClient.LastWriteStatus;

            Console.WriteLine(
                $"[{watch.Elapsed:mm\\:ss}] " +
                $"{name}: {status}");

            if (status == CoreWriteStatus.Ok)
            {
                cycleWriteOk++;

                bool? result =
                    await VerifyAsync(
                        address, expected);

                if (result == true)
                {
                    verified++;

                    verifiedMask |= 1 << operation;

                    Console.WriteLine(
                        "  READBACK VERIFIED");

                    if (failureObserved)
                        recoveredAfterFailure = true;
                }
                else if (result == false)
                {
                    unexpected++;
                    Console.WriteLine(
                        "  READBACK FAILED");
                }
                else
                {
                    verificationUnavailable++;

                    Console.WriteLine(
                        "  READBACK UNAVAILABLE");
                }
            }
            else if (status is
                     CoreWriteStatus.Disconnected or
                     CoreWriteStatus.Timeout or
                     CoreWriteStatus.Failed)
            {
                unavailable++;
                failureObserved = true;
            }
            else
            {
                unexpected++;

                Console.WriteLine(
                    $"  UNEXPECTED STATUS: {status}");
            }

            await Task.Delay(delayMs);
        }
    }
    catch (Exception ex)
    {
        unexpected++;

        Console.WriteLine(
            $"TEST EXCEPTION: {ex}");
    }
    finally
    {
        cycleStopResult =
            await testClient.StopAsync();

        Console.WriteLine(
            $"CORE STOP: {cycleStopResult}");
    }

    bool passed =
        verifiedMask == 0b111 &&
        verified > 0 &&
        unavailable > 0 &&
        recoveredAfterFailure &&
        unexpected == 0 &&
        cycleStopResult == CoreLinkResult.Ok;

    Console.WriteLine();
    Console.WriteLine(
        "========== CLIENT WRITE CYCLE ==========");

    Console.WriteLine(
        $"Write OK              : {cycleWriteOk}");

    Console.WriteLine(
        $"Unavailable           : {unavailable}");

    Console.WriteLine(
        $"Readback verified     : {verified}");

    Console.WriteLine(
        $"Readback unavailable  : {verificationUnavailable}");

    Console.WriteLine(
        $"Unexpected            : {unexpected}");

    Console.WriteLine(
        $"Methods verified mask : {verifiedMask}/7");

    Console.WriteLine(
        $"Recovered after error : {recoveredAfterFailure}");

    Console.WriteLine(
        $"Stop                  : {cycleStopResult}");

    Console.WriteLine(
        passed
            ? "CLIENT WRITE CYCLE PASSED"
            : "CLIENT WRITE CYCLE FAILED");

    Environment.ExitCode = passed ? 0 : 1;
    return;
}


var outputLock = new object();

var client = new CoreLinkClient(
    new CSharpCoreAdapter());

var runCts = new CancellationTokenSource();

long snapshotOk = 0;
long snapshotUnavailable = 0;
long readOk = 0;
long readUnavailable = 0;
long invariantFailures = 0;

long writeOk = 0;
long writeUnavailable = 0;

long fc03Ok = 0;
long fc03Unavailable = 0;
long fc04Ok = 0;
long fc04Unavailable = 0;

long fc03Cycles = 0;
long fc04Cycles = 0;

long rxEvents = 0;
long rxStatusEvents = 0;

long readCycles = 0;
long writeCycles = 0;
long snapshotCycles = 0;

long singleWrites = 0;
long multipleWrites = 0;
long writeVerified = 0;

void Log(string message)
{
    lock (outputLock)
    {
        Console.WriteLine(
            $"[{DateTime.Now:HH:mm:ss.fff}] {message}");
    }
}

void Check(bool condition, string title)
{
    if (!condition)
    {
        Interlocked.Increment(ref invariantFailures);
        Log($"[FAIL] {title}");
    }
}

IDisposable? subValues = null;
IDisposable? subStatuses = null;

Task? snapshotTask = null;
Task? readTask = null;
Task? writeTask = null;
Task? fc03Task = null;
Task? fc04Task = null;

CoreLinkResult stopResult = CoreLinkResult.SessionError;

Log($"STRESS START: PLC={ip}:{port}, UnitId={unitId}");

Log(
    $"CoreLink polling: FC{pollingFunctionCode:D2}, " +
    $"interval={pollingMs}ms");

Log(
    $"Snapshot={snapshotPeriodMs}ms, " +
    $"MappedRead={readPeriodMs}ms, " +
    $"FC04={fc04ReadPeriodMs}ms, " +
    $"FC03={fc03ReadPeriodMs}ms, " +
    $"Write={writePeriodMs}ms");

Log(
    "Diagnostic: separate FC04 read, FC03 read, " +
    "FC06/FC16 writes verified by FC03");

Log("Writes: ONLY emulator Holding Registers addresses 20..23");

try
{
    CoreLinkResult start = client.Start(map);

    if (start != CoreLinkResult.Ok)
    {
        Log(
            $"CORE START FAILED: {start}. " +
            $"State={client.State}. " +
            $"Error={client.LastError}");

        return;
    }

    subValues = client.Values?.Subscribe(
        _ => Interlocked.Increment(ref rxEvents));

    subStatuses = client.Statuses?.Subscribe(status =>
    {
        Interlocked.Increment(ref rxStatusEvents);
        Log($"RX STATUS: {status}");
    });

    using var durationCts =
        new CancellationTokenSource(
            TimeSpan.FromSeconds(durationSeconds));

    using var combinedCts =
        CancellationTokenSource.CreateLinkedTokenSource(
            runCts.Token,
            durationCts.Token);

    CancellationToken token = combinedCts.Token;

    snapshotTask = SnapshotLoop(token);
    readTask = ReadLoop(token);
    fc04Task = Fc04Loop(token);
    fc03Task = Fc03Loop(token);
    writeTask = WriteLoop(token);

    await Task.WhenAll(
        snapshotTask,
        readTask,
        fc04Task,
        fc03Task,
        writeTask);
}
catch (OperationCanceledException)
{
    // Normal duration expiry.
}
catch (Exception ex)
{
    // TODO: Подключить централизованное логирование.
    Check(false, $"TEST EXCEPTION: {ex}");
}
finally
{
    await runCts.CancelAsync();

    foreach (Task? worker in new[]
    {
        snapshotTask,
        readTask,
        fc04Task,
        fc03Task,
        writeTask
    })
    {
        if (worker is null)
            continue;

        try
        {
            await worker;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // TODO: Подключить централизованное логирование.
            Check(false, $"WORKER FAULT: {ex}");
        }
    }

    try
    {
        stopResult = await client.StopAsync();
        Log($"CORE STOP: {stopResult}");
    }
    catch (Exception ex)
    {
        // TODO: Подключить централизованное логирование.
        Check(false, $"STOP EXCEPTION: {ex}");
    }

    subValues?.Dispose();
    subStatuses?.Dispose();

    runCts.Dispose();
}

Check(
    stopResult == CoreLinkResult.Ok,
    "Core Stop must succeed");

Check(
    client.GetSnapshot(out var afterStop) ==
        SnapshotStatus.Stopped &&
    afterStop.IsEmpty,
    "Snapshot after Stop");

CoreReadResult afterStopRead =
    await client.ReadAsync([0]);

Check(
    afterStopRead.Status == CoreReadStatus.Stopped &&
    afterStopRead.Values is null,
    "Read after Stop");

Log("============== STRESS SUMMARY ==============");

Log(
    $"CoreLink Polling FC{pollingFunctionCode:D2}: " +
    $"Rx value events={rxEvents}, " +
    $"Rx status events={rxStatusEvents}");

Log(
    $"Snapshot: cycles={snapshotCycles}, " +
    $"ok={snapshotOk}, unavailable={snapshotUnavailable}");

Log(
    $"CoreLink Direct Read: cycles={readCycles}, " +
    $"ok={readOk}, unavailable={readUnavailable}");

Log(
    $"FC04 Read: cycles={fc04Cycles}, " +
    $"ok={fc04Ok}, unavailable={fc04Unavailable}");

Log(
    $"FC03 Read: cycles={fc03Cycles}, " +
    $"ok={fc03Ok}, unavailable={fc03Unavailable}");

Log(
    $"Write FC06/FC16: cycles={writeCycles}, " +
    $"ok={writeOk}, unavailable={writeUnavailable}, " +
    $"FC06={singleWrites}, FC16={multipleWrites}, " +
    $"verified via FC03={writeVerified}");

Log($"Invariant failures={invariantFailures}");

Log(
    invariantFailures == 0
        ? "INVARIANTS PASSED"
        : "INVARIANTS FAILED");

Log(
    "Note: FC03/FC04 diagnostic reads and FC06/FC16 writes " +
    "use independent TCP connections; only polling and " +
    "mapped ReadAsync traverse CoreLink.");

async Task SnapshotLoop(CancellationToken token)
{
    using var timer = new PeriodicTimer(
        TimeSpan.FromMilliseconds(snapshotPeriodMs));

    while (await timer.WaitForNextTickAsync(token))
    {
        Interlocked.Increment(ref snapshotCycles);

        SnapshotStatus status =
            client.GetSnapshot(out var snapshot);

        if (status == SnapshotStatus.Ok)
        {
            Interlocked.Increment(ref snapshotOk);

            Check(
                snapshot.Length == map.Registers.Length,
                "Snapshot length");
        }
        else
        {
            Interlocked.Increment(ref snapshotUnavailable);
            Log($"SNAPSHOT: {status}");
        }
    }
}

async Task ReadLoop(CancellationToken token)
{
    using var timer = new PeriodicTimer(
        TimeSpan.FromMilliseconds(readPeriodMs));

    int cycle = 0;

    while (await timer.WaitForNextTickAsync(token))
    {
        token.ThrowIfCancellationRequested();

        Interlocked.Increment(ref readCycles);

        int[] ids = (cycle++ % 3) switch
        {
            0 => [0],
            1 => [2, 3, 4],
            _ => [0, 2, 8]
        };

        var watch = Stopwatch.StartNew();

        CoreReadResult result =
            await client.ReadAsync(ids);

        watch.Stop();

        if (result.Status == CoreReadStatus.Ok)
        {
            Interlocked.Increment(ref readOk);

            Check(
                result.Values is not null &&
                result.Values.Length == ids.Length,
                "Direct Read result length");

            if (result.Values is not null &&
                result.Values.Length == ids.Length)
            {
                Log(
                    $"READ OK [{string.Join(',', ids)}] = " +
                    $"[{string.Join(';', result.Values)}], " +
                    $"{watch.ElapsedMilliseconds}ms");
            }
        }
        else
        {
            Interlocked.Increment(ref readUnavailable);

            Check(
                result.Values is null,
                "No partial data when Read fails");

            Log(
                $"READ ERROR: {result.Status}, " +
                $"{watch.ElapsedMilliseconds}ms");
        }
    }
}

// Diagnostic reads and writes intentionally use separate connections.
// They validate the emulator/protocol, not CoreLink Dispatcher routing.
async Task Fc04Loop(CancellationToken token)
{
    using var timer = new PeriodicTimer(
        TimeSpan.FromMilliseconds(fc04ReadPeriodMs));

    while (await timer.WaitForNextTickAsync(token))
    {
        token.ThrowIfCancellationRequested();
        Interlocked.Increment(ref fc04Cycles);

        try
        {
            using var tcp = new TcpClient();

            using var deadline =
                CancellationTokenSource.CreateLinkedTokenSource(token);

            deadline.CancelAfter(ioTimeoutMs);

            await tcp.ConnectAsync(
                ip,
                port,
                deadline.Token);

            using var master =
                new ModbusFactory().CreateMaster(tcp);

            ushort[] input =
                await master.ReadInputRegistersAsync(
                    unitId,
                    0,
                    17)
                .WaitAsync(
                    TimeSpan.FromMilliseconds(ioTimeoutMs),
                    token);

            if (input.Length != 17 || input[0] > 9)
            {
                Check(
                    false,
                    "FC04 response length / Measurme range");

                continue;
            }

            Interlocked.Increment(ref fc04Ok);

            Log(
                $"FC04 OK: 17 Input Registers, " +
                $"Measurme={input[0]}, Status={input[1]}");
        }
        catch (OperationCanceledException)
            when (token.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex) when (IsExpectedIoFailure(ex))
        {
            // TODO: Подключить централизованное логирование.
            Interlocked.Increment(ref fc04Unavailable);

            Log(
                $"FC04 UNAVAILABLE: " +
                $"{ex.GetType().Name}: {ex.Message}");
        }
        catch (Exception ex)
        {
            // TODO: Подключить централизованное логирование.
            Check(false, $"FC04 UNEXPECTED EXCEPTION: {ex}");
        }
    }
}

async Task Fc03Loop(CancellationToken token)
{
    using var timer = new PeriodicTimer(
        TimeSpan.FromMilliseconds(fc03ReadPeriodMs));

    while (await timer.WaitForNextTickAsync(token))
    {
        token.ThrowIfCancellationRequested();
        Interlocked.Increment(ref fc03Cycles);

        try
        {
            using var tcp = new TcpClient();

            using var deadline =
                CancellationTokenSource.CreateLinkedTokenSource(token);

            deadline.CancelAfter(ioTimeoutMs);

            await tcp.ConnectAsync(
                ip,
                port,
                deadline.Token);

            using var master =
                new ModbusFactory().CreateMaster(tcp);

            // Simulator initializes Holding[5..6] as IEEE-754
            // 1234.5f, low-word first.
            // Diagnostic writes touch only addresses 20..23.
            ushort[] holding =
                await master.ReadHoldingRegistersAsync(
                    unitId,
                    0,
                    17)
                .WaitAsync(
                    TimeSpan.FromMilliseconds(ioTimeoutMs),
                    token);

            if (holding.Length != 17)
            {
                Check(false, "FC03 response length");
                continue;
            }

            if (holding[0] != 1 ||
                holding[1] != 17 ||
                holding[3] != 42)
            {
                Check(
                    false,
                    $"FC03 technological map changed: " +
                    $"Measurme={holding[0]}, " +
                    $"Status={holding[1]}, " +
                    $"Cutoff={holding[3]}");

                continue;
            }

            uint bits =
                (uint)holding[5] |
                ((uint)holding[6] << 16);

            float speed =
                BitConverter.UInt32BitsToSingle(bits);

            if (speed != 1234.5f)
            {
                Check(
                    false,
                    $"FC03 Holding Speed expected 1234.5, " +
                    $"actual {speed}");

                continue;
            }

            Interlocked.Increment(ref fc03Ok);

            Log(
                $"FC03 OK: 17 Holding Registers, " +
                $"Speed={speed}, Register0={holding[0]}");
        }
        catch (OperationCanceledException)
            when (token.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex) when (IsExpectedIoFailure(ex))
        {
            // TODO: Подключить централизованное логирование.
            Interlocked.Increment(ref fc03Unavailable);

            Log(
                $"FC03 UNAVAILABLE: " +
                $"{ex.GetType().Name}: {ex.Message}");
        }
        catch (Exception ex)
        {
            // TODO: Подключить централизованное логирование.
            Check(false, $"FC03 UNEXPECTED EXCEPTION: {ex}");
        }
    }
}

async Task WriteLoop(CancellationToken token)
{
    using var timer = new PeriodicTimer(
        TimeSpan.FromMilliseconds(writePeriodMs));

    ushort sequence = 0;

    while (await timer.WaitForNextTickAsync(token))
    {
        token.ThrowIfCancellationRequested();
        Interlocked.Increment(ref writeCycles);

        sequence++;

        ushort word = (ushort)(1000 + sequence);

        ushort[] block =
        [
            (ushort)(2000 + sequence),
            (ushort)(3000 + sequence),
            (ushort)(4000 + sequence)
        ];

        try
        {
            // FC03 reads Holding Registers.
            // Writes use FC06 and FC16.
            // Only emulator 127.0.0.1:1502, UnitId=1 is allowed.
            using var tcp = new TcpClient();

            using var deadline =
                CancellationTokenSource.CreateLinkedTokenSource(token);

            deadline.CancelAfter(ioTimeoutMs);

            await tcp.ConnectAsync(
                ip,
                port,
                deadline.Token);

            using var master =
                new ModbusFactory().CreateMaster(tcp);

            await master.WriteSingleRegisterAsync(
                    unitId,
                    20,
                    word)
                .WaitAsync(
                    TimeSpan.FromMilliseconds(ioTimeoutMs),
                    token);

            Interlocked.Increment(ref singleWrites);

            await master.WriteMultipleRegistersAsync(
                    unitId,
                    21,
                    block)
                .WaitAsync(
                    TimeSpan.FromMilliseconds(ioTimeoutMs),
                    token);

            Interlocked.Increment(ref multipleWrites);

            ushort[] got =
                await master.ReadHoldingRegistersAsync(
                    unitId,
                    20,
                    4)
                .WaitAsync(
                    TimeSpan.FromMilliseconds(ioTimeoutMs),
                    token);

            bool verified =
                got.Length == 4 &&
                got[0] == word &&
                got[1] == block[0] &&
                got[2] == block[1] &&
                got[3] == block[2];

            if (!verified)
            {
                Check(
                    false,
                    $"FC06/FC16 -> FC03 mismatch, received " +
                    $"[{string.Join(',', got)}]");

                continue;
            }

            Interlocked.Increment(ref writeVerified);
            Interlocked.Increment(ref writeOk);

            Log(
                $"WRITE OK: FC06 addr20={word}; " +
                $"FC16 addr21..23=[{string.Join(',', block)}]; " +
                "FC03 readback verified");
        }
        catch (OperationCanceledException)
            when (token.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex) when (IsExpectedIoFailure(ex))
        {
            // TODO: Подключить централизованное логирование.
            Interlocked.Increment(ref writeUnavailable);

            Log(
                $"WRITE UNAVAILABLE: " +
                $"{ex.GetType().Name}: {ex.Message}");
        }
        catch (Exception ex)
        {
            // TODO: Подключить централизованное логирование.
            Check(false, $"WRITE UNEXPECTED EXCEPTION: {ex}");
        }
    }
}

static bool IsExpectedIoFailure(Exception ex) =>
    ex is SocketException or
          IOException or
          TimeoutException or
          OperationCanceledException or
          ObjectDisposedException ||
    ex.GetType().Name.Contains(
        "SlaveException",
        StringComparison.Ordinal);
