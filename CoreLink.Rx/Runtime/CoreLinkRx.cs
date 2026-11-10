
using System.Reactive.Subjects;
using CoreLink.Contracts.Core;
using CoreLink.Rx.Configuration;

namespace CoreLink.Rx.Runtime;

/// <summary>
/// Получает snapshot через Client.
/// Переданная память принадлежит Client и используется
/// только для синхронного копирования.
/// </summary>
public delegate SnapshotStatus SnapshotReader(
    out ReadOnlyMemory<float> values);

/// <summary>
/// Готовое значение для подписчиков.
/// Не содержит ссылок на рабочий буфер Rx.
/// </summary>
public readonly record struct RxValue(
    int ValueId,
    string Name,
    float Value,
    string? Unit);

/// <summary>
/// Получает готовые значения через Client, сравнивает
/// с предыдущим состоянием и публикует только изменения.
///
/// Не управляет Core, Transport и Modbus polling.
/// </summary>
public sealed class CoreLinkRx : IAsyncDisposable
{
    private readonly SnapshotReader _snapshotReader;
    private readonly RxValueDefinition[] _definitions;

    private readonly int _unitSystemValueId;
    private readonly int _intervalMs;

    private readonly float[] _buffer;

    private readonly Subject<RxValue> _values = new();
    private readonly Subject<SnapshotStatus> _statuses = new();

    private readonly object _lifecycleLock = new();

    private CancellationTokenSource? _cts;
    private Task? _task;
    private Task? _stopTask;

    private int? _unitSystemId;
    private bool _hasData;
    private bool _stopped;
    private bool _disposed;

    public IObservable<RxValue> Values => _values;

    public IObservable<SnapshotStatus> Statuses => _statuses;

    public bool HasData => Volatile.Read(ref _hasData);

    public CoreLinkRx(
        SnapshotReader snapshotReader,
        IReadOnlyList<RxValueDefinition> definitions,
        int unitSystemValueId,
        int intervalMs)
    {
        ArgumentNullException.ThrowIfNull(snapshotReader);
        ArgumentNullException.ThrowIfNull(definitions);

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            intervalMs);

        if (definitions.Count == 0)
        {
            throw new ArgumentException(
                "Rx definitions are empty.",
                nameof(definitions));
        }

        if ((uint)unitSystemValueId >=
            (uint)definitions.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(unitSystemValueId));
        }

        _definitions = definitions.ToArray();

        if (_definitions.Any(x => x is null))
        {
            throw new ArgumentException(
                "Rx definitions contain null.",
                nameof(definitions));
        }

        if (_definitions.Select(x => x.Name)
            .Distinct(StringComparer.Ordinal)
            .Count() != _definitions.Length)
        {
            throw new ArgumentException(
                "Rx definitions contain duplicate names.",
                nameof(definitions));
        }

        _snapshotReader = snapshotReader;
        _unitSystemValueId = unitSystemValueId;
        _intervalMs = intervalMs;

        // Единственный рабочий буфер Rx.
        _buffer = new float[_definitions.Length];
    }

    /// <summary>
    /// Запускает один последовательный цикл.
    /// Повторный запуск после остановки запрещён.
    /// </summary>
    public void Start()
    {
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(
                _disposed, this);

            if (_stopped)
            {
                throw new InvalidOperationException(
                    "CoreLinkRx has been stopped.");
            }

            if (_task is not null)
                return;

            _cts = new CancellationTokenSource();

            CancellationToken token = _cts.Token;

            _task = Task.Run(
                () => RunAsync(token));
        }
    }

    /// <summary>
    /// Последовательно получает и обрабатывает snapshot.
    ///
    /// После завершения обработки выдерживает интервал.
    /// Очередь ожидающих запросов не создаётся.
    /// </summary>
    private async Task RunAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                SnapshotStatus status =
                    _snapshotReader(out var values);

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

                _statuses.OnNext(status);

                await Task.Delay(
                    _intervalMs,
                    cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            // Штатная остановка.
        }
    }

    /// <summary>
    /// Сравнивает новый snapshot с рабочим буфером Rx.
    ///
    /// Публикует только значения, которые изменились,
    /// либо значения с изменившейся единицей измерения.
    ///
    /// Первый успешный snapshot публикуется полностью.
    /// Новые рабочие массивы не создаются.
    /// </summary>
    private void ProcessValues(ReadOnlySpan<float> values)
    {
        int? nextUnitSystemId =
            GetUnitSystemId(values[_unitSystemValueId]);

        bool firstSnapshot = !_hasData;

        for (int i = 0; i < _buffer.Length; i++)
        {
            float newValue = values[i];
            float oldValue = _buffer[i];

            RxValueDefinition definition =
                _definitions[i];

            string? newUnit = GetUnit(
                definition,
                nextUnitSystemId);

            string? oldUnit = GetUnit(
                definition,
                _unitSystemId);

            bool valueChanged =
                !newValue.Equals(oldValue);

            bool unitChanged =
                !string.Equals(
                    newUnit,
                    oldUnit,
                    StringComparison.Ordinal);

            // Обновляем собственный буфер.
            _buffer[i] = newValue;

            if (!firstSnapshot &&
                !valueChanged &&
                !unitChanged)
            {
                continue;
            }

            _values.OnNext(new RxValue(
                i,
                definition.Name,
                newValue,
                newUnit));
        }

        _unitSystemId = nextUnitSystemId;

        Volatile.Write(
            ref _hasData,
            true);
    }

    /// <summary>
    /// Извлекает общий идентификатор системы единиц.
    ///
    /// Некорректное или неизвестное число
    /// не подменяется системой по умолчанию.
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
    /// Получает обозначение единицы измерения
    /// для конкретного параметра и системы.
    ///
    /// Не выполняет физического пересчёта.
    /// </summary>
    private static string? GetUnit(
        RxValueDefinition definition,
        int? systemId)
    {
        if (!systemId.HasValue)
            return null;

        return definition.Units.TryGetValue(
            systemId.Value,
            out string? unit)
            ? unit
            : null;
    }

    /// <summary>
    /// Прекращает новые запросы и ожидает
    /// завершения активного рабочего цикла.
    /// </summary>
    public Task StopAsync()
    {
        lock (_lifecycleLock)
        {
            if (_stopTask is not null)
                return _stopTask;

            _stopped = true;

            if (_cts is null || _task is null)
            {
                _stopTask = Task.CompletedTask;
                return _stopTask;
            }

            _cts.Cancel();

            _stopTask = FinishStopAsync(
                _task,
                _cts);

            return _stopTask;
        }
    }

    private static async Task FinishStopAsync(
        Task task,
        CancellationTokenSource cts)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        finally
        {
            cts.Dispose();
        }
    }

    /// <summary>
    /// Завершает цикл и освобождает подписки.
    ///
    /// Client должен дождаться завершения Rx
    /// перед остановкой Core.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);

        lock (_lifecycleLock)
        {
            if (_disposed)
                return;

            _disposed = true;
        }

        _values.OnCompleted();
        _statuses.OnCompleted();

        _values.Dispose();
        _statuses.Dispose();
    }
}
