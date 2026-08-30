using CoreLink.Client;
using CoreLink.Client.Results;

var client = new CoreLinkClient();

Console.WriteLine($"Initial: {client.State}");

var startResult = client.Start("test");

Console.WriteLine($"Start result: {startResult}");
Console.WriteLine($"Started: {client.State}");

var configResult = client.BuildConfigBuffer(out var config);

Console.WriteLine($"Config result: {configResult}");

if (configResult == CoreLinkResult.Ok && config is not null)
{
    Console.WriteLine($"Config length: {config.Length}");
    Console.WriteLine($"Config HEX: {Convert.ToHexString(config)}");
}
// FIXME Временная проверка pinning конфигурационного буфера.
var pinResult = client.TestConfigPin(
    out var pointer,
    out var length);

Console.WriteLine($"Pin result: {pinResult}");
Console.WriteLine($"Pointer: 0x{pointer:X}");
Console.WriteLine($"Pinned length: {length}");
//

// FIXME: Временная проверка реального FFI-вызова CoreLink.Client -> Rust.
// Удалить после подтверждения вызова corelink_configure.
var ffiResult = client.TestConfigure();

Console.WriteLine($"FFI result: {ffiResult}");

client.Stop();

Console.WriteLine($"Stopped: {client.State}");