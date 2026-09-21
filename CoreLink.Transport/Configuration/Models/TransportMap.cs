using CoreLink.Transport.Configuration.Types;

namespace CoreLink.Transport.Configuration.Models;

internal sealed class TransportMap
{
    public required string IpAddress { get; init; }

    public required ushort Port { get; init; }

    public required byte UnitId { get; init; }

    public required int PollIntervalMs { get; init; }

    public required ushort StartAddress { get; init; }

    public required ushort RegisterLength { get; init; }

    public required TransportRegisterOrder RegisterOrder { get; init; }

    public required TransportRegister[] Registers { get; init; }
}