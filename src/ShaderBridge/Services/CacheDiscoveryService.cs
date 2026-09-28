using Microsoft.Win32;
using ShaderBridge.Models;

namespace ShaderBridge.Services;

public sealed class CacheDiscoveryService
{
    public async Task<IReadOnlyList<CacheRoot>> DiscoverAsync(IEnumerable<string> customRoots, CancellationToken ct = default)
    {
        var candidates = new List<(string Name, string Path, string Kind)>();
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        Add(candidates, "NVIDIA DXCache", Path.Combine(local, "NVIDIA", "DXCache"), "Driver/D3D");
        Add(candidates, "NVIDIA GLCache", Path.Combine(local, "NVIDIA", "GLCache"), "Driver/OpenGL");
        Add(candidates, "NVIDIA NV_Cache", Path.Combine(programData, "NVIDIA Corporation", "NV_Cache"), "Driver/Legacy");
        Add(candidates, "AMD DxCache", Path.Combine(local, "AMD", "DxCache"), "Driver/D3D");
        Add(candidates, "AMD DxcCache", Path.Combine(local, "AMD", "DxcCache"), "Driver/DXIL");
        Add(candidates, "AMD VkCache", Path.Combine(local, "AMD", "VkCache"), "Driver/Vulkan");
        Add(candidates, "AMD GLCache", Path.Combine(local, "AMD", "GLCache"), "Driver/OpenGL");
        Add(candidates, "Intel ShaderCache", Path.Combine(local, "Intel", "ShaderCache"), "Driver/Shader");
        Add(candidates, "D3DSCache", Path.Combine(local, "D3DSCache"), "Windows/D3D");

        foreach (var steamRoot in DiscoverSteamLibraries())
            Add(candidates, $"Steam shadercache ({Path.GetFileName(steamRoot.TrimEnd(Path.DirectorySeparatorChar))})",
                Path.Combine(steamRoot, "steamapps", "shadercache"), "Steam/Game");

        var customIndex = 1;
        foreach (var raw in customRoots)
        {
            var path = Environment.ExpandEnvironmentVariables(raw.Trim());
            if (string.IsNullOrWhiteSpace(path)) continue;
            Add(candidates, $"Custom {customIndex++}", path, "Custom");
        }

        var distinct = candidates
            .GroupBy(c => Normalize(c.Path), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        var results = new List<CacheRoot>(distinct.Count);
        foreach (var c in distinct)
        {
            ct.ThrowIfCancellationRequested();
            var exists = Directory.Exists(c.Path);
            long bytes = 0, files = 0;
            if (exists)
            {
                (bytes, files) = await Task.Run(() => Measure(c.Path, ct), ct);
            }
            results.Add(new CacheRoot(c.Name, c.Path, c.Kind, exists) { SizeBytes = bytes, FileCount = files });
        }
        return results.OrderByDescending(r => r.Exists).ThenBy(r => r.Name).ToList();
    }

    public IEnumerable<string> DiscoverSteamLibraries()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            var steamPath = key?.GetValue("SteamPath")?.ToString();
            if (!string.IsNullOrWhiteSpace(steamPath) && Directory.Exists(steamPath))
            {
                roots.Add(Path.GetFullPath(steamPath));
                var vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
                if (File.Exists(vdf))
                {
                    foreach (var line in File.ReadLines(vdf))
                    {
                        var trimmed = line.Trim();
                        if (!trimmed.StartsWith("\"path\"", StringComparison.OrdinalIgnoreCase)) continue;
                        var parts = trimmed.Split('"', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        if (parts.Length < 2) continue;
                        var path = parts[^1].Replace("\\\\", "\\");
                        if (Directory.Exists(path)) roots.Add(Path.GetFullPath(path));
                    }
                }
            }
        }
        catch { }
        return roots;
    }

    private static void Add(List<(string Name, string Path, string Kind)> list, string name, string path, string kind)
    {
        if (!string.IsNullOrWhiteSpace(path)) list.Add((name, Environment.ExpandEnvironmentVariables(path), kind));
    }

    private static (long Bytes, long Files) Measure(string root, CancellationToken ct)
    {
        long bytes = 0, files = 0;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = pending.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    ct.ThrowIfCancellationRequested();
                    try { bytes += new FileInfo(file).Length; files++; } catch { }
                }
                foreach (var sub in Directory.EnumerateDirectories(dir)) pending.Push(sub);
            }
            catch { }
        }
        return (bytes, files);
    }

    private static string Normalize(string path)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch { return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
    }
}
