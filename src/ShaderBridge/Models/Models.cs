namespace ShaderBridge.Models;

public sealed record GpuDriverInfo(
    string Name,
    string Provider,
    string DriverVersion,
    string DriverDate,
    string RegistryKey)
{
    public string Fingerprint => $"{Provider}|{Name}|{DriverVersion}|{DriverDate}";
}

public sealed record CacheRoot(string Name, string Path, string Kind, bool Exists)
{
    public long SizeBytes { get; init; }
    public long FileCount { get; init; }
    public string SizeText => FormatBytes(SizeBytes);

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var i = 0;
        while (value >= 1024 && i < units.Length - 1)
        {
            value /= 1024;
            i++;
        }
        return $"{value:0.##} {units[i]}";
    }
}

public sealed class SnapshotManifest
{
    public int FormatVersion { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string DriverFingerprint { get; set; } = string.Empty;
    public List<GpuDriverInfo> Drivers { get; set; } = [];
    public List<SnapshotFile> Files { get; set; } = [];
    public long TotalBytes => Files.Sum(f => f.Size);
    public string DisplayName => $"{CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} — {Files.Count:N0} files — {FormatBytes(TotalBytes)}";

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var i = 0;
        while (value >= 1024 && i < units.Length - 1)
        {
            value /= 1024;
            i++;
        }
        return $"{value:0.##} {units[i]}";
    }
}

public sealed class SnapshotChunk
{
    public string Sha256 { get; set; } = string.Empty;
    public int Size { get; set; }
}

public sealed class SnapshotFile
{
    public string RootName { get; set; } = string.Empty;
    public string RootPath { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTime LastWriteUtc { get; set; }
    public List<SnapshotChunk> Chunks { get; set; } = [];
    public string OriginalPath => Path.Combine(RootPath, RelativePath);
}

public sealed record SnapshotComparison(int Unchanged, int Modified, int Missing, int NewFiles)
{
    public override string ToString() =>
        $"Unchanged: {Unchanged:N0}   Modified: {Modified:N0}   Missing: {Missing:N0}   New: {NewFiles:N0}";
}

public sealed class ShaderBridgeSettings
{
    public string StoreRoot { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShaderBridge", "Store");
    public List<string> CustomCacheRoots { get; set; } = [];
    public string LastDriverFingerprint { get; set; } = string.Empty;
    public bool WatchCacheActivity { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
    public bool AutoBaselineSnapshot { get; set; } = true;
    public bool StartWithWindows { get; set; } = false;
    public int DriverPollSeconds { get; set; } = 30;
}

public sealed record VulkanCacheHeader(
    uint HeaderSize,
    uint HeaderVersion,
    uint VendorId,
    uint DeviceId,
    byte[] PipelineCacheUuid)
{
    public string UuidHex => Convert.ToHexString(PipelineCacheUuid);
    public string VendorHex => $"0x{VendorId:X4}";
    public string DeviceHex => $"0x{DeviceId:X4}";
}

public sealed record VulkanDeviceInfo(
    string DeviceName,
    uint ApiVersion,
    uint DriverVersion,
    uint VendorId,
    uint DeviceId,
    byte[] PipelineCacheUuid)
{
    public string UuidHex => Convert.ToHexString(PipelineCacheUuid);
    public string VendorHex => $"0x{VendorId:X4}";
    public string DeviceHex => $"0x{DeviceId:X4}";
}


public sealed class VssShadowCheckpoint
{
    public string VolumeRoot { get; set; } = string.Empty;
    public string ShadowId { get; set; } = string.Empty;
    public string DeviceObject { get; set; } = string.Empty;
}

public sealed class VssTransitionState
{
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string DriverFingerprint { get; set; } = string.Empty;
    public List<VssShadowCheckpoint> Shadows { get; set; } = [];
    public List<string> Errors { get; set; } = [];
    public bool IsActive => Shadows.Count > 0;
}
