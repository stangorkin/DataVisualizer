using System.Text.Json;
using DataVizAgent.Models;

namespace DataVizAgent.Services;

internal sealed class ReportPersistenceService : IReportPersistenceService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string _reportPath;

    public ReportPersistenceService()
    {
        string storageDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DataVizAgent");

        _reportPath = Path.Combine(storageDirectory, "autosave-report.json");
    }

    public ReportDocument? TryLoadReport()
    {
        try
        {
            if (!File.Exists(_reportPath))
                return null;

            string json = File.ReadAllText(_reportPath);
            PersistedReportDocument? persistedReport = JsonSerializer.Deserialize<PersistedReportDocument>(json, JsonOptions);
            return persistedReport?.ToReportDocument();
        }
        catch
        {
            return null;
        }
    }

    public void SaveReport(ReportDocument report)
    {
        ArgumentNullException.ThrowIfNull(report);

        try
        {
            string? directoryPath = Path.GetDirectoryName(_reportPath);
            if (!string.IsNullOrWhiteSpace(directoryPath))
                Directory.CreateDirectory(directoryPath);

            PersistedReportDocument persistedReport = PersistedReportDocument.FromReportDocument(report);
            string json = JsonSerializer.Serialize(persistedReport, JsonOptions);
            File.WriteAllText(_reportPath, json);
        }
        catch
        {
            // Autosave should not block charting if local storage is unavailable.
        }
    }

    public void DeleteSavedReport()
    {
        try
        {
            if (File.Exists(_reportPath))
                File.Delete(_reportPath);
        }
        catch
        {
            // Reset should still proceed even if the autosave file cannot be removed.
        }
    }
}