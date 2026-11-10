# CoreLink.Foundation

Модульная инфраструктура обмена с PLC на **C# / .NET 10**. Текущий транспорт — **Modbus TCP / NModbus 3.0.83**.

**Состояние на 10.11.2026:** `CoreLink.Client` и `CoreLink.Rx` **реализованы и интегрированы** с Core/Maps; сборка и испытания переключения `Measurme` пройдены. Rust-ядра и полного подтверждённого NativeAOT publish пока нет.

## Архитектура

```text
Avalonia / приложение
        |
 CoreLink.Client  -------- CoreLink.Rx (Values / Statuses)
        |                      |
       ICore              snapshot через Client
        |
 CoreLink.Adapter.CSharp
        |
 CoreLink.Core
        |
 CoreLink.Transport → Modbus TCP → PLC
```

- `CoreLink.Contracts` — `ICore`, статусы, бинарная карта v2, `IMapProvider`, managed Rx-контракты.
- `CoreLink.Maps` — `RegisterMap`, `MapProvider`, бинарная Core-карта и managed Rx-карта; единый источник значений и систем единиц.
- `CoreLink.Transport` — Modbus polling, timeout, reconnect и арбитраж запросов.
- `CoreLink.Core` — декодирование регистров и последняя согласованная публикация snapshot.
- `CoreLink.Adapter.CSharp` — адаптер `ICore` для C# Core.
- `CoreLink.Client` — Start/Stop, загрузка карт, отдельные буферы публичного snapshot и Rx, lifecycle.
- `CoreLink.Rx` — последовательный цикл чтения через Client, наблюдаемые `RxValue` и `SnapshotStatus`, публикация изменений числа или единицы.
- `CoreLink.Client.TestHost` — интеграционный тест Client + Rx; `CoreLink.PlcSimulator` — отдельный симулятор для испытаний.

## Запуск и сборка

Из корня репозитория:

```powershell
dotnet build
dotnet run --project CoreLink.Client.TestHost
```

В отдельном терминале предварительно запускается внешний `CoreLink.PlcSimulator` в режиме `--stable` на `127.0.0.1:1502`; для этого тестовые параметры подключения в `ConnectionMap` должны совпадать с симулятором. Имя решения — `CoreLink.Foundation.slnx`, **не** `.sln`. Подробности конфигурации сверять с актуальным кодом.

## Работа Measurme

`Measurme` — регистр PLC с точным именем. Его `ValueId` определяется позицией в `RegisterMap` (в текущей карте — 8, Modbus-адрес 0). `RegisterMap.SupportedUnitSystemIds` регистрирует поддерживаемые системы: сейчас 1 (UA) и 2 (EU). Дополнительные положительные ID добавляются через карту, без изменений Rx.

| Measurme | Обозначение |
| --- | --- |
| 0 | Пустая строка, не ошибка |
| 1 | psi (у параметров, где назначена единица) |
| 2 | bar / MPa (согласно параметру) |
| Зарегистрированная новая система | Единица из карты либо пустая строка |
| Незарегистрированный ID | `—`, диагностика ошибки выбора |

Число не пересчитывается. При восстановлении известного ID Rx возвращает обычные символы. Логирование пока через временный `Trace`, его надёжность как постоянного журнала не доказана.

## Испытания 10.11.2026

`dotnet build`: **все 8 проектов успешно**, .NET 10.

В последнем 60-секундном тесте: **121/121 Modbus-запросов**, **120 Snapshot Ok**, **0 Snapshot Errors**, **488 событий Rx**, `Stop Ok`, повторный `Stop Ok`. Проверены `Measurme = 0,1,2,3,5,8,9`, переходы между известными и неизвестными системами, восстановление единиц, отсутствие ненужных публикаций при неизменном обозначении.

Ранее проверены reconnect при циклическом отключении PLC и сценарий зависания FC04 при открытом TCP. Эти результаты относятся к проведённым опытам, а не ко всем возможным отказам.

**Ещё не подтверждено:** реально зарегистрированная третья система, запись диагностических ошибок/восстановления в постоянный журнал, полный NativeAOT publish, промышленное развёртывание и публичный Read/Write через `ICore`.

## Текущий интерфейс Core

```csharp
public unsafe interface ICore
{
    CoreStatus Start(CoreMap coreMap);
    CoreStatus Stop();
    SnapshotStatus GetSnapshot(float* buffer, int length);
    event Action<CoreStatus>? StatusChanged;
}
```

`GetSnapshot` не выполняет Modbus-запрос: копирует уже готовый snapshot; `CoreMap.BufferLength` — количество значений. Core v2 (Little Endian) и транспорт не модифицируются ради Rx. Публичные Read/Write-методы через Client — следующая отдельная задача, не часть существующего `ICore`.

## Документация

- [PROMT.md](PROMT.md) — правила инженерной работы, архитектура и подтверждённые границы.
- [MAP_PROMT.md](MAP_PROMT.md) — физическая/managed карта и расширяемые системы единиц.
- [CLIENT_CONTRACTS.md](CLIENT_CONTRACTS.md) — жизненный цикл Client, буферы, статусы.
- [RX_CONTRACTS.md](RX_CONTRACTS.md) — Rx, публикации, Measurme и диагностика.

**Принцип:** не изменять архитектуру без согласования; отличать код, проверенные сценарии и будущие требования.
