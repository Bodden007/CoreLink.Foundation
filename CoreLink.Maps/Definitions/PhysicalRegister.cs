using CoreLink.Contracts.Registers;

namespace CoreLink.Maps.Definitions;

/// <summary>
/// Человекочитаемое описание физического регистра.
/// Используется только внутри слоя Maps.
/// </summary>
public readonly record struct PhysicalRegister(
    string Name,
    ushort Address,
    RegisterType Type);