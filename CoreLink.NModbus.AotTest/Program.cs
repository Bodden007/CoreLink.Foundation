const string Host = "192.168.0.10";
const int Port = 502;

const byte SlaveId = 255;

// Отдельный executable нужен именно для publish NativeAOT:
// обычный unit test не подтверждает, что нужные NModbus API пережили trimming.
using NModbusAotSmokeTest test =
    new(Host, Port);

// Сначала проверяем чистое локальное преобразование.
// Оно не зависит от доступности PLC и дает быстрый контроль ожидаемого word order.
float value = NModbusAotSmokeTest.RegistersToSingle(
    49343,
    17592);

Console.WriteLine($"FLOAT = {value}");

// После локальной части переходим к реальному сетевому пути.
// Недоступный PLC не считается аварией самого smoke test.
bool connected = await test.ConnectAsync(3000);

Console.WriteLine($"CONNECTED = {connected}");

if (!connected)
    return;

// FC04
// Чтение выполняется первым, потому что оно безопасно для реального PLC
// и одновременно подтверждает основной production-path будущего polling.
ushort[] registers = await test.ReadInputRegistersAsync(
    SlaveId,
    300,
    2);

Console.WriteLine($"READ COUNT = {registers.Length}");

// ВНИМАНИЕ:
// адреса записи здесь тестовые.
// Не запускать эти две операции на реальном ПЛК,
// пока не выбраны безопасные Holding Registers.
//
// Обе операции оставлены в executable, чтобы NativeAOT сохранил FC06 и FC16.
// Фактический запуск допустим только на согласованных тестовых регистрах.

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