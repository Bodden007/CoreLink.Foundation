# CoreLink.Foundation — архитектурный промт v10

**Актуализировано:** 11.11.2026. **Стек:** C# / .NET 10 / NModbus 3.0.83.  
**Нормативные детали:** [SPECIFICATION.md](SPECIFICATION.md), [CLIENT_CONTRACTS.md](CLIENT_CONTRACTS.md), [RX_CONTRACTS.md](RX_CONTRACTS.md), [MAP_PROMT.md](MAP_PROMT.md).

## 1. Обязательные правила работы

- Приоритет: **надёжность → предсказуемость → сопровождаемость → простота → NativeAOT → производительность**.
- Перед правкой читать **актуальный исходник целевой ветки**; документы и отчёты о локальном запуске не доказывают, что изменения уже в GitHub.
- Не менять архитектуру, названия, публичные контракты и файловую структуру без задачи и согласования; минимальный diff. Не делать «заодно» рефакторинг.
- Код не генерировать без запроса. По умолчанию один полный файл за шаг; по прямой просьбе — все необходимые полные файлы.
- GitHub менять только по явному указанию, указывать пути/коммиты и предупреждать о `git pull`.
- После кода: build → целевой тест → отказы → code review → commit. Сборка, runtime, NativeAOT и промышленное подтверждение — разные уровни.
- `TODO` описывает будущую конкретную работу; `FIXME` — известный дефект и критерий удаления.
- На публичных границах нормальные ошибки Modbus/TCP возвращать согласованным **статусом**, а не исключением. Не путать ошибку конфигурации, отсутствие данных и недоступность устройства. Внутренний `throw` не объявлять устранённым без аудита.

## 2. Зафиксированная архитектура

```text
Avalonia / application
      |
 CoreLink.Client <----> CoreLink.Rx (Values / Statuses)
      |
     ICore
      |
 CoreLink.Adapter.CSharp
      |
 CoreLink.Core
      |
 CoreLink.Transport (один Dispatcher)
      |
  Modbus TCP → PLC
```

`Contracts` — API и статусы; `Maps` — две проекции одной карты; `Client` — lifecycle/буферы/Rx; `Adapter.CSharp` — реализация ICore; `Core` — декодирование и snapshot; `Transport` — Modbus, polling, reconnect, timeout и очереди; `Rx` — готовые значения, без обращения к PLC.

Нельзя создавать второй Dispatcher, переносить Modbus-адреса в Rx/UI, соединять Rx с транспортом или подменять snapshot прямым чтением. Rust/FFI и OPC UA остаются отдельными будущими задачами.

## 3. Различать состояние default branch и локальные испытания

**Проверено в GitHub 11.11.2026:** `ICore` и `CoreLinkClient` уже содержат публичный `ReadAsync`; `CoreMapLayout` находится в версии 3, а `PollFunctionCode` выбирает FC03/FC04 для polling.

**Реализовано в GitHub и проверено локально 11.11:** `CoreLinkClient.ReadAsync(IReadOnlyList<int> valueIds)` и `ICore.ReadAsync`, адаптер и Core; `CoreReadResult` со `Status` и `float[]? Values`; типы **Word/Float**. ValueId определяет объект в карте, Float занимает два регистра. Core объединяет смежные физические диапазоны, ограниченные Modbus FC04, декодирует результат и возвращает значения в исходном порядке, в том числе повторные ValueId. При ошибке `Values=null`; недопустимый ValueId/пустой запрос дают `BadConfig`.

В локальном контракте `CoreReadStatus`: `Ok`, `Disconnected`, `Timeout`, `Failed`, `BadConfig`, `Stopped`. Это **не** `SnapshotStatus`, `CoreStatus` и не `CoreLinkState`. Перед дальнейшей правкой сверять API с фактическим кодом ветки.

**Публичный Write API не реализован.** Наличие `ModbusTransportSession.WriteSingleRegisterAsync` / `WriteMultipleRegistersAsync` и FC06/FC16 эмулятора не означает наличия записи через Client. FC03 доступна в штатном polling; прямой Read по ValueId пока использует FC04 Input Registers по умолчанию.

## 4. Polling, Read и Rx

```text
PLC → FC03/FC04 polling → Core decode → internal snapshot
                                   |
Rx → Client.ReadRxSnapshot → ICore.GetSnapshot → RxValue → application

Application → Client.ReadAsync → ICore → Core → Transport Dispatcher → FC04 (default)
```

