namespace CoreLink.Client.Lifecycle;

/// <summary>
/// Состояние жизненного цикла CoreLink.Client.
/// </summary>
public enum CoreLinkState
{
    Created = 0,
    Starting = 1,
    Running = 2,
    Suspended = 3,
    Stopping = 4,
    Stopped = 5,
    Faulted = 6
}