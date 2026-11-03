
using CoreLink.Core.Configuration.Models;
using CoreLink.Transport.Modbus.Configuration;

namespace CoreLink.Core.Configuration;

/// <summary>
/// Формирует конфигурацию Modbus Transport
/// из декодированной карты Core.
///
/// Не управляет транспортной сессией
/// и не выполняет сетевых операций.
/// </summary>
internal static class ModbusConfigFactory
{
    public static ModbusConnectionConfig Create(
        DecodedCoreMap map)
    {
        ArgumentNullException.ThrowIfNull(map);

        return new ModbusConnectionConfig
        {
            Host = map.IpAddress,
            Port = map.Port,
            SlaveId = map.UnitId,

            PollIntervalMs = map.PollIntervalMs,

            InputStartAddress = map.StartAddress,
            InputRegisterCount = map.RegisterLength,

            ConnectTimeoutMs = map.ConnectTimeoutMs,
            RequestTimeoutMs = map.RequestTimeoutMs,
            ReconnectDelayMs = map.ReconnectDelayMs
        };
    }
}
