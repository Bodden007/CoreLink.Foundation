
using CoreLink.Contracts.Registers;

namespace CoreLink.Core.Configuration.Models;

/// <summary>
/// Декодированная конфигурация одной сессии CoreLink.
///
/// Содержит параметры подключения, polling и описание
/// значений ПЛК в порядке их расположения в клиентском буфере.
///
/// Не управляет Transport и не выполняет сетевых операций.
/// </summary>
internal sealed class DecodedCoreMap
{
    public required string IpAddress { get; init; }

    public required ushort Port { get; init; }

    public required byte UnitId { get; init; }

    public required int PollIntervalMs { get; init; }

    /// <summary>
    /// Функция чтения постоянной группы polling.
    /// FC03 — Holding Registers.
    /// FC04 — Input Registers.
    /// </summary>
    public required byte PollFunctionCode { get; init; }

    public required int ConnectTimeoutMs { get; init; }

    public required int RequestTimeoutMs { get; init; }

    public required int ReconnectDelayMs { get; init; }

    public required ushort StartAddress { get; init; }

    public required ushort RegisterLength { get; init; }

    public required RegisterOrder RegisterOrder { get; init; }

    public required RegisterDefinition[] Registers { get; init; }
}
