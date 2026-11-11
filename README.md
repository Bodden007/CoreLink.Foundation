# CoreLink.Foundation

Модульная инфраструктура обмена с PLC на **C# / .NET 10**. Текущий протокол — **Modbus TCP**, библиотека **NModbus 3.0.83**.

**Актуально на 11.11.2026.** В ветке GitHub подтверждены `CoreMap v3`, конфигурируемый polling FC03/FC04, а также `ReadAsync` по ValueId в `ICore`/`CoreLinkClient`. Публичные операции записи и отдельный выбор FC03/FC04 для `ReadAsync` пока не реализованы. Результаты ниже относятся к локальным испытаниям, а не CI.

## Архитектура

```text
Avalonia / приложение
        |
 CoreLink.Client -------- CoreLink.Rx (Values / Statuses)
        |                       |
       ICore              snapshot через Client
        |
 CoreLink.Adapter.CSharp
        |
 CoreLink.Core
        |
 CoreLink.Transport → Modbus TCP → PLC
```

- **Contracts** — Core/Client статусы, `ICore`, карты и интерфейсы.
- **Maps** — источник `ValueId`, адресов, типов; бинарная Core v3 и managed Rx-проекции.
- **Client** — публичный lifecycle, snapshot, Rx и `ReadAsync` по ValueId.
- **Adapter.CSharp** — адаптер C# реализации Core.
- **Core** — декодирование Word/Float32 и согласованный snapshot.
- **Transport** — конфигурируемый FC03/FC04 polling, request timeout, reconnect, единственный Dispatcher, внутренние FC06/FC16.
- **Rx** — поток изменений значений и единиц, без собственного Modbus.
- **Client.TestHost** — стресс-сценарии; **PlcSimulator** — отдельный внешний проект, не включённый в `.slnx`.

## Сборка и запуск

```powershell
dotnet build CoreLink.Foundation.slnx
dotnet run --project CoreLink.Client.TestHost
```

Эмулятор запускается отдельно из его проекта на `127.0.0.1:1502` с `UnitId=1`:

```powershell
dotnet run --project CoreLink.PlcSimulator -- --stable
dotnet run --project CoreLink.PlcSimulator -- --cycle
```

`--stable` — непрерывная доступность. В локально испытанном обновлённом симуляторе `--cycle` — цикл из пяти фаз по 10 секунд: ONLINE → OFFLINE → RECOVERY → FC04 HANG с открытым TCP → RECOVERY. Исходники симулятора и TestHost должны соответствовать этому сценарию. Автоматизированная диагностическая запись в TestHost использует **отдельное TCP-соединение**, не CoreLink Dispatcher.

## Текущая карта и Measurme

`ValueId` — индекс в `RegisterMap`, **не** Modbus-адрес. В тестовой карте 9 значений; `Measurme` — `ValueId=8`, физический адрес 0. Поддержаны 1 (UA), 2 (EU); 0 — без единиц; неизвестный ID даёт `—` и диагностику. Значения PLC не пересчитываются Rx. Формат CoreMap v3 содержит `PollFunctionCode`; единицы остаются в отдельной managed Rx-карте.

## Локально подтверждённые испытания — 11.11.2026

| Сценарий | Наблюдение |
| --- | --- |
| Сборка решения | 8 проектов .NET 10, успешно |
| FC04 Read, стабильный режим | 5/5 прямых сценариев, 120/120 snapshot, 132/132 инварианта |
| Смешанная нагрузка, stable, 60 с | 120/120 snapshot; 85/85 Read; 28/28 диагностических циклов FC06+FC16+FC03; 0 ошибок инвариантов |
| OFFLINE/RECOVERY/HANG, 60 с | 177/177 физических запросов учтены; 10/10 timeout с завершением старого I/O; 0 ошибок инвариантов |
| Read и snapshot при отказах | Ошибки/недоступность обнаружены; после восстановления снова `Ok` |
| Stop при отказах | Успешен; в последнем опыте ~2048 мс, а не мгновенно |

Результаты относятся к **локальным испытанным версиям**, не доказывают работу незафиксированных в GitHub изменений. `RequestCompletedCount` в локальной диагностике после исправления учитывает все завершения, включая ошибки; это не счётчик только успешных ответов. `RecoveredRequestCount` означает завершение старого зависшего I/O после принудительного закрытия сокета, а не обязательно reconnect.

## Границы подтверждения

- Публичный `ReadAsync(ValueIds)` присутствует в GitHub и испытан локально; это адресное FC04 чтение, не автоматически FC03.
- Публичная запись через `Client → ICore → Core → Transport → Dispatcher` **ещё не реализована**. FC06/FC16 через диагностическую TCP-сессию её не заменяют.
- Никакого подтверждения приоритета Write над polling в одном CoreLink Dispatcher пока нет; публичная запись отсутствует.
- Также не подтверждены промышленный PLC, полный NativeAOT publish, постоянное журналирование и гарантированная верхняя граница длительности Stop.

## Документы

- [SPECIFICATION.md](SPECIFICATION.md) — общая спецификация: инварианты, API, статусы и тестовые критерии.
- [PROMT.md](PROMT.md) — инженерные правила и границы архитектуры.
- [MAP_PROMT.md](MAP_PROMT.md) — карта и единицы.
- [CLIENT_CONTRACTS.md](CLIENT_CONTRACTS.md) — Client, память и операции.
- [RX_CONTRACTS.md](RX_CONTRACTS.md) — Rx и публикации.

**Принцип:** не подменять факт локального успешного испытания утверждением о состоянии default branch или промышленной готовности.

## Последние стресс-тесты FC03/FC04 — 11.11.2026

**Факт по GitHub:** `CoreMapLayout.Version=3`, `HeaderSize=34`, `ConnectionMap.PollFunctionCode=3`; значения 3 и 4 направляют polling в Holding/Input Registers через один Dispatcher. `CoreLinkClient.ReadAsync(ValueIds)` в текущей реализации вызывает `ReadSingleAsync` без FC, то есть сохраняет FC04 по умолчанию.

**Локально испытано (60 с, внешний PlcSimulator `--cycle`):**

| Вариант | Snapshot Ok / всего | ReadAsync Ok / всего | Проверки инвариантов | Stop |
| --- | ---: | ---: | ---: | --- |
| Polling FC04 | 81/120 | 59/66 | 0 нарушений | 3,2 мс |
| Polling FC03, до разделения записей | 68/120 | 51/59 | 0 нарушений* | 3,3 мс |
| Polling FC03, запись изолирована в HR20–23 | 67/120 | 49/58 | 0 нарушений | 4,1 мс |

*Во втором варианте диагностическая запись ошибочно меняла HR0 (`Measurme`) и вызывала `Unknown Measurme`; проверки TestHost не считали эти диагностические сообщения нарушением. Исправлено переносом тестовой записи в HR20–23 во внешнем симуляторе и локальном TestHost. После исправления ошибок `Unknown Measurme` не наблюдалось.

В последнем прогоне FC03 физические запросы Transport: 154 начато / 154 завершено; зависания: 10 / 10 завершений старого I/O после timeout; Stop: `Ok`. Диагностические FC03/FC04/FC06/FC16 в этих прогонах использовали **отдельные TCP-клиенты**, а не публичные операции CoreLink. **Запись и её нагрузочные тесты отложены.** Цель следующего этапа — единый TestHost с автоматической проверкой FC03/FC04 polling и прямого чтения через CoreLink, без ручной смены константы.
