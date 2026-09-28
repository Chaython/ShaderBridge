using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using ShaderBridge.Models;
using ShaderBridge.Services;

namespace ShaderBridge;

public partial class MainWindow : Window
{
    private readonly SettingsService _settingsService = new();
    private readonly DriverService _driverService = new();
    private readonly CacheDiscoveryService _cacheDiscovery = new();
    private readonly SnapshotService _snapshotService = new();
    private readonly CacheActivityWatcher _watcher = new();
    private readonly VulkanPipelineCacheService _vulkanCache = new();
    private readonly VulkanProbe _vulkanProbe = new();
    private readonly ObservableCollection<CacheRoot> _cacheRoots = [];
    private readonly ObservableCollection<SnapshotManifest> _snapshots = [];
    private readonly DispatcherTimer _driverTimer = new();
    private ShaderBridgeSettings _settings;
    private IReadOnlyList<GpuDriverInfo> _drivers = [];
    private string _currentDriverFingerprint = string.Empty;
    private bool _allowClose;
    private System.Windows.Forms.NotifyIcon? _trayIcon;

    public MainWindow()
    {
        InitializeComponent();
        _settings = _settingsService.Load();
        CacheGrid.ItemsSource = _cacheRoots;
        SnapshotsList.ItemsSource = _snapshots;
        _snapshotService.Log += Log;
        _watcher.Activity += Log;
        LoadSettingsToUi();
        ConfigureTray();
        ConfigureDriverTimer();
        Loaded += async (_, _) => await InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        RefreshDrivers(initial: true);
        RefreshSnapshots();
        await ScanCachesAsync();
        await EnsureBaselineSnapshotAsync();
        ProbeVulkanDevices();
    }

