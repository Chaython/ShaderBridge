using System.Windows;
using MessageBox = System.Windows.MessageBox;

namespace ShaderBridge;

public partial class MainWindow
{
    private async void ArmDriverUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_cacheRoots.Count == 0) await ScanCachesAsync();
        if (_cacheRoots.All(r => !r.Exists))
        {
            MessageBox.Show(this, "No shader-cache roots are available to protect.", "ShaderBridge",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var answer = MessageBox.Show(this,
            "Create short-lived VSS copy-on-write checkpoints for the local volumes containing your shader caches?\n\n" +
            "Windows will request administrator approval. Live game/driver cache writes remain fully writable. " +
            "Release the checkpoint after the driver transition is finished.",
            "Arm driver update", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        SetBusy(true, "Creating VSS driver-update checkpoint…");
        try
        {
            var state = await _vssCheckpoint.ArmAsync(
                _settings.StoreRoot,
                _cacheRoots,
                _currentDriverFingerprint);

            RefreshVssStatus();
            var message = $"VSS checkpoint active on {state.Shadows.Count} volume(s).";
            if (state.Errors.Count > 0)
                message += $" {state.Errors.Count} volume(s) could not be checkpointed.";
            StatusText.Text = message;
            Log(message);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "VSS checkpoint creation cancelled";
            Log("VSS checkpoint creation was cancelled.");
        }
        catch (Exception ex)
        {
            ShowError("VSS checkpoint failed", ex);
        }
        finally { SetBusy(false); }
    }

    private async void ReleaseVssCheckpoint_Click(object sender, RoutedEventArgs e)
    {
        var state = _vssCheckpoint.Load(_settings.StoreRoot);
        if (state?.IsActive != true)
        {
            RefreshVssStatus();
            return;
        }

        var answer = MessageBox.Show(this,
            $"Permanently release {state.Shadows.Count} ShaderBridge-created VSS checkpoint(s)?\n\n" +
            "Only the shadow-copy IDs recorded by ShaderBridge will be deleted.",
            "Release VSS checkpoint", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        SetBusy(true, "Releasing VSS checkpoint…");
        try
        {
            await _vssCheckpoint.ReleaseAsync(_settings.StoreRoot);
            RefreshVssStatus();
            StatusText.Text = "VSS checkpoint released";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "VSS release cancelled";
        }
        catch (Exception ex)
        {
            ShowError("VSS checkpoint release failed", ex);
        }
        finally { SetBusy(false); }
    }

    private void RefreshVssStatus()
    {
        var state = _vssCheckpoint.Load(_settings.StoreRoot);
        if (state?.IsActive != true)
        {
            VssStatusText.Text = "VSS checkpoint: none";
            return;
        }

        var age = DateTime.UtcNow - state.CreatedUtc;
        VssStatusText.Text =
            $"VSS checkpoint: ACTIVE — {state.Shadows.Count} volume(s), created {state.CreatedUtc.ToLocalTime():g}, age {age.TotalHours:0.#}h" +
            (state.Errors.Count > 0 ? $" — {state.Errors.Count} warning(s)" : string.Empty);
    }
}
