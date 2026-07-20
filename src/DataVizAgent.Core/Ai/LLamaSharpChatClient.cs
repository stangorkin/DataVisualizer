using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using DataVizAgent.Services;
using LLama;
using LLama.Common;
using LLama.Exceptions;
using LLama.Native;
using LLama.Sampling;
using Microsoft.Extensions.AI;

namespace DataVizAgent.Ai;

/// <summary>
/// Adapts a local LLamaSharp GGUF model to <see cref="IChatClient"/> so it can be driven by the
/// Microsoft.Extensions.AI function-invocation pipeline.
///
/// Small local models are unreliable at native tool-call token emission, so this adapter uses
/// "prompted tool calling": the available tools are described in the system preamble and the model
/// is asked to emit a <c>```tool_call { "name", "arguments" }```</c> block. The adapter parses that
/// block into a <see cref="FunctionCallContent"/>, which lets the standard
/// <c>FunctionInvokingChatClient</c> run the tool loop exactly as it would for a cloud model.
/// A future hardening step is to replace the parse with llama.cpp GBNF grammar-constrained decoding.
/// </summary>
internal sealed class LLamaSharpChatClient : IChatClient
{
    private static readonly string[] AntiPrompts = ["<|im_end|>", "</s>", "[INST]", "### User:"];

    private readonly LlamaConfig _config;
    private readonly SemaphoreSlim _loadLock = new(1, 1);

    private LLamaWeights? _weights;
    private ModelParams? _modelParams;
    private StatelessExecutor? _executor;

    // Cache the generated grammar so it is built once per tool-set, not per request.
    private IList<AITool>? _grammarToolsRef;
    private Grammar? _grammar;

