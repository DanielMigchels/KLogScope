using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace KubeLogViewer;

// ─────────────────────────────────────────────────────────────────────────────
// LogLine – one line of log output, bound to each row in LogView
// ─────────────────────────────────────────────────────────────────────────────
public class LogLine : INotifyPropertyChanged
{
    private bool _isSearchMatch;

    public string Text { get; set; } = "";

    public bool IsSearchMatch
    {
        get => _isSearchMatch;
        set
        {
            if (_isSearchMatch == value) return;
            _isSearchMatch = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HighlightBackground)));
        }
    }

    public System.Windows.Media.Brush HighlightBackground =>
        _isSearchMatch
            ? System.Windows.Media.Brushes.Yellow
            : System.Windows.Media.Brushes.Transparent;

    public event PropertyChangedEventHandler? PropertyChanged;
}

public class NodeStatsRow
{
    public string Name { get; set; } = "";
    public string CpuCores { get; set; } = "";
    public string CpuPercentDisplay { get; set; } = "n/a";
    public string MemoryBytes { get; set; } = "";
    public string MemoryPercentDisplay { get; set; } = "n/a";
    public double? CpuPercent { get; set; }
    public double? MemoryPercent { get; set; }
}

// ─────────────────────────────────────────────────────────────────────────────
// MainWindow
// ─────────────────────────────────────────────────────────────────────────────
public partial class MainWindow : Window
{
    // Services & state
    private readonly KubernetesService         _k8s         = new();
    private          AppSettings               _settings    = new();
    private          CancellationTokenSource?  _streamCts;
    private          bool                      _paused;
    private          Process?                  _podShellProcess;
    private          CancellationTokenSource?  _podShellCts;
    private          int                       _podTerminalInputStart;

    // Log buffers
    private readonly ObservableCollection<LogLine> _displayLines = new();
    private readonly List<LogLine>                 _allLines     = new();
    private readonly object                        _pendingLock  = new();
    private          List<LogLine>                 _pendingLines = new(256);
    private          int                           _nextDisplayIndex; // how many _allLines have been pushed to _displayLines
    private readonly ObservableCollection<NodeStatsRow> _nodeStats = new();
    private          DispatcherTimer?                   _nodeStatsTimer;
    private          bool                               _isRefreshingNodeStats;

    // Flush timer – fires on the UI thread every 150 ms
    private DispatcherTimer? _flushTimer;

    // ─── Window lifecycle ────────────────────────────────────────────────────

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _settings = SettingsService.Load();
        ApplyWindowBounds();

        LogView.ItemsSource = _displayLines;
        NodeStatsGrid.ItemsSource = _nodeStats;
        ResetNodeStatsDashboard();

