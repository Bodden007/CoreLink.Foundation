
using CoreLink.Contracts.Registers;

namespace CoreLink.Maps.Maps;

/// <summary>
/// Конфигурация подключения и периодов обработки
/// для конкретной сборки устройства.
///
/// PollIntervalMs — период опроса PLC.
/// RxIntervalMs — задержка между циклами Rx.
///
/// Эти интервалы независимы друг от друга.
/// </summary>
internal static class ConnectionMap
{
    public const string IpAddress = "192.168.0.10";

    public const ushort Port = 502;

    public const byte UnitId = 1;

    public const int PollIntervalMs = 500;

    public const int RxIntervalMs = 500;

    public const int ConnectTimeoutMs = 3000;

    public const int RequestTimeoutMs = 1000;

    public const int ReconnectDelayMs = 1000;

    public const RegisterOrder Order =
        RegisterOrder.ABCD;
}
