
# CoreLink.Foundation

Модульная инфраструктура обмена с PLC на C# / .NET 10.

Текущий промышленный протокол — Modbus TCP.
Используемая библиотека — NModbus 3.0.83.

CoreLink предоставляет единый публичный API для подключения,
чтения, записи и реактивного получения технологических параметров.

## Архитектура

```text
Avalonia / приложение
        |
        | CoreLinkMap
        v
 CoreLink.Client -------- CoreLink.Rx
        |                 Values / Statuses
        |                       |
       ICore              snapshot через Client
        |
 CoreLink.Adapter.CSharp
        |
 CoreLink.Core
        |
 CoreLink.Transport
        |
     Modbus TCP
        |
       PLC
```

### Компоненты

- **CoreLink.Contracts** — публичные контракты, статусы, типы
  регистров и CoreLinkMap.
- **CoreLink.Maps** — построение бинарной карты Core и конфигурации Rx.
- **CoreLink.Client** — публичный API, lifecycle, буферы, чтение,
  запись и управление Rx.
- **CoreLink.Adapter.CSharp** — адаптер реализации ICore.
- **CoreLink.Core** — обработка регистров и согласованный snapshot.
- **CoreLink.Transport** — Modbus TCP, polling, Dispatcher,
  timeout и reconnect.
- **CoreLink.Rx** — реактивная публикация изменений значений.
- **CoreLink.Client.TestHost** — интеграционные и стресс-тесты.
- **CoreLink.NModbus.AotTest** — отдельная проверка совместимости
  NModbus с NativeAOT.

Всего в решении 9 проектов.

## Конфигурация

Конфигурация конкретного PLC принадлежит приложению-потребителю.

Библиотека CoreLink не содержит статических карт устройств.

Внешнее приложение создаёт CoreLinkMap и передаёт его
при инициализации клиента:

```csharp
var client = new CoreLinkClient(
    new CSharpCoreAdapter());

CoreLinkResult result = client.Start(map);
```

CoreLinkMap определяет:

- IP-адрес и порт;
- UnitId;
- PollFunctionCode (FC03 или FC04);
- PollIntervalMs;
- RxIntervalMs;
- таймауты и задержку reconnect;
- порядок регистров RegisterOrder;
- физические регистры PhysicalRegister[];
- поддерживаемые системы единиц измерения.

### Формирование карт

Из одного CoreLinkMap формируются две проекции:

1. Бинарная конфигурация Core.
2. Managed-конфигурация Rx.

Формат бинарной карты — CoreMap v3.

Порядок описания физических регистров определяет ValueId:

ValueId == index

Modbus-адрес регистра не является ValueId.

## Публичный API

CoreLinkClient предоставляет:

- Start(CoreLinkMap)
- StopAsync()
- Stop()
- GetSnapshot()
- ReadAsync()
- ReadSingleAsync()
- WriteWordAsync()
- WriteFloatAsync()
- WriteBatchAsync()
- ObserveValue()

Дополнительно доступны:

- State
- LastError
- LastSingleReadStatus
- LastWriteStatus
- Values
- Statuses
- CoreMap

Исключения операций обрабатываются внутри Client.
Потребитель получает предусмотренные статусы и
диагностические сведения.

Для диагностики временно используется Trace.
Централизованное журналирование запланировано отдельно.

## Валидация конфигурации

Перед запуском Core выполняется проверка карты.

Проверяются параметры подключения, поддерживаемые
функции Modbus, интервалы, таймауты и физические регистры.

Некорректная конфигурация приводит к:

- CoreLinkResult.BadConfig;
- CoreLinkState.Faulted;
- заполнению LastError;
- отказу от запуска Core.

Валидация выполняется до запуска polling.

## Modbus

### Polling

Поддерживаются:

- FC03 — Read Holding Registers;
- FC04 — Read Input Registers.

Функция штатного polling задаётся через PollFunctionCode.

Transport использует Dispatcher, контролирует таймауты
и выполняет восстановление соединения.

### Чтение

ReadAsync получает значения по ValueId.

ReadSingleAsync поддерживает отдельное чтение регистра
с указанием функции Modbus.

### Запись

Публичный API содержит:

- WriteWordAsync — FC06;
- WriteFloatAsync — FC16;
- WriteBatchAsync — FC16.

Операции записи не изменяют snapshot напрямую.

