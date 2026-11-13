
using CoreLink.Contracts.Registers;

namespace CoreLink.Contracts.Maps;

/// <summary>
/// Описание физического регистра PLC.
///
/// Name — техническое имя параметра.
/// Address — адрес первого Modbus-регистра.
/// Type — Word или Float.
///
/// Units — обозначения единиц измерения.
///
/// Порядок записей определяет ValueId.
/// Float32 занимает два Modbus-регистра.
/// </summary>
public readonly record struct PhysicalRegister(
    string Name,
    ushort Address,
    RegisterType Type,
    Units Units = default);
