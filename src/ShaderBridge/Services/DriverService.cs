using Microsoft.Win32;
using ShaderBridge.Models;

namespace ShaderBridge.Services;

public sealed class DriverService
{
    private const string DisplayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    public IReadOnlyList<GpuDriverInfo> GetDisplayDrivers()
    {
        var result = new List<GpuDriverInfo>();
        try
        {
            using var classKey = Registry.LocalMachine.OpenSubKey(DisplayClassKey);
            if (classKey is null) return result;

            foreach (var subKeyName in classKey.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsDigit)))
            {
                using var key = classKey.OpenSubKey(subKeyName);
                if (key is null) continue;

                var desc = ReadString(key, "DriverDesc");
                var version = ReadString(key, "DriverVersion");
                if (string.IsNullOrWhiteSpace(desc) || string.IsNullOrWhiteSpace(version)) continue;

                var provider = ReadString(key, "ProviderName");
                var date = ReadString(key, "DriverDate");
                result.Add(new GpuDriverInfo(desc, provider, version, date,
                    $@"HKLM\{DisplayClassKey}\{subKeyName}"));
            }
        }
        catch
        {
            // Callers show an empty state instead of failing the whole app.
        }

        return result
            .GroupBy(x => x.Fingerprint, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(x => x.Provider)
            .ThenBy(x => x.Name)
            .ToList();
    }

    public string GetFingerprint()
    {
        var drivers = GetDisplayDrivers();
        return string.Join("||", drivers.Select(d => d.Fingerprint));
    }

    private static string ReadString(RegistryKey key, string name) => key.GetValue(name)?.ToString()?.Trim() ?? string.Empty;
}
