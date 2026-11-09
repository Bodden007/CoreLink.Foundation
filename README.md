# CoreLink.Foundation

Фундамент обмена данными с ПЛК для промышленного ПО на **C# / .NET 10**. Проект разделяет транспорт, ядро обработки данных, клиентский API и будущий реактивный слой. Основной транспорт сейчас — **Modbus TCP** (NModbus 3.0.83).

> Статус на 09.11.2026: C# Core, Transport, контракты и C#-адаптер реализованы; обмен и сценарии отказа проверены с внешними PLC/Core симуляторами. **Следующий этап — завершить CoreLink.Client и подключить получение snapshot через ICore.** Rust-ядро не является текущей реализацией.

## Архитектура

```text
Avalonia / приложение                [перспектива]
          |
          v
     CoreLink.Rx                     [ещё не реализован]
          |
          v
     CoreLink.Client                 [есть lifecycle; snapshot — следующий этап]
          |
         ICore                       [утверждённый контракт]
          |
   CoreLink.Adapter.CSharp
          |
     CoreLink.Core                   [C#]
          |
    CoreLink.Transport
          |
       Modbus TCP
          |
          PLC
```

- **CoreLink.Contracts** — `ICore`, статусы, двоичная карта v2, описания регистров.
- **CoreLink.Maps** — исходная физическая карта, провайдер карты и её двоичное кодирование.
- **CoreLink.Transport** — TCP, NModbus, непрерывный polling, timeout, reconnect, диспетчер операций и исходные `ushort[]`.
- **CoreLink.Core** — проверка и декодирование карты, управление Transport, преобразование `Word`/`Float32`, согласованный `float[]` snapshot, статусы.
- **CoreLink.Adapter.CSharp** — реализация `ICore`, делегирование C# Core без FFI.
- **CoreLink.Client** — существующий `CoreLinkClient` загружает карту, владеет выделенным клиентским массивом, вызывает `Start`/`Stop`; периодическое получение snapshot и его публикация **ещё не подключены**.
- **CoreLink.Client.TestHost** — интеграционный smoke test текущей цепочки; получение snapshot пока выполняет напрямую через адаптер.
- **CoreLink.Rx** — запланирован, но не включён в текущий этап.

`ICore` реализует адаптер, а не класс ядра. Выбор реализации адаптера — явный, без runtime reflection и автоматического поиска. Rust-адаптер — лишь возможный будущий вариант.

## Текущий контракт ядра

```csharp
public unsafe interface ICore
{
    CoreStatus Start(CoreMap coreMap);
    CoreStatus Stop();
    SnapshotStatus GetSnapshot(float* buffer, int length);
    event Action<CoreStatus>? StatusChanged;
}
```

- `Start` запускает polling, но `Running` **не гарантирует** установленное соединение или полученные данные.
- Transport независимо выполняет опрос и переподключение.
- Core получает `ushort[]`, декодирует их по карте и хранит последний согласованный snapshot.
- `GetSnapshot` **не выполняет Modbus-запрос**, а синхронно копирует готовые значения в массив вызывающей стороны.
- Массив `float[]` принадлежит Client; временный указатель через `fixed` действителен только внутри вызова. Core его не сохраняет.
- При `SnapshotStatus`, отличном от `Ok`, клиентский буфер не изменяется.
- До первого успешного чтения возвращается `NoData`; при потере связи после успешного чтения — `Disconnected` (старые значения нельзя считать актуальными).
- `Stop` синхронно останавливает Core и освобождает Transport. Повторный `Start` на том же экземпляре ядра не предусмотрен.

Поддерживаются `Word` и `Float32`, порядок `ABCD`, `BADC`, `CDAB`, `DCBA`. Бинарная карта **v2**, числовые поля Little Endian; `CoreMap.BufferLength` означает **количество значений**, а не длину байтового массива.

## Проверенный функционал

- Компиляция Release `.NET 10` и проверка контракта `ICore`.
- Запуск/остановка C# Core, polling и получение `SnapshotStatus.Ok` на контролируемом PLC Simulator.
- Декодирование значений `Word` и `Float32` на проверенной тестовой карте.
- Обнаружение обрыва связи и автоматическое восстановление после появления PLC.
- Отсутствие ложного `Ok` при отсутствии новых данных.
- Сценарий PLC HANG (TCP открыт, ответы FC04 отсутствуют): переход в `Disconnected`, последующая остановка ядра.
- При остановке во время активного запроса наблюдалось ожидание примерно оставшегося времени до `RequestTimeoutMs`. **Мгновенная отмена I/O не подтверждена и не заявляется.**

В `CoreLink.Transport/Modbus/ModbusTransportSession.cs` присутствует временная диагностика остановки с меткой `TODO DEBUG`; удалить после завершения соответствующих проверок. Это не постоянный API диагностики.

Внешние инструменты `CoreLink.PlcSimulator` и `CoreLink.CoreSimulator` использовались для испытаний и **не входят** в `CoreLink.Foundation.slnx`.

## Состав решения

```text
CoreLink.Foundation/
├── CoreLink.Contracts/
├── CoreLink.Maps/
├── CoreLink.Transport/
├── CoreLink.Core/
├── CoreLink.Adapter.CSharp/
├── CoreLink.Client/
├── CoreLink.Client.TestHost/
├── CoreLink.Foundation.slnx
├── README.md
└── PROMT.md
```

Сборка: `dotnet build CoreLink.Foundation.slnx -c Release`.

**NativeAOT-first:** избегать runtime reflection, `dynamic`, `Assembly.Load` и несовместимых с trimming зависимостей. Отдельная проверка NModbus на AOT проводилась ранее; это **не доказательство успешной AOT-публикации всего решения**.

## Следующий этап: CoreLink.Client → ICore

1. Зафиксировать API существующего `CoreLinkClient` без изменения `ICore`.
2. Использовать уже выделяемый Client `float[]` как буфер snapshot; синхронно вызывать `ICore.GetSnapshot` с временным `fixed`.
3. Организовать клиентский цикл чтения snapshot **независимо от polling Transport**. Интервал со стороны Client/Rx — отдельный параметр, ориентир тестов 500 мс.
4. Передавать `SnapshotStatus` и `CoreStatus` без подмены статусов; при ошибках не публиковать stale-значения как свежие.
5. Проверить lifecycle Client и корректное завершение цикла; затем отдельно проектировать и подключать Rx.

**Не входит в текущий этап:** Rust/FFI, OPC UA, изменение бинарной карты, новые методы `Read/Write` в `ICore`, рефакторинг проверенного Transport.

Подробные инженерные правила и актуальные границы компонентов находятся в [PROMT.md](PROMT.md).
