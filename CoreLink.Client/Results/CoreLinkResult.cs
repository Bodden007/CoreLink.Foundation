namespace CoreLink.Client.Results;

/// <summary>
/// Явный результат операций CoreLink.Client и FFI.
/// </summary>
public enum CoreLinkResult
{
    Ok = 0,
    Disconnected = 1,
    Timeout = 2,
    ProtocolError = 3,
    BadConfig = 4,
    SessionError = 5
}