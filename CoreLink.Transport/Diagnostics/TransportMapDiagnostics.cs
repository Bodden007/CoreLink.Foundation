using CoreLink.Transport.Configuration.Decoding;

namespace CoreLink.Transport.Diagnostics;

// FIXME: Temporary diagnostic API.
// Remove after Core -> Transport integration is verified.
public static class TransportMapDiagnostics
{
    public static void Print(
        ReadOnlyMemory<byte> buffer)
    {
        var map =
            CoreMapDecoder.Decode(
                buffer.Span);

        Console.WriteLine();
        Console.WriteLine(
            "======================================");

        Console.WriteLine(
            "TRANSPORT DECODED MAP");

        Console.WriteLine(
            "======================================");

        Console.WriteLine(
            $"IP:       {map.IpAddress}");

        Console.WriteLine(
            $"Port:     {map.Port}");

        Console.WriteLine(
            $"UnitId:   {map.UnitId}");

        Console.WriteLine(
            $"Poll:     {map.PollIntervalMs} ms");

        Console.WriteLine(
            $"Start:    {map.StartAddress}");

        Console.WriteLine(
            $"Length:   {map.RegisterLength}");

        Console.WriteLine(
            $"Order:    {map.RegisterOrder}");

        Console.WriteLine(
            $"Values:   {map.Registers.Length}");

        Console.WriteLine();

        Console.WriteLine(
            "Address | Type");

        Console.WriteLine(
            "--------+------");

        foreach (var register in map.Registers)
        {
            Console.WriteLine(
                $"{register.Address,7} | {register.Type}");
        }

        Console.WriteLine();
    }
}