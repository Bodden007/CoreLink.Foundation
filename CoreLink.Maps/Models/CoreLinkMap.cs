namespace CoreLink.Maps.Models;

/// <summary>
/// Конфигурация источника данных CoreLink.
/// </summary>
public sealed class CoreLinkMap
{
    public required string IpAddress { get; init; }

    public ushort Port { get; init; }

    public ushort StartAddress { get; init; }

    public required byte[] TypeMap { get; init; }

    public int PollIntervalMs { get; init; }
}