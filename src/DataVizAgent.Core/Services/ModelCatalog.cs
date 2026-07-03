using DataVizAgent.Models;

namespace DataVizAgent.Services;

/// <summary>
/// The curated set of GGUF models the first-run downloader offers, spanning hardware tiers. All are
/// the Q4_K_M quant of the Qwen3 family (strong tool calling), pinned to a Hugging Face commit so the
/// SHA-256/size below stay valid. Sizes and hashes come from each repo's LFS pointer.
/// </summary>
public static class ModelCatalog
{
    public static IReadOnlyList<DownloadableModel> Models { get; } =
    [
        new DownloadableModel
        {
            Id = "qwen3-4b",
            Name = "Qwen3-4B",
            Tier = "Entry",
            Description = "Fastest and lightest. Handles straightforward \"chart sales by region\" requests; ideal when you have no dedicated GPU or limited RAM.",
            HardwareHint = "8 GB RAM, CPU-only",
            Repo = "bartowski/Qwen_Qwen3-4B-GGUF",
            Revision = "cb76885dc66d50759b207c5a48c4e78dfa00c638",
            FileName = "Qwen_Qwen3-4B-Q4_K_M.gguf",
            Sha256 = "fbe1d5edd4ce802ae3ae7c7e4ab7d09789d697fdac1fc7929f8df4ca3c41bae3",
            SizeBytes = 2497280960,
        },
        new DownloadableModel
        {
            Id = "qwen3-8b",
            Name = "Qwen3-8B",
            Tier = "Recommended",
            IsRecommended = true,
            Description = "Best balance of quality and size. Noticeably better at choosing columns, applying filters, and multi-step queries.",
            HardwareHint = "16 GB RAM, or 8 GB VRAM",
            Repo = "bartowski/Qwen_Qwen3-8B-GGUF",
            Revision = "0b69f75b7472688e6808490aa2b85efdb81b5ce7",
            FileName = "Qwen_Qwen3-8B-Q4_K_M.gguf",
            Sha256 = "54fffa050078e984116639c83dfb64b5aa6d4cd474e018b076777c632bbccccd",
            SizeBytes = 5027784224,
        },
        new DownloadableModel
        {
            Id = "qwen3-14b",
            Name = "Qwen3-14B",
            Tier = "High quality",
            Description = "Stronger reasoning on ambiguous requests. Best with a mid/high-end GPU; slow if run purely on CPU.",
            HardwareHint = "32 GB RAM, or 12 GB VRAM",
            Repo = "bartowski/Qwen_Qwen3-14B-GGUF",
            Revision = "bd080f768a6401c2d5a7fa53a2e50cd8218a9ce2",
            FileName = "Qwen_Qwen3-14B-Q4_K_M.gguf",
            Sha256 = "915913e22399475dbe6c968ac014d9f1fbe08975e489279aede9d5c7b2c98eb6",
            SizeBytes = 9001753632,
        },
        new DownloadableModel
        {
            Id = "qwen3-30b-a3b",
            Name = "Qwen3-30B-A3B",
            Tier = "Top tier",
            Description = "Mixture-of-experts with ~3B active parameters: runs much faster than its size implies while answering like a far larger model. For workstations / high-VRAM GPUs.",
            HardwareHint = "32 GB+ RAM, or 24 GB VRAM",
            Repo = "bartowski/Qwen_Qwen3-30B-A3B-GGUF",
            Revision = "46f17e079cba70b04390bef39b57d2783e9fd015",
            FileName = "Qwen_Qwen3-30B-A3B-Q4_K_M.gguf",
            Sha256 = "a015794bfb1d69cb03dbb86b185fb2b9b339f757df5f8f9dd9ebdab8f6ed5d32",
            SizeBytes = 18632184480,
        },
    ];
}
