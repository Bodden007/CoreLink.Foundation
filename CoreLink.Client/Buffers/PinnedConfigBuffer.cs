using System.Runtime.InteropServices;

namespace CoreLink.Client.Buffers;

/// <summary>
/// Временно фиксирует конфигурационный буфер в памяти
/// на время одного FFI-вызова.
/// </summary>
internal sealed class PinnedConfigBuffer : IDisposable
{
    private GCHandle _handle;

    public nint Pointer { get; }

    public int Length { get; }

    // FIXME: Конфигурационный FFI-буфер временно создаётся как managed byte[] и pin-ится.
    // Заменить на unmanaged allocation (NativeMemory.Alloc/Free), чтобы память
    // освобождалась детерминированно сразу после завершения Configure без участия GC.

    public PinnedConfigBuffer(byte[] buffer)
    {
        _handle = GCHandle.Alloc(
            buffer,
            GCHandleType.Pinned);

        Pointer = _handle.AddrOfPinnedObject();
        Length = buffer.Length;
    }

    public void Dispose()
    {
        if (_handle.IsAllocated)
        {
            _handle.Free();
        }
    }
}