        _flushTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(150),
            DispatcherPriority.Background,
            FlushPending,
            Dispatcher);
        _flushTimer.Start();

        _nodeStatsTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(30),
            DispatcherPriority.Background,
            NodeStatsTimer_Tick,
            Dispatcher);
        SyncNodeStatsTimerState();

        RefreshClusterList();
        UpdatePodShellTargetText();
        ResetPodTerminal();

        // Restore the previously-used cluster
        if (_settings.LastKubeconfigPath != null && _settings.LastContext != null)
        {
            foreach (ClusterItem item in ClusterComboBox.Items)
            {
                if (item.KubeconfigPath == _settings.LastKubeconfigPath &&
                    item.ContextName    == _settings.LastContext)
                {
                    ClusterComboBox.SelectedItem = item;
                    break;
                }
            }
        }
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        StopStream();
        StopPodShellProcess();
        _flushTimer?.Stop();
        _nodeStatsTimer?.Stop();

        // Persist window state
        if (WindowState == WindowState.Normal)
        {
            _settings.WindowLeft   = Left;
            _settings.WindowTop    = Top;
            _settings.WindowWidth  = Width;
            _settings.WindowHeight = Height;
        }
        _settings.WindowState = WindowState;

        if (ClusterComboBox.SelectedItem is ClusterItem cluster)
        {
            _settings.LastKubeconfigPath = cluster.KubeconfigPath;
            _settings.LastContext        = cluster.ContextName;
        }
        if (NamespaceList.SelectedItem is string ns)
            _settings.LastNamespace = ns;

        if (TailComboBox.SelectedItem is ComboBoxItem tailItem
            && int.TryParse(tailItem.Tag?.ToString(), out var tailVal))
            _settings.LastTailLines = tailVal;

        SettingsService.Save(_settings);
    }

    private void ApplyWindowBounds()
    {
        WindowState = _settings.WindowState;
        if (_settings.WindowState == WindowState.Normal)
        {
            Left   = _settings.WindowLeft;
            Top    = _settings.WindowTop;
            Width  = _settings.WindowWidth;
            Height = _settings.WindowHeight;
        }

        // Restore tail selection
        foreach (ComboBoxItem item in TailComboBox.Items)
        {
            if (int.TryParse(item.Tag?.ToString(), out var v) && v == _settings.LastTailLines)
            {
                item.IsSelected = true;
                break;
            }
        }
    }

    // ─── Cluster management ──────────────────────────────────────────────────

    private void RefreshClusterList()
    {
        var selected = ClusterComboBox.SelectedItem as ClusterItem;
        ClusterComboBox.Items.Clear();

        foreach (var path in _settings.KubeconfigPaths)
        {
            foreach (var item in KubernetesService.GetContexts(path))
                ClusterComboBox.Items.Add(item);
        }

        // Re-select the same item if it is still present
        if (selected != null)
        {
            foreach (ClusterItem item in ClusterComboBox.Items)
            {
                if (item.KubeconfigPath == selected.KubeconfigPath &&
                    item.ContextName    == selected.ContextName)
                {
                    ClusterComboBox.SelectedItem = item;
                    break;
                }
            }
        }
    }

    private void ImportOpenLens_Click(object sender, RoutedEventArgs e)
    {
        var found = OpenLensDiscovery.DiscoverKubeconfigPaths();
        int added = 0;
        foreach (var path in found)
        {
            if (!_settings.KubeconfigPaths.Contains(path))
            {
                _settings.KubeconfigPaths.Add(path);
                added++;
            }
        }

        if (added > 0)
        {
            SettingsService.Save(_settings);
            RefreshClusterList();
            SetStatus($"Imported {added} kubeconfig(s).");
        }
        else
        {
            var scanned = string.Join("\n  ", found.DefaultIfEmpty("(none found)"));
            MessageBox.Show(
                $"No new kubeconfigs found.\n\nLocations checked:\n  {scanned}",
                "Import OpenLens Clusters",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private void AddKubeconfig_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title  = "Select Kubeconfig file",
            Filter = "All files (*.*)|*.*",
            InitialDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".kube")
        };

        if (dlg.ShowDialog() != true) return;

        if (!_settings.KubeconfigPaths.Contains(dlg.FileName))
        {
            _settings.KubeconfigPaths.Add(dlg.FileName);
            SettingsService.Save(_settings);
            RefreshClusterList();
            SetStatus($"Added: {Path.GetFileName(dlg.FileName)}");
        }
        else
        {
            SetStatus("Kubeconfig already in list.");
        }
    }

    private void RemoveCluster_Click(object sender, RoutedEventArgs e)
    {
        if (ClusterComboBox.SelectedItem is not ClusterItem item) return;

        // Remove the kubeconfig path entirely (removes all contexts from that file)
        _settings.KubeconfigPaths.Remove(item.KubeconfigPath);
        SettingsService.Save(_settings);

        StopStream();
        StopPodShellProcess();
        ClearLogs();
        NamespaceList.Items.Clear();
        PodList.Items.Clear();
        ContainerList.Items.Clear();
        ResetNodeStatsDashboard();
        RefreshClusterList();
        ConnStatusText.Text = "Not connected";
    }

    // ─── Node stats dashboard ───────────────────────────────────────────────

    private async void RefreshNodeStatsBtn_Click(object sender, RoutedEventArgs e)
    {
        await RefreshNodeStatsAsync();
    }

    private void NodeStatsAutoRefreshCheck_Changed(object sender, RoutedEventArgs e)
    {
        SyncNodeStatsTimerState();
    }

    private async void WorkspaceTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, WorkspaceTabControl))
            return;

        if (ClusterDashboardTab.IsSelected)
            await RefreshNodeStatsAsync();

        SyncNodeStatsTimerState();
    }

    private async void NodeStatsTimer_Tick(object? sender, EventArgs e)
    {
        if (!ClusterDashboardTab.IsSelected)
            return;

        await RefreshNodeStatsAsync();
    }

    private void SyncNodeStatsTimerState()
    {
        if (_nodeStatsTimer is null)
            return;

        if (NodeStatsAutoRefreshCheck.IsChecked == true && ClusterDashboardTab.IsSelected)
            _nodeStatsTimer.Start();
        else
            _nodeStatsTimer.Stop();
    }

    private async Task RefreshNodeStatsAsync()
    {
        if (_isRefreshingNodeStats)
            return;

        if (ClusterComboBox.SelectedItem is not ClusterItem cluster)
        {
            NodeStatsHintText.Text = "Select a cluster to load node metrics.";
            SetStatus("Select a cluster first.");
            return;
        }

        _isRefreshingNodeStats = true;
        RefreshNodeStatsBtn.IsEnabled = false;
        try
        {
            SetStatus("Loading node metrics…");
            var metrics = await _k8s.GetTopNodesAsync(cluster.KubeconfigPath, cluster.ContextName);

            _nodeStats.Clear();
            foreach (var metric in metrics)
            {
                _nodeStats.Add(new NodeStatsRow
                {
                    Name = metric.Name,
                    CpuCores = metric.CpuCores,
                    CpuPercentDisplay = metric.CpuPercentDisplay,
                    MemoryBytes = metric.MemoryBytes,
                    MemoryPercentDisplay = metric.MemoryPercentDisplay,
                    CpuPercent = metric.CpuPercent,
                    MemoryPercent = metric.MemoryPercent
                });
            }

            UpdateNodeStatsSummary(metrics);
            NodeStatsUpdatedText.Text = $"Last updated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
            NodeStatsHintText.Text = metrics.Count == 0
                ? "No node metrics returned. Ensure metrics-server is installed and healthy."
                : $"Loaded {metrics.Count} node metric(s) from {cluster.ContextName}.";
            SetStatus(metrics.Count == 0 ? "No node metrics returned." : "Node metrics loaded.");
        }
        catch (Exception ex)
        {
            NodeStatsHintText.Text = $"Failed to load node metrics: {ex.Message}";
            SetStatus("Failed to load node metrics.");
        }
        finally
        {
            RefreshNodeStatsBtn.IsEnabled = true;
            _isRefreshingNodeStats = false;
        }
    }

    private void UpdateNodeStatsSummary(List<NodeTopMetric> metrics)
    {
        NodeCountValueText.Text = metrics.Count.ToString();

        var cpuSamples = metrics.Where(m => m.CpuPercent.HasValue).Select(m => m.CpuPercent!.Value).ToList();
        AvgCpuValueText.Text = cpuSamples.Count > 0
            ? $"{cpuSamples.Average():0.#}%"
            : "n/a";

        var memSamples = metrics.Where(m => m.MemoryPercent.HasValue).Select(m => m.MemoryPercent!.Value).ToList();
        AvgMemoryValueText.Text = memSamples.Count > 0
            ? $"{memSamples.Average():0.#}%"
            : "n/a";

        var hottest = metrics
            .Where(m => m.CpuPercent.HasValue)
            .OrderByDescending(m => m.CpuPercent)
            .FirstOrDefault();
        PeakNodeValueText.Text = hottest is null
            ? "-"
            : $"{hottest.Name} ({hottest.CpuPercentDisplay})";
    }

    private void ResetNodeStatsDashboard()
    {
        _nodeStats.Clear();
        NodeCountValueText.Text = "0";
        AvgCpuValueText.Text = "0%";
        AvgMemoryValueText.Text = "0%";
        PeakNodeValueText.Text = "-";
        NodeStatsUpdatedText.Text = "Last updated: never";
        NodeStatsHintText.Text = "Select a cluster, then refresh node metrics.";
    }

    // ─── Navigation: cluster → namespace → pod → container ──────────────────

    private async void ClusterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ClusterComboBox.SelectedItem is not ClusterItem item) return;

        StopPodShellProcess();
        StopStream();
        ClearLogs();
        ResetNodeStatsDashboard();
        NamespaceList.Items.Clear();
        PodList.Items.Clear();
        ContainerList.Items.Clear();
        ConnStatusText.Text = $"Connecting: {item.ContextName}…";

        try
        {
            _k8s.Connect(item.KubeconfigPath, item.ContextName);
            SetStatus("Loading namespaces…");
            var namespaces = await _k8s.GetNamespacesAsync();
            foreach (var ns in namespaces)
                NamespaceList.Items.Add(ns);

            ConnStatusText.Text = $"Connected: {item.ContextName}";
            SetStatus($"Loaded {namespaces.Count} namespace(s).");

            // Restore previously-selected namespace
            if (_settings.LastNamespace != null &&
                NamespaceList.Items.Contains(_settings.LastNamespace))
                NamespaceList.SelectedItem = _settings.LastNamespace;

            if (ClusterDashboardTab.IsSelected)
                await RefreshNodeStatsAsync();
        }
        catch (Exception ex)
        {
            SetStatus($"Connection error: {ex.Message}");
            ConnStatusText.Text = "Connection failed";
        }
    }

    private async void NamespaceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        StopPodShellProcess();
        UpdatePodShellTargetText();

        if (NamespaceList.SelectedItem is not string ns) return;

        StopStream();
        ClearLogs();
        PodList.Items.Clear();
        ContainerList.Items.Clear();

        try
        {
            SetStatus($"Loading pods in {ns}…");
            var pods = await _k8s.GetPodsAsync(ns);
            foreach (var pod in pods)
                PodList.Items.Add(pod);
            SetStatus($"Loaded {pods.Count} pod(s) in {ns}.");
        }
        catch (Exception ex)
        {
            SetStatus($"Error loading pods: {ex.Message}");
        }
    }

    private async void PodList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        StopPodShellProcess();
        UpdatePodShellTargetText();

        if (PodList.SelectedItem is not string pod) return;
        if (NamespaceList.SelectedItem is not string ns) return;

        StopStream();
        ClearLogs();
        ContainerList.Items.Clear();

        try
        {
            SetStatus($"Loading containers for {pod}…");
            var containers = await _k8s.GetContainersAsync(ns, pod);
            foreach (var c in containers)
                ContainerList.Items.Add(c);
            SetStatus($"Pod: {pod}");

            if (containers.Count == 1)
                ContainerList.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            SetStatus($"Error loading containers: {ex.Message}");
        }
    }

    private void PodList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!ClusterDashboardTab.IsSelected)
            return;

        if (e.OriginalSource is not DependencyObject source)
            return;

        var item = FindAncestor<ListBoxItem>(source);
        if (item?.DataContext is not string podName)
            return;

        if (PodList.SelectedItem is string selectedPod && selectedPod == podName)
            WorkspaceTabControl.SelectedItem = PodWorkspaceTab;
    }

    private void ContainerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        StopPodShellProcess();
        UpdatePodShellTargetText();

        if (ContainerList.SelectedItem is not string) return;

        if (ClusterDashboardTab.IsSelected)
            WorkspaceTabControl.SelectedItem = PodWorkspaceTab;

        StartStream();
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T typed)
                return typed;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    // ─── Log streaming ───────────────────────────────────────────────────────

    private void StartStream()
    {
        StopStream();
        ClearLogs();

        if (ContainerList.SelectedItem  is not string container) return;
        if (PodList.SelectedItem        is not string pod)       return;
        if (NamespaceList.SelectedItem  is not string ns)        return;

        var tail     = GetTailLines();
        var previous = PreviousCheck.IsChecked == true;

        _streamCts = new CancellationTokenSource();
        var ct = _streamCts.Token;

        _ = Task.Run(() => StreamLoopAsync(ns, pod, container, tail, previous, ct));
    }

    private async Task StreamLoopAsync(
        string ns, string pod, string container,
        int?   tail, bool previous,
        CancellationToken ct)
    {
        Dispatch(() => SetStatus("Connecting…"));
        bool firstConnect = true;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Can't follow previous-run logs – they're already finished
                bool follow = !previous;

                // On reconnect, we've already seen the initial tail; request only new lines
                int? effectiveTail = firstConnect ? tail : null;
                firstConnect = false;

                var stream = await _k8s.GetLogStreamAsync(
                    ns, pod, container, effectiveTail, follow, previous, ct);

                Dispatch(() => SetStatus($"Streaming  {pod}/{container}"));

                using var reader = new StreamReader(stream);
                while (!ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                    if (line is null) break;  // EOF
                    EnqueueLine(line);
                }

                if (ct.IsCancellationRequested) break;

                // Previous-container logs don't reconnect
                if (previous)
                {
                    Dispatch(() => SetStatus("Previous container logs loaded."));
                    break;
                }

                Dispatch(() => SetStatus("Stream ended – reconnecting in 3 s…"));
                await Task.Delay(3_000, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Dispatch(() => SetStatus($"Stream error: {ex.Message}  –  retrying in 5 s…"));
                try { await Task.Delay(5_000, ct); } catch { break; }
            }
        }

        Dispatch(() =>
        {
            if (!ct.IsCancellationRequested)
                ConnStatusText.Text = "Stream ended";
        });
    }

    private void StopStream()
    {
        _streamCts?.Cancel();
        _streamCts?.Dispose();
        _streamCts = null;
        _paused         = false;
        PauseBtn.Content = "Pause";
    }

    // ─── Log buffer ──────────────────────────────────────────────────────────

    /// <summary>Called from background threads – thread-safe.</summary>
    private void EnqueueLine(string text)
    {
        lock (_pendingLock)
            _pendingLines.Add(new LogLine { Text = text });
    }

    /// <summary>Fires on the UI thread every 150 ms via DispatcherTimer.</summary>
    private void FlushPending(object? sender, EventArgs e)
    {
        List<LogLine> incoming;
        lock (_pendingLock)
        {
            if (_pendingLines.Count == 0) return;
            incoming      = _pendingLines;
            _pendingLines = new List<LogLine>(256);
        }

        _allLines.AddRange(incoming);

        if (!_paused)
            FlushDisplayFromIndex();
    }

    /// <summary>Push _allLines[_nextDisplayIndex..] through filter/search into _displayLines.</summary>
    private void FlushDisplayFromIndex()
    {
        var filter     = FilterBox.Text;
        var search     = SearchBox.Text;
        var filterComp = FilterCaseCheck.IsChecked == true
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        var searchComp = SearchCaseCheck.IsChecked == true
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        bool hasFilter = !string.IsNullOrEmpty(filter);
        bool hasSearch = !string.IsNullOrEmpty(search);

        int before = _displayLines.Count;

        for (int i = _nextDisplayIndex; i < _allLines.Count; i++)
        {
            var line = _allLines[i];
            if (hasFilter && !line.Text.Contains(filter, filterComp))
                continue;

            line.IsSearchMatch = hasSearch && line.Text.Contains(search, searchComp);
            _displayLines.Add(line);
        }

        _nextDisplayIndex = _allLines.Count;

        if (_displayLines.Count != before)
        {
            UpdateLineCount();
            if (AutoScrollCheck.IsChecked == true && _displayLines.Count > 0)
                LogView.ScrollIntoView(_displayLines[^1]);
        }
    }

    // ─── Log toolbar handlers ────────────────────────────────────────────────

    private void TailComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Only take effect when a container is already selected (restart stream)
        if (ContainerList.SelectedItem is string && IsLoaded)
            StartStream();
    }

    private void PauseBtn_Click(object sender, RoutedEventArgs e)
    {
        _paused          = !_paused;
        PauseBtn.Content = _paused ? "Resume" : "Pause";

        if (!_paused)
            FlushDisplayFromIndex(); // drain any lines buffered during pause
    }

    private void PreviousCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (ContainerList.SelectedItem is string && IsLoaded)
            StartStream();
    }

    private void ClearBtn_Click(object sender, RoutedEventArgs e) => ClearLogs();

    private void SaveBtn_Click(object sender, RoutedEventArgs e)
    {
        var pod       = PodList.SelectedItem       as string ?? "pod";
        var container = ContainerList.SelectedItem as string ?? "container";
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title       = "Save Logs",
            Filter      = "Text files (*.txt)|*.txt|Log files (*.log)|*.log|All files (*.*)|*.*",
            DefaultExt  = ".txt",
            FileName    = $"{pod}_{container}_{timestamp}"
        };

        if (dlg.ShowDialog() != true) return;

        try
        {
            File.WriteAllLines(dlg.FileName, _displayLines.Select(l => l.Text));
            SetStatus($"Saved {_displayLines.Count:N0} lines → {Path.GetFileName(dlg.FileName)}");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to save: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ─── Search ──────────────────────────────────────────────────────────────

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) =>
        ApplySearchHighlighting();

    private void SearchCaseCheck_Changed(object sender, RoutedEventArgs e) =>
        ApplySearchHighlighting();

    private void ApplySearchHighlighting()
    {
        var search = SearchBox.Text;
        var comp   = SearchCaseCheck.IsChecked == true
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        bool hasSearch = !string.IsNullOrEmpty(search);

        foreach (var line in _displayLines)
            line.IsSearchMatch = hasSearch && line.Text.Contains(search, comp);
    }

    // ─── Filter ──────────────────────────────────────────────────────────────

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) =>
        RebuildDisplay();

    private void FilterCaseCheck_Changed(object sender, RoutedEventArgs e) =>
        RebuildDisplay();

    /// <summary>Re-filter _allLines from scratch and rebuild _displayLines.</summary>
    private void RebuildDisplay()
    {
        _displayLines.Clear();
        _nextDisplayIndex = 0;
        FlushDisplayFromIndex();
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private void ClearLogs()
    {
        StopFlushingPending();
        _displayLines.Clear();
        _allLines.Clear();
        _nextDisplayIndex = 0;
        UpdateLineCount();
    }

    private void StopFlushingPending()
    {
        lock (_pendingLock)
            _pendingLines.Clear();
    }

    private int? GetTailLines()
    {
        if (TailComboBox.SelectedItem is ComboBoxItem item
            && int.TryParse(item.Tag?.ToString(), out var v))
            return v == -1 ? null : v;
        return 500;
    }

    private void UpdateLineCount() =>
        LineCountText.Text = _allLines.Count == _displayLines.Count
            ? $"{_displayLines.Count:N0} lines"
            : $"{_displayLines.Count:N0} / {_allLines.Count:N0} lines";

    private void SetStatus(string message) => StatusText.Text = message;

    private void Dispatch(Action action)
    {
        if (Dispatcher.CheckAccess()) action();
        else Dispatcher.Invoke(action);
    }

    // ─── Pod shell ───────────────────────────────────────────────────────────

    private async void RunPodShellBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_podShellProcess is { HasExited: false })
        {
            SetStatus("Pod shell session already connected.");
            return;
        }

        if (ClusterComboBox.SelectedItem is not ClusterItem cluster)
        {
            SetStatus("Select a cluster first.");
            return;
        }
        if (NamespaceList.SelectedItem is not string ns)
        {
            SetStatus("Select a namespace first.");
            return;
        }
        if (PodList.SelectedItem is not string pod)
        {
            SetStatus("Select a pod first.");
            return;
        }
        if (ContainerList.SelectedItem is not string container)
        {
            SetStatus("Select a container first.");
            return;
        }

        var shellPath = (PodShellComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "/bin/sh";
        var psi = BuildInteractivePodShellStartInfo(cluster, ns, pod, container, shellPath);
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        try
        {
            if (!process.Start())
            {
                SetStatus("Failed to start kubectl exec.");
                return;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not start kubectl. Ensure kubectl is installed and available in PATH.\n\n{ex.Message}",
                "kubectl not available",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        _podShellProcess = process;
        _podShellCts = new CancellationTokenSource();
        process.StandardInput.NewLine = "\n";

        RunShellBtn.IsEnabled = false;
        StopShellBtn.IsEnabled = true;
        PodTerminalBox.IsReadOnly = false;
        PodTerminalBox.Focus();

        AppendPodTerminalText($"Connected: {ns}/{pod}/{container}  ({shellPath})" + Environment.NewLine);
        SetStatus($"Connected pod shell to {pod}/{container}.");

        _ = Task.Run(() => PumpPodShellOutputAsync(process.StandardOutput, _podShellCts.Token));
        _ = Task.Run(() => PumpPodShellOutputAsync(process.StandardError, _podShellCts.Token));

        try
        {
            await process.WaitForExitAsync();
            AppendPodTerminalText(Environment.NewLine + $"[session ended: exit {process.ExitCode}]" + Environment.NewLine);
            SetStatus($"Pod shell disconnected (exit {process.ExitCode}).");
        }
        catch (Exception ex)
        {
            AppendPodTerminalText(Environment.NewLine + $"[pod shell error] {ex.Message}" + Environment.NewLine);
            SetStatus("Pod shell failed.");
        }
        finally
        {
            process.Dispose();
            if (ReferenceEquals(_podShellProcess, process))
                _podShellProcess = null;

            _podShellCts?.Cancel();
            _podShellCts?.Dispose();
            _podShellCts = null;

            RunShellBtn.IsEnabled = true;
            StopShellBtn.IsEnabled = false;
            PodTerminalBox.IsReadOnly = true;
            _podTerminalInputStart = PodTerminalBox.Text.Length;
        }
    }

    private void StopPodShellBtn_Click(object sender, RoutedEventArgs e)
    {
        StopPodShellProcess();
        SetStatus("Pod shell disconnected.");
    }

    private void ClearPodShellBtn_Click(object sender, RoutedEventArgs e)
    {
        ResetPodTerminal();
        SetStatus("Pod terminal cleared.");
    }

    private ProcessStartInfo BuildInteractivePodShellStartInfo(
        ClusterItem cluster,
        string ns,
        string pod,
        string container,
        string shellPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "kubectl",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("--kubeconfig");
        psi.ArgumentList.Add(cluster.KubeconfigPath);
        psi.ArgumentList.Add("--context");
        psi.ArgumentList.Add(cluster.ContextName);
        psi.ArgumentList.Add("-n");
        psi.ArgumentList.Add(ns);
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(pod);
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(container);
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add(shellPath);
        psi.ArgumentList.Add("-i");

        return psi;
    }

    private async Task PumpPodShellOutputAsync(StreamReader reader, CancellationToken ct)
    {
        var buffer = new char[256];

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                if (read <= 0) break;
                AppendPodTerminalText(new string(buffer, 0, read));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // session is being stopped
        }
        catch (Exception ex)
        {
            AppendPodTerminalText(Environment.NewLine + $"[read error] {ex.Message}" + Environment.NewLine);
        }
    }

    private void StopPodShellProcess()
    {
        _podShellCts?.Cancel();

        var process = _podShellProcess;
        if (process is null)
            return;

        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            AppendPodTerminalText(Environment.NewLine + $"[stop error] {ex.Message}" + Environment.NewLine);
        }
        finally
        {
            if (ReferenceEquals(_podShellProcess, process))
                _podShellProcess = null;

            RunShellBtn.IsEnabled = true;
            StopShellBtn.IsEnabled = false;
            PodTerminalBox.IsReadOnly = true;
            _podTerminalInputStart = PodTerminalBox.Text.Length;
        }
    }

    private void PodTerminalBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_podShellProcess is null || _podShellProcess.HasExited)
        {
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.C)
        {
            // If text is selected, let WPF copy it. If not, send Ctrl+C to the remote shell.
            if (PodTerminalBox.SelectionLength > 0)
                return;

            e.Handled = true;
            try
            {
                _podShellProcess.StandardInput.Write("\x3");
                _podShellProcess.StandardInput.Flush();
            }
            catch { }
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            var current = PodTerminalBox.Text;
            var command = current.Length >= _podTerminalInputStart
                ? current[_podTerminalInputStart..]
                : string.Empty;

            // Remove local typed echo so the command appears only once when the remote shell echoes it.
            if (current.Length >= _podTerminalInputStart)
            {
                PodTerminalBox.Text = current[.._podTerminalInputStart];
                PodTerminalBox.CaretIndex = PodTerminalBox.Text.Length;
            }

            try
            {
                _podShellProcess.StandardInput.Write(command);
                _podShellProcess.StandardInput.Write("\n");
                _podShellProcess.StandardInput.Flush();
            }
            catch (Exception ex)
            {
                AppendPodTerminalText($"[write error] {ex.Message}" + Environment.NewLine);
            }

            return;
        }

        if (e.Key == Key.Back && PodTerminalBox.CaretIndex <= _podTerminalInputStart)
        {
            e.Handled = true;
            return;
        }

        if ((e.Key == Key.Left || e.Key == Key.Home) && PodTerminalBox.CaretIndex <= _podTerminalInputStart)
        {
            e.Handled = true;
            return;
        }

        if (PodTerminalBox.SelectionStart < _podTerminalInputStart)
        {
            e.Handled = true;
            PodTerminalBox.CaretIndex = PodTerminalBox.Text.Length;
        }
    }

    private void PodTerminalBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (_podShellProcess is null || _podShellProcess.HasExited)
        {
            e.Handled = true;
            return;
        }

        if (PodTerminalBox.SelectionStart < _podTerminalInputStart)
            e.Handled = true;
    }

    private void PodTerminalBox_SelectionChanged(object sender, RoutedEventArgs e)
    {
        // Intentionally allow selecting old output so users can copy from the terminal.
    }

    private void UpdatePodShellTargetText()
    {
        var ns = NamespaceList.SelectedItem as string;
        var pod = PodList.SelectedItem as string;
        var container = ContainerList.SelectedItem as string;

        PodShellTargetText.Text =
            ns is null || pod is null || container is null
                ? "Select namespace, pod, and container"
                : $"{ns}/{pod}/{container}";
    }

    private void ResetPodTerminal()
    {
        PodTerminalBox.Clear();
        _podTerminalInputStart = 0;
        AppendPodTerminalText("Pod terminal ready. Click Connect to start a shell session." + Environment.NewLine);
    }

    private void AppendPodTerminalText(string text)
    {
        Dispatch(() =>
        {
            PodTerminalBox.AppendText(text);
            PodTerminalBox.CaretIndex = PodTerminalBox.Text.Length;
            PodTerminalBox.ScrollToEnd();
            _podTerminalInputStart = PodTerminalBox.Text.Length;
        });
    }
}
