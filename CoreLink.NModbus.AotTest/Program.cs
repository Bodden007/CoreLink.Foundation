const string Host = "192.168.0.10";
const int Port = 502;

const byte SlaveId = 255;

using NModbusAotSmokeTest test =
    new(Host, Port);

float value = NModbusAotSmokeTest.RegistersToSingle(
    49343,
    17592);

Console.WriteLine($"FLOAT = {value}");

bool connected = await test.ConnectAsync(3000);

Console.WriteLine($"CONNECTED = {connected}");

if (!connected)
    return;

// FC04
ushort[] registers = await test.ReadInputRegistersAsync(
    SlaveId,
    300,
    2);

Console.WriteLine($"READ COUNT = {registers.Length}");

// ВНИМАНИЕ:
// адреса записи здесь тестовые.
// Не запускать эти две операции на реальном ПЛК,
// пока не выбраны безопасные Holding Registers.

bool singleWrite = await test.WriteSingleRegisterAsync(
    SlaveId,
    0,
    0);

Console.WriteLine($"WRITE SINGLE = {singleWrite}");

bool multipleWrite = await test.WriteMultipleRegistersAsync(
    SlaveId,
    0,
    [0, 0]);

Console.WriteLine($"WRITE MULTIPLE = {multipleWrite}");