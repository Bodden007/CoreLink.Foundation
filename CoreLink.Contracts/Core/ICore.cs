
using CoreLink.Contracts.Maps;

namespace CoreLink.Contracts.Core;

/// <summary>
/// Контракт управления Core.
///
/// Core самостоятельно выполняет polling и хранит
/// последние согласованные значения.
///
/// Клиентский буфер принадлежит Client.
/// Core получает временное право записи только
/// на время синхронного GetSnapshot.
/// </summary>
public unsafe interface ICore
{
    /// <summary>
    /// Инициализирует и запускает Core по заданной карте.
    /// Не принимает клиентский массив.
    /// </summary>
    CoreStatus Start(CoreMap coreMap);

    /// <summary>
    /// Синхронно останавливает Core.
    ///
    /// Возвращает управление после завершения polling
    /// и активных операций Transport.
    /// </summary>
    CoreStatus Stop();

    /// <summary>
    /// Синхронно копирует последний согласованный snapshot
    /// в память, предоставленную Client.
    ///
    /// Указатель действителен только во время вызова.
    /// Core не сохраняет указатель и не владеет памятью.
    ///
    /// При неуспешном результате буфер не изменяется.
    /// </summary>
    SnapshotStatus GetSnapshot(
        float* buffer,
        int length);

    /// <summary>
    /// Уведомляет об изменении состояния Core.
    /// </summary>
    event Action<CoreStatus>? StatusChanged;
}

/// <summary>
/// Результат синхронного получения snapshot.
/// Не является состоянием жизненного цикла Core.
/// </summary>
public enum SnapshotStatus
{
    Ok = 0,
    NoData = 1,
    Disconnected = 2,
    BadBuffer = 3,
    Stopped = 4
}
