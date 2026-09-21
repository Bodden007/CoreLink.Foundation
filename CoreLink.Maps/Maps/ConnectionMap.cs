using CoreLink.Contracts.Registers;

namespace CoreLink.Maps.Maps;

internal static class ConnectionMap
{
    public const string IpAddress = "192.168.0.10";
    public const ushort Port = 502;
    public const byte UnitId = 1;
    public const int PollIntervalMs = 500;
    public const RegisterOrder Order =
        RegisterOrder.ABCD;
}