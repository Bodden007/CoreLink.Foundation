using CoreLink.Client;
using CoreLink.Maps;
using CoreLink.Contracts.Core;

var mapProvider =
    new MapProvider();

var client =
    new CoreLinkClient(
        mapProvider,
        CoreBackend.CSharp);

var result =
    client.Start("main");

Console.WriteLine(
    "======================================");

Console.WriteLine(
    "CLIENT CORE MAP");

Console.WriteLine(
    "======================================");

Console.WriteLine();

Console.WriteLine(
    $"Start result: {result}");

Console.WriteLine(
    $"Client state: {client.State}");

Console.WriteLine();

var coreMap =
    client.CoreMap;

if (coreMap is null)
{
    Console.WriteLine(
        "CORE MAP IS NULL");

    return;
}

var buffer =
    coreMap.Buffer.Span;

Console.WriteLine(
    $"Buffer length: {buffer.Length} bytes");

Console.WriteLine();

Console.WriteLine("HEX:");
Console.WriteLine(
    Convert.ToHexString(buffer));

Console.WriteLine();

Console.WriteLine("Offset | Byte");
Console.WriteLine("-------+-----");

for (var i = 0; i < buffer.Length; i++)
{
    Console.WriteLine(
        $"{i,6} | {buffer[i]:X2}");
}