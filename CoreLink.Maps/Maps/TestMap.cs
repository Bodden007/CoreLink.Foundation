using CoreLink.Maps.Models;

namespace CoreLink.Maps.Maps;

/// <summary>
/// Временная карта для разработки CoreLink.Client.
/// </summary>
internal static class TestMap
{
    public static CoreLinkMap Create()
    {
        return new CoreLinkMap
        {
            IpAddress = "192.168.0.10",
            Port = 502,
            StartAddress = 100,

            TypeMap =
            [
                2,
                1,
                2,
                2,
                1
            ],

            PollIntervalMs = 500
        };
    }
}