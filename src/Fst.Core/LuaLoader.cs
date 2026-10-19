using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace Fst.Core;

public sealed record LoaderState(string Version, Dictionary<string, string> Files);

public static class LuaLoader
{
    // Proxy DLLs that Lua loaders (OpenSteamTool, SteamTools) place next to steam.exe. Stock Steam ships none of them,
    // so without one of these files Steam never reads config/lua and installed games stay invisible in the library.
    public static readonly string[] Markers = ["OpenSteamTool.dll", "xinput1_4.dll", "dwmapi.dll", "hid.dll"];
    public const string MissingMessage = "Lua-загрузчик не найден в папке Steam: без него Steam не видит добавленные игры. Установите OpenSteamTool (dwmapi.dll, xinput1_4.dll и OpenSteamTool.dll рядом с steam.exe) или выберите правильную папку Steam в «Каталогах».";
    public const string UnsupportedMessage = "OpenSteamTool пока не поддерживает установленную версию Steam ({0}): Steam показывает ошибки OpenSteamTool, а добавленные игры появятся в библиотеке только после выхода обновления OpenSteamTool.";
    // Pinned release: the archive hash is checked before anything is written into the Steam folder.
    public const string Version = "1.4.8";
    public const string ArchiveUrl = "https://github.com/OpenSteam001/OpenSteamTool/releases/download/1.4.8/OpenSteamTool-1.4.8-Release.zip";
    public const string ArchiveSha256 = "966654604d258d5d5383e72fec616df7957bf86f760c67eeb3d0e18cd882c710";
    public static readonly string[] Files = ["dwmapi.dll", "xinput1_4.dll", "OpenSteamTool.dll"];
    // OpenSteamTool hooks Steam through signatures published per steamclient64.dll/steamui.dll build.
    private static readonly (string Component, string File)[] Components = [("steamclient", "steamclient64.dll"), ("steamui", "steamui.dll")];
    private static readonly string[] PatternMirrors = ["https://raw.githubusercontent.com/OpenSteam001/steam-monitor/pattern/{0}/{1}.toml", "https://cdn.jsdelivr.net/gh/OpenSteam001/steam-monitor@pattern/{0}/{1}.toml"];

    public static string? Find(string steamRoot) => string.IsNullOrWhiteSpace(steamRoot) ? null : Markers.FirstOrDefault(m => File.Exists(Path.Combine(steamRoot, m)));
    public static void Require(string steamRoot) { if (Find(steamRoot) == null) throw new InvalidOperationException(MissingMessage); }

    /// <summary>True when no loader exists or an older OpenSteamTool installed by FST is still untouched.</summary>
    public static bool NeedsInstall(string steamRoot, string stateFile)
    {
        if (string.IsNullOrWhiteSpace(steamRoot)) return false;
        if (Find(steamRoot) == null) return true;
        var state = ReadState(stateFile);
        if (state == null) return false;
        // A file FST installed has disappeared (for example, quarantined): proxies alone do not load Lua.
        if (state.Files.Keys.Any(f => !File.Exists(Path.Combine(steamRoot, f)))) return true;
        return state.Version != Version && Files.Any(f => Owned(state, Path.Combine(steamRoot, f)));
    }

