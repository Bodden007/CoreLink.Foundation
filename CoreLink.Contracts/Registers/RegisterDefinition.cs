namespace CoreLink.Contracts.Registers;

public readonly record struct RegisterDefinition(
    ushort Address,
    RegisterType Type);