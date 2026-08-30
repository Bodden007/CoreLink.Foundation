using CoreLink.Client.Buffers;
using CoreLink.Client.Interop;
using CoreLink.Client.Lifecycle;
using CoreLink.Client.Results;
using CoreLink.Maps;
using CoreLink.Maps.Models;

namespace CoreLink.Client;

/// <summary>
/// Главная публичная точка управления CoreLink-стеком.
/// </summary>
public sealed class CoreLinkClient
{
    private CoreLinkMap? _map;

    public CoreLinkState State { get; private set; } = CoreLinkState.Created;

    public CoreLinkResult Start(string mapId)
    {
        if (State is CoreLinkState.Starting or CoreLinkState.Running)
        {
            return CoreLinkResult.Ok;
        }

        State = CoreLinkState.Starting;

        if (!MapManager.TryLoad(mapId, out _map))
        {
            State = CoreLinkState.Faulted;
            return CoreLinkResult.BadConfig;
        }

        State = CoreLinkState.Running;

        return CoreLinkResult.Ok;
    }

    // FIXME: Временный публичный доступ к бинарному конфигурационному буферу.
    // Удалить после завершения проверки Configure через FFI.
    public CoreLinkResult BuildConfigBuffer(out byte[]? buffer)
    {
        return ConfigBufferBuilder.TryBuild(_map, out buffer);
    }

    public void Stop()
    {
        if (State is CoreLinkState.Stopped or CoreLinkState.Created)
        {
            return;
        }

        State = CoreLinkState.Stopping;

        _map = null;

        State = CoreLinkState.Stopped;
    }

    public CoreLinkResult Configure()
    {
        var buildResult = ConfigBufferBuilder.TryBuild(
            _map,
            out var buffer);

        if (buildResult != CoreLinkResult.Ok ||
            buffer is null)
        {
            return buildResult;
        }

        using var pinned = new PinnedConfigBuffer(buffer);

        var nativeResult = NativeMethods.Configure(
    pinned.Pointer);

        return (CoreLinkResult)nativeResult;
    }

    // FIXME: Временный метод для проверки pinning конфигурационного буфера.
    // Удалить после завершения FFI-тестов.
    public CoreLinkResult TestConfigPin(
        out nint pointer,
        out int length)
    {
        pointer = 0;
        length = 0;

        var result = ConfigBufferBuilder.TryBuild(
            _map,
            out var buffer);

        if (result != CoreLinkResult.Ok ||
            buffer is null)
        {
            return result;
        }

        using var pinned = new PinnedConfigBuffer(buffer);

        pointer = pinned.Pointer;
        length = pinned.Length;

        return CoreLinkResult.Ok;
    }

    // FIXME: Временный метод для проверки вызова CoreLink.Client -> Rust.
    // Удалить после появления штатного Configure без тестового кода.
    public int TestConfigure()
    {
        var buildResult = ConfigBufferBuilder.TryBuild(
            _map,
            out var buffer);

        if (buildResult != CoreLinkResult.Ok ||
            buffer is null)
        {
            return -1;
        }

        using var pinned = new PinnedConfigBuffer(buffer);

        return NativeMethods.Configure(
       pinned.Pointer);
    }
}