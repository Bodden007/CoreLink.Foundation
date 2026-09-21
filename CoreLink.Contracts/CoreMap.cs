namespace CoreLink.Contracts.Maps;

public sealed class CoreMap
{
    public required ReadOnlyMemory<byte> Buffer { get; init; }

    public required int BufferLength { get; init; }
}