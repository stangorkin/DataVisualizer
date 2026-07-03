using System.Runtime.CompilerServices;
using System.Text;
using DataVizAgent.Models;
using LLama;
using LLama.Common;
using LLama.Sampling;

namespace DataVizAgent.Services;

/// <summary>
/// Standalone chat service that drives LLamaSharp directly — no Coven plumbing.
/// Uses StatelessExecutor + LLamaTemplate to support any GGUF model's built-in chat template.
/// Maintains conversation history in memory for multi-turn dialogue.
/// </summary>
internal sealed class ChatService : IChatService, IAsyncDisposable
{
    private readonly LlamaConfig _config;
    private readonly IDataService _dataService;
    private readonly IChartContextProvider _chartContext;

    private LLamaWeights? _weights;
    private ModelParams? _modelParams;
    private StatelessExecutor? _executor;
    private readonly SemaphoreSlim _loadLock = new(1, 1);

    private readonly List<(string Role, string Content)> _history = [];

    /// <summary>Conservative chars-per-token estimate used to fit history into the context window.</summary>
    private const int EstimatedCharsPerToken = 3;

    public event Action<ChartSpecResult>? OnChartSpec;
    public event Action? HistoryCleared;

    public ChatService(LlamaConfig config, IDataService dataService, IChartContextProvider chartContext)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _dataService = dataService ?? throw new ArgumentNullException(nameof(dataService));
        _chartContext = chartContext ?? throw new ArgumentNullException(nameof(chartContext));
    }

    private async Task<string?> EnsureExecutorAsync()
    {
        if (_executor is not null) return null;

        await _loadLock.WaitAsync();
        try
        {
            if (_executor is not null) return null;

            if (_config.ModelPath is null or { Length: 0 })
                return "No model configured. Set LLamaSharp:ModelPath in appsettings.json.";

            return await Task.Run(() =>
            {
                try
                {
                    _modelParams = new ModelParams(_config.ModelPath)
                    {
                        GpuLayerCount = _config.GpuLayerCount,
                        ContextSize = _config.ContextSize,
                    };
                    _weights = LLamaWeights.LoadFromFile(_modelParams);
                    _executor = new StatelessExecutor(_weights, _modelParams);
                    return (string?)null;
                }
                catch (Exception ex)
                {
                    return ex.Message;
                }
            });
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private string BuildPrompt(string userMessage)
    {
        LLamaTemplate template = new(_weights!, strict: false)
        {
            AddAssistant = true
        };

        if (!string.IsNullOrWhiteSpace(_config.SystemPrompt))
            template.Add("system", _config.SystemPrompt);

        foreach ((string role, string content) in _history)
            template.Add(role, content);

        template.Add("user", userMessage);

        return Encoding.UTF8.GetString(template.Apply());
    }

    /// <summary>Composes the user turn with dataset summary and selected-chart context for the agent.</summary>
    private string BuildModelMessage(string userText)
    {
        var parts = new List<string>();

        if (_dataService.RowCount > 0)
        {
            parts.Add(_dataService.GetDataSummary());
            parts.Add(_dataService.GetColumnProfile());
        }

        string selectedContext = BuildSelectedChartContext();
        if (!string.IsNullOrEmpty(selectedContext))
            parts.Add(selectedContext);

        parts.Add(userText);
        return string.Join("\n\n", parts);
    }

    private string BuildSelectedChartContext()
    {
        SelectedChartContext? selected = _chartContext.SelectedChart;
        if (selected is null)
            return string.Empty;

        string measure = selected.Aggregation == Aggregation.Count
            ? "row count"
            : $"{selected.Aggregation} of {selected.YColumn}";

        return $"The user currently has this chart selected: \"{selected.Title}\" — a {selected.Type} chart showing {measure} by {selected.XColumn}. " +
               "If they ask to change this chart, reply with a chart tool call that sets \"action\": \"update\".";
    }

    public async IAsyncEnumerable<string> SendAsync(string userText, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userText)) yield break;

        string? error = await EnsureExecutorAsync();
        if (error is not null)
        {
            yield return $"[Error: {error}]";
            yield break;
        }

        string modelMessage = BuildModelMessage(userText);
        TrimHistoryToFit(modelMessage.Length);

        string prompt = BuildPrompt(modelMessage);

        InferenceParams inferParams = new()
        {
            MaxTokens = _config.MaxTokens,
            AntiPrompts = ["<|im_end|>", "</s>", "[INST]", "### User:"],
            SamplingPipeline = new DefaultSamplingPipeline { Temperature = _config.Temperature },
        };

        var rawBuffer = new StringBuilder();
        await foreach (string snapshot in StreamInferenceAsync(prompt, inferParams, rawBuffer, ct).ConfigureAwait(false))
            yield return snapshot;

        string finalResponse = NormalizeResponse(rawBuffer.ToString());

        // Agentic step: if the model asked to compute an aggregation, run it and let the model
        // answer the question in prose using the result.
        var query = DataQueryParser.TryParse(finalResponse);
        if (query is not null)
        {
            string toolResult = DataQueryRunner.Run(query, _dataService);
            string followUp =
                $"{modelMessage}\n\n[Tool result for your data query]\n{toolResult}\n\n" +
                "Answer the user's question in plain language using this result. " +
                "If a chart would help illustrate it, also include a chart tool call.";
            await foreach (string snapshot in StreamInferenceAsync(BuildPrompt(followUp), inferParams, rawBuffer, ct).ConfigureAwait(false))
                yield return snapshot;

            finalResponse = NormalizeResponse(rawBuffer.ToString());
        }

        _history.Add(("user", userText));
        _history.Add(("assistant", finalResponse.Trim()));

        var request = ChartSpecParser.TryParse(finalResponse);
        string displayText = ChartSpecParser.StripChartBlocks(finalResponse).Trim();

        if (request is not null)
        {
            var validation = ChartSpecValidator.Validate(request, _dataService);
            if (validation.IsValid)
            {
                var chart = ChartDataComputer.Compute(validation.Normalized!, _dataService);
                string? targetPage = string.IsNullOrWhiteSpace(validation.Normalized!.Page) ? null : validation.Normalized.Page;
                OnChartSpec?.Invoke(new ChartSpecResult(chart, validation.Normalized.Action, targetPage));
            }
            else
            {
                string note = $"I couldn't build that chart because {validation.Error}";
                displayText = string.IsNullOrEmpty(displayText) ? note : $"{displayText}\n\n_{note}_";
            }
        }

        // Always emit the authoritative final snapshot — it replaces anything streamed
        // above (and clears tool-call residue when the reply was only a chart block).
        yield return displayText;
    }

    /// <summary>
    /// Runs one inference pass, yielding a best-effort display snapshot per token.
    /// The raw response accumulates in <paramref name="rawBuffer"/> for final parsing.
    /// </summary>
    private async IAsyncEnumerable<string> StreamInferenceAsync(
        string prompt,
        InferenceParams inferParams,
        StringBuilder rawBuffer,
        [EnumeratorCancellation] CancellationToken ct)
    {
        rawBuffer.Clear();
        string lastDisplay = string.Empty;

        await foreach (string token in _executor!.InferAsync(prompt, inferParams, ct).ConfigureAwait(false))
        {
            rawBuffer.Append(token);
            string display = ComputeStreamingDisplay(rawBuffer.ToString());
            if (display.Length > 0 && display != lastDisplay)
            {
                lastDisplay = display;
                yield return display;
            }
        }
    }

    /// <summary>
    /// Best-effort displayable text while tokens are still arriving: nothing is shown
    /// until the configured response marker (if any) has been seen, and text is hidden
    /// from the point a fenced tool-call block opens. Imperfections are corrected by the
    /// authoritative final snapshot in <see cref="SendAsync"/>.
    /// </summary>
    private string ComputeStreamingDisplay(string rawText)
    {
        string text = rawText;

        if (_config.ResponseStartMarker is not null)
        {
            int idx = text.LastIndexOf(_config.ResponseStartMarker, StringComparison.Ordinal);
            if (idx < 0)
                return string.Empty; // reasoning preamble — keep showing "Thinking…"

            text = text[(idx + _config.ResponseStartMarker.Length)..];
        }

        text = ReasoningFilter.StripForStreaming(text);

        int fenceStart = text.IndexOf("```", StringComparison.Ordinal);
        if (fenceStart >= 0)
            text = text[..fenceStart];

        // A trailing backtick may be the start of a fence split across tokens; hold it back.
        return text.TrimEnd('`').Trim();
    }

    /// <summary>
    /// Drops the oldest exchanges so system prompt + history + the current message + the
    /// response all fit within the model's context window. Uses a conservative
    /// chars-per-token estimate; exact token counts are not worth a tokenizer round-trip here.
    /// </summary>
    private void TrimHistoryToFit(int currentMessageLength)
    {
        int contextChars = (int)_config.ContextSize * EstimatedCharsPerToken;
        int responseReserveChars = (_config.MaxTokens > 0 ? _config.MaxTokens : 1024) * EstimatedCharsPerToken;

        int budget = contextChars
            - (_config.SystemPrompt?.Length ?? 0)
            - currentMessageLength
            - responseReserveChars;

        int historyChars = _history.Sum(turn => turn.Content.Length);

        // History is appended in user/assistant pairs; remove whole exchanges from the front.
        while (_history.Count >= 2 && historyChars > Math.Max(budget, 0))
        {
            historyChars -= _history[0].Content.Length + _history[1].Content.Length;
            _history.RemoveRange(0, 2);
        }
    }

    public void ClearHistory()
    {
        _history.Clear();
        if (_weights is not null && _modelParams is not null)
            _executor = new StatelessExecutor(_weights, _modelParams);

        HistoryCleared?.Invoke();
    }

    private string NormalizeResponse(string text)
    {
        string normalized = ApplyResponseStartMarker(text);
        return ReasoningFilter.StripForFinal(normalized);
    }

    private string ApplyResponseStartMarker(string text)
    {
        if (_config.ResponseStartMarker is null) return text;
        int idx = text.LastIndexOf(_config.ResponseStartMarker, StringComparison.Ordinal);
        return idx < 0 ? text : text[(idx + _config.ResponseStartMarker.Length)..];
    }

    public async ValueTask DisposeAsync()
    {
        _executor = null;
        _weights?.Dispose();
        _loadLock.Dispose();
        await ValueTask.CompletedTask;
        GC.SuppressFinalize(this);
    }
}