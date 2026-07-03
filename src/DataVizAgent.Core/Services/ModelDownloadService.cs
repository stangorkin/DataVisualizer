using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using DataVizAgent.Models;

namespace DataVizAgent.Services;

public interface IModelDownloadService
{
    /// <summary>The models the first-run downloader offers.</summary>
    IReadOnlyList<DownloadableModel> Catalog { get; }

    /// <summary>Folder the app loads models from (and downloads into) — next to the executable.</summary>
    string ModelsDirectory { get; }

    /// <summary>True when a usable .gguf is already configured or present in the models folder.</summary>
    bool IsModelInstalled();

    /// <summary>
    /// Downloads (resuming a partial file when possible), verifies the SHA-256, and atomically
    /// installs the model. Returns the installed file path and points the running app at it.
    /// </summary>
    Task<string> DownloadAsync(DownloadableModel model, IProgress<ModelDownloadProgress>? progress = null, CancellationToken cancellationToken = default);
}

internal sealed class ModelDownloadService : IModelDownloadService
{
    private const long DiskSpaceHeadroomBytes = 64L * 1024 * 1024;

    private readonly LlamaConfig _config;
    private readonly HttpClient _httpClient;

    public string ModelsDirectory { get; }

    public ModelDownloadService(LlamaConfig config, HttpClient httpClient)
        : this(config, httpClient, Path.Combine(AppContext.BaseDirectory, "models"))
    {
    }

    // Test seam: lets a temp folder stand in for the models directory.
    internal ModelDownloadService(LlamaConfig config, HttpClient httpClient, string modelsDirectory)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        ModelsDirectory = modelsDirectory ?? throw new ArgumentNullException(nameof(modelsDirectory));
    }

    public IReadOnlyList<DownloadableModel> Catalog => ModelCatalog.Models;

    public bool IsModelInstalled()
    {
        if (!string.IsNullOrWhiteSpace(_config.ModelPath) && File.Exists(_config.ModelPath))
            return true;

        return Directory.Exists(ModelsDirectory)
            && Directory.EnumerateFiles(ModelsDirectory, "*.gguf", SearchOption.TopDirectoryOnly).Any();
    }

    public async Task<string> DownloadAsync(DownloadableModel model, IProgress<ModelDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);

        Directory.CreateDirectory(ModelsDirectory);
        string targetPath = Path.Combine(ModelsDirectory, model.FileName);
        string partPath = targetPath + ".part";

        long existing = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
        if (existing > model.SizeBytes)
        {
            File.Delete(partPath); // stale/oversized leftover — start clean
            existing = 0;
        }

        EnsureDiskSpace(model.SizeBytes - existing);
        await DownloadToPartAsync(model, partPath, existing, progress, cancellationToken).ConfigureAwait(false);

        long finalLength = new FileInfo(partPath).Length;
        if (finalLength != model.SizeBytes)
        {
            SafeDelete(partPath);
            throw new InvalidOperationException(
                $"Download was incomplete (got {finalLength:N0} bytes, expected {model.SizeBytes:N0}). Please try again.");
        }

        progress?.Report(new ModelDownloadProgress(ModelDownloadPhase.Verifying, model.SizeBytes, model.SizeBytes));
        string actualHash = await ComputeSha256Async(partPath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actualHash, model.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            SafeDelete(partPath);
            throw new InvalidOperationException(
                "The downloaded model failed integrity verification (SHA-256 mismatch) and was discarded. Please try again.");
        }

        File.Move(partPath, targetPath, overwrite: true);
        _config.ModelPath = targetPath; // the lazy model loaders pick this up on the next chat
        return targetPath;
    }

    private async Task DownloadToPartAsync(
        DownloadableModel model, string partPath, long existing, IProgress<ModelDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, model.DownloadUrl);
        if (existing > 0)
            request.Headers.Range = new RangeHeaderValue(existing, null);

        using HttpResponseMessage response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        // If the server ignored our Range request, it returns the whole file (200) — restart from zero.
        if (existing > 0 && response.StatusCode == HttpStatusCode.OK)
            existing = 0;

        response.EnsureSuccessStatusCode();

        FileMode mode = existing > 0 ? FileMode.Append : FileMode.Create;
        long received = existing;
        progress?.Report(new ModelDownloadProgress(ModelDownloadPhase.Downloading, received, model.SizeBytes));

        await using var fileStream = new FileStream(partPath, mode, FileAccess.Write, FileShare.None);
        await using Stream httpStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        var buffer = new byte[81920];
        int read;
        while ((read = await httpStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            received += read;
            progress?.Report(new ModelDownloadProgress(ModelDownloadPhase.Downloading, received, model.SizeBytes));
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        using var sha256 = SHA256.Create();
        byte[] hash = await sha256.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private void EnsureDiskSpace(long bytesNeeded)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(ModelsDirectory));
            if (string.IsNullOrEmpty(root))
                return;

            var drive = new DriveInfo(root);
            if (drive.IsReady && drive.AvailableFreeSpace < bytesNeeded + DiskSpaceHeadroomBytes)
            {
                throw new IOException(
                    $"Not enough free disk space to download this model (need about {bytesNeeded / 1_000_000:N0} MB).");
            }
        }
        catch (IOException)
        {
            throw;
        }
        catch
        {
            // Couldn't probe the drive (unusual path, permissions) — don't block the download over it.
        }
    }

    private static void SafeDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
