using DataVizAgent.Services;
using LLama.Native;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DataVizAgent.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddDataVizAgentCore(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        IConfigurationSection llamaSection = configuration.GetSection("LLamaSharp");
        string backend = llamaSection["Backend"]
            ?? Environment.GetEnvironmentVariable("LLAMASHARP_BACKEND")
            ?? "cpu";

        // WithAutoFallback keeps the app working on CPU when the CUDA backend is not
        // present (builds without -p:EnableCuda=true) or no compatible GPU is found.
        if (string.Equals(backend, "cuda", StringComparison.OrdinalIgnoreCase))
            NativeLibraryConfig.All.WithCuda().WithAutoFallback();

        LlamaConfig llamaConfig = new()
        {
            ModelPath = ResolveModelPath(llamaSection["ModelPath"]),
            GpuLayerCount = int.TryParse(llamaSection["GpuLayerCount"], out int gpuLayers) ? gpuLayers : 0,
            ContextSize = uint.TryParse(llamaSection["ContextSize"], out uint contextSize) ? contextSize : 4096,
            Temperature = float.TryParse(llamaSection["Temperature"], out float temperature) ? temperature : 0.7f,
            MaxTokens = int.TryParse(llamaSection["MaxTokens"], out int maxTokens) ? maxTokens : -1,
            SystemPrompt = llamaSection["SystemPrompt"],
            ResponseStartMarker = llamaSection["ResponseStartMarker"],
            ConstrainToolCalls = !bool.TryParse(llamaSection["ConstrainToolCalls"], out bool constrain) || constrain,
            DisableThinking = bool.TryParse(llamaSection["DisableThinking"], out bool noThink) && noThink,
        };

        services.AddSingleton<IDatasetPersistenceService, DatasetPersistenceService>();
        services.AddSingleton<IDataService, DataService>();
        services.AddSingleton<IReportPersistenceService, ReportPersistenceService>();
        services.AddSingleton<IConversationPersistenceService, ConversationPersistenceService>();
        services.AddSingleton<IChartContextProvider, ChartContextProvider>();
        services.AddSingleton<IDatabaseImportService, DatabaseImportService>();
        services.AddSingleton(_ => llamaConfig);

        // Long-lived client with no overall timeout: model downloads are multi-GB and are bounded
        // by streaming + cancellation, not a request clock.
        services.AddSingleton(_ => new HttpClient { Timeout = Timeout.InfiniteTimeSpan });
        services.AddSingleton<IModelDownloadService, ModelDownloadService>();

        // Pipeline selection (A/B):
        //   "agent"  → Microsoft Agent Framework (ChatClientAgent + serializable session memory)
        //   "tools"  → Microsoft.Extensions.AI function-invocation pipeline
        //   else     → legacy hand-written response parser
        string pipeline = llamaSection["Pipeline"] ?? "legacy";
        if (string.Equals(pipeline, "agent", StringComparison.OrdinalIgnoreCase))
            services.AddSingleton<IChatService, AgentChatService>();
        else if (string.Equals(pipeline, "tools", StringComparison.OrdinalIgnoreCase))
            services.AddSingleton<IChatService, MeaiChatService>();
        else
            services.AddSingleton<IChatService, ChatService>();
        services.AddSingleton<IReportSessionService, ReportSessionService>();
        services.AddSingleton<ISessionFileService, SessionFileService>();
        services.AddSingleton<ISessionCommandBus, SessionCommandBus>();

        // Browser dialogs are the fallback; a host (e.g. the WPF desktop shell) provides its
        // own dialog services by registering them BEFORE calling AddDataVizAgentCore.
        services.TryAddSingleton<ISessionFileDialogService, BrowserSessionFileDialogService>();
        services.TryAddSingleton<IDataFileDialogService, BrowserDataFileDialogService>();

        return services;
    }

    /// <summary>
    /// Resolves the GGUF model path with the following priority:
    ///   1. The value from appsettings.json (absolute or relative to the exe). It may be a
    ///      .gguf file or a folder, in which case the first *.gguf inside is used — this is
    ///      what makes the default "models/" setting work.
    ///   2. The LLAMASHARP_MODEL_PATH environment variable (file or folder, likewise).
    ///   3. The first *.gguf file found in a "models" folder next to the executable.
    /// Returns null when no model is found; <see cref="ChatService"/> surfaces a friendly error.
    /// </summary>
    private static string? ResolveModelPath(string? configured)
    {
        string baseDir = AppContext.BaseDirectory;

        // 1: value from config (absolute or relative to exe)
        if (!string.IsNullOrWhiteSpace(configured))
        {
            string abs = Path.IsPathRooted(configured)
                ? configured
                : Path.GetFullPath(Path.Combine(baseDir, configured));

            string? fromConfig = ResolveFileOrFolder(abs);
            if (fromConfig is not null)
                return fromConfig;
        }

        // 2: environment variable
        string? envPath = Environment.GetEnvironmentVariable("LLAMASHARP_MODEL_PATH");
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            string? fromEnv = ResolveFileOrFolder(envPath);
            if (fromEnv is not null)
                return fromEnv;
        }

        // 3: first *.gguf in a "models" subfolder next to the exe
        return ResolveFileOrFolder(Path.Combine(baseDir, "models"));
    }

    private static string? ResolveFileOrFolder(string path)
    {
        if (File.Exists(path))
            return path;

        if (Directory.Exists(path))
        {
            return Directory.EnumerateFiles(path, "*.gguf", SearchOption.TopDirectoryOnly)
                .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }

        return null;
    }
}