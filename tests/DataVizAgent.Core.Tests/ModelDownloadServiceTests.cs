using System.Net;
using System.Security.Cryptography;
using System.Text;
using DataVizAgent.Models;
using DataVizAgent.Services;
using Xunit;

namespace DataVizAgent.Core.Tests;

public class ModelDownloadServiceTests
{
    /// <summary>Serves a fixed payload, honoring Range requests so resume can be exercised.</summary>
    private sealed class StubHandler(byte[] payload) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            long from = request.Headers.Range?.Ranges.FirstOrDefault()?.From ?? 0;
            byte[] slice = payload[(int)from..];

            var response = new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(slice),
            };
            return Task.FromResult(response);
        }
    }

    private static string Sha256Hex(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static DownloadableModel ModelFor(byte[] payload, string sha) => new()
    {
        Id = "test", Name = "Test", Tier = "Test", Description = "d", HardwareHint = "h",
        Repo = "owner/repo", Revision = "abc123", FileName = "test.gguf",
        Sha256 = sha, SizeBytes = payload.Length,
    };

    private static (ModelDownloadService Service, LlamaConfig Config, string Dir) Build(byte[] payload)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"dva-models-{Guid.NewGuid():N}");
        var config = new LlamaConfig();
        var service = new ModelDownloadService(config, new HttpClient(new StubHandler(payload)), dir);
        return (service, config, dir);
    }

    [Fact]
    public void Catalog_IsPinnedAndWellFormed()
    {
        Assert.NotEmpty(ModelCatalog.Models);
        foreach (DownloadableModel model in ModelCatalog.Models)
        {
            Assert.Equal(64, model.Sha256.Length);                 // full SHA-256 hex
            Assert.Matches("^[0-9a-f]+$", model.Sha256);
            Assert.True(model.SizeBytes > 0);
            Assert.False(string.IsNullOrWhiteSpace(model.Revision)); // pinned to a commit, not a branch
            Assert.Contains("/resolve/", model.DownloadUrl);
        }

        Assert.Single(ModelCatalog.Models, m => m.IsRecommended);
    }

    [Fact]
    public async Task DownloadAsync_VerifiesHash_InstallsFile_AndPointsConfig()
    {
        byte[] payload = Encoding.UTF8.GetBytes(new string('x', 5000));
        var (service, config, dir) = Build(payload);
        try
        {
            var seen = new List<ModelDownloadProgress>();
            string path = await service.DownloadAsync(
                ModelFor(payload, Sha256Hex(payload)), new Progress<ModelDownloadProgress>(seen.Add));

            Assert.True(File.Exists(path));
            Assert.Equal(payload, await File.ReadAllBytesAsync(path));
            Assert.Equal(path, config.ModelPath);                  // running app now points at it
            Assert.False(File.Exists(path + ".part"));             // temp file cleaned up
            Assert.True(service.IsModelInstalled());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task DownloadAsync_HashMismatch_Throws_AndLeavesNoFile()
    {
        byte[] payload = Encoding.UTF8.GetBytes("hello world");
        var (service, config, dir) = Build(payload);
        try
        {
            DownloadableModel model = ModelFor(payload, Sha256Hex(Encoding.UTF8.GetBytes("something else")));

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadAsync(model));

            Assert.False(File.Exists(Path.Combine(dir, model.FileName)));
            Assert.False(File.Exists(Path.Combine(dir, model.FileName + ".part")));
            Assert.Null(config.ModelPath);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task DownloadAsync_ResumesFromPartialFile()
    {
        byte[] payload = Encoding.UTF8.GetBytes(new string('a', 4096));
        var (service, _, dir) = Build(payload);
        try
        {
            // Pre-seed a half-finished .part file; the service should range-request the rest.
            Directory.CreateDirectory(dir);
            string partPath = Path.Combine(dir, "test.gguf.part");
            await File.WriteAllBytesAsync(partPath, payload[..2048]);

            string path = await service.DownloadAsync(ModelFor(payload, Sha256Hex(payload)));

            Assert.Equal(payload, await File.ReadAllBytesAsync(path));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
