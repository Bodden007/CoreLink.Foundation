
using CoreLink.Contracts.Core;
using CoreLink.Contracts.Maps;

namespace CoreLink.Core.Runtime;

/// <summary>
/// C# реализация ядра CoreLink.
///
/// Управляет polling, взаимодействием с Transport
/// и хранением согласованного snapshot.
///
/// Не зависит от Client и адаптеров.
/// </summary>
public sealed unsafe class CoreLinkCore
{
    public event Action<CoreStatus>? StatusChanged;

    /// <summary>
    /// FIXME: Временная заглушка.
    /// Удалить при подключении ModbusTransportSession.
    /// </summary>
    public CoreStatus Start(CoreMap coreMap)
    {
        return CoreStatus.Stopped;
    }

    /// <summary>
    /// FIXME: Временная заглушка.
    /// Удалить при реализации lifecycle Core.
    /// </summary>
    public CoreStatus Stop()
    {
        return CoreStatus.Stopped;
    }

    /// <summary>
    /// FIXME: Временная заглушка.
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
