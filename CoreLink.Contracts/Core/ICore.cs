using CoreLink.Contracts.Maps;

namespace CoreLink.Contracts.Core;

public interface ICore
{
    CoreStatus Start(
        CoreMap coreMap,
        float[] coreBuffer);

    CoreStatus Stop();

    event Action? BufferReady;

    event Action<CoreStatus>? StatusChanged;
}