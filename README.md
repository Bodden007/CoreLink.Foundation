# CoreLink.Foundation

Модульная инфраструктура обмена с PLC на **C# / .NET 10**. Текущий протокол — **Modbus TCP**, библиотека **NModbus 3.0.83**.

**Документация обновлена 11.11.2026.** Важно: исходники в ветке GitHub и локальный испытанный код могут отличаться. По состоянию на момент актуализации в default branch ещё отсутствует публичный `ReadAsync` в `ICore/CoreLinkClient`, хотя его локальная версия успешно собрана и испытана 11 ноября. Запись в публичный Client пока не реализована.

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
- **Maps** — источник `ValueId`, адресов, типов; бинарная Core v2 и managed Rx-проекции.
- **Client** — публичный lifecycle, snapshot, Rx и, в локальной испытанной версии, `ReadAsync`.
- **Adapter.CSharp** — адаптер C# реализации Core.
- **Core** — декодирование Word/Float32 и согласованный snapshot.
- **Transport** — FC04 polling, request timeout, reconnect, единственный Dispatcher, внутренние FC06/FC16.
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

`ValueId` — индекс в `RegisterMap`, **не** Modbus-адрес. В тестовой карте 9 значений; `Measurme` — `ValueId=8`, физический адрес 0. Поддержаны 1 (UA), 2 (EU); 0 — без единиц; неизвестный ID даёт `—` и диагностику. Значения PLC не пересчитываются Rx. Формат CoreMap v2 не меняется ради единиц.

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

- Публичный `ReadAsync(ValueIds)` реализован и проверен **локально**; наличие в GitHub нужно определять по фактическому исходнику.
- Публичная запись через `Client → ICore → Core → Transport → Dispatcher` **ещё не реализована**. FC06/FC16 через диагностическую TCP-сессию её не заменяют.
- Никакого подтверждения приоритета Write над polling в одном CoreLink Dispatcher пока нет.
- Также не подтверждены промышленный PLC, полный NativeAOT publish, постоянное журналирование и гарантированная верхняя граница длительности Stop.

## Документы

- [SPECIFICATION.md](SPECIFICATION.md) — общая спецификация: инварианты, API, статусы и тестовые критерии.
- [PROMT.md](PROMT.md) — инженерные правила и границы архитектуры.
- [MAP_PROMT.md](MAP_PROMT.md) — карта и единицы.
- [CLIENT_CONTRACTS.md](CLIENT_CONTRACTS.md) — Client, память и операции.
- [RX_CONTRACTS.md](RX_CONTRACTS.md) — Rx и публикации.

**Принцип:** не подменять факт локального успешного испытания утверждением о состоянии default branch или промышленной готовности.
