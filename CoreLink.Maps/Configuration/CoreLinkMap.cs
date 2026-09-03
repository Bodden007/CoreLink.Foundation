using CoreLink.Maps.Registers;

namespace CoreLink.Maps.Configuration;

/// <summary>
/// Содержит параметры источника данных и физическую карту значений CoreLink.
/// </summary>
public sealed class CoreLinkMap
{
    public required string IpAddress { get; init; }

    public ushort Port { get; init; }

    public int PollIntervalMs { get; init; }

    public RegisterOrder RegisterOrder { get; init; }

    public required RegisterMapEntry[] Registers { get; init; }
}