    public static async Task<string[]> DownloadAndInstallAsync(string steamRoot, string tempDirectory, string stateFile, HttpClient? httpClient = null, CancellationToken ct = default)
    {
        if (!File.Exists(Path.Combine(steamRoot, "steam.exe"))) throw new FileNotFoundException("steam.exe не найден.");
        Directory.CreateDirectory(tempDirectory);
        var archive = Path.Combine(tempDirectory, "opensteamtool-" + Guid.NewGuid().ToString("N") + ".zip");
        var client = httpClient ?? new HttpClient();
        // HttpClient.Timeout stops at the headers with ResponseHeadersRead; this budget also covers a stalled body.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, ArchiveUrl))
            {
                request.Headers.UserAgent.ParseAdd("FST/0.1");
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > 16L * 1024 * 1024) throw new InvalidDataException("Архив OpenSteamTool превышает допустимый размер.");
                await using var source = await response.Content.ReadAsStreamAsync(budget.Token);
                await using var target = File.Create(archive);
                await source.CopyToAsync(target, budget.Token);
                if (target.Length > 16L * 1024 * 1024) throw new InvalidDataException("Архив OpenSteamTool превышает допустимый размер.");
            }
            return Install(archive, steamRoot, stateFile);
        }
        finally
        {
            if (httpClient == null) client.Dispose();
            try { File.Delete(archive); } catch { }
        }
    }

    public static string[] Install(string archive, string steamRoot, string stateFile, string expectedSha256 = ArchiveSha256, string version = Version)
    {
        if (!File.Exists(Path.Combine(steamRoot, "steam.exe"))) throw new FileNotFoundException("steam.exe не найден.");
        using (var stream = File.OpenRead(archive))
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Контрольная сумма архива OpenSteamTool не совпадает.");
        using var zip = ZipFile.OpenRead(archive);
        var payload = Files.Select(name =>
        {
            var entry = zip.Entries.SingleOrDefault(e => e.FullName == name) ?? throw new InvalidDataException("В архиве OpenSteamTool нет " + name + ".");
            using var source = entry.Open();
            using var buffer = new MemoryStream();
            source.CopyTo(buffer);
            var bytes = buffer.ToArray();
            if (bytes.Length < 64 || bytes[0] != 'M' || bytes[1] != 'Z') throw new InvalidDataException("Файл OpenSteamTool повреждён: " + name);
            return (Name: name, Path: Path.Combine(steamRoot, name), Bytes: bytes);
        }).ToArray();
        if ((File.GetAttributes(steamRoot) & FileAttributes.ReparsePoint) != 0) throw new IOException("Каталог установки содержит ссылку: " + steamRoot);
        RemoveReplaced(steamRoot);
        var state = ReadState(stateFile);
        var owned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var changes = new List<(string Path, string? Old)>();
        try
        {
            foreach (var file in payload)
            {
                string? old = null;
                if (File.Exists(file.Path))
                {
                    // Only files FST wrote earlier and nobody changed since are upgraded; other DLLs belong to another loader or the user.
                    if (state == null || !Owned(state, file.Path)) continue;
                    if (state.Version == version) { owned[file.Name] = state.Files[file.Name]; continue; }
                    // A DLL loaded by a running Steam cannot be overwritten or deleted, but it can be renamed.
                    old = file.Path + ".fst-old-" + Guid.NewGuid().ToString("N");
                    File.Move(file.Path, old);
                }
                changes.Add((file.Path, old));
                var temp = file.Path + ".fst-" + Guid.NewGuid().ToString("N");
                try { File.WriteAllBytes(temp, file.Bytes); File.Move(temp, file.Path); }
                finally { if (File.Exists(temp)) File.Delete(temp); }
                owned[file.Name] = Convert.ToHexString(SHA256.HashData(file.Bytes));
            }
            if (owned.Count > 0)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(stateFile))!);
                File.WriteAllText(stateFile, JsonSerializer.Serialize(new LoaderState(version, owned)));
            }
        }
        catch
        {
            foreach (var change in changes.AsEnumerable().Reverse())
            {
                try { if (File.Exists(change.Path)) File.Delete(change.Path); } catch { }
                // Restore the original even if the new file could not be deleted; overwriting is still possible when it is not locked.
                try { if (change.Old != null) File.Move(change.Old, change.Path, true); } catch { }
            }
            throw;
        }
        RemoveReplaced(steamRoot);
        return changes.Select(c => c.Path).ToArray();
    }

    /// <summary>Steam DLLs that OpenSteamTool has no published signatures for. Network failures are not reported as unsupported.</summary>
    public static async Task<string[]> UnsupportedSteamFilesAsync(string steamRoot, HttpClient? httpClient = null, CancellationToken ct = default)
    {
        if (!File.Exists(Path.Combine(steamRoot, "OpenSteamTool.dll"))) return [];
        var client = httpClient ?? new HttpClient();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var unsupported = new List<string>();
            foreach (var (component, file) in Components)
            {
                var path = Path.Combine(steamRoot, file);
                if (!File.Exists(path)) continue;
                string hash;
                using (var stream = File.OpenRead(path)) hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                if (File.Exists(Path.Combine(steamRoot, "opensteamtool", "pattern", component, hash + ".toml"))) continue;
                foreach (var mirror in PatternMirrors)
                {
                    HttpStatusCode? status = null;
                    try
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Get, string.Format(mirror, component, hash));
                        request.Headers.UserAgent.ParseAdd("FST/0.1");
                        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token);
                        status = response.StatusCode;
                    }
                    catch (HttpRequestException) { }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return unsupported.ToArray(); }
                    if (status == HttpStatusCode.OK) break;
                    // Mirrors serve the same tracker, so a 404 means the signatures are not published yet.
                    if (status == HttpStatusCode.NotFound) { unsupported.Add(file); break; }
                }
            }
            return unsupported.ToArray();
        }
        finally { if (httpClient == null) client.Dispose(); }
    }

    private static LoaderState? ReadState(string stateFile)
    {
        try { return File.Exists(stateFile) ? JsonSerializer.Deserialize<LoaderState>(File.ReadAllText(stateFile)) : null; }
        catch (JsonException) { return null; }
    }

    private static bool Owned(LoaderState state, string path)
    {
        if (!File.Exists(path) || !state.Files.TryGetValue(Path.GetFileName(path), out var expected)) return false;
        // The file may be renamed by a concurrent upgrade; a file that cannot be read is not treated as FST-owned.
        try { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).Equals(expected, StringComparison.OrdinalIgnoreCase); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    private static void RemoveReplaced(string steamRoot)
    {
        foreach (var name in Files)
            foreach (var old in Directory.EnumerateFiles(steamRoot, name + ".fst-old-*"))
                try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
