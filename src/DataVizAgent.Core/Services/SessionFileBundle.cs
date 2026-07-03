using System.Text.Json;

namespace DataVizAgent.Services;

public sealed class SessionFileBundle
{
    public PersistedReportDocument? Report { get; init; }
    public PersistedDatasetSnapshot? Dataset { get; init; }

    /// <summary>
    /// Serialized agent conversation, when the active chat pipeline supports persistence
    /// (see <see cref="IConversationStatePersistence"/>). Null for the legacy/MEAI pipelines.
    /// </summary>
    public JsonElement? Conversation { get; init; }
}