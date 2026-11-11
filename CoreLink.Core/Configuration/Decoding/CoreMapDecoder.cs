
using System.Buffers.Binary;
using System.Net;
using CoreLink.Contracts.Maps;
using CoreLink.Contracts.Registers;
using CoreLink.Core.Configuration.Models;

namespace CoreLink.Core.Configuration.Decoding;

/// <summary>
/// Декодирует бинарную карту CoreLink v3.
///
/// Преобразует бинарную конфигурацию в модель Core.
/// Не создаёт Transport и не выполняет сетевых операций.
/// </summary>
internal static class CoreMapDecoder
{
    public static DecodedCoreMap Decode(
        ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < CoreMapLayout.HeaderSize)
        {
            throw new InvalidOperationException(
                "Core map buffer is too short.");
        }

        var offset = 0;

        var version =
            buffer[offset++];

        if (version != CoreMapLayout.Version)
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


        var pollFunctionCode = buffer[offset++];

        if (pollFunctionCode is not (3 or 4))
        {
            throw new InvalidOperationException(
                $"Unsupported polling function code: {pollFunctionCode}.");
        }


        var connectTimeoutMs =
            BinaryPrimitives.ReadInt32LittleEndian(
                buffer.Slice(offset, 4));
        offset += 4;

        var requestTimeoutMs =
            BinaryPrimitives.ReadInt32LittleEndian(
                buffer.Slice(offset, 4));
        offset += 4;

        var reconnectDelayMs =
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
            (byte)RegisterOrder.DCBA)
        {
            throw new InvalidOperationException(
                "Core map register order is invalid.");
        }

        var registerOrder =
            (RegisterOrder)registerOrderValue;

        var valueCount =
            BinaryPrimitives.ReadUInt16LittleEndian(
                buffer.Slice(offset, 2));
        offset += 2;

        var expectedLength =
            CoreMapLayout.HeaderSize +
            valueCount * CoreMapLayout.ValueSize;

        if (expectedLength != buffer.Length)
        {
            throw new InvalidOperationException(
                "Core map value count is invalid.");
        }

        var registers =
            new RegisterDefinition[valueCount];

        for (var i = 0; i < valueCount; i++)
        {
            var address =
                BinaryPrimitives.ReadUInt16LittleEndian(
                    buffer.Slice(offset, 2));
            offset += 2;

            var typeValue =
                buffer[offset++];

            if (typeValue >
                (byte)RegisterType.Float)
            {
                throw new InvalidOperationException(
                    "Core map register type is invalid.");
            }

            registers[i] =
                new RegisterDefinition(
                    address,
                    (RegisterType)typeValue);
        }

        return new DecodedCoreMap
        {
            IpAddress = ipAddress,
            Port = port,
            UnitId = unitId,
            PollIntervalMs = pollIntervalMs,
            PollFunctionCode = pollFunctionCode,
            ConnectTimeoutMs = connectTimeoutMs,
            RequestTimeoutMs = requestTimeoutMs,
            ReconnectDelayMs = reconnectDelayMs,
            StartAddress = startAddress,
            RegisterLength = registerLength,
            RegisterOrder = registerOrder,
            Registers = registers
            
        };
    }
}
