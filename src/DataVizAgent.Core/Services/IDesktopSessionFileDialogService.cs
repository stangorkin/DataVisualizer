using System.Threading.Tasks;

namespace DataVizAgent.Services;

public interface IDesktopSessionFileDialogService
{
    Task<bool> SaveSessionAsync(string defaultFileName, string content);
    Task<DesktopSessionFileOpenResult?> OpenSessionAsync();
}

public sealed record DesktopSessionFileOpenResult(string FileName, string Content);