# CoreLink.Client — реализованный контракт и границы

**Актуализировано:** 10.11.2026  
**Статус:** Client интегрирован с Core, Maps и Rx; сборка и интеграционный запуск подтверждены. Открытые требования перечислены отдельно.

## 1. Назначение

`CoreLink.Client` — публичная точка подключения внешнего приложения. Клиент координирует lifecycle, но не выполняет Modbus polling, декодирование и вычисление единиц.

```text
Avalonia → CoreLink.Client → ICore → CoreLink.Adapter.CSharp → Core → Transport → PLC
                    |
                    +→ CoreLink.Rx → Values / Statuses
                           |
                           +→ SnapshotReader через Client → ICore.GetSnapshot
```

Rust-адаптер пока не реализован. Выбор `ICore` выполняется явно при создании Client, не через reflection.

## 2. Актуальный API

`CoreLinkClient(IMapProvider mapProvider, ICore core)`  
`CoreLinkResult Start(string mapId)`  
`SnapshotStatus GetSnapshot(out ReadOnlyMemory<float> values)`  
`Task<CoreLinkResult> StopAsync()` и `void Stop()`  
`CoreLinkState State`; `CoreMap? CoreMap`  
`IObservable<RxValue>? Values`; `IObservable<SnapshotStatus>? Statuses`

`Start` загружает бинарную и managed Rx-карту, проверяет совпадение количества значений, выделяет буферы, запускает Core, создаёт `CoreLinkRx` и запускает его. Передаёт `definitions`, `UnitSystemValueId`, `SupportedUnitSystemIds`, `RxIntervalMs`.

Повторный `Start` в `Running` возвращает `Ok`; после `Stopped` повторный запуск того же Client не поддержан. Не создавать несуществующий сценарий полного restart без отдельного решения.

## 3. Память и циклы

- Transport непрерывно опрашивает PLC с `PollIntervalMs` из карты. От него не зависит число подписчиков Rx.
- Rx последовательно вызывает `Client.ReadRxSnapshot` с `RxIntervalMs` (текущая конфигурация — 500 мс); самостоятельного таймера Client нет.
- Client содержит **два выделенных на сессию `float[]`**: `_coreBuffer` для публичного `GetSnapshot` и `_rxBuffer` только для Rx. Длина равна `CoreMap.BufferLength` (числу значений).
- В синхронном `ICore.GetSnapshot(float*, int)` буфер закрепляется через `fixed`, указатель не сохраняется в Core. На `Ok` выдаётся `ReadOnlyMemory<float>`, ссылающийся на внутренний клиентский массив; следующее чтение **может изменить** те же данные. Потребителю нельзя считать его долговременным immutable snapshot.
- При `NoData`/`Disconnected`/ошибке буфер не выдаётся как новое успешное измерение. Состояние PLC не смешивать с состоянием Client.

## 4. Порядок остановки

`StopAsync` переводит Client в `Stopping`, предотвращает дальнейшие операции чтения, завершает/dispose Rx **до** остановки Core, затем освобождает ссылки на буферы/карту и переводит в `Stopped`. Конкурентные вызовы используют одну задачу. Не выполнять блокирующий синхронный `Stop()` прямо из Rx-обработчика.

## 5. Три разных статуса

- `CoreStatus` — lifecycle/состояние ядра; `Running` не доказывает наличие данных.
- `SnapshotStatus`: `Ok`, `NoData`, `Disconnected`, `BadBuffer`, `Stopped`.
- `CoreLinkState`: `Created`, `Starting`, `Running`, `Suspended`, `Stopping`, `Stopped`, `Faulted`.

Отображаемые View-состояния `Connected`, `Connecting`, `Disconnected`, `NoData`, `BadConfig`, `Error` — требование к UI, но единый публичный enum/правила приоритетов **не утверждены**. Не выдумывать его при мелких изменениях Client.

## 6. Managed-карта и Measurme

`IMapProvider` имеет `TryLoad(mapId, out CoreMap?)` и `TryLoadRxMap(mapId, out RxMapConfiguration?, out string? error)`. Карта Rx содержит значения, зарегистрированные положительные ID систем и индекс `Measurme`.

Client не выбирает единицы: он передаёт конфигурацию в Rx. `0` — без единиц; зарегистрированный ID — символ из карты либо пустая строка для параметра без единицы; неизвестный ID — прочерк и диагностика внутри Rx. Подробнее: [MAP_PROMT.md](MAP_PROMT.md).

## 7. Ошибки и оставшиеся задачи

- На внешней границе не распространять исключения как штатный протокол: использовать `CoreLinkResult`, `CoreLinkState`, `SnapshotStatus`. Не подменять `BadConfig/Error` на `Disconnected`.
- В существующем Client диагностика основана на `Trace`; **централизованное журналирование не реализовано**. `Trace` не означает гарантированной записи в файл/консоль.
- Поддержка Read/Write отдельных регистров и блоков через `Client → ICore → Core → Transport` — **план**, методов Read/Write в текущем `ICore` нет. Второй диспетчер запросов не создавать.
- При неуспешном запуске Core и отказе старта Rx имеются адресные `TODO` по освобождению ресурсов/восстановлению; их нельзя выдавать за завершённую производственную обработку отказов.
- Публичный API итоговых View-статусов, production-логгер, полный NativeAOT publish, интеграция Rust/OPC UA — не подтверждены.

## 8. Проверки

10.11.2026: `dotnet build` успешен для восьми проектов. `CoreLink.Client.TestHost` на внешнем симуляторе: 121/121 запрос, 120 `Snapshot Ok`, 0 ошибок snapshot, 488 событий Rx, `Start/Stop/Repeated Stop Ok`. Проверены 0/1/2 и неизвестные 3/5/8/9 с восстановлением единиц. Проверка стабильности `Trace` и добавления реально зарегистрированной третьей системы ещё открыта.

Реальное поведение сверять с `CoreLink.Client/CoreLinkClient.cs`. Другие правила: [PROMT.md](PROMT.md), [RX_CONTRACTS.md](RX_CONTRACTS.md).
