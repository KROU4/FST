using System.IO.Compression;
using System.Text;
using Fst.App;
using Fst.Core;
using YamlDotNet.RepresentationModel;

namespace Fst.Linux;

internal static class SelfTests
{
    public static int Run()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "fst-linux-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var passed = 0;
        void Check(bool success, string name)
        {
            if (!success)
                throw new Exception(name);
            Console.WriteLine("PASS " + name);
            passed++;
        }
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(sandbox, "config"));
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(sandbox, "data"));
            var settings = new Settings { SteamRoot = Path.Combine(sandbox, "Steam"), SlsLibrary = Path.Combine(sandbox, "sls", "SLSsteam.so"), LumaLibrary = Path.Combine(sandbox, "sls", "liblumalinux.so"), LumaKeys = Path.Combine(sandbox, "luma", "keys.txt"), AutoUpdate = false };
            Directory.CreateDirectory(settings.SteamRoot);
            File.WriteAllText(Path.Combine(settings.SteamRoot, "steam.sh"), "#!/bin/sh\n");
            Directory.CreateDirectory(Path.GetDirectoryName(settings.SlsLibrary)!);
            File.WriteAllText(settings.SlsLibrary, "test fixture");
            File.WriteAllText(settings.LumaLibrary, "test fixture");
            Directory.CreateDirectory(Path.GetDirectoryName(settings.LumaKeys)!);
            File.WriteAllText(settings.LumaKeys, "# Manual keys\n900;" + new string('c', 64) + "\n");
            settings.Save();
            Check(Settings.Load().SteamRoot == settings.SteamRoot, "XDG settings round trip");
            var jobs = new JobService(settings, new RyuuSession(loadSaved: false), _ => { }, () => { });
            using (var lease = jobs.BeginMaintenance())
            {
                Check(jobs.IsBusy, "install and library maintenance share one operation lock");
                try
                {
                    jobs.BeginMaintenance();
                    throw new Exception("Expected concurrent operation rejection");
                }
                catch (InvalidOperationException) { Check(true, "concurrent maintenance rejected"); }
            }
            Check(!jobs.IsBusy, "maintenance lock releases after operation");
            Check(!File.ReadAllText(Settings.FilePath).Contains("auth_key"), "settings contain no Ryuu key");
            Directory.CreateDirectory(Path.GetDirectoryName(settings.SlsConfig)!);
            File.WriteAllText(settings.SlsConfig, "AdditionalApps: [42]\nManifestIds:\n  777: 999\nDisableUpdates: yes\nFakeName: preserve-me\n");
            var archive = Path.Combine(sandbox, "manifest.zip");
            var binary = new byte[16];
            BitConverter.GetBytes(0x71F617D0u).CopyTo(binary, 0);
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(zip.CreateEntry("440.lua").Open(), new UTF8Encoding(false)))
                    writer.Write("addappid(440)\naddappid(441,0,\"" + new string('a', 64) + "\")\naddappid(778,0,\"" + new string('b', 64) + "\")\nsetManifestid(777,\"123\")\nsetManifestid(778,\"456\")\n");
                using (var stream = zip.CreateEntry("441_123.manifest").Open())
                    stream.Write(binary);
            }
            YamlMappingNode Root()
            {
                var yaml = new YamlStream();
                yaml.Load(new StringReader(File.ReadAllText(settings.SlsConfig)));
                return (YamlMappingNode)yaml.Documents[0].RootNode;
            }
            string? Scalar(YamlNode node) => ((YamlScalarNode)node).Value;
            bool HasApp(string id) => ((YamlSequenceNode)Root().Children[new YamlScalarNode("AdditionalApps")]).Children.Any(n => Scalar(n) == id);
            var result = SlsBackend.Install(archive, "440", settings);
            Check(HasApp("42") && HasApp("440") && !HasApp("441"), "SLS registers main app and preserves existing apps");
            Check(Scalar(Root().Children[new YamlScalarNode("FakeName")]) == "preserve-me", "unrelated SLS settings preserved");
            var pins = (YamlMappingNode)Root().Children[new YamlScalarNode("ManifestIds")];
            Check(Scalar(pins.Children[new YamlScalarNode("777")]) == "999", "manual manifest pin wins");
            Check(Scalar(pins.Children[new YamlScalarNode("778")]) == "456", "FST manifest pin translated");
            Check(File.Exists(Path.Combine(settings.DepotDirectory, "441_123.manifest")), "native Steam depotcache receives binary manifest");
            Check(File.ReadAllText(Path.Combine(settings.LuaDirectory, "440.lua")).Contains(new string('a', 64)), "depot keys retained in declarative Lua");
            var keys = File.ReadAllText(settings.LumaKeys);
            Check(keys.Contains("440;\n") && keys.Contains("441;440;0;0;" + new string('a', 64)), "lumalinux receives app presence and native depot keys");
            Check(keys.Contains("778;440;456;0;" + new string('b', 64)), "lumalinux receives pinned depot gid");
            Check(keys.Contains("900;" + new string('c', 64)), "manual lumalinux keys preserved");
            Check(Scalar(Root().Children[new YamlScalarNode("DisableUpdates")]) == "false", "SLS download/update blocking disabled for FST games");
            SlsBackend.Synchronize(settings);
            Check(HasApp("42") && HasApp("440"), "SLS sync is idempotent");
            File.WriteAllText(Path.Combine(settings.LuaDirectory, "660.lua"), "addappid(660)\n");
            SlsBackend.Synchronize(settings);
            SlsBackend.Remove(["440", "660"], settings);
            Check(HasApp("42") && !HasApp("440") && !HasApp("660"), "multi-removal preserves manually added apps");
            Check(!File.ReadAllText(settings.LumaKeys).Contains("441;440;") && File.ReadAllText(settings.LumaKeys).Contains("900;"), "removal only drops FST-owned depot key entries");
            Check(Scalar(Root().Children[new YamlScalarNode("DisableUpdates")]) == "yes", "original update preference restored after removing FST games");
            pins = (YamlMappingNode)Root().Children[new YamlScalarNode("ManifestIds")];
            Check(pins.Children.ContainsKey(new YamlScalarNode("777")) && !pins.Children.ContainsKey(new YamlScalarNode("778")), "removal only drops FST-owned pins");
            Check(Directory.EnumerateFiles(Path.Combine(Settings.DataRoot, "backups"), "*.lua", SearchOption.AllDirectories).Count() >= 2, "removal keeps recovery copies");
            File.Delete(Path.Combine(Settings.DataRoot, "sls-owned.json"));
            Directory.CreateDirectory(Path.Combine(Settings.DataRoot, "sls-owned.json"));
            try
            {
                SlsBackend.Install(archive, "440", settings);
                throw new Exception("Expected config failure");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            Check(!File.Exists(Path.Combine(settings.LuaDirectory, "440.lua")), "failed SLS metadata commit rolls back Lua installation");
            Check(HasApp("42") && !HasApp("440"), "failed SLS commit restores original configuration");
            Check(!File.ReadAllText(settings.LumaKeys).Contains("441;440;"), "failed commit preserves native key store");
            Directory.Delete(Path.Combine(Settings.DataRoot, "sls-owned.json"));
            var manualKeys = File.ReadAllBytes(settings.LumaKeys);
            File.Delete(settings.LumaKeys);
            Directory.CreateDirectory(settings.LumaKeys);
            try
            {
                SlsBackend.Install(archive, "440", settings);
                throw new Exception("Expected key store failure");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            Check(!File.Exists(Path.Combine(settings.LuaDirectory, "440.lua")) && !HasApp("440"), "failed native key write rolls back Lua and SLS registration");
            Check(!File.Exists(Path.Combine(Settings.DataRoot, "sls-owned.json")), "failed native key write rolls back ownership metadata");
            Directory.Delete(settings.LumaKeys);
            File.WriteAllBytes(settings.LumaKeys, manualKeys);
            File.AppendAllText(settings.LumaKeys, "# >>> Fast Steam Tools managed keys >>>\n");
            try
            {
                SlsBackend.RequireReady(settings);
                throw new Exception("Expected malformed key block rejection");
            }
            catch (InvalidDataException) { Check(true, "malformed native key block rejected before download/install"); }
            Check(!File.Exists(Path.Combine(settings.LuaDirectory, "440.lua")), "malformed key preflight preserves Steam files");
            File.WriteAllBytes(settings.LumaKeys, manualKeys);
            SteamControl.EnsureDebugMarker(settings);
            Check(File.Exists(Path.Combine(settings.SteamRoot, ".cef-enable-remote-debugging")), "CEF marker created in native Steam root");
            var quoted = Startup.DesktopEntry("/tmp/a b/fst", "--background");
            Check(quoted.Contains("Exec=\"/tmp/a b/fst\" --background"), "desktop launch path is quoted");
            if (OperatingSystem.IsLinux())
            {
                var alias = Path.Combine(sandbox, "steam-alias");
                Directory.CreateSymbolicLink(alias, settings.SteamRoot);
                Check(Settings.CanonicalDirectory(alias) == settings.SteamRoot, "standard Linux Steam symlink resolves before install");
                File.Delete(settings.SlsConfig);
                File.CreateSymbolicLink(settings.SlsConfig, Path.Combine(sandbox, "elsewhere.yaml"));
                File.WriteAllText(Path.Combine(sandbox, "elsewhere.yaml"), "AdditionalApps: [99]\n");
                try
                {
                    SlsBackend.Synchronize(settings);
                    throw new Exception("Expected symlink rejection");
                }
                catch (IOException) { Check(true, "SLS config symlink rejected"); }
            }
            Console.WriteLine($"{passed} Linux checks passed; sandbox: {sandbox}");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine("FAIL " + e.Message + "; sandbox: " + sandbox); return 1; }
        finally { Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", config); Environment.SetEnvironmentVariable("XDG_DATA_HOME", data); }
    }
}
