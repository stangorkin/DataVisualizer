using System.Runtime.CompilerServices;
using System.Text.Json;
using DataVizAgent.Models;
using DataVizAgent.Services;
using Xunit;

namespace DataVizAgent.Core.Tests;

public class SessionConversationPersistenceTests
{
    /// <summary>Chat service that records conversation persistence calls without needing a model.</summary>
    private sealed class FakeConversationChatService : IChatService, IConversationStatePersistence
    {
        public JsonElement? ConversationToExport { get; set; }
        public JsonElement? ImportedConversation { get; private set; }
        public bool ClearHistoryCalled { get; private set; }

        public event Action<ChartSpecResult>? OnChartSpec { add { } remove { } }
        public event Action? HistoryCleared { add { } remove { } }

        public async IAsyncEnumerable<string> SendAsync(string userText, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public void ClearHistory() => ClearHistoryCalled = true;

        public Task<JsonElement?> ExportConversationAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(ConversationToExport);

        public Task ImportConversationAsync(JsonElement conversation, CancellationToken cancellationToken = default)
        {
            ImportedConversation = conversation;
            return Task.CompletedTask;
        }
    }

    private sealed class NullReportPersistenceService : IReportPersistenceService
    {
        public ReportDocument? TryLoadReport() => null;
        public void SaveReport(ReportDocument report) { }
        public void DeleteSavedReport() { }
    }

    private static (SessionFileService Service, FakeConversationChatService Chat) BuildService()
    {
        var data = TestData.CreateSalesDataService();
        var chat = new FakeConversationChatService();
        var reportPersistence = new NullReportPersistenceService();
        var reportSession = new ReportSessionService(chat, data, reportPersistence, new ChartContextProvider());
        var service = new SessionFileService(data, chat, reportSession, new NullDatasetPersistenceService(), reportPersistence);
        return (service, chat);
    }

    private static JsonElement Marker(string value) =>
        JsonDocument.Parse($"{{\"marker\":\"{value}\"}}").RootElement.Clone();

    [Fact]
    public async Task Export_EmbedsConversation_WhenPipelineSupportsPersistence()
    {
        var (service, chat) = BuildService();
        chat.ConversationToExport = Marker("hello");

        string json = await service.ExportCurrentSessionAsync();

        Assert.Contains("marker", json);
        Assert.Contains("hello", json);
    }

    [Fact]
    public async Task LoadRoundTrip_RestoresConversation()
    {
        var (service, chat) = BuildService();
        chat.ConversationToExport = Marker("resume-me");

        string json = await service.ExportCurrentSessionAsync();
        SessionLoadResult result = await service.TryLoadSessionAsync(json);

        Assert.True(result.Succeeded);
        Assert.NotNull(chat.ImportedConversation);
        Assert.Equal("resume-me", chat.ImportedConversation!.Value.GetProperty("marker").GetString());
    }

    [Fact]
    public async Task Load_WithoutConversation_ClearsHistoryInsteadOfImporting()
    {
        var (service, chat) = BuildService();
        chat.ConversationToExport = null; // legacy/MEAI-style: nothing to persist

        string json = await service.ExportCurrentSessionAsync();
        SessionLoadResult result = await service.TryLoadSessionAsync(json);

        Assert.True(result.Succeeded);
        Assert.Null(chat.ImportedConversation);
        Assert.True(chat.ClearHistoryCalled);
    }
}
