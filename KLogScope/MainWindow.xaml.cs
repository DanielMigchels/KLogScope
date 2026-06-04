using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
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

    // Log buffers
    private readonly ObservableCollection<LogLine> _displayLines = new();
    private readonly List<LogLine>                 _allLines     = new();
    private readonly object                        _pendingLock  = new();
    private          List<LogLine>                 _pendingLines = new(256);
    private          int                           _nextDisplayIndex; // how many _allLines have been pushed to _displayLines

    // Flush timer – fires on the UI thread every 150 ms
    private DispatcherTimer? _flushTimer;

    // ─── Window lifecycle ────────────────────────────────────────────────────

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _settings = SettingsService.Load();
        ApplyWindowBounds();

        LogView.ItemsSource = _displayLines;

        _flushTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(150),
            DispatcherPriority.Background,
            FlushPending,
            Dispatcher);
        _flushTimer.Start();

        RefreshClusterList();

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
        _flushTimer?.Stop();

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
        ClearLogs();
        NamespaceList.Items.Clear();
        PodList.Items.Clear();
        ContainerList.Items.Clear();
        RefreshClusterList();
        ConnStatusText.Text = "Not connected";
    }

    // ─── Navigation: cluster → namespace → pod → container ──────────────────

    private async void ClusterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ClusterComboBox.SelectedItem is not ClusterItem item) return;

        StopStream();
        ClearLogs();
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
        }
        catch (Exception ex)
        {
            SetStatus($"Connection error: {ex.Message}");
            ConnStatusText.Text = "Connection failed";
        }
    }

    private async void NamespaceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
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

    private void ContainerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ContainerList.SelectedItem is not string) return;
        StartStream();
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
}
