# CoreLink.Client — контракт, локальные расширения и границы

**Актуализировано:** 12.11.2026. См. [SPECIFICATION.md](SPECIFICATION.md).

## Назначение

Client — единая публичная точка приложения. Он владеет lifecycle, двумя буферами snapshot и Rx; **не** реализует Modbus polling, декодирование или выбор единиц. Путь: `Client → ICore → Adapter.CSharp → Core → Transport → PLC`. Rx получает snapshot только через Client.

## API в актуальной ветке GitHub (13.11.2026)

```csharp
CoreLinkClient(ICore core);
CoreLinkResult Start(CoreLinkMap map);
SnapshotStatus GetSnapshot(out ReadOnlyMemory<float> values);
Task<CoreLinkResult> StopAsync();
void Stop();
Task<CoreReadResult> ReadAsync(IReadOnlyList<int> valueIds);
ReadSingleAsync(...);
WriteWordAsync(...);
WriteFloatAsync(...);
WriteBatchAsync(...);
IObservable<RxValue> ObserveValue(int valueId);
```

Также доступны `State`, `LastError`, `LastSingleReadStatus`, `LastWriteStatus`, `Values`, `Statuses`, `CoreMap`. Сигнатуры с многоточием здесь перечисляют имена методов, не являются копируемым кодом. Актуальное определение всегда проверять в исходниках.

## Актуальный контракт выбора реализации ядра (13.11.2026)

**Инвариант:** `CoreLink.Client` зависит от `ICore`, а не от конкретной реализации ядра. `ICore` определён в `CoreLink.Contracts`. Конкретный адаптер выбирается на внешней точке композиции приложения и передаётся в конструктор `CoreLinkClient(ICore core)`. Это намеренное архитектурное решение, а не утечка внутренней реализации.

- Сегодня реализация `ICore` — `CoreLink.Adapter.CSharp.CSharpCoreAdapter`, который использует `CoreLink.Core` и `CoreLink.Transport`.
- Будущая реализация на Rust может быть подключена через другой адаптер `ICore` с сохранением публичного API Client, **если** выполняет тот же контракт. Rust/FFI-адаптер пока не считается реализованным.
- `CoreLink.Client` не должен создавать `CSharpCoreAdapter` самостоятельно, добавлять к нему прямую зависимость или определять платформенную реализацию ядра. Не скрывать выбор реализации внутри Client.
- Приложение работает с технологическими данными через `CoreLink.Client` и `CoreLink.Contracts`; точка композиции приложения дополнительно знает выбранный адаптер только для его создания. Это не означает, что ViewModel или UI должны вызывать методы адаптера.
- При поставке готовых DLL необходимы все транзитивные managed-зависимости выбранной реализации. Наличие `Adapter.CSharp.dll` в комплекте поставки не делает методы адаптера публичным прикладным API.
- Контракты и карты доступны извне: приложение составляет `CoreLinkMap` и вызывает `Start(CoreLinkMap)`. `ValueId` определяется позицией в карте, не адресом Modbus.
- Текущие методы записи: `WriteWordAsync`, `WriteFloatAsync`, `WriteBatchAsync`; интеграционное тестирование и промышленное подтверждение этих путей следует оценивать отдельно от наличия методов.
- Отдельная сборка managed DLL не тождественна NativeAOT-публикации приложения или созданию native-библиотеки Rust.

## Реализованный ReadAsync — 11.11.2026

```csharp
Task<CoreReadResult> ReadAsync(IReadOnlyList<int> valueIds);
```

Проведён через `Client → ICore → Adapter.CSharp → Core → Transport → Dispatcher` (FC04). `CoreReadResult.Status` — `CoreReadStatus`: `Ok`, `Disconnected`, `Timeout`, `Failed`, `BadConfig`, `Stopped`. `Values` — отдельный `float[]` при успехе, `null` при ошибке; значения соответствуют запрошенному порядку ValueId. Допускаются повторы, блочные и разрозненные значения. Пустой/неверный запрос даёт `BadConfig`; после Stop — `Stopped`.

**Есть в GitHub и проверен локально.** `ReadAsync` не читает snapshot и не публикует Rx-события сам по себе.

## Циклы и память

- `PollIntervalMs` управляет штатным polling FC03/FC04, выбираемым `PollFunctionCode`; `RxIntervalMs` — отдельный цикл Rx.
- `GetSnapshot` закрепляет клиентский буфер `fixed` только на время `ICore.GetSnapshot`. Нельзя сохранять указатель в Core.
- `_coreBuffer` и `_rxBuffer` — отдельные массивы. Возвращаемый публичный `ReadOnlyMemory<float>` ссылается на повторно используемый буфер и может измениться после следующего вызова.
- Успех Read не означает автоматическое обновление snapshot. Свежесть данных и lifecycle не смешивать.

## Lifecycle и статусы

`Start` загружает Core/Rx-карты, проверяет их согласованность и запускает Core и Rx. `StopAsync` сначала останавливает Rx, затем Core; конкурентные Stop объединены в одну Task. После `Stopped` повторный Start на том же экземпляре не предусмотрен.

