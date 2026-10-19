using System.Diagnostics;
using Fst.Core;

namespace Fst.App;

public static class SteamControl
{
    public static volatile bool DowngradeActive;
    public static void EnsureDebugMarker(Settings settings)
    {
        var marker = Path.Combine(settings.SteamRoot, ".cef-enable-remote-debugging");
        // Existing Millennium marker may be a dangling junction: never replace it.
        if (Directory.EnumerateFileSystemEntries(settings.SteamRoot, ".cef-enable-remote-debugging").Any()) return;
        File.WriteAllText(marker, "");
        File.WriteAllText(Path.Combine(Settings.DataRoot, "debug-marker-created.txt"), marker);
    }
    public static async Task RestartAsync(Settings settings)
    {
        var exe = Path.Combine(settings.SteamRoot, "steam.exe");
        if (!File.Exists(exe)) throw new FileNotFoundException("steam.exe не найден.");
        EnsureDebugMarker(settings);
        await ShutdownAsync(exe, TimeSpan.FromSeconds(45));
        Start(exe);
    }
    private static void Start(string exe) => Process.Start(new ProcessStartInfo(exe, "-cef-enable-debugging") { UseShellExecute = true });
    private static bool Running(string exe) => Process.GetProcessesByName("steam").Any(p => { using (p) { try { return string.Equals(p.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase); } catch { return true; } } });
    private static async Task WaitForExitAsync(string exe, TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && Running(exe)) await Task.Delay(500, ct);
    }
    private static async Task ShutdownAsync(string exe, TimeSpan timeout, string failure = "Steam не завершился за 45 секунд. Файлы установлены; перезапустите Steam вручную.")
    {
        if (!Running(exe)) return;
        Process.Start(new ProcessStartInfo(exe, "-shutdown") { UseShellExecute = false, CreateNoWindow = true });
        await WaitForExitAsync(exe, timeout);
        if (Process.GetProcessesByName("steam").Any()) throw new InvalidOperationException(failure);
    }

    /// <summary>
    /// Installs the newest stable Steam build OpenSteamTool supports and blocks Steam self-updates with steam.cfg.
    /// Packages come from Valve's CDN through a loopback mirror that pins the archived client manifest.
    /// </summary>
    public static async Task<string> DowngradeAsync(Settings settings, Action<string> progress, CancellationToken ct = default)
    {
        if (DowngradeActive) throw new InvalidOperationException("Идёт откат Steam. Дождитесь завершения.");
        if (JobService.InstallationActive) throw new InvalidOperationException("Установка уже идёт. Дождитесь завершения.");
        DowngradeActive = true;
        var root = settings.SteamRoot;
        var exe = Path.Combine(root, "steam.exe");
        var aside = Path.Combine(Settings.DataRoot, "loader-aside");
        var moved = new List<string>();
        var stopped = false;
        try
        {
            if (!File.Exists(exe)) throw new FileNotFoundException("steam.exe не найден.");
            if (SteamDowngrade.UpdatesBlocked(root) && !SteamDowngrade.UpdatesBlockedByFst(root)) throw new InvalidOperationException("Файл steam.cfg в папке Steam запрещает обновления. Удалите его и повторите.");
            using var client = new HttpClient(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All }) { Timeout = TimeSpan.FromMinutes(1) };
            progress(L.T("Поиск версии Steam, которую поддерживает OpenSteamTool…"));
            var target = await SteamDowngrade.SupportedStableAsync(client, ct);
            var installed = SteamDowngrade.InstalledVersion(root);
            progress(L.F("Поиск файлов Steam {0}…", target));
            var manifest = await SteamDowngrade.FindManifestAsync(target, client, ct);
            await using var mirror = new PackageMirror(manifest, client);
            progress(L.T("Закрытие Steam…"));
            await ShutdownAsync(exe, TimeSpan.FromSeconds(45), "Steam не завершился за 45 секунд. Закройте Steam и повторите.");
            stopped = true;
            // FST's own update block would stop the bootstrapper from installing anything.
            SteamDowngrade.AllowUpdates(root);
            // Keep OpenSteamTool out of the bootstrapper run: its dialogs for the old build would block the update.
            Directory.CreateDirectory(aside);
            foreach (var file in LuaLoader.Files.Where(f => File.Exists(Path.Combine(root, f))))
            {
                File.Move(Path.Combine(root, file), Path.Combine(aside, file), true);
                moved.Add(file);
            }
            progress(L.F("Установка Steam {0}…", target));
            var update = new ProcessStartInfo(exe) { UseShellExecute = false };
            foreach (var arg in new[] { "-forcesteamupdate", "-forcepackagedownload", "-overridepackageurl", mirror.Url, "-exitsteam" }) update.ArgumentList.Add(arg);
            using (Process.Start(update)) { }
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            await WaitForExitAsync(exe, TimeSpan.FromMinutes(20), ct);
            var now = SteamDowngrade.InstalledVersion(root);
            if (now != target) throw new InvalidOperationException(L.F("Steam не установил версию {0} (сейчас {1}). Подробности в logs\\bootstrap_log.txt.", target, now ?? "?"));
            SteamDowngrade.BlockUpdates(root);
            JobService.Log("Steam откатан с {0} на {1}, обновления Steam отключены.", installed ?? "?", target);
            return target;
        }
        finally
        {
            foreach (var file in moved)
                try { File.Move(Path.Combine(aside, file), Path.Combine(root, file), true); }
                catch (Exception e) { JobService.Log("Не удалось вернуть {0}: {1}", file, e.Message); }
            DowngradeActive = false;
            if (stopped && File.Exists(exe) && !Running(exe)) try { Start(exe); } catch { }
        }
    }
}
