
using System.Diagnostics;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using CoreLink.Contracts.Core;
using CoreLink.Rx.Configuration;

namespace CoreLink.Rx.Runtime;

/// <summary>
/// Получает готовый snapshot через Client.
/// Память принадлежит Client и остаётся стабильной
/// на время синхронной обработки.
/// </summary>
public delegate SnapshotStatus SnapshotReader(
    out ReadOnlyMemory<float> values);

/// <summary>
/// Значение для подписчика.
/// Не содержит ссылок на рабочие массивы.
/// </summary>
public readonly record struct RxValue(
    int ValueId,
    string Name,
    float Value,
    string? Unit);

/// <summary>
/// Последовательно получает snapshot через Client,
/// сравнивает значения и публикует изменения.
///
/// Не выполняет Modbus polling и не управляет Core.
/// Ошибки локализуются внутри компонента.
/// </summary>
public sealed class CoreLinkRx : IAsyncDisposable
{
    private const int DefaultUnitSystemId = 1;
    private const string UnknownUnit = "—";

    private readonly SnapshotReader? _snapshotReader;
    private readonly RxValueDefinition[] _definitions;
    private readonly int? _unitSystemValueId;
    private readonly HashSet<int> _supportedUnitSystems;
    private readonly int _intervalMs;
    private readonly float[] _buffer;
    private readonly bool _configurationValid;

    private readonly Subject<RxValue> _values = new();
    private readonly Subject<SnapshotStatus> _statuses = new();

    private readonly IObservable<RxValue> _safeValues;
    private readonly IObservable<SnapshotStatus> _safeStatuses;

    private readonly object _lifecycleLock = new();

    private CancellationTokenSource? _cts;
    private Task? _task;
    private Task? _stopTask;
    private Task? _disposeTask;

    private int? _unitSystemId;
    private SnapshotStatus? _lastStatus;
    private string? _lastUnitError;

    private bool _hasData;
    private bool _stopped;
    private bool _disposed;
    private bool _faulted;
    private string? _lastError;

    public IObservable<RxValue> Values => _safeValues;
    public IObservable<SnapshotStatus> Statuses => _safeStatuses;

    public bool HasData => Volatile.Read(ref _hasData);
    public bool IsFaulted => Volatile.Read(ref _faulted);
    public string? LastError => Volatile.Read(ref _lastError);

    public CoreLinkRx(
        SnapshotReader snapshotReader,
        IReadOnlyList<RxValueDefinition> definitions,
        int? unitSystemValueId,
        IReadOnlyCollection<int> supportedUnitSystemIds,
        int intervalMs)
    {
        _snapshotReader = snapshotReader;
        _definitions = definitions?.ToArray() ?? [];
        _unitSystemValueId = unitSystemValueId;
        _intervalMs = intervalMs;

        _supportedUnitSystems = supportedUnitSystemIds is null
            ? []
            : new HashSet<int>(supportedUnitSystemIds);

        _buffer = new float[_definitions.Length];

        _safeValues = Observable.Create<RxValue>(
            observer => SubscribeSafely(_values, observer));

        _safeStatuses = Observable.Create<SnapshotStatus>(
            observer => SubscribeSafely(_statuses, observer));

        _configurationValid = ValidateConfiguration();
    }

