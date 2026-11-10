using CoreLink.Contracts.Registers;

namespace CoreLink.Maps.Definitions;

/// <summary>
/// Человекочитаемое описание физического регистра.
/// Используется только внутри слоя Maps.
///
/// Name — техническое имя параметра.
/// Address — адрес первого Modbus-регистра.
/// Type — тип значения Word или Float.
///
/// Units — единицы измерения UA/EU.
/// Если не указаны, используются пустые значения.
///
/// Порядок записей в RegisterMap определяет ValueId.
/// Для Float32 два Modbus-регистра образуют одно значение.
/// </summary>
public readonly record struct PhysicalRegister(
    string Name,
    ushort Address,
    RegisterType Type,
    Units Units = default);
