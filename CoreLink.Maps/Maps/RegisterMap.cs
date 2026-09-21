using CoreLink.Contracts.Registers;
using CoreLink.Maps.Definitions;

namespace CoreLink.Maps.Maps;

internal static class RegisterMap
{
    public static readonly PhysicalRegister[] Registers =
    [
        new("Status",     1, RegisterType.Word),
        new("Cutoff",     3, RegisterType.Word),
        new("Speed",      5, RegisterType.Float),
        new("Pressure_1", 7, RegisterType.Float),
        new("Pressure_2", 9, RegisterType.Float),
        new("Pressure_3", 11, RegisterType.Float),
        new("Pressure_4", 13, RegisterType.Float),
        new("Pressure_5", 15, RegisterType.Float),
    ];
}