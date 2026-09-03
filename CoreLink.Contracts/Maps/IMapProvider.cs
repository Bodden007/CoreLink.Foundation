namespace CoreLink.Contracts.Maps;

public interface IMapProvider
{
    bool TryLoad(
        string mapId,
        out MapConfiguration? configuration);
}