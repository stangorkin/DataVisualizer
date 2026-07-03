using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using DataVizAgent.Services;
using Microsoft.Win32;

namespace DataVizAgent.Desktop.Services;

internal sealed class DesktopSessionFileDialogService : IDesktopSessionFileDialogService, ISessionFileDialogService
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DataVizAgent",
        "desktop-ui.json");

    private string? _lastFolder = LoadLastFolder();

    public bool UsesNativeDialogs => true;

    public Task<bool> ConfirmReplaceAsync(string actionDescription)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionDescription);

        return InvokeOnUiThreadAsync(() =>
        {
            MessageBoxResult result = MessageBox.Show(
                $"This will discard the current dataset, report, and chat history.\n\nContinue to {actionDescription}?",
                "DataViz Agent",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning,
                MessageBoxResult.Cancel);

            return result == MessageBoxResult.OK;
        });
    }

    public async Task<bool> SaveSessionAsync(string defaultFileName, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultFileName);
        ArgumentNullException.ThrowIfNull(content);

        return await SaveSessionToFileAsync(defaultFileName, content) is not null;
    }

    public async Task<DesktopSessionFileOpenResult?> OpenSessionAsync()
    {
        return await InvokeOnUiThreadAsync(() =>
        {
            var dialog = new OpenFileDialog
            {
                Filter = "DataVizAgent Session (*.dva-session.json)|*.dva-session.json|JSON Files (*.json)|*.json|All Files (*.*)|*.*",
                DefaultExt = ".dva-session.json",
                CheckFileExists = true,
                Multiselect = false,
                InitialDirectory = GetInitialDirectory()
            };

            if (dialog.ShowDialog() != true)
            {
                return null;
            }

            RememberFolder(dialog.FileName);
            string content = File.ReadAllText(dialog.FileName);
            return new DesktopSessionFileOpenResult(Path.GetFileName(dialog.FileName), content);
        });
    }

    async Task<SessionFileSaveResult> ISessionFileDialogService.SaveSessionAsync(string defaultFileName, string content)
    {
        try
        {
            string? savedPath = await SaveSessionToFileAsync(defaultFileName, content);
            return savedPath is not null
                ? new SessionFileSaveResult(
                    Succeeded: true,
                    IsCanceled: false,
                    FileName: Path.GetFileName(savedPath))
                : new SessionFileSaveResult(Succeeded: false, IsCanceled: true, FileName: defaultFileName);
        }
        catch (Exception ex)
        {
            return new SessionFileSaveResult(Succeeded: false, IsCanceled: false, FileName: defaultFileName, ErrorMessage: ex.Message);
        }
    }

    async Task<SessionFileOpenResult> ISessionFileDialogService.OpenSessionAsync()
    {
        try
        {
            DesktopSessionFileOpenResult? openedFile = await OpenSessionAsync();
            return openedFile is null
                ? new SessionFileOpenResult(Succeeded: false, IsCanceled: true)
                : new SessionFileOpenResult(
                    Succeeded: true,
                    IsCanceled: false,
                    FileName: openedFile.FileName,
                    Content: openedFile.Content);
        }
        catch (Exception ex)
        {
            return new SessionFileOpenResult(Succeeded: false, IsCanceled: false, ErrorMessage: ex.Message);
        }
    }

    private Task<string?> SaveSessionToFileAsync(string defaultFileName, string content)
    {
        return InvokeOnUiThreadAsync(() =>
        {
            var dialog = new SaveFileDialog
            {
                Filter = "DataVizAgent Session (*.dva-session.json)|*.dva-session.json|JSON Files (*.json)|*.json|All Files (*.*)|*.*",
                DefaultExt = ".dva-session.json",
                FileName = defaultFileName,
                AddExtension = true,
                OverwritePrompt = true,
                InitialDirectory = GetInitialDirectory()
            };

            if (dialog.ShowDialog() != true)
            {
                return null;
            }

            File.WriteAllText(dialog.FileName, content);
            RememberFolder(dialog.FileName);
            return dialog.FileName;
        });
    }

    private string GetInitialDirectory()
    {
        if (!string.IsNullOrWhiteSpace(_lastFolder) && Directory.Exists(_lastFolder))
        {
            return _lastFolder;
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    private void RememberFolder(string filePath)
    {
        string? folder = Path.GetDirectoryName(filePath);
        if (string.IsNullOrWhiteSpace(folder) || string.Equals(folder, _lastFolder, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _lastFolder = folder;
        SaveLastFolder(folder);
    }

    private static string? LoadLastFolder()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return null;
            }

            string json = File.ReadAllText(SettingsPath);
            DesktopUiSettings? settings = JsonSerializer.Deserialize<DesktopUiSettings>(json);
            return settings?.LastFolder;
        }
        catch
        {
            return null;
        }
    }

    private static void SaveLastFolder(string folder)
    {
        try
        {
            string? directory = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string json = JsonSerializer.Serialize(new DesktopUiSettings { LastFolder = folder });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Persisting the last-used folder is best-effort and must never break the dialog flow.
        }
    }

    private static async Task<T> InvokeOnUiThreadAsync<T>(Func<T> action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            return action();
        }

        return await dispatcher.InvokeAsync(action);
    }

    private sealed class DesktopUiSettings
    {
        public string? LastFolder { get; set; }
    }
}