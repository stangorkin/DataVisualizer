using DataVizAgent.Models;

namespace DataVizAgent.Services;

/// <summary>A .gguf file the app can load — usually from the models folder.</summary>
public sealed record InstalledModel(string FileName, string FullPath, long SizeBytes);

/// <summary>
/// UI-facing façade for everything model-related after first run: which models are installed,
/// which is active, switching between them, and downloading more from the curated catalog.
/// Switching is deferred — the chat service reloads the model on the next message, so an
/// in-flight reply is never interrupted.
/// </summary>
public interface IModelManagerService
{
    /// <summary>Raised when the installed set or the active model changes.</summary>
    event Action? Changed;

    string? ActiveModelPath { get; }
    IReadOnlyList<DownloadableModel> Catalog { get; }
    string ModelsDirectory { get; }

    bool IsAnyModelInstalled();
    IReadOnlyList<InstalledModel> GetInstalledModels();
    bool IsInstalled(DownloadableModel model);

    /// <summary>
    /// Makes the model at <paramref name="modelFilePath"/> the active one (persisted; loads on
    /// the next message). Returns an error message, or null on success.
    /// </summary>
    string? SwitchTo(string modelFilePath);

    /// <summary>Downloads a catalog model and makes it the active model. Returns the installed path.</summary>
    Task<string> DownloadAsync(DownloadableModel model, IProgress<ModelDownloadProgress>? progress = null, CancellationToken cancellationToken = default);
}

internal sealed class ModelManagerService : IModelManagerService
{
    private readonly LlamaConfig _config;
    private readonly IModelDownloadService _downloads;
    private readonly IChatService _chatService;
    private readonly string? _selectionFilePath; // test seam; null → the per-user default

    public event Action? Changed;

    public ModelManagerService(LlamaConfig config, IModelDownloadService downloads, IChatService chatService)
        : this(config, downloads, chatService, selectionFilePath: null)
    {
    }

    internal ModelManagerService(LlamaConfig config, IModelDownloadService downloads, IChatService chatService, string? selectionFilePath)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _downloads = downloads ?? throw new ArgumentNullException(nameof(downloads));
        _chatService = chatService ?? throw new ArgumentNullException(nameof(chatService));
        _selectionFilePath = selectionFilePath;
    }

    public string? ActiveModelPath => _config.ModelPath;

    public IReadOnlyList<DownloadableModel> Catalog => _downloads.Catalog;

    public string ModelsDirectory => _downloads.ModelsDirectory;

    public bool IsAnyModelInstalled() => _downloads.IsModelInstalled();

    public IReadOnlyList<InstalledModel> GetInstalledModels()
    {
        var models = new List<InstalledModel>();

        if (Directory.Exists(ModelsDirectory))
        {
            foreach (string file in Directory.EnumerateFiles(ModelsDirectory, "*.gguf", SearchOption.TopDirectoryOnly)
                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                models.Add(new InstalledModel(Path.GetFileName(file), Path.GetFullPath(file), SafeLength(file)));
            }
        }

        // A model configured from outside the models folder (env var or an explicit config
        // path) still belongs in the switcher.
        string? active = ActiveModelPath;
        if (!string.IsNullOrWhiteSpace(active) && File.Exists(active)
            && !models.Any(m => PathsEqual(m.FullPath, active)))
        {
            models.Insert(0, new InstalledModel(Path.GetFileName(active), Path.GetFullPath(active), SafeLength(active)));
        }

        return models;
    }

    public bool IsInstalled(DownloadableModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return File.Exists(Path.Combine(ModelsDirectory, model.FileName));
    }

    public string? SwitchTo(string modelFilePath)
    {
        if (string.IsNullOrWhiteSpace(modelFilePath))
            return "No model file was given.";

        string fullPath = Path.GetFullPath(modelFilePath);
        if (!File.Exists(fullPath))
            return $"The model file was not found: {fullPath}";

        if (!PathsEqual(fullPath, ActiveModelPath))
        {
            _config.ModelPath = fullPath;
            _chatService.RequestModelReload();
        }

        ModelSelectionStore.Save(fullPath, _selectionFilePath);
        Changed?.Invoke();
        return null;
    }

    public async Task<string> DownloadAsync(DownloadableModel model, IProgress<ModelDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        string path = await _downloads.DownloadAsync(model, progress, cancellationToken).ConfigureAwait(false);

        // The download service already pointed LlamaConfig at the new file; make that choice
        // survive restarts and take effect on the next message.
        ModelSelectionStore.Save(path, _selectionFilePath);
        _chatService.RequestModelReload();
        Changed?.Invoke();
        return path;
    }

    private static long SafeLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch
        {
            return 0;
        }
    }

    private static bool PathsEqual(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) &&
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
