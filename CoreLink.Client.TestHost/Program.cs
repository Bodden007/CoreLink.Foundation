
using System.Buffers.Binary;
using System.Reactive.Linq;
using CoreLink.Adapter.CSharp;
using CoreLink.Client;
using CoreLink.Client.Results;
using CoreLink.Contracts.Core;
using CoreLink.Maps;

// Интеграционный smoke test.
//
// Проверяет цепочку:
// TestHost -> Client -> Rx -> Client -> ICore
//                         -> Core -> Transport -> PLC
//
// Публичное чтение snapshot выполняется параллельно
// с собственным рабочим циклом Rx.
//
// Тест не выполняет Write и не изменяет регистры PLC.

const string mapId = "physical";
const int testDurationMs = 60_000;
const int snapshotIntervalMs = 500;

var mapProvider = new MapProvider();

if (!mapProvider.TryLoad(mapId, out var map) ||
    map is null)
{
    Console.WriteLine("MAP LOAD FAILED");
    return;
}

if (!mapProvider.TryLoadRxMap(
        mapId,
        out var rxMap,
        out string? mapError) ||
    rxMap is null)
{
    Console.WriteLine(
        $"RX MAP LOAD FAILED: {mapError}");
    return;
}

if (map.Buffer.Length < 14 ||
    map.BufferLength <= 0 ||
    map.BufferLength != rxMap.Values.Length)
{
    Console.WriteLine("INVALID MAP");
    return;
}

int pollIntervalMs =
    BinaryPrimitives.ReadInt32LittleEndian(
        map.Buffer.Span.Slice(10, 4));

Console.WriteLine("======================================");
Console.WriteLine("CORELINK CLIENT + RX TEST");
Console.WriteLine("======================================");
Console.WriteLine($"Map: {mapId}");
Console.WriteLine($"Values: {map.BufferLength}");
Console.WriteLine($"Polling: {pollIntervalMs} ms");
Console.WriteLine($"Rx interval: {rxMap.RxIntervalMs} ms");
Console.WriteLine($"Test duration: {testDurationMs} ms");
Console.WriteLine();

var adapter = new CSharpCoreAdapter();
var client = new CoreLinkClient(mapProvider, adapter);

var outputLock = new object();

int rxValueCount = 0;
int rxStatusCount = 0;
int snapshotOkCount = 0;
int snapshotNoDataCount = 0;
int snapshotDisconnectedCount = 0;
int snapshotErrorCount = 0;

adapter.StatusChanged += status =>
{
    WriteLine($"CORE STATUS: {status}");
};

IDisposable? valuesSubscription = null;
IDisposable? statusesSubscription = null;

CoreLinkResult startResult = CoreLinkResult.SessionError;
CoreLinkResult stopResult = CoreLinkResult.SessionError;

try
{
    startResult = client.Start(mapId);

    WriteLine($"START RESULT: {startResult}");
    WriteLine($"CLIENT STATE: {client.State}");

    if (startResult == CoreLinkResult.Ok)
    {
        var valuesStream = client.Values;
        var statusesStream = client.Statuses;

        if (valuesStream is null ||
            statusesStream is null)
        {
            WriteLine("RX STREAMS UNAVAILABLE");
        }
        else
        {
            valuesSubscription = valuesStream.Subscribe(value =>
            {
                Interlocked.Increment(ref rxValueCount);

                WriteLine(
                    $"RX VALUE [{value.ValueId}] " +
                    $"{value.Name} = {value.Value} " +
                    $"{value.Unit}");
            });

            statusesSubscription = statusesStream.Subscribe(status =>
            {
                Interlocked.Increment(ref rxStatusCount);

                WriteLine($"RX STATUS: {status}");
            });

            // CoreLinkRx — hot stream без replay.
            // Публикации до подписки могут быть пропущены.

            int iterations =
                testDurationMs / snapshotIntervalMs;

            for (int i = 0; i < iterations; i++)
            {
                await Task.Delay(snapshotIntervalMs);

                // Публичный snapshot использует _coreBuffer.
                // Рабочий цикл Rx использует _rxBuffer.
                // Оба обращения синхронизированы Client.
                SnapshotStatus status =
                    client.GetSnapshot(out var snapshot);

                switch (status)
                {
                    case SnapshotStatus.Ok:
                        snapshotOkCount++;

                        WriteLine(
                            $"SNAPSHOT: Ok, values={snapshot.Length}");

                        break;

                    case SnapshotStatus.NoData:
                        snapshotNoDataCount++;
                        WriteLine("SNAPSHOT: NoData");
                        break;

                    case SnapshotStatus.Disconnected:
                        snapshotDisconnectedCount++;
                        WriteLine("SNAPSHOT: Disconnected");
                        break;

                    default:
                        snapshotErrorCount++;
                        WriteLine($"SNAPSHOT: {status}");
                        break;
                }
            }
        }
    }
}
catch (Exception ex)
{
    WriteLine($"TEST EXCEPTION: {ex}");
}
finally
{
    WriteLine("STOPPING CLIENT...");

    try
    {
        stopResult = await client.StopAsync();

        WriteLine($"STOP RESULT: {stopResult}");
        WriteLine($"CLIENT STATE: {client.State}");
    }
    catch (Exception ex)
    {
        WriteLine($"STOP EXCEPTION: {ex}");
    }

    // Подписки освобождаются после завершения Rx.
    valuesSubscription?.Dispose();
    statusesSubscription?.Dispose();
}

WriteLine("");
WriteLine("======================================");
WriteLine("TEST SUMMARY");
WriteLine("======================================");

WriteLine($"Start: {startResult}");
WriteLine($"Stop: {stopResult}");
WriteLine($"Final state: {client.State}");

WriteLine($"Rx value events: {rxValueCount}");
WriteLine($"Rx status events: {rxStatusCount}");

WriteLine($"Snapshot Ok: {snapshotOkCount}");
WriteLine($"Snapshot NoData: {snapshotNoDataCount}");
WriteLine($"Snapshot Disconnected: {snapshotDisconnectedCount}");
WriteLine($"Snapshot Errors: {snapshotErrorCount}");

SnapshotStatus afterStop =
    client.GetSnapshot(out var afterStopValues);

WriteLine($"Snapshot after Stop: {afterStop}");
WriteLine($"Values after Stop: {afterStopValues.Length}");

CoreLinkResult repeatedStop =
    await client.StopAsync();

WriteLine($"Repeated Stop: {repeatedStop}");

WriteLine("");
WriteLine("TEST COMPLETED");

void WriteLine(string message)
{
    lock (outputLock)
    {
        Console.WriteLine(
            $"[{DateTime.Now:HH:mm:ss.fff}] {message}");
    }
}
