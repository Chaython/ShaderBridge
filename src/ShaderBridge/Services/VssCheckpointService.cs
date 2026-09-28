using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using ShaderBridge.Models;

namespace ShaderBridge.Services;

public sealed class VssCheckpointService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public event Action<string>? Log;

    public static string? GetVolumeRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
            return null;

        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full);
            if (string.IsNullOrWhiteSpace(root) || root.Length < 3 || root[1] != ':')
                return null;
            return root[..3];
        }
        catch
        {
            return null;
        }
    }

    public static string MapPathToShadow(string livePath, string volumeRoot, string shadowDeviceObject)
    {
        var actualRoot = GetVolumeRoot(livePath)
            ?? throw new ArgumentException("Only local drive-letter paths can be mapped to VSS.", nameof(livePath));

        if (!actualRoot.Equals(volumeRoot, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Live path does not belong to the supplied VSS volume.", nameof(livePath));

        var full = Path.GetFullPath(livePath);
        var relative = full[volumeRoot.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return shadowDeviceObject.TrimEnd('\\') + "\\" + relative;
    }

    public VssTransitionState? Load(string storeRoot)
    {
        var path = StatePath(storeRoot);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<VssTransitionState>(File.ReadAllText(path), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public async Task<VssTransitionState> ArmAsync(
        string storeRoot,
        IEnumerable<CacheRoot> roots,
        string driverFingerprint,
        CancellationToken ct = default)
    {
        var existing = Load(storeRoot);
        if (existing?.IsActive == true)
            throw new InvalidOperationException("A ShaderBridge VSS checkpoint is already active. Release it before arming another.");

        var volumes = roots
            .Where(r => r.Exists)
            .Select(r => GetVolumeRoot(r.Path))
            .Where(v => v is not null)
            .Select(v => v!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v)
            .ToList();

        if (volumes.Count == 0)
            throw new InvalidOperationException("No local drive-letter cache volumes are available for VSS.");

        var state = new VssTransitionState
        {
            DriverFingerprint = driverFingerprint
        };

        foreach (var volume in volumes)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var shadow = await CreateShadowAsync(volume, ct);
                state.Shadows.Add(shadow);
                Log?.Invoke($"VSS checkpoint created for {volume}: {shadow.ShadowId}");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                state.Errors.Add($"{volume}: {ex.Message}");
                Log?.Invoke($"VSS checkpoint failed for {volume}: {ex.Message}");
            }
        }

        if (state.Shadows.Count == 0)
            throw new InvalidOperationException("VSS could not create a checkpoint for any shader-cache volume. " +
                string.Join(" | ", state.Errors));

        Directory.CreateDirectory(storeRoot);
        await File.WriteAllTextAsync(StatePath(storeRoot), JsonSerializer.Serialize(state, JsonOptions), ct);
        return state;
    }

    public async Task ReleaseAsync(string storeRoot, CancellationToken ct = default)
    {
        var state = Load(storeRoot);
        if (state is null || state.Shadows.Count == 0)
        {
            TryDeleteState(storeRoot);
            return;
        }

        var errors = new List<string>();
        foreach (var shadow in state.Shadows)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await DeleteShadowAsync(shadow, ct);
                Log?.Invoke($"Released VSS checkpoint {shadow.ShadowId} for {shadow.VolumeRoot}.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                errors.Add($"{shadow.ShadowId}: {ex.Message}");
            }
        }

        if (errors.Count > 0)
            throw new InvalidOperationException("One or more ShaderBridge VSS checkpoints could not be released: " +
                string.Join(" | ", errors));

        TryDeleteState(storeRoot);
    }

    private static async Task<VssShadowCheckpoint> CreateShadowAsync(string volumeRoot, CancellationToken ct)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "ShaderBridge", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var scriptPath = Path.Combine(tempRoot, "create-shadow.ps1");
        var resultPath = Path.Combine(tempRoot, "result.json");

        var escapedVolume = volumeRoot.Replace("'", "''");
        var escapedResult = resultPath.Replace("'", "''");
        var script = string.Join(Environment.NewLine,
            "$ErrorActionPreference = 'Stop'",
            "$class = [wmiclass]'root\\cimv2:Win32_ShadowCopy'",
            $"$result = $class.Create('{escapedVolume}', 'ClientAccessible')",
            "if ($result.ReturnValue -ne 0) { throw \"Win32_ShadowCopy.Create returned $($result.ReturnValue).\" }",
            "$shadow = Get-WmiObject Win32_ShadowCopy | Where-Object { $_.ID -eq $result.ShadowID } | Select-Object -First 1",
            "if ($null -eq $shadow) { throw 'Created shadow copy could not be queried.' }",
            "[pscustomobject]@{",
            $"    VolumeRoot = '{escapedVolume}'",
            "    ShadowId = [string]$result.ShadowID",
            "    DeviceObject = [string]$shadow.DeviceObject",
            $"}} | ConvertTo-Json -Compress | Set-Content -LiteralPath '{escapedResult}' -Encoding UTF8");

        await File.WriteAllTextAsync(scriptPath, script, ct);
        try
        {
            await RunElevatedPowerShellAsync(scriptPath, ct);
            if (!File.Exists(resultPath))
                throw new InvalidOperationException("Elevated VSS helper completed without returning shadow-copy metadata.");

            var json = await File.ReadAllTextAsync(resultPath, ct);
            return JsonSerializer.Deserialize<VssShadowCheckpoint>(json, JsonOptions)
                ?? throw new InvalidOperationException("VSS helper returned invalid metadata.");
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { }
        }
    }

    private static async Task DeleteShadowAsync(VssShadowCheckpoint shadow, CancellationToken ct)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "ShaderBridge", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var scriptPath = Path.Combine(tempRoot, "delete-shadow.ps1");
        var id = shadow.ShadowId.Trim().Trim('{', '}');
        var volume = shadow.VolumeRoot.TrimEnd('\\');
        var vssadmin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "vssadmin.exe");
        var escapedExe = vssadmin.Replace("'", "''");

        var script = string.Join(Environment.NewLine,
            "$ErrorActionPreference = 'Stop'",
            $"$p = Start-Process -FilePath '{escapedExe}' -ArgumentList @('delete','shadows','/for={volume}','/shadow={id}','/quiet') -Wait -PassThru -WindowStyle Hidden",
            "if ($p.ExitCode -ne 0) { throw \"vssadmin exited with code $($p.ExitCode).\" }");

        await File.WriteAllTextAsync(scriptPath, script, ct);
        try
        {
            await RunElevatedPowerShellAsync(scriptPath, ct);
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { }
        }
    }

    private static async Task RunElevatedPowerShellAsync(string scriptPath, CancellationToken ct)
    {
        var powerShell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");

        var psi = new ProcessStartInfo(powerShell)
        {
            UseShellExecute = true,
            Verb = "runas",
            Arguments = $"-NoLogo -NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
            WindowStyle = ProcessWindowStyle.Hidden
        };

        try
        {
            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Could not start the elevated VSS helper.");
            await process.WaitForExitAsync(ct);
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Elevated VSS helper exited with code {process.ExitCode}.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException("VSS elevation was cancelled.", ex, ct);
        }
    }

    private static string StatePath(string storeRoot) => Path.Combine(storeRoot, "vss-transition.json");

    private static void TryDeleteState(string storeRoot)
    {
        try
        {
            var path = StatePath(storeRoot);
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }
}
