
using CoreLink.Contracts.Registers;

namespace CoreLink.Contracts.Maps;

/// <summary>
/// Публичная конфигурация CoreLink.
///
/// Создаётся внешним приложением и передаётся
/// клиенту при запуске.
///
/// Содержит параметры подключения, polling,
/// Rx и описание физических регистров.
/// </summary>
public sealed class CoreLinkMap
{
    public required string IpAddress { get; init; }

    public ushort Port { get; init; }

    public byte UnitId { get; init; }

    public int PollIntervalMs { get; init; }

    /// <summary>
    /// Функция штатного polling: FC03 или FC04.
    /// </summary>
    public byte PollFunctionCode { get; init; } = 4;

    public int ConnectTimeoutMs { get; init; }

    public int RequestTimeoutMs { get; init; }

    public int ReconnectDelayMs { get; init; }

    public RegisterOrder RegisterOrder { get; init; }

    public required PhysicalRegister[] Registers { get; init; }

    /// <summary>
    /// Интервал обработки Rx.
    /// </summary>
    public int RxIntervalMs { get; init; }

    /// <summary>
    /// Поддерживаемые системы единиц измерения.
    /// </summary>
    public required int[] SupportedUnitSystemIds { get; init; }
}
