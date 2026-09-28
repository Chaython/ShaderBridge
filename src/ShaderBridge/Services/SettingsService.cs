using System.Text.Json;
using ShaderBridge.Models;

namespace ShaderBridge.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public string SettingsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ShaderBridge", "settings.json");

    public ShaderBridgeSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new ShaderBridgeSettings();
            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<ShaderBridgeSettings>(json, JsonOptions) ?? new ShaderBridgeSettings();
        }
        catch
        {
            return new ShaderBridgeSettings();
        }
    }

    public void Save(ShaderBridgeSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
    }
}
