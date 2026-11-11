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
// + independent test-only Modbus FC06/FC16/FC03 client.
// WARNING: write operations are intentionally restricted to loopback.
const string mapId = "physical";
const int durationSeconds = 60;
const int snapshotPeriodMs = 500;
const int readPeriodMs = 700;
const int writePeriodMs = 2100;
const int ioTimeoutMs = 1500;

var provider = new MapProvider();

if (!provider.TryLoad(mapId, out var map) ||
    map is null)
{
    Console.WriteLine("CORE MAP LOAD FAILED");
    return;
}

if (!provider.TryLoadRxMap(
        mapId,
        out var rxMap,
        out var mapError) ||
    rxMap is null)
{
    Console.WriteLine($"RX MAP ERROR: {mapError}");
    return;
}

byte[] config = map.Buffer.ToArray();
if (config.Length < 14 || map.BufferLength != rxMap.Values.Length)
{
    Console.WriteLine("INVALID MAP");
    return;
}

IPAddress ip = new IPAddress(config.AsSpan(3, 4));
ushort port = BinaryPrimitives.ReadUInt16LittleEndian(config.AsSpan(7, 2));
byte unitId = config[9];
int pollingMs = BinaryPrimitives.ReadInt32LittleEndian(config.AsSpan(10, 4));

// No accidental writes to production PLCs, regardless of the map configuration.
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
long rxEvents = 0, rxStatusEvents = 0;
long readCycles = 0, writeCycles = 0;
long snapshotCycles = 0;
long singleWrites = 0, multipleWrites = 0;

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
CoreLinkResult stopResult = CoreLinkResult.SessionError;

Log($"STRESS START: PLC={ip}:{port}, UnitId={unitId}");
Log($"Polling={pollingMs}ms, Snapshot={snapshotPeriodMs}ms, Read={readPeriodMs}ms, Write={writePeriodMs}ms");
Log("Writes: ONLY emulator Holding Registers addresses 0..3");

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
    writeTask = WriteLoop(token);

    await Task.WhenAll(snapshotTask, readTask, writeTask);
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
    foreach (Task? worker in new[] { snapshotTask, readTask, writeTask })
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
Log($"Snapshot: cycles={snapshotCycles}, ok={snapshotOk}, unavailable={snapshotUnavailable}");
Log($"Direct Read: cycles={readCycles}, ok={readOk}, unavailable={readUnavailable}");
Log($"Writes: cycles={writeCycles}, ok={writeOk}, unavailable={writeUnavailable}, FC06={singleWrites}, FC16={multipleWrites}");
Log($"Rx value events={rxEvents}, Rx status events={rxStatusEvents}");
Log($"Invariant failures={invariantFailures}");
Log(invariantFailures == 0 ? "INVARIANTS PASSED" : "INVARIANTS FAILED");
Log("Note: FC06/FC16/FC03 here use an independent diagnostic TCP connection, not CoreLink Dispatcher.");

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
            // Separate diagnostic client: do not change CoreLink transport ownership.
            using var tcp = new TcpClient();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(ioTimeoutMs);
            await tcp.ConnectAsync(ip, port, deadline.Token);
            using var master = new ModbusFactory().CreateMaster(tcp);

            // NModbus APIs do not accept cancellation; socket is disposed on timeout.
            await master.WriteSingleRegisterAsync(unitId, 0, word)
                .WaitAsync(TimeSpan.FromMilliseconds(ioTimeoutMs), token);
            Interlocked.Increment(ref singleWrites);
            await master.WriteMultipleRegistersAsync(unitId, 1, block)
                .WaitAsync(TimeSpan.FromMilliseconds(ioTimeoutMs), token);
            Interlocked.Increment(ref multipleWrites);

            ushort[] got = await master.ReadHoldingRegistersAsync(unitId, 0, 4)
                .WaitAsync(TimeSpan.FromMilliseconds(ioTimeoutMs), token);
            Check(got.Length == 4, "FC03 readback length");
            if (got.Length == 4)
                Check(got[0] == word && got[1] == block[0] &&
                      got[2] == block[1] && got[3] == block[2],
                      "FC06/FC16 -> FC03 readback values");
            Interlocked.Increment(ref writeOk);
            Log($"WRITE OK FC06 addr0={word}; FC16 addr1..3=[{string.Join(',', block)}]; FC03 verified");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex) when (ex is SocketException or IOException or
                                   TimeoutException or OperationCanceledException or
                                   ObjectDisposedException ||
                                   ex.GetType().Name.Contains("SlaveException", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref writeUnavailable);
            Log($"WRITE/VERIFY UNAVAILABLE: {ex.GetType().Name}: {ex.Message}");
        }
        catch (Exception ex)
        {
            Check(false, $"WRITE UNEXPECTED EXCEPTION: {ex}");
        }
    }
}
