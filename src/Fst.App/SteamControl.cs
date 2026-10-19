using System.Diagnostics;

namespace Fst.App;

public static class SteamControl
{
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
        Process.Start(new ProcessStartInfo(exe, "-shutdown") { UseShellExecute = false, CreateNoWindow = true });
        var timeout = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < timeout)
        {
            if (!Process.GetProcessesByName("steam").Any(p => { using (p) { try { return string.Equals(p.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase); } catch { return true; } } })) break;
            await Task.Delay(500);
        }
        if (Process.GetProcessesByName("steam").Any()) throw new InvalidOperationException("Steam не завершился за 45 секунд. Файлы установлены; перезапустите Steam вручную.");
        Process.Start(new ProcessStartInfo(exe, "-cef-enable-debugging") { UseShellExecute = true });
    }
}
