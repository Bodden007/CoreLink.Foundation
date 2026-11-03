
using System.Buffers.Binary;
using CoreLink.Adapter.CSharp;
using CoreLink.Client;
using CoreLink.Client.Results;
using CoreLink.Contracts.Core;
using CoreLink.Maps;

// Интеграционный smoke test.
//
// Проверяет реальную цепочку:
// Maps -> Client -> ICore -> C# Adapter -> Core -> Transport.
//
// Не выполняет Write и не изменяет регистры PLC.

const string mapId = "physical";
const int testDurationMs = 10_000;
const int snapshotIntervalMs = 500;

var mapProvider = new MapProvider();

if (!mapProvider.TryLoad(mapId, out var map) ||
    map is null)
{
    Console.WriteLine("MAP LOAD FAILED");
    return;
}

if (map.Buffer.Length < 14 ||
    map.BufferLength <= 0)
{
    Console.WriteLine("INVALID MAP");
    return;
}

int pollIntervalMs =
    BinaryPrimitives.ReadInt32LittleEndian(
        map.Buffer.Span.Slice(10, 4));

Console.WriteLine(
    "======================================");

Console.WriteLine(
    "CORELINK INTEGRATION TEST");

Console.WriteLine(
    "======================================");

Console.WriteLine($"Map: {mapId}");
Console.WriteLine($"Values: {map.BufferLength}");
Console.WriteLine($"Polling: {pollIntervalMs} ms");
Console.WriteLine($"Snapshot: {snapshotIntervalMs} ms");

Console.WriteLine();

var adapter = new CSharpCoreAdapter();

var client = new CoreLinkClient(
    mapProvider,
    adapter);

adapter.StatusChanged += status =>
{
    Console.WriteLine(
        $"[{DateTime.Now:HH:mm:ss.fff}] CORE STATUS: {status}");
};

float[] buffer = new float[map.BufferLength];

CoreLinkResult startResult;

try
{
    startResult = client.Start(mapId);
}
catch (Exception ex)
{
    Console.WriteLine(
        $"START EXCEPTION: {ex}");

    return;
}

Console.WriteLine($"START RESULT: {startResult}");
Console.WriteLine($"CLIENT STATE: {client.State}");

if (startResult != CoreLinkResult.Ok)
{
    Console.WriteLine("START FAILED");
    client.Stop();
    return;
}

int okCount = 0;
int noDataCount = 0;
int disconnectedCount = 0;
int errorCount = 0;

try
{
    int iterations =
        testDurationMs / snapshotIntervalMs;

    for (int i = 0; i < iterations; i++)
    {
        Thread.Sleep(snapshotIntervalMs);

        SnapshotStatus status =
            ReadSnapshot(adapter, buffer);

        Console.WriteLine();

        Console.WriteLine(
            $"[{DateTime.Now:HH:mm:ss.fff}] SNAPSHOT: {status}");

        switch (status)
        {
            case SnapshotStatus.Ok:
                okCount++;

                for (int j = 0; j < buffer.Length; j++)
                {
                    Console.WriteLine(
                        $"  [{j}] = {buffer[j]}");
                }

                break;

            case SnapshotStatus.NoData:
                noDataCount++;
                break;

            case SnapshotStatus.Disconnected:
                disconnectedCount++;
                break;

            default:
                errorCount++;
                break;
        }
    }
}
finally
{
    Console.WriteLine();
    Console.WriteLine("STOPPING CORE...");

    try
    {
        client.Stop();

        Console.WriteLine(
            $"CLIENT STATE: {client.State}");
    }
    catch (Exception ex)
    {
        Console.WriteLine(
            $"STOP EXCEPTION: {ex}");
    }
}

Console.WriteLine();
Console.WriteLine(
    "======================================");

Console.WriteLine("TEST SUMMARY");

Console.WriteLine(
    "======================================");

Console.WriteLine($"Snapshot OK: {okCount}");
Console.WriteLine($"NoData: {noDataCount}");
Console.WriteLine($"Disconnected: {disconnectedCount}");
Console.WriteLine($"Errors: {errorCount}");

Console.WriteLine();
Console.WriteLine("TEST COMPLETED");

/// <summary>
/// Передаёт временно закреплённый клиентский массив
/// через утверждённый контракт ICore.
///
/// Указатель действует только внутри вызова.
/// </summary>
static unsafe SnapshotStatus ReadSnapshot(
    ICore core,
    float[] buffer)
{
    fixed (float* pointer = buffer)
    {
        return core.GetSnapshot(
            pointer,
            buffer.Length);
    }
}