- Polling управляется `PollIntervalMs`; Rx — независимым `RxIntervalMs`. Client не создаёт собственный Modbus polling.
- `GetSnapshot` не отправляет запрос, а синхронно копирует последний согласованный snapshot. Client владеет отдельными `float[]` для публичного API и Rx.
- `ReadAsync` — дополнительный Modbus-запрос, **не snapshot и не Rx-публикация**. Прямые запросы сериализуются тем же Dispatcher, что и polling.
- При `Disconnected`/ошибке snapshot не маркируется как свежий; Rx не пересылает старые значения как новые.
- В Dispatcher приоритет: Write → polling → auxiliary Read, с ограничением starvation для polling. **Сам приоритет Write при общей сессии ещё не прошёл интеграционное испытание**.
- В текущем `Core` существуют два участка декодирования (polling/Read); TODO на унификацию не является разрешением менять их в другой задаче.

## 5. Карта, память, единицы

`CoreMap v3`: Little Endian, 34 байта заголовка, по 3 байта на запись (`ushort Address` + `byte Type`); `BufferLength` — число значений. `ValueId == index` в `RegisterMap`, не адрес Modbus. Типы `Word` и `Float`, 32-битный Float читается из двух физических регистров с учётом настроенного порядка слов/байтов.

Точное имя PLC-регистра **`Measurme`**, тестовый `ValueId=8`, адрес 0. Системы 1=UA, 2=EU; 0=пустая единица; неизвестное значение=`—` и диагностика. Rx не конвертирует значения; новая система расширяется картой, без прошитых ID. Подробности в [MAP_PROMT.md](MAP_PROMT.md).

## 6. Испытания 11.11.2026 (локальные, не CI)

- Решение из 8 проектов: успешная сборка .NET 10.
- FC04 прямой Read: 5/5 сценариев; стабильный тест 120/120 snapshot, 132/132 инварианта.
- 60 с stable: 85/85 прямых Read; 28/28 диагностических циклов FC06+FC16 с FC03 readback; 120/120 snapshot.
- 60 с с отказами: 177/177 физически начатых запросов диагностически завершены; `HungRequestCount=10`, `RecoveredRequestCount=10`; Read и Rx восстановились после TCP OFFLINE и HANG FC04; инвариантов нарушено 0.
- `Stop` при одном раннем отказовом испытании завершён примерно за 2048 мс; в последних прогонах FC04/FC03 остановка занимала 3,2/4,1 мс. Это не гарантированная верхняя граница.

Пояснение: `RecoveredRequestCount` отмечает завершение **старой NModbus Task после закрытия TCP при timeout**, не количество успешно восстановленных соединений. `RequestCompletedCount` после локального исправления считает и неуспешные физические запросы. Стресс-запись шла через **другое TCP-соединение**, не CoreLink Dispatcher.

## 7. Не реализовано / не выдавать за доказанное

Публичный прямой FC03 Read Holding и FC06/FC16 Write через `Client → ICore → Core → Transport`; подтверждённый приоритет Write в едином Dispatcher; промышленный PLC; полная поддержка всех порядков Float; production-логгер; NativeAOT publish; доказанный предел Stop при любом зависании; Rust/OPC UA.

За действующей реализацией следить по коду; локальные результаты не подменяют merge или push.

## 8. Обновление по реализации и тестам 11.11.2026

Текущий GitHub: `CoreMap v3` (`Version=3`, `HeaderSize=34`, один байт FC после `PollIntervalMs`); Poller и Dispatcher направляют polling на FC03 либо FC04. `ConnectionMap.PollFunctionCode=3`. Публичный `ReadAsync(ValueIds)` присутствует, но чтение в нём по-прежнему FC04 по умолчанию. Не смешивать конфигурацию polling с выбором функции для прямого Read.

Локальные результаты последних 60-секундных отказовых прогонов: FC04 polling — 81/120 Snapshot, 59/66 ReadAsync, Stop 3,2 мс; FC03 polling после изоляции диагностических HR20–23 — 67/120 Snapshot, 49/58 ReadAsync, 154/154 завершённых физических запросов и Stop 4,1 мс; по 0 нарушений проверяемых инвариантов. `Unknown Measurme` до изоляции был вызван записью теста в HR0 и не являлся дефектом транспортного механизма.

**Решение по объёму работ:** публичную запись и тесты записи пока отложить. Далее — единый сценарий чтения: FC03/FC04 polling, прямые чтения через один CoreLink Dispatcher, ValueId/Word/Float, OFFLINE/HANG/RECOVERY, Rx/snapshot, lifecycle, единый PASS/FAIL. Не выдавать диагностические запросы NModbus через отдельный TCP за проверку публичного CoreLink API. Исправления проводить точечно, без попутного рефакторинга.
