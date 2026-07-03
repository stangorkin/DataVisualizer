using System.Threading.Tasks;

namespace DataVizAgent.Services;

public interface ISessionFileDialogService
{
    bool UsesNativeDialogs { get; }

    Task<bool> ConfirmReplaceAsync(string actionDescription);
    Task<SessionFileSaveResult> SaveSessionAsync(string defaultFileName, string content);
    Task<SessionFileOpenResult> OpenSessionAsync();
}

public sealed record SessionFileSaveResult(
    bool Succeeded,
    bool IsCanceled,
    string? FileName = null,
    string? ErrorMessage = null);

public sealed record SessionFileOpenResult(
    bool Succeeded,
    bool IsCanceled,
    string? FileName = null,
    string? Content = null,
    string? ErrorMessage = null);