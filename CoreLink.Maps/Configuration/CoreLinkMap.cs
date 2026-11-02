
using CoreLink.Contracts.Registers;
using CoreLink.Maps.Definitions;

namespace CoreLink.Maps.Configuration;

internal sealed class CoreLinkMap
{
    public required string IpAddress { get; init; }

    public ushort Port { get; init; }

    public byte UnitId { get; init; }

    public int PollIntervalMs { get; init; }

    public int ConnectTimeoutMs { get; init; }

    public int RequestTimeoutMs { get; init; }

    public int ReconnectDelayMs { get; init; }

    public RegisterOrder RegisterOrder { get; init; }

    public required PhysicalRegister[] Registers { get; init; }
}
