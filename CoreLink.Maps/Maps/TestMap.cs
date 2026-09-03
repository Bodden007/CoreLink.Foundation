using CoreLink.Contracts.Registers;
using CoreLink.Maps.Definitions;

namespace CoreLink.Maps.Maps;

internal static class TestMap
{
    public static PhysicalMap Create()
    {
        return new PhysicalMap
        {
            IpAddress = "192.168.0.10",
            Port = 502,
            PollIntervalMs = 500,
            RegisterOrder = RegisterOrder.ABCD,

            Registers =
            [
                new PhysicalRegister(
                    "Pressure_1",
                    8,
                    RegisterType.Float),

                new PhysicalRegister(
                    "Opko_1",
                    12,
                    RegisterType.Float),

                new PhysicalRegister(
                    "StatOpko_1",
                    14,
                    RegisterType.Word),

                new PhysicalRegister(
                    "TempOutlet",
                    20,
                    RegisterType.Float),

                new PhysicalRegister(
                    "Vaporizer",
                    22,
                    RegisterType.Float),

                new PhysicalRegister(
                    "Bath",
                    24,
                    RegisterType.Float)
            ]
        };
    }
}