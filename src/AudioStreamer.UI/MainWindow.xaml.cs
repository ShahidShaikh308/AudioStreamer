using AudioStreamer.UI.Core.Connection;
using AudioStreamer.UI.Core.Navigation;
using AudioStreamer.UI.Core.ViewModels;
using Microsoft.UI.Windowing;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using WinRT.Interop;

namespace AudioStreamer.UI;

public sealed partial class MainWindow : Window
{
    private readonly RuntimeStateViewModel _state;
    private readonly IUiPreferencesStore _preferences;
    private readonly IUiPageFactory _pageFactory;
    private readonly IpcConnectionCoordinator _connection;
    private readonly WindowHandleProvider _windowHandle;
    private readonly SettingsViewModel _settings;
    private readonly Dictionary<UiPage, Page> _pageCache = [];
    private bool _allowClose;
    private AppWindow _appWindow;

    public MainWindow(RuntimeStateViewModel state, IUiPreferencesStore preferences, IUiPageFactory pageFactory,
        IpcConnectionCoordinator connection, WindowHandleProvider windowHandle, SettingsViewModel settings)
    {
        InitializeComponent();
        var settingsAccelerator = new KeyboardAccelerator
        { Key = (VirtualKey)0xBC, Modifiers = VirtualKeyModifiers.Control };
        settingsAccelerator.Invoked += OnSettingsShortcut;
        Navigation.KeyboardAccelerators.Add(settingsAccelerator);
        SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
        _state = state;
        _preferences = preferences;
        _pageFactory = pageFactory;
        _connection = connection;
        _windowHandle = windowHandle;
        _settings = settings;
        _settings.ThemeChanged += ApplyTheme;
        Navigation.DataContext = state;
        _state.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RuntimeStateViewModel.ConnectionStatus))
                OfflineOverlay.Visibility = state.ConnectionStatus.State is ServiceConnectionState.ServiceUnavailable or ServiceConnectionState.Reconnecting
                    ? Visibility.Visible : Visibility.Collapsed;
        };
        var hwnd = WindowNative.GetWindowHandle(this);
        _windowHandle.Set(hwnd);
        _appWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd));
        _appWindow.Closing += OnAppWindowClosing;
        Closed += async (_, _) => await ((App)Application.Current).ShutdownUiAsync();
        Navigation.SelectedItem = Navigation.MenuItems[0];
        Navigate(UiPage.Dashboard);
    }

    public void ApplyTheme(string theme) => Navigation.RequestedTheme = theme switch
    { "Dark" => ElementTheme.Dark, "Light" => ElementTheme.Light, _ => ElementTheme.Default };
    public void HideToTray() => _appWindow.Hide();
    public void ShowWindow() { _appWindow.Show(); Activate(); }
    public void NavigateTo(UiPage page) { ShowWindow(); Navigate(page); }

    private void OnSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && Enum.TryParse<UiPage>(item.Tag?.ToString(), out var page)) Navigate(page);
    }

    private void Navigate(UiPage page)
    {
        if (!_pageCache.TryGetValue(page, out var view))
        {
            view = _pageFactory.Create(page);
            if (view is IPageNavigation navigation) navigation.NavigateRequested += NavigateTo;
            _pageCache.Add(page, view);
        }
        PageHost.Content = view;
    }

    private void OnReconnect(object sender, RoutedEventArgs e) => _connection.Retry();
    private void OnRefreshShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    { _ = _state.RefreshAsync(); args.Handled = true; }
    private void OnDiagnosticsShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    { NavigateTo(UiPage.Diagnostics); args.Handled = true; }
    private void OnSettingsShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    { NavigateTo(UiPage.Settings); args.Handled = true; }
    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose) return;
        if (_preferences.Load().CloseToTray) { args.Cancel = true; HideToTray(); }
    }

    public async Task QuitUiAsync()
    {
        _allowClose = true;
        await ((App)Application.Current).ShutdownUiAsync();
        Close();
    }
}