    private void ConfigureDriverTimer()
    {
        _driverTimer.Tick += (_, _) => RefreshDrivers(initial: false);
        _driverTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(_settings.DriverPollSeconds, 10, 3600));
        _driverTimer.Start();
    }

    private void ConfigureTray()
    {
        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Text = "ShaderBridge",
            Icon = System.Drawing.SystemIcons.Application,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => RestoreWindow();
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open ShaderBridge", null, (_, _) => RestoreWindow());
        menu.Items.Add("Create snapshot", null, (_, _) => Dispatcher.BeginInvoke(new Action(() => _ = CreateSnapshotAsync())));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(() =>
        {
            _allowClose = true;
            Close();
        }));
        _trayIcon.ContextMenuStrip = menu;
    }

    private void RestoreWindow()
    {
        Dispatcher.Invoke(() =>
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        });
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized && _settings.MinimizeToTray)
            Hide();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose && _settings.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        _driverTimer.Stop();
        _watcher.Dispose();
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
        base.OnClosing(e);
    }

    private void RefreshDrivers(bool initial)
    {
        _drivers = _driverService.GetDisplayDrivers();
        _currentDriverFingerprint = string.Join("||", _drivers.Select(d => d.Fingerprint));
        DriverSummaryText.Text = _drivers.Count == 0
            ? "GPU driver unavailable"
            : string.Join("  |  ", _drivers.Select(d => $"{d.Name} — {d.DriverVersion}"));

        if (string.IsNullOrWhiteSpace(_settings.LastDriverFingerprint))
        {
            DriverStateText.Text = "Baseline recorded";
        }
        else if (!_settings.LastDriverFingerprint.Equals(_currentDriverFingerprint, StringComparison.Ordinal))
        {
            DriverStateText.Text = "Driver change detected";
            if (!initial)
            {
                Log("GPU driver fingerprint changed. Existing cache activity will be tracked against the new driver.");
                _trayIcon?.ShowBalloonTip(5000, "ShaderBridge", "GPU driver change detected. Compare your latest cache snapshot.", System.Windows.Forms.ToolTipIcon.Info);
            }
        }
        else
        {
            DriverStateText.Text = "Driver unchanged";
        }

        if (!string.IsNullOrWhiteSpace(_currentDriverFingerprint))
        {
            _settings.LastDriverFingerprint = _currentDriverFingerprint;
            _settingsService.Save(_settings);
        }
    }

    private async Task ScanCachesAsync()
    {
        SetBusy(true, "Scanning cache roots…");
        try
        {
            var roots = await _cacheDiscovery.DiscoverAsync(_settings.CustomCacheRoots);
            _cacheRoots.Clear();
            foreach (var root in roots) _cacheRoots.Add(root);
            ConfigureWatcher();
            var found = roots.Count(r => r.Exists);
            var totalBytes = roots.Where(r => r.Exists).Sum(r => r.SizeBytes);
            StatusText.Text = $"Found {found} cache roots, {FormatBytes(totalBytes)} total";
            Log($"Cache scan complete: {found} roots, {FormatBytes(totalBytes)}.");
        }
        catch (Exception ex)
        {
            ShowError("Cache scan failed", ex);
        }
        finally { SetBusy(false); }
    }

    private async Task EnsureBaselineSnapshotAsync()
    {
        if (!_settings.AutoBaselineSnapshot || string.IsNullOrWhiteSpace(_currentDriverFingerprint)) return;
        RefreshSnapshots();
        if (_snapshots.Any(s => s.DriverFingerprint.Equals(_currentDriverFingerprint, StringComparison.Ordinal)))
        {
            Log("A baseline snapshot already exists for the current GPU-driver fingerprint.");
            return;
        }

        var existing = _cacheRoots.Where(r => r.Exists).ToList();
        if (existing.Count == 0) return;

        SetBusy(true, "Creating automatic driver baseline snapshot…");
        try
        {
            var manifest = await _snapshotService.CreateAsync(_settings.StoreRoot, existing, _drivers);
            RefreshSnapshots();
            Log($"Automatic baseline snapshot {manifest.Id} created for the current driver fingerprint.");
            StatusText.Text = $"Automatic baseline created: {manifest.Files.Count:N0} files";
        }
        catch (Exception ex)
        {
            Log($"Automatic baseline snapshot failed: {ex.Message}");
        }
        finally { SetBusy(false); }
    }

    private void ConfigureWatcher()
    {
        if (_settings.WatchCacheActivity)
            _watcher.Start(_cacheRoots.Where(r => r.Exists).Select(r => r.Path));
        else
            _watcher.Stop();
    }

    private async Task CreateSnapshotAsync()
    {
        if (_cacheRoots.Count == 0) await ScanCachesAsync();
        var existing = _cacheRoots.Where(r => r.Exists).ToList();
        if (existing.Count == 0)
        {
            MessageBox.Show(this, "No existing shader-cache roots were found.", "ShaderBridge", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SetBusy(true, "Creating deduplicated snapshot…");
        try
        {
            var manifest = await _snapshotService.CreateAsync(_settings.StoreRoot, existing, _drivers);
            RefreshSnapshots();
            StatusText.Text = $"Snapshot created: {manifest.Files.Count:N0} files, {FormatBytes(manifest.TotalBytes)} logical size";
            Log($"Snapshot {manifest.Id} created with {manifest.Files.Count:N0} files.");
        }
        catch (Exception ex) { ShowError("Snapshot failed", ex); }
        finally { SetBusy(false); }
    }

    private void RefreshSnapshots()
    {
        _snapshots.Clear();
        foreach (var snapshot in _snapshotService.List(_settings.StoreRoot)) _snapshots.Add(snapshot);
        if (_snapshots.Count > 0 && SnapshotsList.SelectedItem is null) SnapshotsList.SelectedIndex = 0;
    }

    private async void ScanCaches_Click(object sender, RoutedEventArgs e) => await ScanCachesAsync();
    private async void CreateSnapshot_Click(object sender, RoutedEventArgs e) => await CreateSnapshotAsync();
    private void RefreshSnapshots_Click(object sender, RoutedEventArgs e) => RefreshSnapshots();
}
