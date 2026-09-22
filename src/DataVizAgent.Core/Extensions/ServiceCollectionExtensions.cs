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
        services.AddSingleton<IModelManagerService, ModelManagerService>();

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
    ///   1. A config value that names a concrete .gguf file (an explicit override).
    ///   2. The model last chosen with the in-app switcher, when that file still exists.
    ///   3. The config value as a folder (the default "models/") — first *.gguf inside.
    ///   4. The LLAMASHARP_MODEL_PATH environment variable (file or folder).
    ///   5. The first *.gguf file found in a "models" folder next to the executable.
    /// Returns null when no model is found; <see cref="ChatService"/> surfaces a friendly error.
    /// </summary>
    private static string? ResolveModelPath(string? configured)
    {
        string baseDir = AppContext.BaseDirectory;

        // 1: a config value naming a concrete file is an explicit override
        string? configuredAbs = null;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            configuredAbs = Path.IsPathRooted(configured)
                ? configured
                : Path.GetFullPath(Path.Combine(baseDir, configured));

            if (File.Exists(configuredAbs))
                return configuredAbs;
        }

        // 2: the user's persisted choice from the in-app model switcher
        string? selected = ModelSelectionStore.TryLoad();
        if (!string.IsNullOrWhiteSpace(selected) && File.Exists(selected))
            return selected;

        // 3: config value as a folder — first *.gguf inside (makes the default "models/" work)
        if (configuredAbs is not null)
        {
            string? fromConfig = ResolveFileOrFolder(configuredAbs);
            if (fromConfig is not null)
                return fromConfig;
        }

        // 4: environment variable
        string? envPath = Environment.GetEnvironmentVariable("LLAMASHARP_MODEL_PATH");
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            string? fromEnv = ResolveFileOrFolder(envPath);
            if (fromEnv is not null)
                return fromEnv;
        }

        // 5: first *.gguf in a "models" subfolder next to the exe
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