    public LLamaSharpChatClient(LlamaConfig config) =>
        _config = config ?? throw new ArgumentNullException(nameof(config));

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        await GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken).ConfigureAwait(false);

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await EnsureExecutorAsync(cancellationToken).ConfigureAwait(false);

        // Trim history to fit and count the prompt exactly, so generation can be capped to the
        // remaining context window — this is what prevents the llama.cpp NoKvSlot error.
        string prompt = BuildPrompt(messages, options);
        int promptTokens = PromptBudget.CountTokens(_weights!, prompt);
        if (!PromptBudget.PromptFits(_config, promptTokens))
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, PromptBudget.ContextTooSmallMessage(_config));
            yield break;
        }

        InferenceParams inferenceParams = new()
        {
            MaxTokens = PromptBudget.MaxGeneration(_config, promptTokens),
            AntiPrompts = AntiPrompts,
            SamplingPipeline = new DefaultSamplingPipeline
            {
                Temperature = (float?)options?.Temperature ?? _config.Temperature,
                Grammar = ResolveGrammar(options),
            },
        };

        var raw = new StringBuilder();
        int emittedDisplayLength = 0;

        IAsyncEnumerator<string> tokens = _executor!.InferAsync(prompt, inferenceParams, cancellationToken).GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                bool contextFull = false;
                try
                {
                    if (!await tokens.MoveNextAsync().ConfigureAwait(false))
                        break;
                }
                catch (LLamaDecodeError decodeError) when (decodeError.ReturnCode == DecodeResult.NoKvSlot)
                {
                    contextFull = true; // belt-and-suspenders: budgeting should already prevent this
                }

                if (contextFull)
                {
                    yield return new ChatResponseUpdate(ChatRole.Assistant, PromptBudget.ContextFullMessage);
                    yield break;
                }

                raw.Append(tokens.Current);

                // Stream the prose portion (reasoning removed, everything before a fenced block) live;
                // the tool-call JSON itself is held back and surfaced as a FunctionCallContent at the end.
                string display = ComputeDisplayText(raw.ToString());
                if (display.Length > emittedDisplayLength)
                {
                    yield return new ChatResponseUpdate(ChatRole.Assistant, display[emittedDisplayLength..]);
                    emittedDisplayLength = display.Length;
                }
            }
        }
        finally
        {
            await tokens.DisposeAsync().ConfigureAwait(false);
        }

        if (TryParseToolCall(raw.ToString(), out string toolName, out IDictionary<string, object?> arguments))
        {
            var call = new FunctionCallContent(Guid.NewGuid().ToString("N"), toolName, arguments);
            yield return new ChatResponseUpdate(ChatRole.Assistant, [call]);
        }
        else if (emittedDisplayLength == 0 && raw.Length > 0 && ReasoningFilter.StripForFinal(raw.ToString()).Length == 0)
        {
            // The whole reply was hidden reasoning cut off at the token cap — say so rather
            // than ending a long wait with silence.
            yield return new ChatResponseUpdate(ChatRole.Assistant, PromptBudget.ThinkingExhaustedMessage);
        }
    }

    /// <summary>
    /// Returns the GBNF grammar that constrains tool calls for the current tool-set, or null when
    /// constraint is disabled, there are no tools, or the schema is outside the builder's subset
    /// (in which case the model falls back to prompted-only tool calling).
    /// </summary>
    private Grammar? ResolveGrammar(ChatOptions? options)
    {
        if (!_config.ConstrainToolCalls)
            return null;

        IList<AITool>? tools = options?.Tools;
        if (tools is not { Count: > 0 })
            return null;

        if (!ReferenceEquals(tools, _grammarToolsRef))
        {
            _grammarToolsRef = tools;
            string? gbnf = GbnfToolGrammarBuilder.TryBuild([.. tools.OfType<AIFunction>()]);
            _grammar = gbnf is null ? null : new Grammar(gbnf, "root");
        }

        return _grammar;
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    }

    private async Task EnsureExecutorAsync(CancellationToken cancellationToken)
    {
        if (_executor is not null) return;

        await _loadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_executor is not null) return;

            if (_config.ModelPath is null or { Length: 0 })
                throw new InvalidOperationException("No model configured. Set LLamaSharp:ModelPath in appsettings.json.");

            await Task.Run(() =>
            {
                _modelParams = new ModelParams(_config.ModelPath)
                {
                    GpuLayerCount = _config.GpuLayerCount,
                    ContextSize = _config.ContextSize,
                };
                _weights = LLamaWeights.LoadFromFile(_modelParams);
                _executor = new StatelessExecutor(_weights, _modelParams);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _loadLock.Release();
        }
    }

    /// <summary>
    /// Renders the conversation into a single prompt using the model's own chat template, dropping the
    /// oldest conversation turns until the prompt fits the context window. The system block (system
    /// messages + agent instructions + tool schemas) is fixed and never trimmed; only the back-and-forth
    /// history shrinks, so the model always keeps the dataset context and the latest user turn.
    /// </summary>
    private string BuildPrompt(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        List<ChatMessage> messageList = [.. messages];
        string systemBlock = BuildSystemBlock(messageList, options);
        if (_config.DisableThinking)
            systemBlock = $"{systemBlock}\n/no_think".Trim();
        List<(string Role, string Text)> turns = BuildTurns(messageList);

        // Cap the turns considered so trimming stays cheap on very long sessions.
        if (turns.Count > PromptBudget.MaxConsideredTurns)
            turns = turns.GetRange(turns.Count - PromptBudget.MaxConsideredTurns, PromptBudget.MaxConsideredTurns);

        int budget = PromptBudget.PromptBudgetTokens(_config);
        int start = 0;
        string prompt = RenderTemplate(systemBlock, turns, start);
        while (start < turns.Count - 1 && PromptBudget.CountTokens(_weights!, prompt) > budget)
        {
            start++; // drop the oldest remaining turn and re-render
            prompt = RenderTemplate(systemBlock, turns, start);
        }

        return prompt;
    }

    /// <summary>The fixed preamble: agent instructions, tool schemas, and system messages, merged.</summary>
    private static string BuildSystemBlock(List<ChatMessage> messages, ChatOptions? options)
    {
        var systemParts = new List<string>();

        // Agent-layer instructions (and any AIContextProvider output) arrive via ChatOptions.Instructions
        // rather than as a system ChatMessage, so fold them in here.
        if (!string.IsNullOrWhiteSpace(options?.Instructions))
            systemParts.Add(options!.Instructions!);

        string toolInstructions = BuildToolInstructions(options);
        if (toolInstructions.Length > 0)
            systemParts.Add(toolInstructions);

        foreach (ChatMessage message in messages.Where(m => m.Role == ChatRole.System))
        {
            string text = RenderContent(message);
            if (!string.IsNullOrWhiteSpace(text))
                systemParts.Add(text);
        }

        return string.Join("\n\n", systemParts);
    }

    /// <summary>The trimmable conversation turns, rendered to text (tool-call/result folded into user turns).</summary>
    private static List<(string Role, string Text)> BuildTurns(List<ChatMessage> messages)
    {
        var turns = new List<(string, string)>();
        foreach (ChatMessage message in messages.Where(m => m.Role != ChatRole.System))
        {
            string text = RenderContent(message);
            if (string.IsNullOrWhiteSpace(text))
                continue;

            // GGUF templates rendered with strict:false may not know a "tool" role; fold
            // tool results into a user turn (the content already carries a clear marker).
            string role = message.Role == ChatRole.Assistant ? "assistant" : "user";
            turns.Add((role, text));
        }

        return turns;
    }

    private string RenderTemplate(string systemBlock, List<(string Role, string Text)> turns, int start)
    {
        LLamaTemplate template = new(_weights!, strict: false) { AddAssistant = true };

        if (systemBlock.Length > 0)
            template.Add("system", systemBlock);

        for (int i = start; i < turns.Count; i++)
            template.Add(turns[i].Role, turns[i].Text);

        return Encoding.UTF8.GetString(template.Apply());
    }

    private static string BuildToolInstructions(ChatOptions? options)
    {
        var tools = options?.Tools?.OfType<AIFunction>().ToList();
        if (tools is not { Count: > 0 })
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("You can call tools to query the dataset and to create or edit charts.");
        sb.AppendLine("To call a tool, output exactly one fenced block and nothing after it:");
        sb.AppendLine("```tool_call");
        sb.AppendLine("{\"name\": \"<tool>\", \"arguments\": { ... }}");
        sb.AppendLine("```");
        sb.AppendLine("After a tool runs you will be given its result; then answer the user in plain language.");
        sb.AppendLine("If no tool is needed, just answer conversationally. Available tools:");

        foreach (AIFunction tool in tools)
        {
            sb.AppendLine($"- {tool.Name}: {tool.Description}");
            sb.AppendLine($"  parameters schema: {tool.JsonSchema.GetRawText()}");
        }

        return sb.ToString().TrimEnd();
    }

    private static string RenderContent(ChatMessage message)
    {
        var parts = new List<string>();

        foreach (AIContent content in message.Contents)
        {
            switch (content)
            {
                case TextContent text when !string.IsNullOrEmpty(text.Text):
                    parts.Add(text.Text);
                    break;
                case FunctionCallContent call:
                    string args = JsonSerializer.Serialize(call.Arguments ?? new Dictionary<string, object?>());
                    parts.Add($"```tool_call\n{{\"name\": \"{call.Name}\", \"arguments\": {args}}}\n```");
                    break;
                case FunctionResultContent result:
                    parts.Add($"[Tool result]\n{result.Result}");
                    break;
            }
        }

        return string.Join("\n", parts);
    }

    /// <summary>
    /// The prose shown to the user: reasoning stripped (universally, across model families),
    /// an optional configured response marker applied, and everything from the first fenced
    /// block onward held back (that's the tool call).
    /// </summary>
    private string ComputeDisplayText(string raw)
    {
        string text = raw;

        if (_config.ResponseStartMarker is not null)
        {
            int idx = text.LastIndexOf(_config.ResponseStartMarker, StringComparison.Ordinal);
            if (idx < 0)
                return string.Empty; // reasoning preamble — keep showing "Thinking…"

            text = text[(idx + _config.ResponseStartMarker.Length)..];
        }

        text = ReasoningFilter.StripForStreaming(text);

        int fence = text.IndexOf("```", StringComparison.Ordinal);
        if (fence >= 0)
            text = text[..fence];

        // A trailing backtick may be the start of a fence split across tokens; hold it back.
        return text.TrimEnd('`').Trim();
    }

    private static bool TryParseToolCall(string raw, out string name, out IDictionary<string, object?> arguments)
    {
        name = string.Empty;
        arguments = new Dictionary<string, object?>();

        foreach (string candidate in EnumerateJsonObjects(raw))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(candidate);
                JsonElement root = document.RootElement;

                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("name", out JsonElement nameElement) ||
                    nameElement.ValueKind != JsonValueKind.String)
                    continue;

                name = nameElement.GetString() ?? string.Empty;
                if (string.IsNullOrEmpty(name))
                    continue;

                if (root.TryGetProperty("arguments", out JsonElement argsElement) && argsElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty property in argsElement.EnumerateObject())
                        arguments[property.Name] = property.Value.Clone();
                }

                return true;
            }
            catch (JsonException)
            {
                // Not valid JSON — keep scanning.
            }
        }

        return false;
    }

    /// <summary>Yields balanced top-level <c>{ ... }</c> substrings, so a tool call embedded in prose is found.</summary>
    private static IEnumerable<string> EnumerateJsonObjects(string text)
    {
        int depth = 0;
        int start = -1;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '{')
            {
                if (depth == 0) start = i;
                depth++;
            }
            else if (c == '}' && depth > 0 && --depth == 0 && start >= 0)
            {
                yield return text[start..(i + 1)];
                start = -1;
            }
        }
    }

    public void Dispose()
    {
        _weights?.Dispose();
        _loadLock.Dispose();
    }
}