    /// <summary>
    /// Проверяет конфигурацию до запуска рабочего цикла.
    /// </summary>
    private bool ValidateConfiguration()
    {
        if (_snapshotReader is null)
            return ConfigurationError("SnapshotReader is null.");

        if (_intervalMs <= 0)
            return ConfigurationError(
                "Rx interval must be positive.");

        if (_definitions.Length == 0)
            return ConfigurationError(
                "Rx definitions are empty.");

        if (_supportedUnitSystems.Count == 0)
            return ConfigurationError(
                "Supported unit systems are empty.");

        foreach (int systemId in _supportedUnitSystems)
        {
            if (systemId <= 0)
            {
                return ConfigurationError(
                    $"Invalid unit system ID: {systemId}.");
            }
        }

        if (_unitSystemValueId is int valueId &&
            (uint)valueId >= (uint)_definitions.Length)
        {
            return ConfigurationError(
                "Invalid Measurme ValueId.");
        }

        if (_unitSystemValueId is null &&
            !_supportedUnitSystems.Contains(DefaultUnitSystemId))
        {
            return ConfigurationError(
                "Default unit system 1 is not registered.");
        }

        var names = new HashSet<string>(
            StringComparer.Ordinal);

        foreach (RxValueDefinition definition in _definitions)
        {
            if (definition is null)
                return ConfigurationError(
                    "Rx definition is null.");

            if (string.IsNullOrWhiteSpace(definition.Name))
                return ConfigurationError(
                    "Rx value name is empty.");

            if (!names.Add(definition.Name))
            {
                return ConfigurationError(
                    $"Duplicate Rx value name: {definition.Name}");
            }

            if (definition.Units is null)
            {
                return ConfigurationError(
                    $"Units are null: {definition.Name}");
            }

            foreach (var pair in definition.Units)
            {
                if (!_supportedUnitSystems.Contains(pair.Key))
                {
                    return ConfigurationError(
                        $"Unregistered unit system {pair.Key}: " +
                        definition.Name);
                }

                if (string.IsNullOrWhiteSpace(pair.Value))
                {
                    return ConfigurationError(
                        $"Invalid unit symbol: {definition.Name}, " +
                        $"system {pair.Key}.");
                }
            }
        }

        return true;
    }

    private bool ConfigurationError(string message)
    {
        ReportError(message, faulted: true);
        return false;
    }

    /// <summary>
    /// Запускает один последовательный рабочий цикл.
    /// После Stop повторный запуск запрещён.
    /// </summary>
    public void Start()
    {
        lock (_lifecycleLock)
        {
            if (!_configurationValid || _disposed || _stopped)
                return;

            if (_task is not null)
                return;

            try
            {
                _cts = new CancellationTokenSource();
                CancellationToken token = _cts.Token;

                _task = Task.Run(() => RunAsync(token));
            }
            catch (Exception ex)
            {
                ReportError(
                    "Failed to start CoreLinkRx.",
                    ex,
                    faulted: true);

                _cts?.Dispose();
                _cts = null;
                _stopped = true;
            }
        }
    }

    /// <summary>
    /// Получает snapshot и публикует изменения.
    /// Следующий цикл начинается после обработки
    /// предыдущего и заданной задержки.
    /// </summary>
    private async Task RunAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    SnapshotStatus status =
                        _snapshotReader!(out var values);

                    if (status == SnapshotStatus.Ok)
                    {
                        if (values.Length != _buffer.Length)
                        {
                            status = SnapshotStatus.BadBuffer;
                        }
                        else
                        {
                            ProcessValues(values.Span);
                        }
                    }

                    if (!_hasData &&
                        _unitSystemValueId.HasValue &&
                        status != SnapshotStatus.Ok)
                    {
                        ReportUnitError(
                            "Measurme unavailable: no valid snapshot.");
                    }

                    if (_lastStatus != status)
                    {
                        _lastStatus = status;
                        _statuses.OnNext(status);

                        if (status == SnapshotStatus.BadBuffer)
                        {
                            ReportError(
                                "Snapshot buffer length mismatch.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    ReportError(
                        "Rx processing failed.",
                        ex);
                }

                await Task.Delay(
                    _intervalMs,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            // Штатная остановка.
        }
        catch (Exception ex)
        {
            ReportError(
                "CoreLinkRx worker terminated.",
                ex,
                faulted: true);
        }
    }

