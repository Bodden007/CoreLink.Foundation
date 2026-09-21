using System.Buffers.Binary;
using System.Net;
using CoreLink.Contracts.Maps;
using CoreLink.Contracts.Registers;
using CoreLink.Maps.Definitions;

namespace CoreLink.Maps.Configuration;

internal static class CoreMapEncoder
{
    public static CoreMap Encode(
        CoreLinkMap map)
    {
        var registers = map.Registers;

        if (registers.Length == 0)
        {
            throw new InvalidOperationException(
                "Register map is empty.");
        }

        var startAddress =
            GetStartAddress(registers);

        var registerLength =
            GetRegisterLength(
                registers,
                startAddress);

        var valueCount =
            registers.Length;

        var totalLength =
            CoreMapLayout.HeaderSize +
            valueCount * CoreMapLayout.ValueSize;

        var buffer =
            new byte[totalLength];

        var span = buffer.AsSpan();
        var offset = 0;

        span[offset++] =
            CoreMapLayout.Version;

        BinaryPrimitives.WriteUInt16LittleEndian(
            span.Slice(offset, 2),
            checked((ushort)totalLength));
        offset += 2;

        var ipAddress =
            IPAddress.Parse(map.IpAddress)
                .GetAddressBytes();

        if (ipAddress.Length != 4)
        {
            throw new InvalidOperationException(
                "Only IPv4 is supported.");
        }

        ipAddress.CopyTo(
            span.Slice(offset, 4));
        offset += 4;

        BinaryPrimitives.WriteUInt16LittleEndian(
            span.Slice(offset, 2),
            map.Port);
        offset += 2;

        span[offset++] =
            map.UnitId;

        BinaryPrimitives.WriteInt32LittleEndian(
            span.Slice(offset, 4),
            map.PollIntervalMs);
        offset += 4;

        BinaryPrimitives.WriteUInt16LittleEndian(
            span.Slice(offset, 2),
            startAddress);
        offset += 2;

        BinaryPrimitives.WriteUInt16LittleEndian(
            span.Slice(offset, 2),
            registerLength);
        offset += 2;

        span[offset++] =
            (byte)map.RegisterOrder;

        BinaryPrimitives.WriteUInt16LittleEndian(
            span.Slice(offset, 2),
            checked((ushort)valueCount));
        offset += 2;

        for (var i = 0; i < valueCount; i++)
        {
            var register =
                registers[i];

            BinaryPrimitives.WriteUInt16LittleEndian(
                span.Slice(offset, 2),
                register.Address);
            offset += 2;

            span[offset++] =
                (byte)register.Type;
        }

        return new CoreMap
        {
            Buffer = buffer,
            BufferLength = valueCount
        };
    }

    private static ushort GetStartAddress(
        PhysicalRegister[] registers)
    {
        var startAddress =
            registers[0].Address;

        for (var i = 1; i < registers.Length; i++)
        {
            if (registers[i].Address < startAddress)
            {
                startAddress =
                    registers[i].Address;
            }
        }

        return startAddress;
    }

    private static ushort GetRegisterLength(
        PhysicalRegister[] registers,
        ushort startAddress)
    {
        var lastAddress =
            startAddress;

        for (var i = 0; i < registers.Length; i++)
        {
            var register =
                registers[i];

            var valueLength =
                register.Type == RegisterType.Float
                    ? 2
                    : 1;

            var endAddress =
                register.Address +
                valueLength -
                1;

            if (endAddress > lastAddress)
            {
                lastAddress =
                    checked((ushort)endAddress);
            }
        }

        return checked(
            (ushort)(
                lastAddress -
                startAddress +
                1));
    }
}