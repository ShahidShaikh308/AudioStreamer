using AudioStreamer.UI.Core.Navigation;
using AudioStreamer.UI.Core.ViewModels;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace AudioStreamer.UI;

internal static class PageUi
{
    public static StackPanel Root(string title, string subtitle) => new()
    {
        Padding = new Thickness(32),
        Spacing = 18,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        Children = { new TextBlock { Text = title, FontSize = 30, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }, new TextBlock { Text = subtitle, TextWrapping = TextWrapping.Wrap, Opacity = 0.72 } }
    };
    public static TextBlock Text(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };
    public static Border Card(UIElement child) => new() { Style = (Style)Application.Current.Resources["AudioCardStyle"], Child = child };
    public static Border Metric(string title, string glyph, Func<string> value, string caption)
    {
        var number = new TextBlock { Text = value(), FontSize = 27, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        var content = new Grid { RowSpacing = 12 };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9 };
        heading.Children.Add(new FontIcon { Glyph = glyph, FontSize = 16, Foreground = (Brush)Application.Current.Resources["AccentBrush"] });
        heading.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.75 });
        content.Children.Add(heading);
        Grid.SetRow(number, 1); content.Children.Add(number);
        var description = new TextBlock { Text = caption, FontSize = 12, Opacity = 0.65, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetRow(description, 2); content.Children.Add(description);
        return new Border { Style = (Style)Application.Current.Resources["AudioCardStyle"], Child = content, Width = 245, Height = 142, Margin = new Thickness(0, 0, 12, 12), Tag = number };
    }
    public static void UpdateMetric(Border card, string text) { if (card.Tag is TextBlock value) value.Text = text; }
}