    /// <summary>
    /// Сравнивает значения с последним успешным snapshot.
    ///
    /// 0: единицы отсутствуют, ошибки нет.
    /// Известная система: обозначения из карты.
    /// Неизвестная система: прочерк и диагностика.
    ///
    /// Публикует только изменение числа или единицы.
    /// </summary>
    private void ProcessValues(ReadOnlySpan<float> values)
    {
        int? nextUnitSystemId;

        if (_unitSystemValueId is int valueId)
        {
            float rawUnitId = values[valueId];

            nextUnitSystemId = GetUnitSystemId(rawUnitId);

            if (!IsValidUnitSystem(nextUnitSystemId))
            {
                ReportUnitError(
                    $"Unknown Measurme value: {rawUnitId}.");
            }
            else
            {
                ClearUnitError();
            }
        }
        else
        {
            nextUnitSystemId = DefaultUnitSystemId;
            ClearUnitError();
        }

        bool firstSnapshot = !_hasData;

        for (int i = 0; i < _buffer.Length; i++)
        {
            float newValue = values[i];
            float oldValue = _buffer[i];

            RxValueDefinition definition = _definitions[i];

            string newUnit =
                GetUnit(definition, nextUnitSystemId);

            string oldUnit =
                GetUnit(definition, _unitSystemId);

            bool valueChanged = !newValue.Equals(oldValue);

            bool unitChanged = !string.Equals(
                newUnit,
                oldUnit,
                StringComparison.Ordinal);

            _buffer[i] = newValue;

            if (!firstSnapshot && !valueChanged && !unitChanged)
                continue;

            _values.OnNext(new RxValue(
                i,
                definition.Name,
                newValue,
                newUnit));
        }

        _unitSystemId = nextUnitSystemId;
        Volatile.Write(ref _hasData, true);
    }

    /// <summary>
    /// 0 является допустимым специальным значением.
    /// Остальные ID должны быть зарегистрированы.
    /// </summary>
    private bool IsValidUnitSystem(int? systemId)
    {
        return systemId is int id &&
            (id == 0 || _supportedUnitSystems.Contains(id));
    }

    /// <summary>
    /// Проверяет, что значение PLC представляет
    /// целочисленный идентификатор системы.
    /// </summary>
    private static int? GetUnitSystemId(float value)
    {
        if (!float.IsFinite(value) ||
            MathF.Truncate(value) != value ||
            (double)value < int.MinValue ||
            (double)value > int.MaxValue)
        {
            return null;
        }

        return (int)value;
    }

    /// <summary>
    /// Выбирает единицу параметра.
    ///
    /// 0: пустая строка.
    /// Известная система без единицы: пустая строка.
    /// Неизвестная система: прочерк.
    /// </summary>
    private string GetUnit(
        RxValueDefinition definition,
        int? systemId)
    {
        if (systemId == 0)
            return string.Empty;

        if (!IsValidUnitSystem(systemId))
            return UnknownUnit;

        return definition.Units.TryGetValue(
            systemId!.Value,
            out string? unit)
            ? unit ?? string.Empty
            : string.Empty;
    }

    /// <summary>
    /// Ошибка логируется только при изменении причины.
    /// </summary>
    private void ReportUnitError(string message)
    {
        if (string.Equals(
                _lastUnitError,
                message,
                StringComparison.Ordinal))
        {
            return;
        }

        _lastUnitError = message;
        ReportError(message);
    }

    /// <summary>
    /// Фиксирует восстановление корректной системы.
    /// </summary>
    private void ClearUnitError()
    {
        if (_lastUnitError is null)
            return;

        _lastUnitError = null;

        try
        {
            Trace.TraceInformation(
                "Measurme unit selection restored.");
        }
        catch (Exception)
        {
            // Диагностика не должна останавливать Rx.
        }
    }

    /// <summary>
    /// Отменяет дальнейшие запросы и ожидает
    /// завершения рабочего цикла.
    /// Конкурентные вызовы используют одну задачу.
    /// </summary>
    public Task StopAsync()
    {
        lock (_lifecycleLock)
        {
            if (_stopTask is not null)
                return _stopTask;

            _stopped = true;

            if (_cts is null || _task is null)
                return _stopTask = Task.CompletedTask;

            _cts.Cancel();

            return _stopTask = FinishStopAsync(_task, _cts);
        }
    }

