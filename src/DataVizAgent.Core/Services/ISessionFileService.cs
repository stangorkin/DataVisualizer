namespace DataVizAgent.Services;

public interface ISessionFileService
{
    Task<string> ExportCurrentSessionAsync(CancellationToken cancellationToken = default);
    string BuildDefaultFileName();
    bool HasActiveSession();
    Task<SessionLoadResult> TryLoadSessionAsync(string json, CancellationToken cancellationToken = default);
    void StartNewSession();
}

/// <summary>Outcome of loading a session file.</summary>
public readonly record struct SessionLoadResult(bool Succeeded, string? ErrorMessage)
{
    public static SessionLoadResult Ok() => new(true, null);
    public static SessionLoadResult Fail(string error) => new(false, error);
}