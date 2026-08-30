using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CoreLink.Client.Interop;

/// <summary>
/// Низкоуровневые вызовы нативного CoreLink.
/// Только этот слой имеет право обращаться к FFI.
/// </summary>
internal static partial class NativeMethods
{
    private const string LibraryName = "corelink";

    /// <summary>
    /// Передаёт конфигурационный буфер в Rust.
    /// Rust обязан скопировать данные до возврата из функции.
    /// </summary>
    [LibraryImport(
       LibraryName,
       EntryPoint = "corelink_configure")]
    [UnmanagedCallConv(
       CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int Configure(
       nint configPointer);
}