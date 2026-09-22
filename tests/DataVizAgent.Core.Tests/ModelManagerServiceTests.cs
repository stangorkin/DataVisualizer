using DataVizAgent.Models;
using DataVizAgent.Services;
using Xunit;

namespace DataVizAgent.Core.Tests;

/// <summary>
/// Covers the in-app model switcher: listing installed .gguf files, switching (config update +
/// persisted selection + deferred chat reload), and download-then-activate.
/// </summary>
public sealed class ModelManagerServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"dva-models-{Guid.NewGuid():N}");

    private string SelectionPath => Path.Combine(_dir, "selected-model.json");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // Best-effort temp cleanup.
        }
    }

    private string AddFakeGguf(string name)
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, "not a real model");
        return path;
    }

    private ModelManagerService CreateManager(LlamaConfig config, FakeChatService chat) =>
        new(config, new ModelDownloadService(config, new HttpClient(), _dir), chat, SelectionPath);

    [Fact]
    public void GetInstalledModels_ListsGgufFilesInTheFolder()
    {
        AddFakeGguf("b-model.gguf");
        AddFakeGguf("a-model.gguf");
        var manager = CreateManager(new LlamaConfig(), new FakeChatService());

        IReadOnlyList<InstalledModel> installed = manager.GetInstalledModels();

        Assert.Equal(["a-model.gguf", "b-model.gguf"], installed.Select(m => m.FileName).ToArray());
    }

    [Fact]
    public void SwitchTo_SetsConfigPersistsAndRequestsReload()
    {
        string a = AddFakeGguf("a.gguf");
        string b = AddFakeGguf("b.gguf");
        var config = new LlamaConfig { ModelPath = a };
        var chat = new FakeChatService();
        var manager = CreateManager(config, chat);
        bool changedRaised = false;
        manager.Changed += () => changedRaised = true;

        string? error = manager.SwitchTo(b);

        Assert.Null(error);
        Assert.Equal(Path.GetFullPath(b), config.ModelPath);
        Assert.Equal(1, chat.ReloadRequests);
        Assert.True(changedRaised);
        Assert.Equal(Path.GetFullPath(b), ModelSelectionStore.TryLoad(SelectionPath));
    }

    [Fact]
    public void SwitchTo_MissingFile_ReturnsErrorAndKeepsConfig()
    {
        string a = AddFakeGguf("a.gguf");
        var config = new LlamaConfig { ModelPath = a };
        var chat = new FakeChatService();
        var manager = CreateManager(config, chat);

        string? error = manager.SwitchTo(Path.Combine(_dir, "missing.gguf"));

        Assert.NotNull(error);
        Assert.Equal(a, config.ModelPath);
        Assert.Equal(0, chat.ReloadRequests);
    }

    [Fact]
    public async Task DownloadAsync_ActivatesPersistsAndRequestsReload()
    {
        var config = new LlamaConfig();
        var chat = new FakeChatService();
        var manager = new ModelManagerService(config, new FakeDownloadService(_dir, config), chat, SelectionPath);

        string path = await manager.DownloadAsync(ModelCatalog.Models[0]);

        Assert.True(File.Exists(path));
        Assert.Equal(path, config.ModelPath);
        Assert.Equal(1, chat.ReloadRequests);
        Assert.Equal(path, ModelSelectionStore.TryLoad(SelectionPath));
    }

    [Fact]
    public void SelectionStore_RoundTripsAndToleratesCorruptFiles()
    {
        Directory.CreateDirectory(_dir);
        ModelSelectionStore.Save(@"c:\models\x.gguf", SelectionPath);
        Assert.Equal(@"c:\models\x.gguf", ModelSelectionStore.TryLoad(SelectionPath));

        File.WriteAllText(SelectionPath, "{not json");
        Assert.Null(ModelSelectionStore.TryLoad(SelectionPath));
    }

    private sealed class FakeChatService : IChatService
    {
#pragma warning disable CS0067 // required by the interface, unused in the fake
        public event Action? HistoryCleared;
        public event Action<ChartSpecResult>? OnChartSpec;
#pragma warning restore CS0067

        public int ReloadRequests { get; private set; }

        public IAsyncEnumerable<string> SendAsync(string userText, CancellationToken ct = default) => Empty();

        public void ClearHistory()
        {
        }

        public void RequestModelReload() => ReloadRequests++;

        private static async IAsyncEnumerable<string> Empty()
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class FakeDownloadService(string modelsDirectory, LlamaConfig config) : IModelDownloadService
    {
        public IReadOnlyList<DownloadableModel> Catalog => ModelCatalog.Models;

        public string ModelsDirectory => modelsDirectory;

        public bool IsModelInstalled() =>
            Directory.Exists(modelsDirectory) && Directory.EnumerateFiles(modelsDirectory, "*.gguf").Any();

        public Task<string> DownloadAsync(DownloadableModel model, IProgress<ModelDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(modelsDirectory);
            string path = Path.Combine(modelsDirectory, model.FileName);
            File.WriteAllText(path, "fake");
            config.ModelPath = path; // mirrors the real download service
            return Task.FromResult(path);
        }
    }
}
