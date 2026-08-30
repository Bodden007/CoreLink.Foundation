using System.Buffers.Binary;
using CoreLink.Client.Results;
using CoreLink.Maps.Models;

namespace CoreLink.Client.Buffers;

/// <summary>
/// Формирует самодостаточный бинарный конфигурационный блок.
/// Rust получает только указатель и читает длину из заголовка.
/// </summary>
internal static class ConfigBufferBuilder
{
    private const int TotalLengthSize = 4;
    private const int IpSize = 4;
    private const int PortSize = 2;
    private const int StartAddressSize = 2;
    private const int PollIntervalSize = 4;
    private const int TypeMapLengthSize = 4;

    private const int HeaderSize =
        TotalLengthSize +
        IpSize +
        PortSize +
        StartAddressSize +
        PollIntervalSize +
        TypeMapLengthSize;

    public static CoreLinkResult TryBuild(
        CoreLinkMap? map,
        out byte[]? buffer)
    {
        buffer = null;

        if (map is null)
        {
            return CoreLinkResult.BadConfig;
        }

        if (!TryParseIpv4(map.IpAddress, out var ip))
        {
            return CoreLinkResult.BadConfig;
        }

        if (map.TypeMap is null ||
            map.TypeMap.Length == 0)
        {
            return CoreLinkResult.BadConfig;
        }

        if (map.PollIntervalMs <= 0)
        {
            return CoreLinkResult.BadConfig;
        }

        if (map.TypeMap.Length > int.MaxValue - HeaderSize)
        {
            return CoreLinkResult.BadConfig;
        }

        var totalLength =
            HeaderSize + map.TypeMap.Length;

        buffer = new byte[totalLength];

        var offset = 0;

        BinaryPrimitives.WriteInt32LittleEndian(
            buffer.AsSpan(offset, TotalLengthSize),
            totalLength);
        offset += TotalLengthSize;

        ip.CopyTo(
            buffer.AsSpan(offset, IpSize));
        offset += IpSize;

        BinaryPrimitives.WriteUInt16LittleEndian(
            buffer.AsSpan(offset, PortSize),
            map.Port);
        offset += PortSize;

        BinaryPrimitives.WriteUInt16LittleEndian(
            buffer.AsSpan(offset, StartAddressSize),
            map.StartAddress);
        offset += StartAddressSize;

        BinaryPrimitives.WriteInt32LittleEndian(
            buffer.AsSpan(offset, PollIntervalSize),
            map.PollIntervalMs);
        offset += PollIntervalSize;

        BinaryPrimitives.WriteInt32LittleEndian(
            buffer.AsSpan(offset, TypeMapLengthSize),
            map.TypeMap.Length);
        offset += TypeMapLengthSize;

        map.TypeMap.CopyTo(
            buffer.AsSpan(offset));

        return CoreLinkResult.Ok;
    }

    private static bool TryParseIpv4(
        string? ipAddress,
        out byte[] ip)
    {
        ip = new byte[4];

        if (string.IsNullOrWhiteSpace(ipAddress))
        {
            return false;
        }

        var parts = ipAddress.Split('.');

        if (parts.Length != 4)
        {
            return false;
        }

        for (var i = 0; i < 4; i++)
        {
            if (!byte.TryParse(
                    parts[i],
                    out ip[i]))
            {
                return false;
            }
        }

        return true;
    }
}