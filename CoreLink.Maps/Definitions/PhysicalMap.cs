using CoreLink.Contracts.Registers;

namespace CoreLink.Maps.Definitions;

/// <summary>
/// Полная человекочитаемая физическая карта устройства.
/// </summary>
public sealed class PhysicalMap
{
    public required string IpAddress { get; init; }

    public ushort Port { get; init; }

    public int PollIntervalMs { get; init; }

    public RegisterOrder RegisterOrder { get; init; }

    public required PhysicalRegister[] Registers { get; init; }
}