using CoreLink.Transport.Configuration.Types;

namespace CoreLink.Transport.Configuration.Models;

internal readonly record struct TransportRegister(
    ushort Address,
    TransportRegisterType Type);