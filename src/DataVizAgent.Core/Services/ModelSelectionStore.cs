using System.Text.Json;

namespace DataVizAgent.Services;

/// <summary>
/// Persists which model the user chose with the in-app switcher, in the same per-user folder as
/// the other autosaves. Read at startup by model-path resolution (so a switched model survives a
/// restart) and written by <see cref="ModelManagerService"/>.
/// </summary>
internal static class ModelSelectionStore
{
    internal static readonly string DefaultFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DataVizAgent",
        "selected-model.json");

    private sealed class SelectionDto
    {
        public string? ModelPath { get; set; }
    }

    /// <summary>The persisted model path, or null when none was saved or the file is unreadable.</summary>
    public static string? TryLoad(string? filePath = null)
    {
        try
        {
            string path = filePath ?? DefaultFilePath;
            if (!File.Exists(path))
                return null;

            SelectionDto? dto = JsonSerializer.Deserialize<SelectionDto>(File.ReadAllText(path));
            return string.IsNullOrWhiteSpace(dto?.ModelPath) ? null : dto.ModelPath;
        }
        catch
        {
            return null; // a corrupt selection file must never block startup
        }
    }

    public static void Save(string modelPath, string? filePath = null)
    {
        try
        {
            string path = filePath ?? DefaultFilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new SelectionDto { ModelPath = modelPath }));
        }
        catch
        {
            // Best-effort: the switch still applies for this run even if it can't be persisted.
        }
    }
}
