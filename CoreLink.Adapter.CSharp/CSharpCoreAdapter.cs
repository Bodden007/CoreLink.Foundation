
using System.Diagnostics;
using CoreLink.Contracts.Core;
using CoreLink.Contracts.Maps;
using CoreLink.Core.Runtime;
using CoreLink.Contracts.Registers;

namespace CoreLink.Adapter.CSharp;

/// <summary>
/// Адаптирует C# Core к контракту ICore.
/// Не выполняет polling и не хранит snapshot.
/// </summary>
public sealed  class CSharpCoreAdapter : ICore
{
    private readonly CoreLinkCore _core;

    public CSharpCoreAdapter()
    {
        _core = new CoreLinkCore();
    }

    public event Action<CoreStatus>? StatusChanged
    {
        add => _core.StatusChanged += value;
        remove => _core.StatusChanged -= value;
    }

    public CoreStatus Start(CoreMap coreMap)
    {
        return _core.Start(coreMap);
    }

    public CoreStatus Stop()
    {
        return _core.Stop();
    }

    public unsafe SnapshotStatus GetSnapshot(
        float* buffer,
        int length)
    {
        return _core.GetSnapshot(buffer, length);
    }

    /// <summary>
    /// Передаёт прямое чтение в Core.
    ///
    /// Исключения не пересекают границу адаптера.
    /// </summary>
    public async Task<CoreReadResult> ReadAsync(
        IReadOnlyList<int> valueIds)
    {
        try
        {
            return await _core.ReadAsync(valueIds)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // TODO: Заменить Trace на централизованное
            // диагностическое логирование CoreLink.
            try
            {
                Trace.TraceError(
                    $"CoreLink ReadAsync failed: {ex}");
            }
            catch (Exception)
            {
                // Ошибка диагностики не должна
                // нарушать контракт адаптера.
            }

            return new CoreReadResult
            {
                Status = CoreReadStatus.Failed,
                Values = null
            };
        }
    }


    /// <summary>
    /// Передаёт одиночное чтение в Core.
    ///
    /// Не создаёт транспортную сессию
    /// и не выполняет собственное декодирование.
    ///
    /// Исключения не пересекают границу адаптера.
    /// </summary>
    public async Task<CoreSingleReadResult> ReadSingleAsync(
        ushort address,
        RegisterType type,
        ModbusReadFunction function)
    {
        try
        {
            return await _core.ReadSingleAsync(
                address,
                type,
                function)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // TODO: Подключить централизованное
            // логирование исключений адаптера.
            try
            {
                Trace.TraceError(
                    $"Single Read adapter failed: {ex}");
            }
            catch (Exception)
            {
                // Ошибка диагностики не должна
                // нарушать выполнение адаптера.
            }

            return new CoreSingleReadResult(
                CoreReadStatus.Failed,
                default);
        }
    }

}
