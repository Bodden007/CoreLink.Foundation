using CoreLink.Client;
using CoreLink.Maps;
using CoreLink.Contracts.Core;
using CoreLink.Transport.Diagnostics;

var mapProvider =
    new MapProvider();

if (!mapProvider.TryLoad(
        "physical",
        out var testMap) ||
    testMap is null)
{
    Console.WriteLine("MAP LOAD FAILED");
    return;
}

Console.WriteLine();
Console.WriteLine("======================================");
Console.WriteLine("CORE MAP BUFFER");
Console.WriteLine("======================================");

Console.WriteLine(
    $"BufferLength: {testMap.BufferLength}");

Console.WriteLine(
    $"Config bytes: {testMap.Buffer.Length}");

Console.WriteLine(
    $"HEX: {Convert.ToHexString(testMap.Buffer.Span)}");

Console.WriteLine();

// FIXME: Temporary Transport map decoder test.
// Remove after Core -> Transport integration is verified.
TransportMapDiagnostics.Print(
    testMap.Buffer);

// FIXME: Temporary Transport decoder validation tests.
// Remove after Core -> Transport integration is verified.

Console.WriteLine(
    "======================================");

Console.WriteLine(
    "TRANSPORT VALIDATION TESTS");

Console.WriteLine(
    "======================================");

TestInvalidMap(
    "Invalid Version",
    testMap.Buffer,
    buffer => buffer[0] = 99);

TestInvalidMap(
    "Invalid Length",
    testMap.Buffer,
    buffer => buffer[1] = 0);

TestInvalidMap(
    "Invalid RegisterOrder",
    testMap.Buffer,
    buffer => buffer[18] = 99);

TestInvalidMap(
    "Invalid RegisterType",
    testMap.Buffer,
    buffer => buffer[23] = 99);

// FIXME: Temporary Transport decoder validation helper.
// Remove after Core -> Transport integration is verified.
static void TestInvalidMap(
    string name,
    ReadOnlyMemory<byte> source,
    Action<byte[]> corrupt)
{
    var buffer =
        source.ToArray();

    corrupt(buffer);

    try
    {
        TransportMapDiagnostics.Print(
            buffer);

        Console.WriteLine(
            $"{name}: FAILED");
    }
    catch (InvalidOperationException ex)
    {
        Console.WriteLine(
            $"{name}: OK");

        Console.WriteLine(
            $"  {ex.Message}");
    }
}