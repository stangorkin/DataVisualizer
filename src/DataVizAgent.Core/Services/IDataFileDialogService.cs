namespace DataVizAgent.Services;

/// <summary>
/// Native open-file dialog for data files. On the desktop host this returns a real path so the
/// dataset can be attached in place (no size limit, no copy); the browser dev harness has no
/// native dialogs and keeps the upload flow instead.
/// </summary>
public interface IDataFileDialogService
{
    bool UsesNativeDialogs { get; }

    /// <summary>Shows the picker and returns the chosen file path, or null when canceled/unsupported.</summary>
    Task<string?> PickDataFileAsync();
}

internal sealed class BrowserDataFileDialogService : IDataFileDialogService
{
    public bool UsesNativeDialogs => false;

    public Task<string?> PickDataFileAsync() => Task.FromResult<string?>(null);
}
