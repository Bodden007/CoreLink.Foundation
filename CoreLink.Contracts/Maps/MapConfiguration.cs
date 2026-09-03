using CoreLink.Contracts.Registers;

namespace CoreLink.Contracts.Maps;

public sealed class MapConfiguration
{
    public required string IpAddress { get; init; }

    public ushort Port { get; init; }

    public int PollIntervalMs { get; init; }

    public ushort StartAddress { get; init; }

    public ushort Length { get; init; }

    public RegisterOrder RegisterOrder { get; init; }

    public required RegisterDefinition[] Registers { get; init; }
}