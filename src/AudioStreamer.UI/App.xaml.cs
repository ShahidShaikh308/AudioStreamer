using AudioStreamer.UI.Core.Connection;
using AudioStreamer.UI.Core.Navigation;
using AudioStreamer.UI.Core.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace AudioStreamer.UI;

public partial class App : Application
{
    private ServiceProvider? _services;
    private MainWindow? _window;
    private TrayIconController? _tray;

    public App()
    {
        InitializeComponent();
    }


    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddDebug();
        });
        services.AddSingleton<IUiDispatcher, WinUiDispatcher>();
        services.AddSingleton<AppStateStore>();
        services.AddSingleton<IpcConnectionCoordinator>();
        services.AddSingleton<RuntimeStateViewModel>();
        services.AddSingleton<SessionsViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<DiagnosticsViewModel>();
        services.AddSingleton<UiPreferencesStore>();
        services.AddSingleton<IUiPreferencesStore>(provider => provider.GetRequiredService<UiPreferencesStore>());
        services.AddSingleton<WindowHandleProvider>();
        services.AddSingleton<IWindowHandleProvider>(provider => provider.GetRequiredService<WindowHandleProvider>());
        services.AddSingleton<IUiPageFactory, UiPageFactory>();
        services.AddSingleton<MainWindow>();
        _services = services.BuildServiceProvider();

        _window = _services.GetRequiredService<MainWindow>();
        _tray = new TrayIconController(_window, _services.GetRequiredService<RuntimeStateViewModel>());
        _window.Activate();
        _services.GetRequiredService<IpcConnectionCoordinator>().Start();

        var preferences = _services.GetRequiredService<UiPreferencesStore>().Load();
        _window.ApplyTheme(preferences.Theme);
        if (preferences.StartMinimized) _window.HideToTray();
    }

    internal async Task ShutdownUiAsync()
    {
        _tray?.Dispose();
        _tray = null;
        if (_services is not null)
        {
            await _services.DisposeAsync();
            _services = null;
        }
    }
}

internal sealed class WinUiDispatcher : IUiDispatcher
{
    private readonly DispatcherQueue _queue = DispatcherQueue.GetForCurrentThread();
    public bool TryEnqueue(Action action) => _queue.TryEnqueue(() => action());
}
