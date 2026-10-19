using System.Text.Json;

namespace Fst.Core;

/// <summary>Persists the original destination before moving DLLs out of Steam, so recovery survives settings changes and restarts.</summary>
public static class LoaderRecovery
{
    private sealed record Plan(string SteamRoot);
    private const string PlanFile = "restore.json";

    public static void SetAside(string steamRoot, string aside)
    {
        if (Directory.Exists(aside) && LuaLoader.Files.Any(file => File.Exists(Path.Combine(aside, file))))
            throw new IOException("Previous loader recovery is incomplete.");
        Directory.CreateDirectory(aside);
        var plan = Path.Combine(aside, PlanFile);
        var temp = plan + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(new Plan(Path.GetFullPath(steamRoot))));
            File.Move(temp, plan, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        foreach (var file in LuaLoader.Files)
        {
            var source = Path.Combine(steamRoot, file);
            if (File.Exists(source)) File.Move(source, Path.Combine(aside, file));
        }
    }

    public static void Recover(string aside, Action<string, Exception> onError)
    {
        if (!Directory.Exists(aside)) return;
        string root;
        var plan = Path.Combine(aside, PlanFile);
        try
        {
            // Without a recorded destination, preserve DLLs rather than inject them into an unrelated Steam directory.
            if (!LuaLoader.Files.Any(file => File.Exists(Path.Combine(aside, file)))) { File.Delete(plan); return; }
            root = JsonSerializer.Deserialize<Plan>(File.ReadAllText(plan))?.SteamRoot ?? throw new InvalidDataException("Missing loader recovery destination.");
            if (!Path.IsPathFullyQualified(root) || !File.Exists(Path.Combine(root, "steam.exe")))
                throw new InvalidDataException("Original Steam directory is unavailable.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { onError(PlanFile, e); return; }
        foreach (var file in LuaLoader.Files)
        {
            var source = Path.Combine(aside, file);
            if (!File.Exists(source)) continue;
            try
            {
                var target = Path.Combine(root, file);
                if (File.Exists(target)) File.Delete(source); else File.Move(source, target);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { onError(file, e); }
        }
        if (!LuaLoader.Files.Any(file => File.Exists(Path.Combine(aside, file))))
            try { File.Delete(plan); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { onError(PlanFile, e); }
    }
}
