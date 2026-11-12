using CoreLink.Contracts.Registers;
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
    /// Асинхронно читает значения PLC по логическим ValueId.
    ///
    /// Адреса и типы регистров определяются картой Core.
    /// Порядок результата соответствует порядку ValueId.
    ///
    /// При любой ошибке чтение прекращается.
    /// Частичные результаты не возвращаются.
    /// Штатные ошибки преобразуются в CoreReadStatus.
    ///
    /// Прямое чтение не изменяет основной snapshot.
    /// </summary>
    Task<CoreReadResult> ReadAsync(
        IReadOnlyList<int> valueIds);


    /// <summary>
    /// Выполняет одиночное адресное чтение PLC.
    ///
    /// Address — логический адрес регистра,
    /// не ValueId из карты polling.
    ///
    /// Word занимает один регистр,
    /// Float — два последовательных регистра.
    ///
    /// Функция FC03/FC04 выбирается явно.
    /// Чтение проходит через существующий Dispatcher.
    ///
    /// Snapshot и Rx не изменяются.
    /// Ошибки возвращаются через CoreReadStatus.
    /// </summary>
    Task<CoreSingleReadResult> ReadSingleAsync(
        ushort address,
        RegisterType type,
        ModbusReadFunction function);


    /// <summary>
    /// Уведомляет об изменении состояния Core.
    /// </summary>
    event Action<CoreStatus>? StatusChanged;


    /// <summary>
    /// Записывает один Word через FC06.
    ///
    /// Адрес логический, не ValueId.
    /// Не изменяет snapshot и Rx.
    /// </summary>
    Task<CoreWriteStatus> WriteWordAsync(
        ushort address,
        ushort value);

    /// <summary>
    /// Записывает Float32 через FC16.
    ///
    /// Занимает два последовательных регистра.
    /// RegisterOrder берётся из текущей сессии.
    /// </summary>
    Task<CoreWriteStatus> WriteFloatAsync(
        ushort address,
        float value);

    /// <summary>
    /// Записывает непрерывный диапазон через FC16.
    ///
    /// Дескриптор создан Client.
    /// Максимум 123 физических регистра.
    /// Весь запрос проверяется до отправки.
    ///
    /// Результат возвращается статусом.
    /// Повторная запись не выполняется.
    /// </summary>
    Task<CoreWriteStatus> WriteBatchAsync(
        ReadOnlyMemory<byte> descriptor);

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
