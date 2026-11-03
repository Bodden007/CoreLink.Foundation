using CoreLink.Contracts.Core;
using CoreLink.Contracts.Maps;
using CoreLink.Core.Runtime;

namespace CoreLink.Adapter.CSharp;

/// <summary>
/// Адаптирует C# Core к контракту ICore.
/// Не выполняет polling и не хранит snapshot.
/// </summary>
public sealed unsafe class CSharpCoreAdapter : ICore
{
    private readonly CoreLinkCore _core;

    public CSharpCoreAdapter()
    {
        _core = new CoreLinkCore();
    }

    public event Action<CoreStatus>? StatusChanged
    {
        add => _core.StatusChanged += value;
        remove => _core.StatusChanged -= value;
    }

    public CoreStatus Start(CoreMap coreMap)
    {
        return _core.Start(coreMap);
    }

    public CoreStatus Stop()
    {
        return _core.Stop();
    }

    public SnapshotStatus GetSnapshot(
        float* buffer,
        int length)
    {
        return _core.GetSnapshot(buffer, length);
    }
}