public sealed class DashboardPage : Page, IPageNavigation
{
    public event Action<UiPage>? NavigateRequested;
    public DashboardPage(RuntimeStateViewModel state)
    {
        var root = new StackPanel { Padding = new Thickness(32, 26, 32, 32), Spacing = 22, HorizontalAlignment = HorizontalAlignment.Stretch };
        root.Children.Add(new TextBlock { Text = "Your audio, connected.", FontSize = 32, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        root.Children.Add(PageUi.Text("Manage your Windows node and start a stream to a nearby device."));

        var heroGrid = new Grid { ColumnSpacing = 20 };
        heroGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heroGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heroGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heroGrid.Children.Add(new Image { Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/audiostreamer_logo.png")), Width = 76, Height = 76, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center });
        var nodeInfo = new StackPanel { Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        var nodeName = new TextBlock { Text = state.NodeName, FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        var nodeStatus = PageUi.Text(ConnectionSummary(state));
        nodeInfo.Children.Add(nodeName); nodeInfo.Children.Add(nodeStatus); Grid.SetColumn(nodeInfo, 1); heroGrid.Children.Add(nodeInfo);
        var refresh = new Button { Content = "Refresh status", Command = state.RefreshCommand, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(refresh, 2); heroGrid.Children.Add(refresh);
        root.Children.Add(new Border { Padding = new Thickness(24), CornerRadius = new CornerRadius(18), Background = (Brush)Application.Current.Resources["AccentSoftBrush"], Child = heroGrid });

        var overview = new Grid { ColumnSpacing = 14 };
        overview.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        overview.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var devices = SummaryCard("\uE716", "Nearby devices", "", "Browse available devices", () => NavigateRequested?.Invoke(UiPage.Devices));
        var streams = SummaryCard("\uE768", "Active streams", "", "Manage sessions", () => NavigateRequested?.Invoke(UiPage.Sessions));
        Grid.SetColumn(streams, 1); overview.Children.Add(devices); overview.Children.Add(streams);
        root.Children.Add(overview);

        var start = new Button { Content = "Start a stream", Style = (Style)Application.Current.Resources["AccentButtonStyle"], HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 150 };
        start.Click += (_, _) => NavigateRequested?.Invoke(UiPage.Sessions);
        root.Children.Add(start);

        void UpdateSummary()
        {
            nodeName.Text = state.NodeName;
            nodeStatus.Text = ConnectionSummary(state);
            SetSummary(devices, state.PeerCount == 1 ? "1 device online" : $"{state.PeerCount} devices online");
            SetSummary(streams, state.StreamCount == 1 ? "1 stream is active" : state.StreamCount == 0 ? "No active streams" : $"{state.StreamCount} streams are active");
        }
        state.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is null or nameof(RuntimeStateViewModel.NodeName) or nameof(RuntimeStateViewModel.NodeState) or nameof(RuntimeStateViewModel.ConnectionStatus) or nameof(RuntimeStateViewModel.PeerCount) or nameof(RuntimeStateViewModel.StreamCount)) UpdateSummary();
        };
        state.SnapshotChanged += eventType => { if (eventType is null or "PeerAdded" or "PeerUpdated" or "PeerRemoved" or "SessionsChanged" or "SessionCreated" or "SessionPrepared" or "SessionStarted" or "SessionRecovered" or "SessionStopped" or "SessionFailed" or "SessionChanged") UpdateSummary(); };
        UpdateSummary();
        Content = new ScrollViewer { Content = root, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    }

    private static Border SummaryCard(string glyph, string title, string summary, string action, Action onClick)
    {
        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { new FontIcon { Glyph = glyph, FontSize = 18, Foreground = (Brush)Application.Current.Resources["AccentBrush"], VerticalAlignment = VerticalAlignment.Center }, new TextBlock { Text = title, FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center } } });
        var value = new TextBlock { Text = summary, FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.Medium };
        body.Children.Add(value);
        var button = new Button { Content = action, HorizontalAlignment = HorizontalAlignment.Left };
        button.Click += (_, _) => onClick(); body.Children.Add(button);
        return new Border { Style = (Style)Application.Current.Resources["AudioCardStyle"], Child = body, Tag = value };
    }

    private static void SetSummary(Border card, string summary)
    {
        if (card.Tag is TextBlock value) value.Text = summary;
    }

    private static string ConnectionSummary(RuntimeStateViewModel state) => state.ConnectionStatus.State switch
    {
        AudioStreamer.UI.Core.Connection.ServiceConnectionState.Connected => $"{state.NodeState} · Connected to AudioStreamer",
        AudioStreamer.UI.Core.Connection.ServiceConnectionState.Synchronizing => "Getting your node ready…",
        AudioStreamer.UI.Core.Connection.ServiceConnectionState.Connecting => "Connecting to AudioStreamer…",
        AudioStreamer.UI.Core.Connection.ServiceConnectionState.Reconnecting => "Reconnecting to AudioStreamer…",
        _ => "AudioStreamer service is unavailable"
    };
}

public sealed class DevicesPage : Page, IPageNavigation
{
    public event Action<UiPage>? NavigateRequested;
    public DevicesPage(RuntimeStateViewModel state, SessionsViewModel sessionsViewModel)
    {
        var root = new Grid { Padding = new Thickness(30, 22, 30, 20), RowSpacing = 12 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var header = PageUi.Root("Devices", "AudioStreamer peers discovered on your network.");
        root.Children.Add(header);
        var grid = new GridView
        {
            ItemsSource = state.Snapshot.Peers,
            ItemTemplate = (DataTemplate)Application.Current.Resources["PeerCardTemplate"],
            ItemsPanel = (ItemsPanelTemplate)Application.Current.Resources["PeerItemsPanel"],
            SelectionMode = ListViewSelectionMode.Single,
            IsItemClickEnabled = false
        };
        var flyout = new Flyout();
        grid.SelectionChanged += (_, _) =>
        {
            if (grid.SelectedItem is not AudioStreamer.IPC.Contracts.IpcPeerSnapshot peer) return;
            flyout.Content = PeerDetails(peer, sessionsViewModel);
            flyout.ShowAt(grid);
            grid.SelectedItem = null;
        };
        var empty = EmptyPeers(state);
        void UpdatePeers()
        {
            grid.ItemsSource = state.Snapshot.Peers;
            if (state.Snapshot.Peers.Length == 0)
            {
                root.Children.Remove(grid);
                if (!root.Children.Contains(empty)) { Grid.SetRow(empty, 1); root.Children.Add(empty); }
            }
            else
            {
                root.Children.Remove(empty);
                if (!root.Children.Contains(grid)) { Grid.SetRow(grid, 1); root.Children.Add(grid); }
            }
        }
        state.SnapshotChanged += eventType =>
        {
            if (eventType is null or "PeerAdded" or "PeerUpdated" or "PeerRemoved") UpdatePeers();
        };
        UpdatePeers(); Content = root;
    }

    private Border EmptyPeers(RuntimeStateViewModel state) => PageUi.Card(new StackPanel
    {
        Spacing = 10,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Children = { new FontIcon { Glyph = "\uE716", FontSize = 34, Foreground = (Brush)Application.Current.Resources["AccentBrush"], HorizontalAlignment = HorizontalAlignment.Center },
            new TextBlock { Text = "No devices yet", FontSize = 21, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
            PageUi.Text("Nearby peers will appear here when AudioStreamer discovers them."),
            new Button { Content = "Refresh", Command = state.RefreshCommand, HorizontalAlignment = HorizontalAlignment.Center } }
    });

    private UIElement PeerDetails(AudioStreamer.IPC.Contracts.IpcPeerSnapshot peer, SessionsViewModel sessions)
    {
        var details = new StackPanel { Spacing = 10, Width = 330 };
        details.Children.Add(new TextBlock { Text = peer.Name, FontSize = 21, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        details.Children.Add(PageUi.Text($"{peer.Platform} · {peer.Status}\nIP address · {peer.Address}\nAudio port · {peer.AudioPort}\nControl port · {peer.ControlPort}\nProtocol · {peer.ProtocolVersion}\nCapabilities · 0x{peer.Capabilities:X}\nLast seen · {peer.LastSeenUtc.ToLocalTime():g}"));
        var start = new Button { Content = "Use for a stream", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        start.Click += (_, _) => { sessions.SelectRouteForPeer(peer); NavigateRequested?.Invoke(UiPage.Sessions); };
        details.Children.Add(start);
        foreach (var route in sessions.State.Snapshot.Streams.Where(route => route.SenderNodeId == peer.NodeId || route.ReceiverNodeId == peer.NodeId))
        {
            var stop = new Button { Content = $"Stop {route.State} stream" };
            stop.Click += (_, _) => sessions.StopCommand.Execute(route);
            details.Children.Add(stop);
        }
        return PageUi.Card(details);
    }
}

public sealed class SessionsPage : Page
{
    public SessionsPage(SessionsViewModel model)
    {
        DataContext = model;
        var root = PageUi.Root("Sessions", "Choose an online sender and receiver. AudioStreamer keeps session state in the background.");
        var sender = new ComboBox { Header = "Sender", ItemsSource = model.Senders, DisplayMemberPath = "Name", MinWidth = 260 };
        sender.SelectionChanged += (_, _) => model.SelectedSender = sender.SelectedItem as AudioStreamer.IPC.Contracts.IpcPeerSnapshot;
        var receiver = new ComboBox { Header = "Receiver", ItemsSource = model.Receivers, DisplayMemberPath = "Name", MinWidth = 260 };
        receiver.SelectionChanged += (_, _) => model.SelectedReceiver = receiver.SelectedItem as AudioStreamer.IPC.Contracts.IpcPeerSnapshot;
        sender.SelectedItem = model.SelectedSender;
        receiver.SelectedItem = model.SelectedReceiver;
        model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SessionsViewModel.SelectedSender) && !ReferenceEquals(sender.SelectedItem, model.SelectedSender))
                sender.SelectedItem = model.SelectedSender;
            if (args.PropertyName == nameof(SessionsViewModel.SelectedReceiver) && !ReferenceEquals(receiver.SelectedItem, model.SelectedReceiver))
                receiver.SelectedItem = model.SelectedReceiver;
        };
        var start = new Button { Content = "Start stream", Command = model.StartCommand };
        root.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { sender, receiver, start } });
        var operationMessage = PageUi.Text(model.OperationMessage ?? string.Empty);
        operationMessage.TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap;
        model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SessionsViewModel.OperationMessage))
                operationMessage.Text = model.OperationMessage ?? string.Empty;
        };
        root.Children.Add(operationMessage);
        var sessions = new StackPanel { Spacing = 8 };
        void Refresh()
        {
            sessions.Children.Clear();
            foreach (var session in model.State.Snapshot.Streams)
            {
                var card = new StackPanel { Spacing = 6 };
                string EndpointName(Guid id) => id == model.State.Snapshot.Node?.NodeId ? model.State.Snapshot.Node?.NodeName ?? "This device" :
                    model.State.Snapshot.Peers.FirstOrDefault(p => p.NodeId == id)?.Name ?? id.ToString("N");
                card.Children.Add(PageUi.Text($"{EndpointName(session.SenderNodeId)} → {EndpointName(session.ReceiverNodeId)}\n{session.State}"));
                var stop = new Button { Content = "Stop" }; stop.Click += (_, _) => model.StopCommand.Execute(session); card.Children.Add(stop);
                sessions.Children.Add(PageUi.Card(card));
            }
            if (sessions.Children.Count == 0) sessions.Children.Add(PageUi.Card(new StackPanel { Spacing = 8, Children = { new FontIcon { Glyph = "\uE768", FontSize = 26, Foreground = (Brush)Application.Current.Resources["AccentBrush"] }, new TextBlock { Text = "No active streams", FontSize = 19, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }, PageUi.Text("Choose compatible sender and receiver devices above to start a stream.") } }));
        }
        model.State.SnapshotChanged += eventType =>
        {
            if (eventType is null or "SessionsChanged" or "SessionCreated" or "SessionPrepared" or "SessionStarted" or
                "SessionRecovered" or "SessionStopped" or "SessionFailed" or "SessionChanged") Refresh();
        };
        Refresh(); root.Children.Add(sessions); root.Children.Add(PageUi.Text("Pause and resume apply to this device's audio lane when supported.")); Content = new ScrollViewer { Content = root, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    }
}

public sealed class AudioDevicesPage : Page
{
    public AudioDevicesPage() => Content = PageUi.Root("Audio devices", "Audio device enumeration and default-device selection are not available in the current IPC contract. No local audio APIs are called by this client.");
}

public sealed class StatisticsPage : Page
{
    public StatisticsPage(RuntimeStateViewModel state)
    {
        var root = new StackPanel { Padding = new Thickness(30, 22, 30, 30), Spacing = 18, HorizontalAlignment = HorizontalAlignment.Stretch };
        root.Children.Add(PageUi.Root("Statistics", "Live reported counters. Trend lines use pushed samples collected during this app session."));
        var graphs = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, MaximumRowsOrColumns = 2 };
        var bandwidth = TrendCard("Bandwidth", "\uE774", "throughput_sent_bytes_per_second", "throughput_received_bytes_per_second", "bytes/s", state);
        var queue = TrendCard("Audio queue", "\uE8A5", "queue_depth_packets", null, "packets", state);
        var jitter = TrendCard("Jitter buffer target", "\uE8B4", "jitter_buffer_milliseconds", null, "ms", state);
        var latency = TrendCard("Reported average latency", "\uE8B4", "latency_average_microseconds", null, "µs", state);
        var packets = TrendCard("Packets received", "\uE7BA", "packets_received", null, "packets", state);
        var dropped = TrendCard("Dropped packets", "\uE8A5", "dropped_packets", "packet_recovered", "packets", state);
        graphs.Children.Add(bandwidth); graphs.Children.Add(queue); graphs.Children.Add(jitter);
        graphs.Children.Add(latency); graphs.Children.Add(packets); graphs.Children.Add(dropped); root.Children.Add(graphs);
        var current = PageUi.Text("");
        void UpdateCounters()
        {
            var counters = state.Snapshot.Statistics;
            if (counters.Count == 0) { current.Text = "Waiting for the first statistics snapshot."; return; }
            var keys = counters.Keys.ToArray();
            Array.Sort(keys, StringComparer.Ordinal);
            var text = new StringBuilder();
            foreach (var key in keys)
                text.Append(Humanize(key)).Append("    ").Append(counters[key].ToString("N0")).AppendLine();
            current.Text = text.ToString();
        }
        state.SnapshotChanged += eventType => { if (eventType is null or "StatisticsChanged") UpdateCounters(); };
        UpdateCounters(); root.Children.Add(PageUi.Card(new StackPanel { Spacing = 12, Children = { new TextBlock { Text = "Current counters", FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }, current } }));
        Content = new ScrollViewer { Content = root, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    }

    private static UIElement TrendCard(string title, string glyph, string firstKey, string? secondKey, string unit, RuntimeStateViewModel state)
    {
        var current = new TextBlock { FontSize = 24, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        var chart = new Canvas { Height = 72, Width = 420 };
        var lineA = new Microsoft.UI.Xaml.Shapes.Polyline { Stroke = (Brush)Application.Current.Resources["AccentBrush"], StrokeThickness = 2.5 };
        var lineB = new Microsoft.UI.Xaml.Shapes.Polyline { Stroke = new SolidColorBrush(Microsoft.UI.Colors.CornflowerBlue), StrokeThickness = 2 };
        chart.Children.Add(lineA); if (secondKey is not null) chart.Children.Add(lineB);
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new FontIcon { Glyph = glyph, Foreground = (Brush)Application.Current.Resources["AccentBrush"] }, new TextBlock { Text = title, FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold } } });
        content.Children.Add(current); content.Children.Add(chart);
        var empty = PageUi.Text("Waiting for reported samples"); content.Children.Add(empty);
        var card = PageUi.Card(content); card.Width = 450; card.Height = 210; card.Margin = new Thickness(0, 0, 14, 14);
        void Update(IReadOnlyList<MetricSample> samples)
        {
            var valuesA = ExtractValues(samples, firstKey);
            var valuesB = secondKey is null ? Array.Empty<long>() : ExtractValues(samples, secondKey);
            var latestA = valuesA.Length == 0 ? 0 : valuesA[^1];
            var latestB = valuesB.Length == 0 ? 0 : valuesB[^1];
            current.Text = valuesA.Length == 0 ? "Unavailable" : secondKey is null ? $"{latestA:N0} {unit}" : valuesB.Length == 0 ? $"↑ {FormatRate(latestA)}   ↓ Unavailable" : $"↑ {FormatRate(latestA)}   ↓ {FormatRate(latestB)}";
            empty.Visibility = valuesA.Length > 1 ? Visibility.Collapsed : Visibility.Visible;
            lineA.Points = Points(valuesA);
            if (secondKey is not null) lineB.Points = Points(valuesB);
        }
        state.StatisticsUpdated += Update; Update(state.MetricHistory);
        return card;
    }

