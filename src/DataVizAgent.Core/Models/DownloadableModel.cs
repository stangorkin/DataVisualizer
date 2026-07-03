namespace DataVizAgent.Models;

/// <summary>
/// A GGUF model the app can fetch on first run. Every entry is pinned to an immutable Hugging Face
/// commit revision, exact filename, byte size, and SHA-256 so the download is reproducible and can
/// be integrity-verified.
/// </summary>
public sealed record DownloadableModel
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Tier { get; init; }
    public bool IsRecommended { get; init; }
    public required string Description { get; init; }
    public required string HardwareHint { get; init; }

    public required string Repo { get; init; }
    public required string Revision { get; init; }
    public required string FileName { get; init; }

    /// <summary>Lowercase hex SHA-256 of the file, from the Hugging Face LFS pointer.</summary>
    public required string Sha256 { get; init; }
    public required long SizeBytes { get; init; }

    /// <summary>Direct download URL, pinned to the commit revision (not a moving branch).</summary>
    public string DownloadUrl => $"https://huggingface.co/{Repo}/resolve/{Revision}/{FileName}";
    public string RepoUrl => $"https://huggingface.co/{Repo}";
    public double SizeGb => SizeBytes / 1_000_000_000d;
}

/// <summary>Phase of an in-progress model download, for UI reporting.</summary>
public enum ModelDownloadPhase
{
    Downloading,
    Verifying,
}

/// <summary>Progress snapshot emitted while a model downloads or is verified.</summary>
public readonly record struct ModelDownloadProgress(ModelDownloadPhase Phase, long BytesReceived, long TotalBytes)
{
    public double Fraction => TotalBytes > 0 ? Math.Clamp((double)BytesReceived / TotalBytes, 0, 1) : 0;
}