Результат последней операции доступен
через LastWriteStatus.

## Rx

Rx не создаёт собственных Modbus-подключений.

Получение данных осуществляется через snapshot Client.

Поддерживаются:

- публикация изменений технологических значений;
- публикация статусов;
- системы единиц измерения;
- подписка на отдельный ValueId.

Единицы измерения описываются через Units и UnitEntry.

Rx не выполняет числового пересчёта технологических значений.

## Сборка

```powershell
dotnet build CoreLink.Foundation.slnx -c Release
```

Проверено 13.11.2026.

Результат: успешная Release-сборка всех 9 проектов.

## Интеграционные испытания

Тестовый PLC Simulator — внешний проект.

Используемое тестовое подключение:

- IP: 127.0.0.1
- Port: 1502
- UnitId: 1

### Автоматический симулятор

```powershell
dotnet run -c Release -- --cycle
```

Команда выполняется из каталога проекта симулятора.

Автоматический сценарий:

1. ONLINE — 10 секунд.
2. OFFLINE — 10 секунд.
3. RECOVERY — 10 секунд.
4. HANG FC03/FC04 — 10 секунд.
5. RECOVERY — 10 секунд.

При HANG TCP-соединение остаётся открытым,
но Modbus-запросы чтения не получают ответов.

### Стресс-тест CoreLink

```powershell
dotnet run --project CoreLink.Client.TestHost -c Release --no-build
```

Последнее подтверждённое испытание: 13.11.2026.

Продолжительность — 60 секунд.

| Показатель | Результат |
| --- | --- |
| Snapshot | 68 успешных |
| ReadAsync | 46 успешных |
| Диагностический FC04 | 28 успешных |
| Диагностический FC03 | 24 успешных |
| Проверенные циклы FC06/FC16 | 14 |
| Начато запросов Transport | 150 |
| Завершено запросов Transport | 150 |
| Завершено зависших I/O | 10 из 10 |
| Stop Transport | 6,7 мс |
| Нарушения инвариантов | 0 |

Результат: INVARIANTS PASSED.

Во время теста подтверждены:

- обнаружение отключения PLC;
- восстановление после OFFLINE;
- обработка таймаутов;
- восстановление после HANG;
- возобновление работы Rx;
- завершение Transport без незавершённых запросов.

Диагностические операции FC03/FC04/FC06/FC16
в TestHost используют отдельные TCP-соединения.

Они не являются проверкой публичной записи
через CoreLink Dispatcher.

### Тестирование невалидной карты

```powershell
dotnet run --project CoreLink.Client.TestHost -c Release -- --invalid-map
```

Проверено 13.11.2026.

| Сценарий | Результат |
| --- | --- |
| Недопустимый порт | PASSED |
| Перекрытие регистров | PASSED |
| Недопустимый Function Code | PASSED |
| Диапазон более 125 регистров | PASSED |

Итог: 4/4 — ALL INVALID MAP TESTS PASSED.

Во всех сценариях подтверждены BadConfig,
Faulted и диагностический LastError.

## Ограничения подтверждения

Проведённые испытания не являются подтверждением
промышленной готовности.

Отдельно требуют проверки:

- публичный путь записи CoreLink → Dispatcher → PLC;
- освобождение ресурсов при частичном сбое Start;
- промышленное оборудование;
- длительная непрерывная эксплуатация;
- полный NativeAOT publish;
- централизованное журналирование;
- гарантированная верхняя граница длительности Stop.

## Документация

- SPECIFICATION.md — спецификация системы.
- PROMT.md — инженерные правила.
- MAP_PROMT.md — конфигурация карты.
- CLIENT_CONTRACTS.md — контракты Client.
- RX_CONTRACTS.md — реактивная обработка.
- REGISTER_IO_PROMPT.md — чтение и запись регистров.
- READ_WRITE_BUFFER_PROMPT.md — буферы чтения и записи.

## Основные архитектурные принципы

- Слабая связанность компонентов.
- Конфигурация устройства принадлежит потребителю.
- Единый источник конфигурации Core и Rx.
- ValueId всегда соответствует индексу карты.
- Разделение ответственности Client, Core, Rx и Transport.
- Контроль времени выполнения операций.
- Исключения не должны нарушать публичный контракт Client.
- Изменения архитектуры выполняются осознанно и проверяются тестами.