Не смешивать `CoreStatus` (Core), `CoreLinkState` (Client), `SnapshotStatus` (snapshot) и `CoreReadStatus` (операция Read). `Running` не значит наличие актуальных данных. Ошибка внутреннего компонента не должна превращаться в `NoData` или ложный успех.

## Запись

**Историческая запись (11–12.11.2026):** тогда публичный контракт записи ещё не был реализован; сейчас имеются `WriteWordAsync`, `WriteFloatAsync`, `WriteBatchAsync`. FC06/FC16 есть во внутреннем Transport, но диагностический TestHost пишет через отдельное соединение. Не считать это нагрузочным тестом приоритетов CoreLink Dispatcher. Отдельный проект дальнейшей реализации — тот же путь `Client → ICore → Core → Transport`, без второго соединения и Dispatcher, с явными статусами и валидацией.

## Локальные испытания

11.11.2026: 8 проектов собрано; 5/5 прямых сценариев Read; 85/85 Read и 120/120 snapshot в стабильном стрессе; при циклических OFFLINE/HANG прямой Read возвращал `Disconnected`/`Timeout`, после recovery — `Ok`. В последнем тесте 177/177 физических запросов учтены, 10 timeout и 10 завершений старого I/O, 0 нарушений тестовых инвариантов. При остановке в фазе отключения — около 2048 мс.

Открыто: публичная запись, lifecycle со сложными гонками и гарантия верхней границы Stop, полный NativeAOT, промышленный логгер, аппаратный PLC и проверка всех byte/word order.

Дополнительно: [RX_CONTRACTS.md](RX_CONTRACTS.md), [MAP_PROMT.md](MAP_PROMT.md), [PROMT.md](PROMT.md).

## Уточнение FC03/FC04 и актуальные испытания

`PollFunctionCode` входит в бинарную CoreMap v3 и выбирает FC03 или FC04 **для штатного polling**. Публичный `ReadAsync(ValueIds)` остаётся чтением через FC04 по умолчанию; не приписывать его сигнатуре выбор функции Modbus. Внутренний Dispatcher умеет маршрутизировать оба кода, но публичный отдельный FC03/FC04 Read API ещё не согласован и не реализован.

11.11.2026 локально выполнены циклы FC04/FC03 polling с OFFLINE, HANG и RECOVERY. После разделения тестовых адресов Holding Registers на `0–16` (технологические) и `20–23` (диагностическая запись) исчезли ошибки `Unknown Measurme`. Последний FC03 прогон: Snapshot 67/120, ReadAsync 49/58, 154/154 завершённых физических запросов, 0 нарушений тестовых инвариантов, Stop `Ok` за 4,1 мс.

**Историческое состояние на 11.11.2026:** публичная запись тогда не была реализована. По состоянию на 13.11.2026 методы записи добавлены; результаты их тестов оценивать по актуальному README и TestHost. Последующие изменения исправлять точечно после завершения чтения и единого сценария тестирования.


## Публичная адресная Rx-подписка — 12.11.2026

**Реализовано в текущей ветке GitHub:** `IObservable<RxValue> ObserveValue(int valueId)` в `CoreLinkClient` (`System.Reactive.Linq.Where` поверх существующего защищённого `_rx.Values`). Это **не** новый worker, не отдельный `Subject` и не вызов PLC. Доступно после успешного `Start(mapId)`.

```csharp
IDisposable sub = client.ObserveValue(8)
    .ObserveOn(RxSchedulers.MainThreadScheduler)
    .Subscribe(value =>
    {
        // value.Name, value.Value, value.Unit
    });
// При закрытии окна: sub.Dispose();
```

`RxValue` содержит `ValueId`, `Name`, `Value`, `Unit`; публикуется при изменении значения **или** обозначения единицы. Нет replay для новых подписчиков и нет дополнительных snapshot-запросов. Подписчики одного `ValueId` получают одинаковые изменения, каждый владеет своим `IDisposable`. UI-scheduler выбирает потребитель, фундамент его не навязывает. `Statuses` — отдельный поток, и окно подписывается на него лишь при необходимости.

**Реальное поведение ошибки в исходнике:** если Client не `Running`, Rx недоступен или `ValueId` вне `[0, BufferLength)`, метод возвращает `Observable.Empty<RxValue>()`; метод также перехватывает ошибки формирования потока и возвращает `Empty`. Исключение наружу не пробрасывается. `Observable.Empty` вызовет `OnCompleted` при подписке; не путать это с желаемым поведением штатного `StopAsync`. В методе оставлены `TODO` централизованного логирования всех трёх причин; **логирование пока не внедрено**.

**Открытое несоответствие:** утверждено прекращать события при штатном Stop **без** `OnCompleted` и `OnError`, но текущий `CoreLinkRx.DisposeCoreAsync()` всё ещё вызывает `OnCompleted()` обоих Subjects. Это не исправлено. Не выдавать утверждённое поведение за проверенную реализацию. `System.Reactive.Linq.Where` не требует динамической генерации кода сам по себе; полный NativeAOT smoke test для нового API ещё нужен.
