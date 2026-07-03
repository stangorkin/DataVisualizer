using System;
using System.Windows;
using DataVizAgent.Desktop.Services;
using DataVizAgent.Extensions;
using DataVizAgent.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DataVizAgent.Desktop;

public partial class App : Application
{
    public IServiceProvider Services { get; }

    public App()
    {
        var services = new ServiceCollection();

        IConfiguration configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .Build();

        services.AddSingleton(configuration);
        services.AddLogging();
        services.AddWpfBlazorWebView();
#if DEBUG
        services.AddBlazorWebViewDeveloperTools();
#endif
        // Register desktop dialogs before AddDataVizAgentCore so the core's TryAdd
        // fallback (browser dialogs) does not win.
        services.AddSingleton<DesktopSessionFileDialogService>();
        services.AddSingleton<IDesktopSessionFileDialogService>(sp =>
            sp.GetRequiredService<DesktopSessionFileDialogService>());
        services.AddSingleton<ISessionFileDialogService>(sp =>
            sp.GetRequiredService<DesktopSessionFileDialogService>());
        services.AddDataVizAgentCore(configuration);
        services.AddTransient<MainWindow>();

        Services = services.BuildServiceProvider();
        Resources["services"] = Services;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        MainWindow = Services.GetRequiredService<MainWindow>();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // ChatService is IAsyncDisposable-only (model weights), so the provider must be
        // disposed asynchronously; blocking here is fine because the app is shutting down.
        if (Services is IAsyncDisposable asyncDisposable)
            asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();

        base.OnExit(e);
    }
}