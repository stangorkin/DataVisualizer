using System.Text.Json;

namespace DataVizAgent.Services;

/// <summary>
/// Autosaves the agent conversation to local app data so chat survives an app restart, mirroring
/// <see cref="DatasetPersistenceService"/> and <see cref="ReportPersistenceService"/>. The stored
/// value is the opaque composite produced by the agent pipeline (serialized session + display log).
/// </summary>
public interface IConversationPersistenceService
{
    JsonElement? TryLoadConversation();
    void SaveConversation(JsonElement conversation);
    void DeleteSavedConversation();
}

internal sealed class ConversationPersistenceService : IConversationPersistenceService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _conversationPath;
    private readonly object _writeLock = new();

    public ConversationPersistenceService()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DataVizAgent",
            "autosave-conversation.json"))
    {
    }

    // Test seam: lets a temp path be supplied without touching the user's app-data folder.
    internal ConversationPersistenceService(string conversationPath) =>
        _conversationPath = conversationPath ?? throw new ArgumentNullException(nameof(conversationPath));

    public JsonElement? TryLoadConversation()
    {
        try
        {
            if (!File.Exists(_conversationPath))
                return null;

            string json = File.ReadAllText(_conversationPath);
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    public void SaveConversation(JsonElement conversation)
    {
        try
        {
            string? directoryPath = Path.GetDirectoryName(_conversationPath);
            if (!string.IsNullOrWhiteSpace(directoryPath))
                Directory.CreateDirectory(directoryPath);

            string json = JsonSerializer.Serialize(conversation, JsonOptions);
            lock (_writeLock)
            {
                File.WriteAllText(_conversationPath, json);
            }
        }
        catch
        {
            // Conversation autosave is best-effort and must never block the active session.
        }
    }

    public void DeleteSavedConversation()
    {
        try
        {
            if (File.Exists(_conversationPath))
                File.Delete(_conversationPath);
        }
        catch
        {
            // Reset should still proceed even if the autosave file cannot be removed.
        }
    }
}
