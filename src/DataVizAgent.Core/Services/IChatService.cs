using DataVizAgent.Models;

namespace DataVizAgent.Services;

public interface IChatService
{
    event Action? HistoryCleared;

    /// <summary>
    /// Send a user message and stream back snapshots of the displayable response text.
    /// Each yielded value REPLACES the previous one (it is the full text so far, not a
    /// delta); the final snapshot is authoritative, with tool-call blocks stripped.
    /// </summary>
    IAsyncEnumerable<string> SendAsync(string userText, CancellationToken ct = default);

    /// <summary>Clear conversation history and reset the model context.</summary>
    void ClearHistory();

    /// <summary>
    /// Requests that the model be reloaded from <see cref="LlamaConfig.ModelPath"/> before the
    /// next message. Deferred on purpose: an in-flight reply finishes on the old model — nothing
    /// is disposed mid-stream. Conversation history is kept across the switch.
    /// </summary>
    void RequestModelReload();

    /// <summary>Raised when the agent produces a valid chart spec in its response.</summary>
    event Action<ChartSpecResult>? OnChartSpec;
}