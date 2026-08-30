# CoreLink.Foundation

Набор C#/.NET библиотек для интеграции приложений с нативным ядром **CoreLink**.

Проект предоставляет управляемый API между прикладным слоем и Rust CoreLink, а также общие библиотеки карт и доступа к данным.

## Архитектура

```text
Application / Avalonia
        │
        ▼
   CoreLink.Rx
        │
        ▼
 CoreLink.Client
        │
        │ FFI
        ▼
 CoreLink (Rust)

CoreLink.Maps
CoreLink.Units
```

### CoreLink.Client

Основной C# API для работы с CoreLink.

Отвечает за:

- жизненный цикл CoreLink;
- конфигурацию;
- FFI-взаимодействие с Rust;
- получение snapshot данных;
- операции чтения и записи;
- управление состоянием соединения.

Только `CoreLink.Client` имеет право напрямую обращаться к native CoreLink через FFI.

### CoreLink.Maps

Описание и загрузка карт данных.

Карта определяет:

- IP-адрес;
- порт;
- начальный адрес;
- типы значений;
- интервал опроса.

Библиотека не зависит от UI и native CoreLink.

### CoreLink.Rx

Реактивный слой поверх `CoreLink.Client`.

Отвечает за:

- периодическое получение snapshot;
- публикацию потоков данных;
- контроль `Alive`;
- обработку `Status`;
- передачу состояния CoreLink прикладному слою.

### CoreLink.Units

Общие типы и преобразования единиц измерения.

## Основные принципы

- .NET 10
- AOT-first
- trim-safe
- без runtime reflection
- coarse-grained FFI
- snapshot вместо большого количества FFI-вызовов
- явные result codes для ожидаемых ошибок
- Rust владеет native-состоянием
- C# не хранит native pointers после завершения FFI-вызова
- UI не зависит напрямую от native CoreLink

## Result codes

| Code | Result |
|---:|---|
| 0 | Ok |
| 1 | Disconnected |
| 2 | Timeout |
| 3 | ProtocolError |
| 4 | BadConfig |
| 5 | SessionError |

## Текущая структура

```text
CoreLink.Foundation/
├── CoreLink.Client/
├── CoreLink.Client.TestHost/
├── CoreLink.Maps/
└── CoreLink.Foundation.slnx
```

## Native CoreLink

Native-часть проекта реализована на Rust и находится в отдельном репозитории `corelink`.

Связь C# ↔ Rust выполняется через стабильный C ABI.

## Статус

Проект находится в активной разработке.

Текущий этап:

- [x] базовая структура `CoreLink.Client`
- [x] базовая структура `CoreLink.Maps`
- [x] формирование бинарной конфигурации
- [x] pinning managed-конфигурации
- [x] вызов Rust через FFI
- [x] копирование конфигурации в Rust-owned структуру
- [ ] хранение конфигурации в CoreLink
- [ ] lifecycle FFI
- [ ] snapshot buffer
- [ ] Read / Write
- [ ] CoreLink.Rx
- [ ] CoreLink.Units
