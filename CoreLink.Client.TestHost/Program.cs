using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reactive.Linq;
using CoreLink.Adapter.CSharp;
using CoreLink.Client;
using CoreLink.Client.Results;
using CoreLink.Contracts.Core;
using CoreLink.Maps;
using NModbus;

// Integration stress test: CoreLink polling + periodic CoreLink ReadAsync
// + independent test-only Modbus FC03/FC04/FC06/FC16 client.
// WARNING: write operations are intentionally restricted to loopback.
const string mapId = "physical";
const int durationSeconds = 60;
const int snapshotPeriodMs = 500;
const int readPeriodMs = 700;
const int fc04ReadPeriodMs = 1100;
const int fc03ReadPeriodMs = 1300;
const int writePeriodMs = 2100;
const int ioTimeoutMs = 1500;

Trace.Listeners.Add(new TextWriterTraceListener(Console.Error));
Trace.AutoFlush = true;

var provider = new MapProvider();

if (!provider.TryLoad(mapId, out var map) || map is null)
{
    Console.WriteLine("CORE MAP LOAD FAILED");
    return;
}

if (!provider.TryLoadRxMap(mapId, out var rxMap, out var mapError) || rxMap is null)
{
    Console.WriteLine($"RX MAP ERROR: {mapError}");
    return;
}

byte[] config = map.Buffer.ToArray();
if (config.Length < 15 || map.BufferLength != rxMap.Values.Length)
{
    Console.WriteLine("INVALID MAP");
    return;
}

IPAddress ip = new IPAddress(config.AsSpan(3, 4));
ushort port = BinaryPrimitives.ReadUInt16LittleEndian(config.AsSpan(7, 2));
byte unitId = config[9];
int pollingMs = BinaryPrimitives.ReadInt32LittleEndian(config.AsSpan(10, 4));
byte pollingFunctionCode = config[14]; // CoreMap v3: immediately after PollIntervalMs.

if (pollingFunctionCode is not (3 or 4))
{
    Console.WriteLine($"INVALID POLLING FUNCTION CODE: {pollingFunctionCode}");
    return;
}

// No accidental writes to production PLCs, regardless of map configuration.
if (!IPAddress.IsLoopback(ip) || port != 1502 || unitId != 1)
{
    Console.WriteLine("SAFETY STOP: stress writer permits only 127.0.0.1:1502, UnitId=1.");
    return;
}

var outputLock = new object();
var client = new CoreLinkClient(provider, new CSharpCoreAdapter());
var runCts = new CancellationTokenSource();

long snapshotOk = 0, snapshotUnavailable = 0, readOk = 0, readUnavailable = 0;
long writeOk = 0, writeUnavailable = 0, invariantFailures = 0;
long fc03Ok = 0, fc03Unavailable = 0, fc04Ok = 0, fc04Unavailable = 0;
long fc03Cycles = 0, fc04Cycles = 0;
long rxEvents = 0, rxStatusEvents = 0;
long readCycles = 0, writeCycles = 0;
long snapshotCycles = 0;
long singleWrites = 0, multipleWrites = 0;
long writeVerified = 0;

void Log(string message)
{
    lock (outputLock)
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
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
Task? snapshotTask = null, readTask = null, writeTask = null;
Task? fc03Task = null, fc04Task = null;
CoreLinkResult stopResult = CoreLinkResult.SessionError;

Log($"STRESS START: PLC={ip}:{port}, UnitId={unitId}");
Log($"CoreLink polling: FC{pollingFunctionCode:D2}, interval={pollingMs}ms");
Log($"Snapshot={snapshotPeriodMs}ms, MappedRead={readPeriodMs}ms, FC04={fc04ReadPeriodMs}ms, FC03={fc03ReadPeriodMs}ms, Write={writePeriodMs}ms");
Log("Diagnostic: separate FC04 read, FC03 read, FC06/FC16 writes verified by FC03");
Log("Writes: ONLY emulator Holding Registers addresses 20..23");

try
{
    CoreLinkResult start = client.Start(mapId);
    if (start != CoreLinkResult.Ok)
    {
        Log($"CORE START FAILED: {start}");
        return;
    }

    subValues = client.Values?.Subscribe(_ => Interlocked.Increment(ref rxEvents));
    subStatuses = client.Statuses?.Subscribe(status =>
    {
        Interlocked.Increment(ref rxStatusEvents);
        Log($"RX STATUS: {status}");
    });

    using var durationCts = new CancellationTokenSource(TimeSpan.FromSeconds(durationSeconds));
    using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(
        runCts.Token, durationCts.Token);
    CancellationToken token = combinedCts.Token;

    snapshotTask = SnapshotLoop(token);
    readTask = ReadLoop(token);
    fc04Task = Fc04Loop(token);
    fc03Task = Fc03Loop(token);
    writeTask = WriteLoop(token);

    await Task.WhenAll(snapshotTask, readTask, fc04Task, fc03Task, writeTask);
}
catch (OperationCanceledException)
{
    // Normal duration expiry.
}
catch (Exception ex)
{
    Check(false, $"TEST EXCEPTION: {ex}");
}
finally
{
    await runCts.CancelAsync();
    foreach (Task? worker in new[] { snapshotTask, readTask, fc04Task, fc03Task, writeTask })
    {
        if (worker is null) continue;
        try { await worker; }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Check(false, $"WORKER FAULT: {ex}"); }
    }

    try
    {
        stopResult = await client.StopAsync();
        Log($"CORE STOP: {stopResult}");
    }
    catch (Exception ex)
    {
        Check(false, $"STOP EXCEPTION: {ex}");
    }

    subValues?.Dispose();
    subStatuses?.Dispose();
    runCts.Dispose();
}

