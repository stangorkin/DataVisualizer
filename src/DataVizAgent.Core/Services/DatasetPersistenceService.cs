using System.Text.Json;

namespace DataVizAgent.Services;

internal sealed class DatasetPersistenceService : IDatasetPersistenceService
{
    // Compact JSON: dataset autosaves can be large, and nobody reads them by hand.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _datasetPath;
    private readonly object _writeLock = new();

    public DatasetPersistenceService()
    {
        string storageDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DataVizAgent");

        _datasetPath = Path.Combine(storageDirectory, "autosave-dataset.json");
    }

    public PersistedDatasetSnapshot? TryLoadDataset()
    {
        try
        {
            if (!File.Exists(_datasetPath))
                return null;

            string json = File.ReadAllText(_datasetPath);
            return JsonSerializer.Deserialize<PersistedDatasetSnapshot>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public void SaveDataset(PersistedDatasetSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        try
        {
            string? directoryPath = Path.GetDirectoryName(_datasetPath);
            if (!string.IsNullOrWhiteSpace(directoryPath))
                Directory.CreateDirectory(directoryPath);

            string json = JsonSerializer.Serialize(snapshot, JsonOptions);
            lock (_writeLock)
            {
                File.WriteAllText(_datasetPath, json);
            }
        }
        catch
        {
            // Dataset autosave should not block the active session.
        }
    }

    public void DeleteSavedDataset()
    {
        try
        {
            if (File.Exists(_datasetPath))
                File.Delete(_datasetPath);
        }
        catch
        {
            // Reset should still proceed even if the autosave file cannot be removed.
        }
    }
}