    private static long? Value(MetricSample sample, string key) => key switch
    {
        "throughput_sent_bytes_per_second" => sample.SentBytesPerSecond,
        "throughput_received_bytes_per_second" => sample.ReceivedBytesPerSecond,
        "queue_depth_packets" => sample.QueueDepthPackets,
        "jitter_buffer_milliseconds" => sample.JitterTargetMilliseconds,
        "latency_average_microseconds" => sample.LatencyAverageMicroseconds,
        "packets_received" => sample.PacketsReceived,
        "dropped_packets" => sample.DroppedPackets,
        "packet_recovered" => sample.PacketsRecovered,
        _ => null
    };
    private static long[] ExtractValues(IReadOnlyList<MetricSample> samples, string key)
    {
        var count = 0;
        for (var index = 0; index < samples.Count; index++)
            if (Value(samples[index], key).HasValue) count++;
        if (count == 0) return Array.Empty<long>();

        var values = new long[count];
        var destination = 0;
        for (var index = 0; index < samples.Count; index++)
            if (Value(samples[index], key) is { } value) values[destination++] = value;
        return values;
    }
    private static Microsoft.UI.Xaml.Media.PointCollection Points(long[] values)
    {
        var points = new Microsoft.UI.Xaml.Media.PointCollection();
        if (values.Length < 2) return points;
        var min = values[0]; var max = min;
        for (var index = 1; index < values.Length; index++)
        {
            min = Math.Min(min, values[index]);
            max = Math.Max(max, values[index]);
        }
        var range = Math.Max(1, max - min);
        for (var index = 0; index < values.Length; index++)
            points.Add(new Windows.Foundation.Point(index * 420d / (values.Length - 1), 66d - (values[index] - min) * 60d / range));
        return points;
    }
    private static string FormatRate(long value) => value >= 1024 * 1024 ? $"{value / 1024d / 1024d:0.0} MB/s" : $"{value / 1024d:0} KB/s";
    private static string Humanize(string key)
    {
        var text = new StringBuilder(key.Length);
        var capitalize = true;
        foreach (var character in key)
        {
            if (character == '_') { text.Append(' '); capitalize = true; }
            else { text.Append(capitalize ? char.ToUpperInvariant(character) : character); capitalize = false; }
        }
        return text.ToString();
    }
}

