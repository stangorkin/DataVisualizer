using System.Text.RegularExpressions;

namespace DataVizAgent.Services;

/// <summary>
/// Removes a model's chain-of-thought / channel preamble so only the final answer is shown.
/// Handles the two common local-model conventions universally rather than tying the app to one
/// model family: Qwen/DeepSeek-style <c>&lt;think&gt;…&lt;/think&gt;</c> blocks and gpt-oss
/// "harmony" channel markers (<c>&lt;|channel|&gt;final&lt;|message|&gt;</c>).
/// </summary>
internal static partial class ReasoningFilter
{
    // Text immediately after one of these marks the start of the user-facing answer.
    [GeneratedRegex(@"</think>|<\|channel\|>\s*final\s*<\|message\|>|<final>|assistant\s*final\s*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AnswerStartRegex();

    // A reasoning section the model may still be writing (no answer-start seen yet).
    [GeneratedRegex(@"<think>|<\|channel\|>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReasoningOpenRegex();

    [GeneratedRegex(@"<think>.*?</think>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex ThinkBlockRegex();

    /// <summary>
    /// The final answer: everything after the last answer-start marker, with any remaining
    /// reasoning block removed. Plain output (no markers) is returned unchanged.
    /// </summary>
    public static string StripForFinal(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        int answerStart = LastAnswerStart(text);
        string result = answerStart >= 0 ? text[answerStart..] : ThinkBlockRegex().Replace(text, string.Empty);
        return result.Trim();
    }

    /// <summary>
    /// A streaming snapshot: the answer-so-far, or an empty string while the model is still
    /// reasoning (the caller keeps showing a "Thinking…" placeholder until the answer begins).
    /// </summary>
    public static string StripForStreaming(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        int answerStart = LastAnswerStart(text);
        if (answerStart >= 0)
            return text[answerStart..].TrimStart();

        // No answer marker yet — if a reasoning section has opened, show nothing.
        return ReasoningOpenRegex().IsMatch(text) ? string.Empty : text;
    }

    private static int LastAnswerStart(string text)
    {
        int end = -1;
        foreach (Match match in AnswerStartRegex().Matches(text))
            end = match.Index + match.Length;
        return end;
    }
}
