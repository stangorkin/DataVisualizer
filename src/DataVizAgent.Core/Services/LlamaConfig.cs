namespace DataVizAgent.Services;

public sealed class LlamaConfig
{
    /// <summary>
    /// Resolved path to the GGUF model. Settable so the first-run downloader can point the running
    /// app at a freshly installed model without a restart (the lazy model loaders re-read it).
    /// </summary>
    public string? ModelPath { get; set; }
    public int GpuLayerCount { get; init; } = 0;
    public uint ContextSize { get; init; } = 4096;
    public float Temperature { get; init; } = 0.7f;
    public int MaxTokens { get; init; } = -1;
    public string? SystemPrompt { get; init; }

    /// <summary>
    /// When true (and the "tools" pipeline is active), tool calls are constrained with a GBNF
    /// grammar generated from the tool schemas, so the model cannot emit a malformed call.
    /// </summary>
    public bool ConstrainToolCalls { get; init; } = true;
    /// <summary>
    /// If set, everything before (and including) the last occurrence of this marker
    /// in the response is stripped. Useful for thinking models that emit reasoning before answering.
    /// </summary>
    public string? ResponseStartMarker { get; init; }
}