public sealed class DiagnosticsPage : Page
{
    public DiagnosticsPage(DiagnosticsViewModel model, IWindowHandleProvider windowHandle)
    {
        var state = model.State; var root = PageUi.Root("Diagnostics", "Connection health and support summary. Detailed log records are not available to this client.");
        var output = PageUi.Text(""); void Refresh() => output.Text = $"Connection: {state.ConnectionStatus.State}\nAudioStreamer: {state.ServiceVersion}\nEngine: {state.EngineVersion}\nNode: {state.NodeName} ({state.NodeId})\nStatus: {state.NodeState}\nPeers: {state.PeerCount}\nSessions: {state.StreamCount}\nCPU: {state.CpuText}\nMemory: {state.MemoryText}\nSynced: {state.Snapshot.SynchronizedAt:O}";
        state.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is null or nameof(RuntimeStateViewModel.ConnectionStatus) or nameof(RuntimeStateViewModel.ServiceVersion) or
                nameof(RuntimeStateViewModel.EngineVersion) or nameof(RuntimeStateViewModel.NodeName) or nameof(RuntimeStateViewModel.NodeId) or
                nameof(RuntimeStateViewModel.NodeState) or nameof(RuntimeStateViewModel.PeerCount) or nameof(RuntimeStateViewModel.StreamCount) or
                nameof(RuntimeStateViewModel.CpuText) or nameof(RuntimeStateViewModel.MemoryText)) Refresh();
        };
        state.SnapshotChanged += eventType => { if (eventType is null) Refresh(); };
        Refresh(); root.Children.Add(PageUi.Card(output));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var copy = new Button { Content = "Copy support summary" }; copy.Click += (_, _) => { var package = new Windows.ApplicationModel.DataTransfer.DataPackage(); package.SetText(model.BuildExportText()); Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package); };
        var export = new Button { Content = "Export summary" }; export.Click += async (_, _) =>
        {
            var picker = new FileSavePicker(); picker.FileTypeChoices.Add("Text document", new List<string> { ".txt" });
            picker.SuggestedFileName = "AudioStreamer-diagnostics";
            InitializeWithWindow.Initialize(picker, windowHandle.Handle);
            var file = await picker.PickSaveFileAsync();
            if (file is not null) await Windows.Storage.FileIO.WriteTextAsync(file, model.BuildExportText());
        };
        actions.Children.Add(copy); actions.Children.Add(export); root.Children.Add(actions); Content = new ScrollViewer { Content = root, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    }
}

