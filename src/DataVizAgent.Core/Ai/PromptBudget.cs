using System.Text;
using DataVizAgent.Services;
using LLama;

namespace DataVizAgent.Ai;

/// <summary>
/// Token-budget math shared by the chat pipelines so a conversation can never overflow the model's
/// context window (the llama.cpp <c>NoKvSlot</c> error). Token counts come from the model's real
/// tokenizer rather than a character estimate, so budgeting is exact.
/// </summary>
internal static class PromptBudget
{
    /// <summary>Tokens kept free for the model's answer when trimming the prompt down to fit.</summary>
    public const int MinResponseTokens = 256;

    /// <summary>Small guard against tokenizer/template drift when capping generation length.</summary>
    public const int GenerationMargin = 16;

    /// <summary>If a prompt is within this many tokens of the full window it is treated as not fitting.</summary>
    public const int MinRoomToAttempt = 32;

    /// <summary>Upper bound on conversation turns considered per request (keeps trimming O(n), not O(n²)).</summary>
    public const int MaxConsideredTurns = 50;

    /// <summary>Message shown when the response is cut short by the context window mid-generation.</summary>
    public const string ContextFullMessage =
        "The response was cut short because the conversation filled the model's context window. " +
        "Start a new session, or increase LLamaSharp:ContextSize in appsettings.json, to continue.";

    /// <summary>Shown when the model spent its entire reply reasoning and never produced an answer.</summary>
    public const string ThinkingExhaustedMessage =
        "The model spent its whole reply thinking and ran out of room before answering. " +
        "Try a more specific question, or increase LLamaSharp:MaxTokens in appsettings.json.";

    /// <summary>Shown when generation stopped mid tool call, leaving a half-written chart/query block.</summary>
    public const string ReplyCutOffMessage =
        "The reply hit its length limit before the chart request was complete, so nothing was changed. " +
        "Please try asking again.";

    /// <summary>Exact token count of <paramref name="text"/> as the model would see it.</summary>
    public static int CountTokens(LLamaWeights weights, string text) =>
        weights.Tokenize(text, add_bos: false, special: true, Encoding.UTF8).Length;

    /// <summary>Token budget a prompt must fit within, leaving room for the answer.</summary>
    public static int PromptBudgetTokens(LlamaConfig config) =>
        (int)config.ContextSize - MinResponseTokens;

    /// <summary>True when a prompt of this size leaves enough room to attempt a response.</summary>
    public static bool PromptFits(LlamaConfig config, int promptTokens) =>
        promptTokens <= (int)config.ContextSize - MinRoomToAttempt;

    /// <summary>
    /// Max new tokens to allow so that prompt + generation stays inside the context window.
    /// Respects a configured <see cref="LlamaConfig.MaxTokens"/> cap when one is set.
    /// </summary>
    public static int MaxGeneration(LlamaConfig config, int promptTokens)
    {
        int available = (int)config.ContextSize - promptTokens - GenerationMargin;
        int cap = config.MaxTokens > 0 ? Math.Min(config.MaxTokens, available) : available;
        return Math.Max(cap, 1);
    }

    /// <summary>Message shown when even a trimmed prompt (dataset context + one turn) will not fit.</summary>
    public static string ContextTooSmallMessage(LlamaConfig config) =>
        $"The dataset context is too large for the model's context window " +
        $"(LLamaSharp:ContextSize = {config.ContextSize} tokens). Increase ContextSize in appsettings.json, " +
        $"or use a dataset with fewer columns, then try again.";
}