Check(stopResult == CoreLinkResult.Ok, "Core Stop must succeed");
Check(client.GetSnapshot(out var afterStop) == SnapshotStatus.Stopped &&
      afterStop.IsEmpty, "Snapshot after Stop");
CoreReadResult afterStopRead = await client.ReadAsync([0]);
Check(afterStopRead.Status == CoreReadStatus.Stopped &&
      afterStopRead.Values is null, "Read after Stop");

Log("============== STRESS SUMMARY ==============");
Log($"CoreLink Polling FC{pollingFunctionCode:D2}: Rx value events={rxEvents}, Rx status events={rxStatusEvents}");
Log($"Snapshot: cycles={snapshotCycles}, ok={snapshotOk}, unavailable={snapshotUnavailable}");
Log($"CoreLink Direct Read: cycles={readCycles}, ok={readOk}, unavailable={readUnavailable}");
Log($"FC04 Read: cycles={fc04Cycles}, ok={fc04Ok}, unavailable={fc04Unavailable}");
Log($"FC03 Read: cycles={fc03Cycles}, ok={fc03Ok}, unavailable={fc03Unavailable}");
Log($"Write FC06/FC16: cycles={writeCycles}, ok={writeOk}, unavailable={writeUnavailable}, FC06={singleWrites}, FC16={multipleWrites}, verified via FC03={writeVerified}");
Log($"Invariant failures={invariantFailures}");
Log(invariantFailures == 0 ? "INVARIANTS PASSED" : "INVARIANTS FAILED");
Log("Note: FC03/FC04 diagnostic reads and FC06/FC16 writes use independent TCP connections; only polling and mapped ReadAsync traverse CoreLink.");

async Task SnapshotLoop(CancellationToken token)
{
    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(snapshotPeriodMs));
    while (await timer.WaitForNextTickAsync(token))
    {
        Interlocked.Increment(ref snapshotCycles);
        SnapshotStatus status = client.GetSnapshot(out var snapshot);
        if (status == SnapshotStatus.Ok)
        {
            Interlocked.Increment(ref snapshotOk);
            Check(snapshot.Length == map.BufferLength, "Snapshot length");
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
    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(readPeriodMs));
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
        CoreReadResult result = await client.ReadAsync(ids);
        watch.Stop();
        if (result.Status == CoreReadStatus.Ok)
        {
            Interlocked.Increment(ref readOk);
            Check(result.Values is not null && result.Values.Length == ids.Length,
                  "Direct Read result length");
            if (result.Values is not null && result.Values.Length == ids.Length)
                Log($"READ OK [{string.Join(',', ids)}] = [{string.Join(';', result.Values)}], {watch.ElapsedMilliseconds}ms");
        }
        else
        {
            Interlocked.Increment(ref readUnavailable);
            Check(result.Values is null, "No partial data when Read fails");
            Log($"READ ERROR: {result.Status}, {watch.ElapsedMilliseconds}ms");
        }
    }
}

// The diagnostic reads and writes use separate connections intentionally.
// They validate the emulator/protocol, not CoreLink Dispatcher routing.
async Task Fc04Loop(CancellationToken token)
{
    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(fc04ReadPeriodMs));
    while (await timer.WaitForNextTickAsync(token))
    {
        token.ThrowIfCancellationRequested();
        Interlocked.Increment(ref fc04Cycles);
        try
        {
            using var tcp = new TcpClient();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(ioTimeoutMs);
            await tcp.ConnectAsync(ip, port, deadline.Token);
            using var master = new ModbusFactory().CreateMaster(tcp);

            ushort[] input = await master.ReadInputRegistersAsync(unitId, 0, 17)
                .WaitAsync(TimeSpan.FromMilliseconds(ioTimeoutMs), token);
            if (input.Length != 17 || input[0] > 9)
            {
                Check(false, "FC04 response length / Measurme range");
                continue;
            }

            Interlocked.Increment(ref fc04Ok);
            Log($"FC04 OK: 17 Input Registers, Measurme={input[0]}, Status={input[1]}");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex) when (IsExpectedIoFailure(ex))
        {
            Interlocked.Increment(ref fc04Unavailable);
            Log($"FC04 UNAVAILABLE: {ex.GetType().Name}: {ex.Message}");
        }
        catch (Exception ex)
        {
            Check(false, $"FC04 UNEXPECTED EXCEPTION: {ex}");
        }
    }
}