public sealed class SettingsPage : Page
{
    public SettingsPage(SettingsViewModel model)
    {
        var root = new StackPanel { Padding = new Thickness(30, 22, 30, 30), Spacing = 16, HorizontalAlignment = HorizontalAlignment.Stretch };
        root.Children.Add(PageUi.Root("Settings", "Personalize this app and manage settings available to your node."));
        model.LoadFromSnapshot();
        var name = new TextBox { Header = "Node name", Text = model.NodeName, Width = 360 };
        var save = new Button { Content = "Save node name", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        save.Click += (_, _) => { model.NodeName = name.Text; model.SaveNodeNameCommand.Execute(null); };
        var nodeMessage = PageUi.Text(model.Message ?? "Shown to other AudioStreamer devices on your network.");
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.NodeName)) name.Text = model.NodeName;
            if (e.PropertyName is nameof(SettingsViewModel.Message) or nameof(SettingsViewModel.IsBusy))
            {
                nodeMessage.Text = model.Message ?? "Shown to other AudioStreamer devices on your network.";
                save.IsEnabled = !model.IsBusy;
            }
        };
        root.Children.Add(PageUi.Card(new StackPanel { Spacing = 10, Children = { new TextBlock { Text = "Node", FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }, name, save, nodeMessage } }));

        var theme = new ComboBox { Header = "Theme", ItemsSource = new[] { "System", "Dark", "Light" }, SelectedItem = model.Theme, Width = 180 };
        theme.SelectionChanged += (_, _) => model.Theme = theme.SelectedItem?.ToString() ?? "System";
        var closeToTray = new ToggleSwitch { Header = "Close window to notification area", IsOn = model.CloseToTray };
        closeToTray.Toggled += (_, _) => model.CloseToTray = closeToTray.IsOn;
        var startMinimized = new ToggleSwitch { Header = "Start minimized", IsOn = model.StartMinimized };
        startMinimized.Toggled += (_, _) => model.StartMinimized = startMinimized.IsOn;
        root.Children.Add(PageUi.Card(new StackPanel { Spacing = 10, Children = { new TextBlock { Text = "Appearance", FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }, theme, PageUi.Text("System follows your Windows app theme."), closeToTray, startMinimized } }));
        root.Children.Add(PageUi.Card(new StackPanel { Spacing = 8, Children = { new TextBlock { Text = "Streaming and discovery", FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }, PageUi.Text("This version does not expose transport, discovery, or audio-device preferences in the client. The active runtime continues to use its configured settings.") } }));
        root.Children.Add(PageUi.Card(new StackPanel { Spacing = 8, Children = { new TextBlock { Text = "Notifications", FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }, PageUi.Text("Windows toast notifications are not enabled in this build.") } }));
        root.Children.Add(PageUi.Card(new StackPanel { Spacing = 8, Children = { new TextBlock { Text = "Advanced and about", FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }, PageUi.Text($"IPC schema {model.State.IpcVersion}. Diagnostic export is available on the Diagnostics page. Runtime settings are managed by AudioStreamer.") } }));
        Content = new ScrollViewer { Content = root, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    }
}

