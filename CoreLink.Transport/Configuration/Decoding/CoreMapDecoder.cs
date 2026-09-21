using System.Buffers.Binary;
using System.Net;
using CoreLink.Transport.Configuration.Models;
using CoreLink.Transport.Configuration.Types;

namespace CoreLink.Transport.Configuration.Decoding;

internal static class CoreMapDecoder
{
    private const byte SupportedVersion = 1;

    private const int HeaderSize = 21;

    private const int ValueSize = 3;

    public static TransportMap Decode(
        ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < HeaderSize)
        {
            throw new InvalidOperationException(
                "Core map buffer is too short.");
        }

        var offset = 0;

        var version =
            buffer[offset++];

        if (version != SupportedVersion)
        {
            throw new InvalidOperationException(
                $"Unsupported core map version: {version}.");
        }

        var totalLength =
            BinaryPrimitives.ReadUInt16LittleEndian(
                buffer.Slice(offset, 2));
        offset += 2;

        if (totalLength != buffer.Length)
        {
            throw new InvalidOperationException(
                "Core map buffer length is invalid.");
        }

        var ipAddress =
            new IPAddress(
                buffer.Slice(offset, 4))
            .ToString();
        offset += 4;

        var port =
            BinaryPrimitives.ReadUInt16LittleEndian(
                buffer.Slice(offset, 2));
        offset += 2;

        var unitId =
            buffer[offset++];

        var pollIntervalMs =
            BinaryPrimitives.ReadInt32LittleEndian(
                buffer.Slice(offset, 4));
        offset += 4;

        var startAddress =
            BinaryPrimitives.ReadUInt16LittleEndian(
                buffer.Slice(offset, 2));
        offset += 2;

        var registerLength =
            BinaryPrimitives.ReadUInt16LittleEndian(
                buffer.Slice(offset, 2));
        offset += 2;

        var registerOrderValue =
            buffer[offset++];

        if (registerOrderValue >
            (byte)TransportRegisterOrder.DCBA)
        {
            throw new InvalidOperationException(
                "Core map register order is invalid.");
        }

        var registerOrder =
            (TransportRegisterOrder)registerOrderValue;

        var valueCount =
            BinaryPrimitives.ReadUInt16LittleEndian(
                buffer.Slice(offset, 2));
        offset += 2;

        var expectedLength =
            HeaderSize +
            valueCount * ValueSize;

        if (expectedLength != buffer.Length)
        {
            throw new InvalidOperationException(
                "Core map value count is invalid.");
        }

        var registers =
            new TransportRegister[valueCount];

        for (var i = 0; i < valueCount; i++)
        {
            var address =
                BinaryPrimitives.ReadUInt16LittleEndian(
                    buffer.Slice(offset, 2));
            offset += 2;

            var typeValue =
                buffer[offset++];

            if (typeValue >
                (byte)TransportRegisterType.Float)
            {
                throw new InvalidOperationException(
                    "Core map register type is invalid.");
            }

            registers[i] =
                new TransportRegister(
                    address,
                    (TransportRegisterType)typeValue);
        }

        return new TransportMap
        {
            IpAddress = ipAddress,
            Port = port,
            UnitId = unitId,
            PollIntervalMs = pollIntervalMs,
            StartAddress = startAddress,
            RegisterLength = registerLength,
            RegisterOrder = registerOrder,
            Registers = registers
        };
    }
}