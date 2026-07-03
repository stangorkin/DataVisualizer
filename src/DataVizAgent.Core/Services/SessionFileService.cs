using System.Text.Json;

namespace DataVizAgent.Services;

internal sealed class SessionFileService : ISessionFileService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly IDataService _dataService;
    private readonly IChatService _chatService;
    private readonly IReportSessionService _reportSessionService;
    private readonly IDatasetPersistenceService _datasetPersistenceService;
    private readonly IReportPersistenceService _reportPersistenceService;

    public SessionFileService(
        IDataService dataService,
        IChatService chatService,
        IReportSessionService reportSessionService,
        IDatasetPersistenceService datasetPersistenceService,
        IReportPersistenceService reportPersistenceService)
    {
        _dataService = dataService ?? throw new ArgumentNullException(nameof(dataService));
        _chatService = chatService ?? throw new ArgumentNullException(nameof(chatService));
        _reportSessionService = reportSessionService ?? throw new ArgumentNullException(nameof(reportSessionService));
        _datasetPersistenceService = datasetPersistenceService ?? throw new ArgumentNullException(nameof(datasetPersistenceService));
        _reportPersistenceService = reportPersistenceService ?? throw new ArgumentNullException(nameof(reportPersistenceService));
    }

    public async Task<string> ExportCurrentSessionAsync(CancellationToken cancellationToken = default)
    {
        JsonElement? conversation = _chatService is IConversationStatePersistence persistence
            ? await persistence.ExportConversationAsync(cancellationToken)
            : null;

        var bundle = new SessionFileBundle
        {
            Report = PersistedReportDocument.FromReportDocument(_reportSessionService.CurrentReport),
            Dataset = _dataService.CreateSnapshot(),
            Conversation = conversation,
        };

        return JsonSerializer.Serialize(bundle, JsonOptions);
    }

    public string BuildDefaultFileName()
    {
        string baseName = string.IsNullOrWhiteSpace(_reportSessionService.CurrentReport.Title)
            ? "data-viz-session"
            : _reportSessionService.CurrentReport.Title.Trim();

        var sanitizedChars = baseName.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '-' : ch).ToArray();
        return $"{new string(sanitizedChars)}.dva-session.json";
    }

    public bool HasActiveSession()
    {
        if (_dataService.RowCount > 0)
            return true;

        if (_reportSessionService.GetCurrentPageCharts().Count > 0)
            return true;

        return !IsBlankReport(_reportSessionService.CurrentReport);
    }

    public async Task<SessionLoadResult> TryLoadSessionAsync(string json, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(json))
            return SessionLoadResult.Fail("The selected session file is empty.");

        SessionFileBundle? bundle;
        try
        {
            bundle = JsonSerializer.Deserialize<SessionFileBundle>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return SessionLoadResult.Fail("The selected file is not a valid DataViz session export.");
        }

        if (bundle?.Report is null && bundle?.Dataset is null)
            return SessionLoadResult.Fail("The selected file does not contain a session bundle.");

        try
        {
            if (bundle.Dataset is not null)
                _dataService.LoadSnapshot(bundle.Dataset);

            if (bundle.Report is not null)
            {
                _reportSessionService.LoadReport(bundle.Report.ToReportDocument());
            }
            else if (_dataService.RowCount > 0)
            {
                _reportSessionService.StartNewReport(_dataService.DatasetName, BuildFallbackReportTitle(_dataService.DatasetName));
            }

            // Restore the conversation when both the file and the active pipeline support it;
            // otherwise start the chat fresh.
            if (bundle.Conversation is JsonElement conversation && _chatService is IConversationStatePersistence persistence)
                await persistence.ImportConversationAsync(conversation, cancellationToken);
            else
                _chatService.ClearHistory();

            return SessionLoadResult.Ok();
        }
        catch (Exception ex)
        {
            return SessionLoadResult.Fail($"Failed to load session: {ex.Message}");
        }
    }

    private static string BuildFallbackReportTitle(string? datasetName)
    {
        string trimmedName = string.IsNullOrWhiteSpace(datasetName) ? "Untitled" : datasetName.Trim();
        return $"{trimmedName} Report";
    }

    public void StartNewSession()
    {
        _chatService.ClearHistory();
        _dataService.Clear();
        _reportSessionService.StartNewReport();
        _datasetPersistenceService.DeleteSavedDataset();
        _reportPersistenceService.DeleteSavedReport();
    }

    private static bool IsBlankReport(Models.ReportDocument report)
    {
        ArgumentNullException.ThrowIfNull(report);

        bool isDefaultTitle = string.Equals(report.Title, "Untitled Report", StringComparison.Ordinal);
        bool hasDatasetName = !string.IsNullOrWhiteSpace(report.DatasetName);
        bool hasVisuals = report.Pages.Any(page => page.Visuals.Count > 0);

        return isDefaultTitle && !hasDatasetName && !hasVisuals;
    }
}