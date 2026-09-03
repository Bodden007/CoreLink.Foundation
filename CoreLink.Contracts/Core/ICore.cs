using CoreLink.Contracts.Maps;

namespace CoreLink.Contracts.Core;

public interface ICore
{
    Task<CoreStatus> StartAsync(
        MapConfiguration configuration,
        float[] coreBuffer,
        CancellationToken cancellationToken);

    Task<CoreStatus> StopAsync();

    void BufferReleased();

    event Action? BufferReady;

    event Action<CoreStatus>? StatusChanged;
}