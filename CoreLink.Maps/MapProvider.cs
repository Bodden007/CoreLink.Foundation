using CoreLink.Contracts.Maps;
using CoreLink.Contracts.Registers;
using CoreLink.Maps.Definitions;
using CoreLink.Maps.Maps;

namespace CoreLink.Maps;

/// <summary>
/// Загружает физическую карту и преобразует её
/// в техническую конфигурацию для Client/Core.
/// </summary>
public sealed class MapProvider : IMapProvider
{
    public bool TryLoad(
        string mapId,
        out MapConfiguration? configuration)
    {
        configuration = null;

        if (!TryLoadPhysical(
                mapId,
                out var physicalMap) ||
            physicalMap is null)
        {
            return false;
        }

        if (physicalMap.Registers.Length == 0)
        {
            return false;
        }

        var registers =
            new RegisterDefinition[physicalMap.Registers.Length];

        for (var i = 0; i < physicalMap.Registers.Length; i++)
        {
            var source = physicalMap.Registers[i];

            registers[i] = new RegisterDefinition(
                source.Address,
                source.Type);
        }

        var startAddress = GetStartAddress(
            physicalMap.Registers);

        var length = GetRegisterLength(
            physicalMap.Registers,
            startAddress);

        configuration = new MapConfiguration
        {
            IpAddress = physicalMap.IpAddress,
            Port = physicalMap.Port,
            PollIntervalMs = physicalMap.PollIntervalMs,
            StartAddress = startAddress,
            Length = length,
            RegisterOrder = physicalMap.RegisterOrder,
            Registers = registers
        };

        return true;
    }

    /// <summary>
    /// Возвращает исходную человекочитаемую карту.
    /// Не входит в IMapProvider и не используется Client.
    /// Нужен сейчас для проверки Maps в TestHost.
    /// </summary>
    public bool TryLoadPhysical(
        string mapId,
        out PhysicalMap? map)
    {
        map = mapId switch
        {
            "test" => TestMap.Create(),
            _ => null
        };

        return map is not null;
    }

    private static ushort GetStartAddress(
        PhysicalRegister[] registers)
    {
        var startAddress = registers[0].Address;

        for (var i = 1; i < registers.Length; i++)
        {
            if (registers[i].Address < startAddress)
            {
                startAddress = registers[i].Address;
            }
        }

        return startAddress;
    }

    private static ushort GetRegisterLength(
        PhysicalRegister[] registers,
        ushort startAddress)
    {
        var lastAddress = startAddress;

        for (var i = 0; i < registers.Length; i++)
        {
            var register = registers[i];

            var registerLength =
                register.Type == RegisterType.Float
                    ? 2
                    : 1;

            var endAddress =
                register.Address + registerLength - 1;

            if (endAddress > lastAddress)
            {
                lastAddress = (ushort)endAddress;
            }
        }

        return (ushort)(
            lastAddress -
            startAddress +
            1);
    }
}