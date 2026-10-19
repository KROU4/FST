using Microsoft.Win32;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fst.Core;

namespace Fst.App;

public sealed class Settings
{
    public string SteamRoot { get; set; } = "";
    public string LuaDirectory { get; set; } = "";
    public string DepotDirectory { get; set; } = "";
    public bool AutoUpdate { get; set; } = true;
    public bool MirrorManifests { get; set; } = true;
    public bool RestartAfterInstall { get; set; } = true;
    public bool RyuuImportAttempted
    {
        get; set;
    }
    public string Language { get; set; } = "ru";
    public static string DataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FST");
    public static string FilePath => Path.Combine(DataRoot, "settings.json");
    public static Settings Load(string? ryuuPath = null)
    {
        Directory.CreateDirectory(DataRoot);
        if (File.Exists(FilePath))
            return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath))!;
        var result = new Settings();
        var candidates = new[] { ryuuPath, Path.Combine(AppContext.BaseDirectory, "RyuuManifestTool", "config.json"), Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "RyuuManifestTool", "config.json")), Path.Combine(Environment.CurrentDirectory, "RyuuManifestTool", "config.json") };
        var source = candidates.FirstOrDefault(p => p != null && File.Exists(p));
        if (source != null)
        {
            using var json = JsonDocument.Parse(File.ReadAllText(source));
            var root = json.RootElement;
            string Read(string name) => root.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";
            result.SteamRoot = Path.GetDirectoryName(Read("steam_exe_path")) ?? "";
            result.LuaDirectory = Read("steam_config_path");
            result.DepotDirectory = Read("depot_cache_path");
            if (root.TryGetProperty("auto_updating_manifests", out var auto))
                result.AutoUpdate = auto.GetBoolean();
            if (root.TryGetProperty("mirror_manifests_to_steam_depotcache", out var mirror))
                result.MirrorManifests = mirror.GetBoolean();
        }
        if (string.IsNullOrWhiteSpace(result.SteamRoot))
            result.SteamRoot = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null)?.ToString()?.Replace('/', Path.DirectorySeparatorChar) ?? "";
        if (string.IsNullOrWhiteSpace(result.LuaDirectory))
            result.LuaDirectory = Path.Combine(result.SteamRoot, "config", "lua");
        if (string.IsNullOrWhiteSpace(result.DepotDirectory))
            result.DepotDirectory = Path.Combine(result.SteamRoot, "config", "depotcache");
        result.Save();
        return result;
    }
    public string[] ActiveLuaDirectories()
    {
        var defaults = LuaLoader.DefaultLuaDirectories(SteamRoot);
        var toml = Path.Combine(SteamRoot, "opensteamtool.toml");
        if (!File.Exists(toml))
            return defaults;
        var section = Regex.Match(File.ReadAllText(toml), @"(?ms)^\[lua\]\s*(.*?)(?=^\[|\z)").Groups[1].Value;
        var paths = Regex.Match(section, @"(?m)^paths\s*=\s*\[([^\]]*)\]").Groups[1].Value;
        return defaults.Concat(Regex.Matches(paths, "\"([^\"]+)\"").Select(m => m.Groups[1].Value.Replace(@"\\", @"\")).Select(p => Path.GetFullPath(Path.Combine(SteamRoot, p)))).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public InstallPlan Plan() => new(SteamRoot, LuaDirectory, DepotDirectory, AutoUpdate, MirrorManifests, ActiveLuaDirectories());
    public string[] LuaDirectories() => new[] { LuaDirectory }.Concat(ActiveLuaDirectories()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    public void Save()
    {
        Directory.CreateDirectory(DataRoot);
        var data = new
        {
            SteamRoot,
            LuaDirectory,
            DepotDirectory,
            AutoUpdate,
            MirrorManifests,
            RestartAfterInstall,
            RyuuImportAttempted,
            Language
        };
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, FilePath, true);
    }
}
