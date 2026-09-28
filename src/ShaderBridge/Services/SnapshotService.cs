using System.Security.Cryptography;
using System.Text.Json;
using ShaderBridge.Models;

namespace ShaderBridge.Services;

public sealed class SnapshotService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly ChunkStore _chunkStore = new();
    public event Action<string>? Log;

    public async Task<SnapshotManifest> CreateAsync(
        string storeRoot,
        IEnumerable<CacheRoot> roots,
        IReadOnlyList<GpuDriverInfo> drivers,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(ManifestRoot(storeRoot));

        var manifest = new SnapshotManifest
        {
            FormatVersion = 2,
            Drivers = drivers.ToList(),
            DriverFingerprint = string.Join("||", drivers.Select(d => d.Fingerprint))
        };

        foreach (var root in roots.Where(r => r.Exists))
        {
            foreach (var file in EnumerateFilesSafe(root.Path))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var info = new FileInfo(file);
                    var stored = await _chunkStore.StoreFileDetailedAsync(file, storeRoot, ct);

                    manifest.Files.Add(new SnapshotFile
                    {
                        RootName = root.Name,
                        RootPath = root.Path,
                        RelativePath = Path.GetRelativePath(root.Path, file),
                        Kind = root.Kind,
                        Sha256 = stored.FullSha256,
                        Size = stored.Size,
                        LastWriteUtc = info.LastWriteTimeUtc,
                        Chunks = stored.Chunks.Select(c => new SnapshotChunk
                        {
                            Sha256 = c.Sha256,
                            Size = c.Size
                        }).ToList()
                    });

                    Log?.Invoke($"Stored {stored.Chunks.Count:N0} chunks ({FormatBytes(stored.Size)})  {file}");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Log?.Invoke($"Skipped locked/unreadable cache file: {file} ({ex.Message})");
                }
            }
        }

        var manifestPath = Path.Combine(ManifestRoot(storeRoot), manifest.Id + ".json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions), ct);
        return manifest;
    }

    public IReadOnlyList<SnapshotManifest> List(string storeRoot)
    {
        var root = ManifestRoot(storeRoot);
        if (!Directory.Exists(root)) return [];
        var result = new List<SnapshotManifest>();
        foreach (var file in Directory.EnumerateFiles(root, "*.json"))
        {
            try
            {
                var manifest = JsonSerializer.Deserialize<SnapshotManifest>(File.ReadAllText(file), JsonOptions);
                if (manifest is not null) result.Add(manifest);
            }
            catch { }
        }
        return result.OrderByDescending(m => m.CreatedUtc).ToList();
    }

    public async Task<SnapshotComparison> CompareToCurrentAsync(SnapshotManifest manifest, CancellationToken ct = default)
    {
        var unchanged = 0;
        var modified = 0;
        var missing = 0;
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in manifest.Files)
        {
            ct.ThrowIfCancellationRequested();
            var path = item.OriginalPath;
            known.Add(Path.GetFullPath(path));
            if (!File.Exists(path)) { missing++; continue; }

            try
            {
                var info = new FileInfo(path);
                if (info.Length != item.Size) { modified++; continue; }
                var hash = await ComputeSha256Async(path, ct);
                if (hash.Equals(item.Sha256, StringComparison.OrdinalIgnoreCase)) unchanged++;
                else modified++;
            }
            catch { modified++; }
        }

        var newFiles = 0;
        foreach (var root in manifest.Files.Select(f => f.RootPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root)) continue;
            foreach (var file in EnumerateFilesSafe(root))
            {
                ct.ThrowIfCancellationRequested();
                if (!known.Contains(Path.GetFullPath(file))) newFiles++;
            }
        }
        return new SnapshotComparison(unchanged, modified, missing, newFiles);
    }

    public async Task<(int Restored, int ExistingSkipped, int DriverNativeSkipped, int Failed)> RestoreMissingSafeAsync(
        string storeRoot, SnapshotManifest manifest, CancellationToken ct = default)
    {
        var restored = 0;
        var skipped = 0;
        var driverNativeSkipped = 0;
        var failed = 0;
        foreach (var item in manifest.Files)
        {
            ct.ThrowIfCancellationRequested();
            if (item.Kind.StartsWith("Driver/", StringComparison.OrdinalIgnoreCase) ||
                item.Kind.StartsWith("Windows/", StringComparison.OrdinalIgnoreCase))
            {
                driverNativeSkipped++;
                continue;
            }
            if (File.Exists(item.OriginalPath)) { skipped++; continue; }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(item.OriginalPath)!);

                if (manifest.FormatVersion >= 2)
                {
                    await _chunkStore.RehydrateAsync(item.Chunks, item.OriginalPath, storeRoot, ct);
                }
                else
                {
                    var objectPath = LegacyObjectPath(storeRoot, item.Sha256);
                    if (!File.Exists(objectPath)) { failed++; continue; }
                    await CopyStableAsync(objectPath, item.OriginalPath, ct);
                }

                try { File.SetLastWriteTimeUtc(item.OriginalPath, item.LastWriteUtc); } catch { }
                restored++;
                Log?.Invoke($"Restored missing cache file: {item.OriginalPath}");
            }
            catch (Exception ex)
            {
                failed++;
                Log?.Invoke($"Restore failed: {item.OriginalPath} ({ex.Message})");
            }
        }
        return (restored, skipped, driverNativeSkipped, failed);
    }

    public (long Objects, long Bytes) GetStoreStats(string storeRoot)
    {
        var chunks = _chunkStore.GetStats(storeRoot);
        var legacy = LegacyObjectRoot(storeRoot);
        long legacyCount = 0, legacyBytes = 0;
        if (Directory.Exists(legacy))
        {
            foreach (var file in EnumerateFilesSafe(legacy))
            {
                try
                {
                    legacyCount++;
                    legacyBytes += new FileInfo(file).Length;
                }
                catch { }
            }
        }
        return (chunks.ChunkCount + legacyCount, chunks.Bytes + legacyBytes);
    }

    private static IEnumerable<string> EnumerateFilesSafe(string root)
    {
        if (!Directory.Exists(root)) yield break;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            string[] files;
            string[] dirs;
            try { files = Directory.GetFiles(dir); } catch { files = []; }
            try { dirs = Directory.GetDirectories(dir); } catch { dirs = []; }
            foreach (var file in files) yield return file;
            foreach (var sub in dirs) pending.Push(sub);
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash);
    }

    private static async Task CopyStableAsync(string source, string destination, CancellationToken ct)
    {
        var temp = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await input.CopyToAsync(output, 1024 * 1024, ct);
                await output.FlushAsync(ct);
            }
            File.Move(temp, destination, false);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            throw;
        }
    }

    private static string ManifestRoot(string storeRoot) => Path.Combine(storeRoot, "manifests");
    private static string LegacyObjectRoot(string storeRoot) => Path.Combine(storeRoot, "objects");
    private static string LegacyObjectPath(string storeRoot, string sha256) =>
        Path.Combine(LegacyObjectRoot(storeRoot), sha256[..2], sha256);

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
