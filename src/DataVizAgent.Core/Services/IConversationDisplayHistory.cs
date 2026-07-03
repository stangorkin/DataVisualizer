namespace DataVizAgent.Services;

/// <summary>A single rendered chat turn for rehydrating the chat panel: role plus display text.</summary>
public readonly record struct ChatHistoryEntry(string Role, string Text);

/// <summary>
/// Optional capability that exposes the rendered conversation log so the UI can repopulate the chat
/// panel after a restart or session load. Implemented by the Agent Framework pipeline (whose history
/// is persisted); the legacy and MEAI pipelines do not implement it.
/// </summary>
public interface IConversationDisplayHistory
{
    /// <summary>The conversation as user/assistant display turns, oldest first.</summary>
    IReadOnlyList<ChatHistoryEntry> GetDisplayHistory();

    /// <summary>Raised when the display history is replaced (e.g. a session was loaded at runtime).</summary>
    event Action? DisplayHistoryChanged;
}
