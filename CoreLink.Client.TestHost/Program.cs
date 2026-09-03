using CoreLink.Contracts.Maps;
using CoreLink.Maps;

var mapProvider = new MapProvider();

Console.WriteLine("======================================");
Console.WriteLine("PHYSICAL MAP");
Console.WriteLine("======================================");
Console.WriteLine();

if (!mapProvider.TryLoadPhysical(
        "test",
        out var physicalMap) ||
    physicalMap is null)
{
    Console.WriteLine("PHYSICAL MAP LOAD ERROR");

    return;
}

Console.WriteLine($"IP:     {physicalMap.IpAddress}");
Console.WriteLine($"Port:   {physicalMap.Port}");
Console.WriteLine($"Poll:   {physicalMap.PollIntervalMs} ms");
Console.WriteLine($"Order:  {physicalMap.RegisterOrder}");
Console.WriteLine();

Console.WriteLine(
    "Index | Name                 | Address | Type");

Console.WriteLine(
    "------+----------------------+---------+------");

for (var i = 0; i < physicalMap.Registers.Length; i++)
{
    var register = physicalMap.Registers[i];

    Console.WriteLine(
        $"{i,5} | " +
        $"{register.Name,-20} | " +
        $"{register.Address,7} | " +
        $"{register.Type}");
}

Console.WriteLine();
Console.WriteLine("======================================");
Console.WriteLine("CONTRACT MAP");
Console.WriteLine("======================================");
Console.WriteLine();

IMapProvider provider = mapProvider;

if (!provider.TryLoad(
        "test",
        out var configuration) ||
    configuration is null)
{
    Console.WriteLine("CONTRACT MAP LOAD ERROR");

    return;
}

Console.WriteLine($"IP:            {configuration.IpAddress}");
Console.WriteLine($"Port:          {configuration.Port}");
Console.WriteLine($"Poll:          {configuration.PollIntervalMs} ms");
Console.WriteLine($"StartAddress:  {configuration.StartAddress}");
Console.WriteLine($"Length:        {configuration.Length}");
Console.WriteLine($"Order:         {configuration.RegisterOrder}");
Console.WriteLine($"Values:        {configuration.Registers.Length}");
Console.WriteLine();

Console.WriteLine(
    "Index | Address | Type");

Console.WriteLine(
    "------+---------+------");

for (var i = 0; i < configuration.Registers.Length; i++)
{
    var register = configuration.Registers[i];

    Console.WriteLine(
        $"{i,5} | " +
        $"{register.Address,7} | " +
        $"{register.Type}");
}