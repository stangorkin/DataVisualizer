using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataVizAgent.Ai;
using DataVizAgent.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace DataVizAgent.Services;

/// <summary>
/// <see cref="IChatService"/> built on Microsoft Agent Framework. A <see cref="ChatClientAgent"/>
/// wraps the local model (via <see cref="LLamaSharpChatClient"/>); chart and query operations are
/// typed <see cref="ChartTools"/>; dataset context is injected by <see cref="DatasetContextProvider"/>.
///
/// Conversation state is persisted two ways from a single composite blob ({ session, display log }):
/// into the <c>.dva-session</c> file (via <see cref="IConversationStatePersistence"/>) and, after
/// every turn, into a local autosave (via <see cref="IConversationPersistenceService"/>) so the chat
/// — both the model's memory and the visible log — returns automatically on the next launch.
///
/// Selected via <c>LLamaSharp:Pipeline = "agent"</c>.
/// </summary>
internal sealed class AgentChatService : IChatService, IConversationStatePersistence, IConversationDisplayHistory, IAsyncDisposable
{
    private static readonly JsonSerializerOptions CompositeOptions = new(JsonSerializerDefaults.Web);

    private readonly LlamaConfig _config;
    private readonly IDataService _dataService;
    private readonly IChartContextProvider _chartContext;
    private readonly IConversationPersistenceService _conversationPersistence;

    private readonly SemaphoreSlim _buildLock = new(1, 1);
    private readonly List<ChatHistoryEntry> _displayHistory = [];

    private LLamaSharpChatClient? _inner;
    private ChatClientAgent? _agent;
    private AgentSession? _session;

    // Serialized session restored from disk but not yet rebuilt into a live AgentSession.
    private JsonElement? _pendingSessionState;
    private int _persistVersion;

    public event Action<ChartSpecResult>? OnChartSpec;
    public event Action? HistoryCleared;
    public event Action? DisplayHistoryChanged;

    public AgentChatService(
        LlamaConfig config,
        IDataService dataService,
        IChartContextProvider chartContext,
        IConversationPersistenceService conversationPersistence)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _dataService = dataService ?? throw new ArgumentNullException(nameof(dataService));
        _chartContext = chartContext ?? throw new ArgumentNullException(nameof(chartContext));
        _conversationPersistence = conversationPersistence ?? throw new ArgumentNullException(nameof(conversationPersistence));

