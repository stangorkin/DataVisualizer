using System.Text.Json;
using DataVizAgent.Services;
using Xunit;

namespace DataVizAgent.Core.Tests;

public class ConversationAutosaveTests
{
    private sealed class FakeConversationPersistence : IConversationPersistenceService
    {
        public JsonElement? ToLoad { get; set; }
        public JsonElement? Saved { get; private set; }
        public bool Deleted { get; private set; }

        public JsonElement? TryLoadConversation() => ToLoad;
        public void SaveConversation(JsonElement conversation) => Saved = conversation;
        public void DeleteSavedConversation() { Deleted = true; Saved = null; }
    }

    private static JsonElement Composite(params (string Role, string Text)[] messages)
    {
        var messageJson = string.Join(",", messages.Select(m => $"{{\"role\":\"{m.Role}\",\"text\":\"{m.Text}\"}}"));
        return JsonDocument.Parse($"{{\"session\":{{\"x\":1}},\"messages\":[{messageJson}]}}").RootElement.Clone();
    }

    [Fact]
    public void ConversationPersistenceService_RoundTripsAndDeletes()
    {
        string path = Path.Combine(Path.GetTempPath(), $"dva-conv-{Guid.NewGuid():N}.json");
        try
        {
            var service = new ConversationPersistenceService(path);
            Assert.Null(service.TryLoadConversation());

            JsonElement payload = JsonDocument.Parse("{\"answer\":42}").RootElement.Clone();
            service.SaveConversation(payload);

            JsonElement? loaded = service.TryLoadConversation();
            Assert.NotNull(loaded);
            Assert.Equal(42, loaded!.Value.GetProperty("answer").GetInt32());

            service.DeleteSavedConversation();
            Assert.Null(service.TryLoadConversation());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void AgentChatService_RestoresDisplayHistoryFromAutosaveOnConstruction()
    {
        var persistence = new FakeConversationPersistence
        {
            ToLoad = Composite(("user", "hi"), ("assistant", "hello there")),
        };

        var service = new AgentChatService(
            new LlamaConfig(), TestData.CreateSalesDataService(), new ChartContextProvider(), persistence);

        IReadOnlyList<ChatHistoryEntry> history = ((IConversationDisplayHistory)service).GetDisplayHistory();

        Assert.Equal(2, history.Count);
        Assert.Equal(new ChatHistoryEntry("user", "hi"), history[0]);
        Assert.Equal(new ChatHistoryEntry("assistant", "hello there"), history[1]);
    }

    [Fact]
    public void AgentChatService_ClearHistory_EmptiesLogAndDeletesAutosave()
    {
        var persistence = new FakeConversationPersistence { ToLoad = Composite(("user", "hi")) };
        var service = new AgentChatService(
            new LlamaConfig(), TestData.CreateSalesDataService(), new ChartContextProvider(), persistence);

        service.ClearHistory();

        Assert.Empty(((IConversationDisplayHistory)service).GetDisplayHistory());
        Assert.True(persistence.Deleted);
    }

    [Fact]
    public async Task AgentChatService_ImportConversation_RestoresAndAutosaves()
    {
        var persistence = new FakeConversationPersistence();
        var service = new AgentChatService(
            new LlamaConfig(), TestData.CreateSalesDataService(), new ChartContextProvider(), persistence);

        bool changed = false;
        ((IConversationDisplayHistory)service).DisplayHistoryChanged += () => changed = true;

        await ((IConversationStatePersistence)service).ImportConversationAsync(Composite(("user", "restored")));

        Assert.True(changed);
        Assert.NotNull(persistence.Saved); // loaded chat is re-persisted for the next restart
        ChatHistoryEntry only = Assert.Single(((IConversationDisplayHistory)service).GetDisplayHistory());
        Assert.Equal(new ChatHistoryEntry("user", "restored"), only);
    }
}
