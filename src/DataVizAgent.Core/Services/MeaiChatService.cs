using System.Runtime.CompilerServices;
using System.Text;
using DataVizAgent.Ai;
using DataVizAgent.Models;
using Microsoft.Extensions.AI;

namespace DataVizAgent.Services;

/// <summary>
/// Alternative <see cref="IChatService"/> built on the Microsoft.Extensions.AI function-invocation
/// pipeline instead of hand-rolled response parsing. A local GGUF model is wrapped as an
/// <see cref="IChatClient"/> (<see cref="LLamaSharpChatClient"/>), chart and query operations are
/// exposed as typed <see cref="ChartTools"/>, and <c>UseFunctionInvocation</c> runs the tool loop.
///
/// Selected via <c>LLamaSharp:Pipeline = "tools"</c>; the default keeps the legacy
/// <see cref="ChatService"/> so the two can be compared on the same model.
/// </summary>
internal sealed class MeaiChatService : IChatService, IAsyncDisposable
{
    private const int EstimatedCharsPerToken = 3;

    private const string DomainSystemPrompt =
        "You are a data-analysis assistant for a charting app. Use the tools to query the dataset " +
        "and to create or edit charts; never invent column names or numbers. Copy column names " +
        "exactly as listed. When the user just chats, answer conversationally without a tool call.";

    private readonly LlamaConfig _config;
    private readonly IDataService _dataService;
    private readonly IChartContextProvider _chartContext;

    private readonly List<ChatMessage> _history = [];
    private readonly SemaphoreSlim _buildLock = new(1, 1);

    private LLamaSharpChatClient? _inner;
    private IChatClient? _pipeline;
    private IList<AITool>? _tools;

    public event Action<ChartSpecResult>? OnChartSpec;
    public event Action? HistoryCleared;

    public MeaiChatService(LlamaConfig config, IDataService dataService, IChartContextProvider chartContext)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _dataService = dataService ?? throw new ArgumentNullException(nameof(dataService));
        _chartContext = chartContext ?? throw new ArgumentNullException(nameof(chartContext));
    }

    public async IAsyncEnumerable<string> SendAsync(string userText, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userText)) yield break;

        await EnsurePipelineAsync().ConfigureAwait(false);

        List<ChatMessage> messages = BuildMessages(userText);
        ChatOptions options = new()
        {
            Tools = _tools,
            Temperature = _config.Temperature,
            MaxOutputTokens = _config.MaxTokens > 0 ? _config.MaxTokens : null,
        };

        var assistantText = new StringBuilder();
        await foreach (ChatResponseUpdate update in _pipeline!.GetStreamingResponseAsync(messages, options, ct).ConfigureAwait(false))
        {
            if (string.IsNullOrEmpty(update.Text))
                continue;

            assistantText.Append(update.Text);
            yield return assistantText.ToString();
        }

        string finalText = assistantText.ToString().Trim();

        _history.Add(new ChatMessage(ChatRole.User, userText));
        _history.Add(new ChatMessage(ChatRole.Assistant, finalText));
        TrimHistory();

        // Final authoritative snapshot (also clears the bubble for a chart-only reply).
        yield return finalText;
    }

    public void ClearHistory()
    {
        _history.Clear();
        HistoryCleared?.Invoke();
    }

    private async Task EnsurePipelineAsync()
    {
        if (_pipeline is not null) return;

        await _buildLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_pipeline is not null) return;

            _inner = new LLamaSharpChatClient(_config);
            _tools = new ChartTools(_dataService, _chartContext, result => OnChartSpec?.Invoke(result)).CreateTools();
            _pipeline = _inner.AsBuilder().UseFunctionInvocation().Build();
        }
        finally
        {
            _buildLock.Release();
        }
    }

    /// <summary>
    /// Composes system prompt + a freshly rendered dataset-context message + conversation history +
    /// the new user turn. Dataset context is rebuilt every turn from current state rather than
    /// remembered, so the model always sees the real schema.
    /// </summary>
    private List<ChatMessage> BuildMessages(string userText)
    {
        var messages = new List<ChatMessage> { new(ChatRole.System, DomainSystemPrompt) };

        string datasetContext = BuildDatasetContext();
        if (datasetContext.Length > 0)
            messages.Add(new ChatMessage(ChatRole.System, datasetContext));

        messages.AddRange(_history);
        messages.Add(new ChatMessage(ChatRole.User, userText));
        return messages;
    }

    private string BuildDatasetContext()
    {
        if (_dataService.RowCount == 0)
            return string.Empty;

        var parts = new List<string>
        {
            _dataService.GetDataSummary(),
            _dataService.GetColumnProfile(),
        };

        SelectedChartContext? selected = _chartContext.SelectedChart;
        if (selected is not null)
        {
            string measure = selected.Aggregation == Aggregation.Count
                ? "row count"
                : $"{selected.Aggregation} of {selected.YColumn}";
            parts.Add(
                $"The user currently has this chart selected: \"{selected.Title}\" — a {selected.Type} chart " +
                $"showing {measure} by {selected.XColumn}. To change it, call the chart tool with action \"update\".");
        }

        return string.Join("\n\n", parts);
    }

    /// <summary>Drops the oldest exchanges so the conversation fits the model context window.</summary>
    private void TrimHistory()
    {
        int contextChars = (int)_config.ContextSize * EstimatedCharsPerToken;
        int reserve = (_config.MaxTokens > 0 ? _config.MaxTokens : 1024) * EstimatedCharsPerToken;
        int budget = contextChars - DomainSystemPrompt.Length - reserve;

        int historyChars = _history.Sum(m => m.Text?.Length ?? 0);
        while (_history.Count >= 2 && historyChars > Math.Max(budget, 0))
        {
            historyChars -= (_history[0].Text?.Length ?? 0) + (_history[1].Text?.Length ?? 0);
            _history.RemoveRange(0, 2);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _pipeline?.Dispose();
        _inner?.Dispose();
        _buildLock.Dispose();
        await ValueTask.CompletedTask;
    }
}
