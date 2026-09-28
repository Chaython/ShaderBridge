using System.Text;
using System.Windows;
using ShaderBridge.Models;

namespace ShaderBridge;

public partial class MainWindow
{
    private async void CompareSnapshot_Click(object sender, RoutedEventArgs e)
    {
        if (SnapshotsList.SelectedItem is not SnapshotManifest manifest) return;
        SetBusy(true, "Comparing snapshot to current cache files…");
        try
        {
            var comparison = await _snapshotService.CompareToCurrentAsync(manifest);
            SnapshotDetailsText.Text = BuildSnapshotDetails(manifest) + Environment.NewLine + Environment.NewLine + "Current comparison" + Environment.NewLine + comparison;
            StatusText.Text = comparison.ToString();
            Log($"Snapshot comparison: {comparison}");
        }
        catch (Exception ex) { ShowError("Comparison failed", ex); }
        finally { SetBusy(false); }
    }

    private async void RestoreMissing_Click(object sender, RoutedEventArgs e)
    {
        if (SnapshotsList.SelectedItem is not SnapshotManifest manifest) return;
        var answer = MessageBox.Show(this,
            "Restore only missing game/custom cache files? Existing files will not be overwritten, and ShaderBridge will skip known driver-native and Windows D3D caches.\n\nThis is intentionally conservative after a driver update.",
            "Restore missing cache files", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        SetBusy(true, "Restoring missing files…");
        try
        {
            var result = await _snapshotService.RestoreMissingSafeAsync(_settings.StoreRoot, manifest);
            StatusText.Text = $"Restored {result.Restored:N0}; existing {result.ExistingSkipped:N0}; driver-native skipped {result.DriverNativeSkipped:N0}; failed {result.Failed:N0}";
        }
        catch (Exception ex) { ShowError("Restore failed", ex); }
        finally { SetBusy(false); }
    }

    private void SnapshotsList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (SnapshotsList.SelectedItem is SnapshotManifest manifest)
            SnapshotDetailsText.Text = BuildSnapshotDetails(manifest);
    }

    private string BuildSnapshotDetails(SnapshotManifest manifest)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Created: {manifest.CreatedUtc.ToLocalTime():F}");
        sb.AppendLine($"Snapshot ID: {manifest.Id}");
        sb.AppendLine($"Files: {manifest.Files.Count:N0}");
        sb.AppendLine($"Logical size: {FormatBytes(manifest.TotalBytes)}");
        sb.AppendLine();
        sb.AppendLine("Drivers:");
        foreach (var driver in manifest.Drivers)
            sb.AppendLine($"  {driver.Provider} {driver.Name} — {driver.DriverVersion} ({driver.DriverDate})");
        sb.AppendLine();
        sb.AppendLine("Cache roots:");
        foreach (var group in manifest.Files.GroupBy(f => new { f.RootName, f.RootPath }))
            sb.AppendLine($"  {group.Key.RootName}: {group.Count():N0} files — {group.Key.RootPath}");
        return sb.ToString();
    }
}