async Task Fc03Loop(CancellationToken token)
{
    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(fc03ReadPeriodMs));
    while (await timer.WaitForNextTickAsync(token))
    {
        token.ThrowIfCancellationRequested();
        Interlocked.Increment(ref fc03Cycles);
        try
        {
            using var tcp = new TcpClient();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(ioTimeoutMs);
            await tcp.ConnectAsync(ip, port, deadline.Token);
            using var master = new ModbusFactory().CreateMaster(tcp);

            // Simulator initializes Holding[5..6] as IEEE-754 1234.5f,
            // low-word first; diagnostic writes touch only 20..23.
            ushort[] holding = await master.ReadHoldingRegistersAsync(unitId, 0, 17)
                .WaitAsync(TimeSpan.FromMilliseconds(ioTimeoutMs), token);
            if (holding.Length != 17)
            {
                Check(false, "FC03 response length");
                continue;
            }
            if (holding[0] != 1 || holding[1] != 17 || holding[3] != 42)
            {
                Check(false, $"FC03 technological map changed: Measurme={holding[0]}, Status={holding[1]}, Cutoff={holding[3]}");
                continue;
            }
            uint bits = (uint)holding[5] | ((uint)holding[6] << 16);
            float speed = BitConverter.UInt32BitsToSingle(bits);
            if (speed != 1234.5f)
            {
                Check(false, $"FC03 Holding Speed expected 1234.5, actual {speed}");
                continue;
            }

            Interlocked.Increment(ref fc03Ok);
            Log($"FC03 OK: 17 Holding Registers, Speed={speed}, Register0={holding[0]}");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex) when (IsExpectedIoFailure(ex))
        {
            Interlocked.Increment(ref fc03Unavailable);
            Log($"FC03 UNAVAILABLE: {ex.GetType().Name}: {ex.Message}");
        }
        catch (Exception ex)
        {
            Check(false, $"FC03 UNEXPECTED EXCEPTION: {ex}");
        }
    }
}

async Task WriteLoop(CancellationToken token)
{
    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(writePeriodMs));
    ushort sequence = 0;
    while (await timer.WaitForNextTickAsync(token))
    {
        token.ThrowIfCancellationRequested();
        Interlocked.Increment(ref writeCycles);
        sequence++;
        ushort word = (ushort)(1000 + sequence);
        ushort[] block = [(ushort)(2000 + sequence), (ushort)(3000 + sequence), (ushort)(4000 + sequence)];

        try
        {
            // FC03 reads Holding Registers; actual writes use FC06 and FC16.
            // Only emulator 127.0.0.1:1502 UnitId=1 is permitted above.
            using var tcp = new TcpClient();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(ioTimeoutMs);
            await tcp.ConnectAsync(ip, port, deadline.Token);
            using var master = new ModbusFactory().CreateMaster(tcp);

            await master.WriteSingleRegisterAsync(unitId, 20, word)
                .WaitAsync(TimeSpan.FromMilliseconds(ioTimeoutMs), token);
            Interlocked.Increment(ref singleWrites);
            await master.WriteMultipleRegistersAsync(unitId, 21, block)
                .WaitAsync(TimeSpan.FromMilliseconds(ioTimeoutMs), token);
            Interlocked.Increment(ref multipleWrites);

            ushort[] got = await master.ReadHoldingRegistersAsync(unitId, 20, 4)
                .WaitAsync(TimeSpan.FromMilliseconds(ioTimeoutMs), token);
            bool verified = got.Length == 4 &&
                            got[0] == word && got[1] == block[0] &&
                            got[2] == block[1] && got[3] == block[2];
            if (!verified)
            {
                Check(false, $"FC06/FC16 -> FC03 mismatch, received [{string.Join(',', got)}]");
                continue;
            }

            Interlocked.Increment(ref writeVerified);
            Interlocked.Increment(ref writeOk);
            Log($"WRITE OK: FC06 addr20={word}; FC16 addr21..23=[{string.Join(',', block)}]; FC03 readback verified");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex) when (IsExpectedIoFailure(ex))
        {
            Interlocked.Increment(ref writeUnavailable);
            Log($"WRITE UNAVAILABLE: {ex.GetType().Name}: {ex.Message}");
        }
        catch (Exception ex)
        {
            Check(false, $"WRITE UNEXPECTED EXCEPTION: {ex}");
        }
    }
}

static bool IsExpectedIoFailure(Exception ex) =>
    ex is SocketException or IOException or TimeoutException or
          OperationCanceledException or ObjectDisposedException ||
    ex.GetType().Name.Contains("SlaveException", StringComparison.Ordinal);
