using CoreLink.Contracts.Core;
using CoreLink.Contracts.Maps;

namespace CoreLink.Core.Runtime;

public sealed class CoreLinkCore : ICore
{
    private readonly CoreBackend _backend;

    public CoreLinkCore(CoreBackend backend)
    {
        _backend = backend;
    }

    public event Action? BufferReady;

    public event Action<CoreStatus>? StatusChanged;

    public CoreStatus Start(
        CoreMap coreMap,
        float[] coreBuffer)
    {
        return CoreStatus.Stopped;
    }

    public CoreStatus Stop()
    {
        return CoreStatus.Stopped;
    }

    public void BufferReleased()
    {
    }
}