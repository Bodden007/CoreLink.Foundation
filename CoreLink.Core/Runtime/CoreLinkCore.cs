
using CoreLink.Contracts.Core;
using CoreLink.Contracts.Maps;

namespace CoreLink.Core.Runtime;

/// <summary>
/// Реализация Core.
///
/// Управляет polling и хранением согласованных данных.
/// Интеграция с Transport выполняется отдельным этапом.
/// </summary>
public sealed unsafe class CoreLinkCore : ICore
{
    private readonly CoreBackend _backend;

    public CoreLinkCore(CoreBackend backend)
    {
        _backend = backend;
    }

    public event Action<CoreStatus>? StatusChanged;

    /// <summary>
    /// FIXME: временная заглушка.
    /// Удалить при подключении ModbusTransportSession.
    /// </summary>
    public CoreStatus Start(CoreMap coreMap)
    {
        return CoreStatus.Stopped;
    }

    /// <summary>
    /// FIXME: временная заглушка.
    /// Удалить при реализации lifecycle Core.
    /// </summary>
    public CoreStatus Stop()
    {
        return CoreStatus.Stopped;
    }

    /// <summary>
    /// FIXME: временная заглушка.
    /// Удалить при реализации согласованного snapshot.
    ///
    /// Указатель не разыменовывается и не сохраняется.
    /// </summary>
    public SnapshotStatus GetSnapshot(
        float* buffer,
        int length)
    {
        return SnapshotStatus.Stopped;
    }
}
