using CoreLink.Client;
using CoreLink.Maps;
using CoreLink.Contracts.Core;
using CoreLink.Transport.Diagnostics;

// TestHost намеренно начинает с реального MapProvider:
// здесь проверяется не только формат буфера, но и актуальная цепочка Maps -> Contracts.
var mapProvider =
    new MapProvider();

// "physical" — текущая тестовая карта проекта.
// Если она перестала грузиться, дальнейшая проверка transport buffer бессмысленна.
if (!mapProvider.TryLoad(
    "physical",
    out var testMap) ||
    testMap is null)
{
    Console.WriteLine("MAP LOAD FAILED");
    return;
}

Console.WriteLine();
Console.WriteLine("======================================");
Console.WriteLine("CORE MAP BUFFER");
Console.WriteLine("======================================");

// BufferLength — длина runtime-массива значений, а не размер config buffer.
// Выводим оба параметра рядом, чтобы не перепутать два независимых контракта.
Console.WriteLine(
    $"BufferLength: {testMap.BufferLength}");

Console.WriteLine(
    $"Config bytes: {testMap.Buffer.Length}");

// HEX оставлен как диагностический "эталон на глаз":
// при изменении encoder сразу видно изменение бинарного layout.
Console.WriteLine(
    $"HEX: {Convert.ToHexString(testMap.Buffer.Span)}");

Console.WriteLine();

// FIXME: Temporary Transport map decoder test.
// Remove after Core -> Transport integration is verified.
//
// Вызов проходит через диагностический декодер Transport, чтобы одна и та же
// конфигурация проверялась на обеих сторонах будущей границы Core -> Transport.
TransportMapDiagnostics.Print(
    testMap.Buffer);

// FIXME: Temporary Transport decoder validation tests.
// Remove after Core -> Transport integration is verified.
//
// Ниже deliberately corrupt один байт за тест: так проще понять,
// какой именно инвариант decoder обязан отвергнуть.
Console.WriteLine(
    "======================================");

Console.WriteLine(
    "TRANSPORT VALIDATION TESTS");

Console.WriteLine(
    "======================================");

// Неверная версия проверяет жесткую совместимость формата.
// Decoder не должен молча интерпретировать неизвестный layout.
TestInvalidMap(
    "Invalid Version",
    testMap.Buffer,
    buffer => buffer[0] = 99);

// Нулевая длина моделирует поврежденный/усеченный contract buffer.
TestInvalidMap(
    "Invalid Length",
    testMap.Buffer,
    buffer => buffer[1] = 0);

// Неизвестный RegisterOrder должен отклоняться до начала runtime decoding.
TestInvalidMap(
    "Invalid RegisterOrder",
    testMap.Buffer,
    buffer => buffer[18] = 99);

// Неизвестный RegisterType проверяет защиту от неподдерживаемого типа значения.
TestInvalidMap(
    "Invalid RegisterType",
    testMap.Buffer,
    buffer => buffer[23] = 99);

// FIXME: Temporary Transport decoder validation helper.
// Remove after Core -> Transport integration is verified.
static void TestInvalidMap(
    string name,
    ReadOnlyMemory<byte> source,
    Action<byte[]> corrupt)
{
    // Работаем с копией, чтобы каждый негативный сценарий начинался
    // с одной и той же валидной исходной конфигурации.
    var buffer =
        source.ToArray();

    // Corrupt передается снаружи, чтобы helper не знал конкретный layout поля
    // и оставался простым механизмом проверки ожидаемого отказа.
    corrupt(buffer);

    try
    {
        TransportMapDiagnostics.Print(
            buffer);

        // Если исключения нет — decoder принял заведомо поврежденный контракт.
        Console.WriteLine(
            $"{name}: FAILED");
    }
    catch (InvalidOperationException ex)
    {
        // Для текущего smoke test сам факт контролируемого отказа является успехом.
        Console.WriteLine(
            $"{name}: OK");

        Console.WriteLine(
            $"  {ex.Message}");
    }
}