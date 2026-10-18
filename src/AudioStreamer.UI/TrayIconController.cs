using AudioStreamer.UI.Core.Navigation;
using AudioStreamer.UI.Core.ViewModels;
using Forms = System.Windows.Forms;

namespace AudioStreamer.UI;

public sealed class TrayIconController : IDisposable
{
    private readonly MainWindow _window;
    private readonly Forms.NotifyIcon _icon;
    private readonly System.Drawing.Icon _appIcon;

    public TrayIconController(MainWindow window, RuntimeStateViewModel state)
    {
        _window = window;
        var menu = new Forms.ContextMenuStrip();
        var recentDevices = new Forms.ToolStripMenuItem("Recent devices");
        void UpdateRecentDevices()
        {
            recentDevices.DropDownItems.Clear();
            var peers = state.Snapshot.Peers.OrderByDescending(peer => peer.LastSeenUtc).Take(5).ToArray();
            if (peers.Length == 0)
            {
                recentDevices.DropDownItems.Add("No devices discovered", null, (_, _) => { }).Enabled = false;
                return;
            }
            foreach (var peer in peers)
                recentDevices.DropDownItems.Add(peer.Name, null, (_, _) => window.NavigateTo(UiPage.Devices));
        }

        menu.Items.Add("Open AudioStreamer", null, (_, _) => window.ShowWindow());
        menu.Items.Add("Open Sessions to start a stream…", null, (_, _) => window.NavigateTo(UiPage.Sessions));
        menu.Items.Add("Open Sessions to stop a stream…", null, (_, _) => window.NavigateTo(UiPage.Sessions));
        menu.Items.Add(recentDevices);
        menu.Items.Add("Settings", null, (_, _) => window.NavigateTo(UiPage.Settings));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit AudioStreamer UI", null, async (_, _) => await window.QuitUiAsync());
        _appIcon = new System.Drawing.Icon(Path.Combine(AppContext.BaseDirectory, "Assets", "AudioStreamer.ico"));
        _icon = new Forms.NotifyIcon
        {
            Text = "AudioStreamer",
            Icon = _appIcon,
            ContextMenuStrip = menu,
            Visible = true
        };
        _icon.DoubleClick += (_, _) => window.ShowWindow();
        _state = state;
        _updateRecentDevices = UpdateRecentDevices;
        UpdateRecentDevices();
        UpdateTooltip();
        state.PropertyChanged += OnStatePropertyChanged;
        state.SnapshotChanged += OnSnapshotChanged;
    }

    private readonly RuntimeStateViewModel _state;
    private readonly Action _updateRecentDevices;

    private void OnStatePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(RuntimeStateViewModel.NodeName) or nameof(RuntimeStateViewModel.ConnectionStatus))
            UpdateTooltip();
    }

    private void OnSnapshotChanged(string? eventType)
    {
        if (eventType is null or "PeerAdded" or "PeerUpdated" or "PeerRemoved") _updateRecentDevices();
    }

    private void UpdateTooltip()
    {
        var text = _state.IsConnected ? $"AudioStreamer · {_state.NodeName}" : "AudioStreamer · Reconnecting";
        _icon.Text = text.Length <= 63 ? text : text[..60] + "…";
    }

    public void Dispose()
    {
        _state.PropertyChanged -= OnStatePropertyChanged;
        _state.SnapshotChanged -= OnSnapshotChanged;
        _icon.Dispose();
        _appIcon.Dispose();
    }
}