        // Restore the display log (and stash the session for lazy rebuild) from the last autosave.
        if (_conversationPersistence.TryLoadConversation() is JsonElement saved)
            TryRestoreFromComposite(saved);
    }

    public async IAsyncEnumerable<string> SendAsync(string userText, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userText)) yield break;

        await EnsureAgentAsync().ConfigureAwait(false);

        var assistantText = new StringBuilder();
        await foreach (AgentResponseUpdate update in _agent!.RunStreamingAsync(userText, _session, options: null, ct).ConfigureAwait(false))
        {
            if (string.IsNullOrEmpty(update.Text))
                continue;

            assistantText.Append(update.Text);
            yield return assistantText.ToString();
        }

        string finalText = assistantText.ToString().Trim();

        _displayHistory.Add(new ChatHistoryEntry("user", userText));
        if (finalText.Length > 0)
            _displayHistory.Add(new ChatHistoryEntry("assistant", finalText));

        PersistConversationInBackground();

        // Authoritative final snapshot (also clears the bubble for a chart-only reply).
        yield return finalText;
    }

    public void ClearHistory()
    {
        Interlocked.Increment(ref _persistVersion); // supersede any in-flight autosave
        _displayHistory.Clear();
        _session = null;
        _pendingSessionState = null;
        _conversationPersistence.DeleteSavedConversation();
        HistoryCleared?.Invoke();
    }

    public IReadOnlyList<ChatHistoryEntry> GetDisplayHistory() => _displayHistory.ToArray();

    public async Task<JsonElement?> ExportConversationAsync(CancellationToken cancellationToken = default) =>
        await BuildCompositeAsync(cancellationToken).ConfigureAwait(false);

    public Task ImportConversationAsync(JsonElement conversation, CancellationToken cancellationToken = default)
    {
        if (TryRestoreFromComposite(conversation))
        {
            // Persist the just-loaded chat so a restart restores it too.
            _conversationPersistence.SaveConversation(conversation);
            DisplayHistoryChanged?.Invoke();
        }

        return Task.CompletedTask;
    }

    private async Task EnsureAgentAsync()
    {
        if (_agent is not null && _session is not null) return;

        await _buildLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_agent is null)
            {
                _inner = new LLamaSharpChatClient(_config);
                IList<AITool> tools = new ChartTools(_dataService, _chartContext, result => OnChartSpec?.Invoke(result)).CreateTools();

                var options = new ChatClientAgentOptions
                {
                    Name = "DataVizAgent",
                    ChatOptions = new ChatOptions
                    {
                        Tools = tools,
                        Temperature = _config.Temperature,
                        MaxOutputTokens = _config.MaxTokens > 0 ? _config.MaxTokens : null,
                    },
                    AIContextProviders = [new DatasetContextProvider(_dataService, _chartContext)],
                };

                _agent = new ChatClientAgent(_inner, options);
            }

            if (_session is null)
                _session = await RebuildSessionAsync().ConfigureAwait(false);
        }
        finally
        {
            _buildLock.Release();
        }
    }

    private async Task<AgentSession> RebuildSessionAsync()
    {
        if (_pendingSessionState is JsonElement state)
        {
            _pendingSessionState = null;
            try
            {
                return await _agent!.DeserializeSessionAsync(state).ConfigureAwait(false);
            }
            catch
            {
                // A stale or incompatible autosave shouldn't wedge the chat — start fresh.
            }
        }

        return await _agent!.CreateSessionAsync().ConfigureAwait(false);
    }

    private async Task<JsonElement?> BuildCompositeAsync(CancellationToken cancellationToken)
    {
        List<PersistedChatEntry> messages = SnapshotMessages();

        JsonElement? sessionState = _pendingSessionState;
        if (_agent is not null && _session is not null)
            sessionState = await _agent.SerializeSessionAsync(_session, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (messages.Count == 0 && sessionState is null)
            return null;

        var dto = new PersistedConversation { Session = sessionState, Messages = messages };
        return JsonSerializer.SerializeToElement(dto, CompositeOptions);
    }

    private void PersistConversationInBackground()
    {
        int version = Interlocked.Increment(ref _persistVersion);
        _ = Task.Run(async () =>
        {
            try
            {
                JsonElement? composite = await BuildCompositeAsync(CancellationToken.None).ConfigureAwait(false);
                if (composite is null || Volatile.Read(ref _persistVersion) != version)
                    return;

                _conversationPersistence.SaveConversation(composite.Value);
            }
            catch
            {
                // Autosave is best-effort.
            }
        });
    }

    private bool TryRestoreFromComposite(JsonElement composite)
    {
        try
        {
            PersistedConversation? dto = composite.Deserialize<PersistedConversation>(CompositeOptions);
            if (dto is null)
                return false;

            _displayHistory.Clear();
            foreach (PersistedChatEntry entry in dto.Messages)
                _displayHistory.Add(new ChatHistoryEntry(entry.Role, entry.Text));

            _pendingSessionState = dto.Session;
            _session = null; // force rebuild from the restored state on the next turn
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private List<PersistedChatEntry> SnapshotMessages() =>
        [.. _displayHistory.Select(entry => new PersistedChatEntry { Role = entry.Role, Text = entry.Text })];

    public async ValueTask DisposeAsync()
    {
        _inner?.Dispose();
        _buildLock.Dispose();
        await ValueTask.CompletedTask;
    }

    /// <summary>On-disk shape: the serialized agent session (model memory) plus the rendered log (UI).</summary>
    private sealed class PersistedConversation
    {
        [JsonPropertyName("session")] public JsonElement? Session { get; set; }
        [JsonPropertyName("messages")] public List<PersistedChatEntry> Messages { get; set; } = [];
    }

    private sealed class PersistedChatEntry
    {
        [JsonPropertyName("role")] public string Role { get; set; } = string.Empty;
        [JsonPropertyName("text")] public string Text { get; set; } = string.Empty;
    }
}