    private async Task FinishStopAsync(
        Task task,
        CancellationTokenSource cts)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ReportError(
                "CoreLinkRx stop failed.",
                ex,
                faulted: true);
        }
        finally
        {
            cts.Dispose();
        }
    }

    /// <summary>
    /// Однократно запускает освобождение Rx.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        lock (_lifecycleLock)
        {
            if (_disposeTask is null)
            {
                _stopped = true;
                _disposeTask = Task.Run(DisposeCoreAsync);
            }

            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await StopAsync().ConfigureAwait(false);

        lock (_lifecycleLock)
        {
            _disposed = true;
        }

        try
        {
            _values.OnCompleted();
            _statuses.OnCompleted();
        }
        catch (Exception ex)
        {
            ReportError(
                "CoreLinkRx completion failed.",
                ex);
        }
        finally
        {
            _values.Dispose();
            _statuses.Dispose();
        }
    }

    /// <summary>
    /// Сохраняет диагностику в LastError и Trace.
    /// </summary>
    private void ReportError(
        string message,
        Exception? exception = null,
        bool faulted = false)
    {
        string diagnostic = exception is null
            ? message
            : $"{message} {exception}";

        Volatile.Write(ref _lastError, diagnostic);

        if (faulted)
            Volatile.Write(ref _faulted, true);

        WriteDiagnostic(diagnostic);
    }

    private static void WriteDiagnostic(string message)
    {
        try
        {
            Trace.TraceError(message);
        }
        catch (Exception)
        {
            // Ошибки логирования не должны нарушать Rx.
        }
    }

    /// <summary>
    /// Изолирует исключения пользовательских подписчиков.
    /// </summary>
    private IDisposable SubscribeSafely<T>(
        Subject<T> subject,
        IObserver<T> observer)
    {
        var guarded = new GuardedObserver<T>(
            observer,
            message => ReportError(message));

        try
        {
            guarded.SetSubscription(subject.Subscribe(guarded));
        }
        catch (Exception ex)
        {
            ReportError("Rx subscription failed.", ex);
            guarded.Dispose();
        }

        return guarded;
    }

    /// <summary>
    /// Неисправный подписчик отключается.
    /// Остальные продолжают получать события.
    /// </summary>
    private sealed class GuardedObserver<T> :
        IObserver<T>, IDisposable
    {
        private readonly IObserver<T> _observer;
        private readonly Action<string> _reportError;

        private readonly SingleAssignmentDisposable
            _subscription = new();

        private int _terminated;

        public GuardedObserver(
            IObserver<T> observer,
            Action<string> reportError)
        {
            _observer = observer;
            _reportError = reportError;
        }

        public void SetSubscription(IDisposable subscription)
        {
            _subscription.Disposable = subscription;
        }

        public void OnNext(T value)
        {
            if (Volatile.Read(ref _terminated) != 0)
                return;

            try
            {
                _observer.OnNext(value);
            }
            catch (Exception ex)
            {
                Detach(ex);
            }
        }

        public void OnError(Exception error)
        {
            if (Interlocked.Exchange(ref _terminated, 1) != 0)
                return;

            try
            {
                _observer.OnError(error);
            }
            catch (Exception ex)
            {
                Report(ex);
            }
            finally
            {
                _subscription.Dispose();
            }
        }

        public void OnCompleted()
        {
            if (Interlocked.Exchange(ref _terminated, 1) != 0)
                return;

            try
            {
                _observer.OnCompleted();
            }
            catch (Exception ex)
            {
                Report(ex);
            }
            finally
            {
                _subscription.Dispose();
            }
        }

        private void Detach(Exception exception)
        {
            if (Interlocked.Exchange(ref _terminated, 1) != 0)
                return;

            try
            {
                Report(exception);
            }
            finally
            {
                _subscription.Dispose();
            }
        }

        private void Report(Exception exception)
        {
            try
            {
                _reportError(
                    $"Rx subscriber failed: {exception}");
            }
            catch (Exception)
            {
                // Не нарушаем распространение событий.
            }
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _terminated, 1);
            _subscription.Dispose();
        }
    }
}
