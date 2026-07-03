using System.Text.Json;

namespace DataVizAgent.Services;

/// <summary>
/// Optional capability implemented by chat services whose conversation state can be serialized and
/// restored — letting the chat survive a save/load of the session. The legacy and MEAI pipelines do
/// not implement this (their history is transient); the Agent Framework pipeline does, via the
/// serializable agent session. <see cref="ISessionFileService"/> checks for this at runtime.
/// </summary>
public interface IConversationStatePersistence
{
    /// <summary>Serializes the current conversation, or null when there is nothing to persist.</summary>
    Task<JsonElement?> ExportConversationAsync(CancellationToken cancellationToken = default);

    /// <summary>Restores a conversation previously produced by <see cref="ExportConversationAsync"/>.</summary>
    Task ImportConversationAsync(JsonElement conversation, CancellationToken cancellationToken = default);
}
