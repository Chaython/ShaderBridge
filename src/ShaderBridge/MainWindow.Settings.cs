using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;

namespace ShaderBridge;

public partial class MainWindow
{
    private void OpenStore_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_settings.StoreRoot);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_settings.StoreRoot}\"") { UseShellExecute = true });
    }

    private void WatchCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _settings.WatchCacheActivity = WatchCheckBox.IsChecked == true;
        SettingsWatchCheckBox.IsChecked = _settings.WatchCacheActivity;
        _settingsService.Save(_settings);
        ConfigureWatcher();
    }

    private void LoadSettingsToUi()
    {
        StoreRootText.Text = _settings.StoreRoot;
        CustomRootsText.Text = string.Join(Environment.NewLine, _settings.CustomCacheRoots);
        WatchCheckBox.IsChecked = _settings.WatchCacheActivity;
        SettingsWatchCheckBox.IsChecked = _settings.WatchCacheActivity;
        MinimizeTrayCheckBox.IsChecked = _settings.MinimizeToTray;
        AutoBaselineCheckBox.IsChecked = _settings.AutoBaselineSnapshot;
        StartWithWindowsCheckBox.IsChecked = _settings.StartWithWindows;
        PollSecondsText.Text = _settings.DriverPollSeconds.ToString();
    }

    private async void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        var poll = int.TryParse(PollSecondsText.Text, out var parsed) ? Math.Clamp(parsed, 10, 3600) : 30;
        _settings.StoreRoot = Environment.ExpandEnvironmentVariables(StoreRootText.Text.Trim());
        _settings.CustomCacheRoots = CustomRootsText.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        _settings.WatchCacheActivity = SettingsWatchCheckBox.IsChecked == true;
        _settings.MinimizeToTray = MinimizeTrayCheckBox.IsChecked == true;
        _settings.AutoBaselineSnapshot = AutoBaselineCheckBox.IsChecked == true;
        _settings.StartWithWindows = StartWithWindowsCheckBox.IsChecked == true;
        _settings.DriverPollSeconds = poll;
        _settingsService.Save(_settings);
        ApplyStartupSetting();
        WatchCheckBox.IsChecked = _settings.WatchCacheActivity;
        _driverTimer.Interval = TimeSpan.FromSeconds(poll);
        await ScanCachesAsync();
        StatusText.Text = "Settings saved";
    }

    private void ApplyStartupSetting()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (_settings.StartWithWindows)
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(exe)) key?.SetValue("ShaderBridge", $"\"{exe}\"");
            }
            else
            {
                key?.DeleteValue("ShaderBridge", false);
            }
        }
        catch (Exception ex)
        {
            Log($"Could not update Windows startup setting: {ex.Message}");
        }
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => ActivityText.Clear();

    private void Log(string message)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (ActivityText.Text.Length > 250_000)
                ActivityText.Text = ActivityText.Text[^150_000..];
            ActivityText.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
            ActivityText.ScrollToEnd();
        });
    }

    private void SetBusy(bool busy, string? status = null)
    {
        BusyProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (!string.IsNullOrWhiteSpace(status)) StatusText.Text = status;
    }

    private void ShowError(string title, Exception ex)
    {
        Log($"ERROR {title}: {ex}");
        StatusText.Text = title;
        MessageBox.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var i = 0;
        while (value >= 1024 && i < units.Length - 1) { value /= 1024; i++; }
        return $"{value:0.##} {units[i]}";
    }
}
