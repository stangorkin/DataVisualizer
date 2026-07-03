using System;
using System.Threading.Tasks;

namespace DataVizAgent.Services;

internal sealed class BrowserSessionFileDialogService : ISessionFileDialogService
{
    public bool UsesNativeDialogs => false;

    public Task<bool> ConfirmReplaceAsync(string actionDescription)
    {
        // The browser host handles confirmation through JavaScript prompts in the UI layer.
        return Task.FromResult(true);
    }

    public Task<SessionFileSaveResult> SaveSessionAsync(string defaultFileName, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultFileName);
        ArgumentNullException.ThrowIfNull(content);

        return Task.FromResult(new SessionFileSaveResult(
            Succeeded: false,
            IsCanceled: true,
            FileName: defaultFileName));
    }

    public Task<SessionFileOpenResult> OpenSessionAsync()
    {
        return Task.FromResult(new SessionFileOpenResult(
            Succeeded: false,
            IsCanceled: true));
    }
}