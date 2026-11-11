# CoreLink.Client — контракт, локальные расширения и границы

**Актуализировано:** 11.11.2026. См. [SPECIFICATION.md](SPECIFICATION.md).

## Назначение

Client — единая публичная точка приложения. Он владеет lifecycle, двумя буферами snapshot и Rx; **не** реализует Modbus polling, декодирование или выбор единиц. Путь: `Client → ICore → Adapter.CSharp → Core → Transport → PLC`. Rx получает snapshot только через Client.

## API в default branch на момент актуализации

```csharp
CoreLinkClient(IMapProvider mapProvider, ICore core);
CoreLinkResult Start(string mapId);
SnapshotStatus GetSnapshot(out ReadOnlyMemory<float> values);
Task<CoreLinkResult> StopAsync();
void Stop();
CoreLinkState State;
CoreMap? CoreMap;
IObservable<RxValue>? Values;
IObservable<SnapshotStatus>? Statuses;
```

`ICore` содержит `Start(CoreMap)`, `Stop()`, `GetSnapshot(float*, int)`, `StatusChanged`. `ReadAsync` пока отсутствует в считанных исходниках default branch.

## Локально реализованный ReadAsync — 11.11.2026

```csharp
Task<CoreReadResult> ReadAsync(IReadOnlyList<int> valueIds);
```

Проведён через `Client → ICore → Adapter.CSharp → Core → Transport → Dispatcher` (FC04). `CoreReadResult.Status` — `CoreReadStatus`: `Ok`, `Disconnected`, `Timeout`, `Failed`, `BadConfig`, `Stopped`. `Values` — отдельный `float[]` при успехе, `null` при ошибке; значения соответствуют запрошенному порядку ValueId. Допускаются повторы, блочные и разрозненные значения. Пустой/неверный запрос даёт `BadConfig`; после Stop — `Stopped`.

**Доказано локальными тестами**, но перед вызовом в коде из GitHub необходимо проверить наличие изменений в соответствующей ветке. `ReadAsync` не читает snapshot и не публикует Rx-события сам по себе.

## Циклы и память

- `PollIntervalMs` управляет штатным FC04 polling; `RxIntervalMs` — отдельный цикл Rx.
- `GetSnapshot` закрепляет клиентский буфер `fixed` только на время `ICore.GetSnapshot`. Нельзя сохранять указатель в Core.
- `_coreBuffer` и `_rxBuffer` — отдельные массивы. Возвращаемый публичный `ReadOnlyMemory<float>` ссылается на повторно используемый буфер и может измениться после следующего вызова.
- Успех Read не означает автоматическое обновление snapshot. Свежесть данных и lifecycle не смешивать.

## Lifecycle и статусы

`Start` загружает Core/Rx-карты, проверяет их согласованность и запускает Core и Rx. `StopAsync` сначала останавливает Rx, затем Core; конкурентные Stop объединены в одну Task. После `Stopped` повторный Start на том же экземпляре не предусмотрен.

Не смешивать `CoreStatus` (Core), `CoreLinkState` (Client), `SnapshotStatus` (snapshot) и `CoreReadStatus` (локальная операция Read). `Running` не значит наличие актуальных данных. Ошибка внутреннего компонента не должна превращаться в `NoData` или ложный успех.

## Запись

**Публичного `CoreLinkClient.WriteAsync` и контракта Write в ICore ещё нет.** FC06/FC16 есть во внутреннем Transport, но диагностический TestHost пишет через отдельное соединение. Не считать это нагрузочным тестом приоритетов CoreLink Dispatcher. Отдельный проект дальнейшей реализации — тот же путь `Client → ICore → Core → Transport`, без второго соединения и Dispatcher, с явными статусами и валидацией.

## Локальные испытания

11.11.2026: 8 проектов собрано; 5/5 прямых сценариев Read; 85/85 Read и 120/120 snapshot в стабильном стрессе; при циклических OFFLINE/HANG прямой Read возвращал `Disconnected`/`Timeout`, после recovery — `Ok`. В последнем тесте 177/177 физических запросов учтены, 10 timeout и 10 завершений старого I/O, 0 нарушений тестовых инвариантов. При остановке в фазе отключения — около 2048 мс.

Открыто: публичная запись, lifecycle со сложными гонками и гарантия верхней границы Stop, полный NativeAOT, промышленный логгер, аппаратный PLC и проверка всех byte/word order.

Дополнительно: [RX_CONTRACTS.md](RX_CONTRACTS.md), [MAP_PROMT.md](MAP_PROMT.md), [PROMT.md](PROMT.md).