public sealed class AboutPage : Page
{
    public AboutPage(RuntimeStateViewModel state) { Content = PageUi.Root("About AudioStreamer", $"Windows app · {typeof(App).Assembly.GetName().Version}\nAudioStreamer runtime · {state.ServiceVersion}\nEngine · {state.EngineVersion}\nIPC · {state.IpcVersion}\nAudio protocol · reported by node\nLicense and project URL are not configured in this build."); }
}

public interface IPageNavigation { event Action<UiPage>? NavigateRequested; }

public interface IUiPageFactory { Page Create(UiPage page); }

public sealed class UiPageFactory(RuntimeStateViewModel state, SessionsViewModel sessions,
    SettingsViewModel settings, DiagnosticsViewModel diagnostics,
    IWindowHandleProvider windowHandle) : IUiPageFactory
{
    public Page Create(UiPage page) => page switch
    {
        UiPage.Dashboard => new DashboardPage(state),
        UiPage.Devices => new DevicesPage(state, sessions),
        UiPage.Sessions => new SessionsPage(sessions),
        UiPage.AudioDevices => new AudioDevicesPage(),
        UiPage.Statistics => new StatisticsPage(state),
        UiPage.Diagnostics => new DiagnosticsPage(diagnostics, windowHandle),
        UiPage.Settings => new SettingsPage(settings),
        UiPage.About => new AboutPage(state),
        _ => new DashboardPage(state)
    };
}

public interface IWindowHandleProvider { IntPtr Handle { get; } }

public sealed class WindowHandleProvider : IWindowHandleProvider
{
    public IntPtr Handle { get; private set; }
    public void Set(IntPtr handle) => Handle = handle;
}
