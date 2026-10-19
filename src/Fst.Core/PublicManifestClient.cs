using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fst.Core;

public sealed record DownloadResult(string Source, bool FromCache, string Freshness);

public sealed class PublicManifestClient : IDisposable
{
    private const long MaximumDownload = 256L * 1024 * 1024;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(6);
    private static readonly string[] Repositories =
        ["SteamAutoCracks/ManifestHub", "bsinwhg/ManifestHub", "steamtools-games/ManifestHub3"];
    private readonly HttpClient client = new(new HttpClientHandler { AllowAutoRedirect = true, UseCookies = false })
    {
        Timeout = TimeSpan.FromMinutes(3)
    };

    public async Task<DownloadResult> DownloadAsync(string appId, string target, string cacheRoot,
        bool allowStale = false, CancellationToken ct = default)
    {
        ManifestInstaller.ValidateAppId(appId);
        Directory.CreateDirectory(cacheRoot);
        // Recheck public depot metadata even when the archive is cached.
        var expected = await ReadPublicManifestsAsync(appId, ct);
        var failures = new List<string>();
        var stale = new List<(string Path, string Source, bool Cached)>();
        var olderCache = new List<(string Path, string Source, string Freshness)>();
        foreach (var repository in Repositories)
        {
            ct.ThrowIfCancellationRequested();
            var cache = Path.Combine(cacheRoot, repository.Replace('/', '_') + "-" + appId + ".zip");
            if (File.Exists(cache))
            {
                try
                {
                    var freshness = Freshness(ValidateArchive(cache, appId), expected);
                    if (freshness == "stale")
                        stale.Add((cache, repository, true));
                    else if (DateTime.UtcNow - File.GetLastWriteTimeUtc(cache) <= CacheTtl)
                        return CopyResult(cache, target, repository, true, freshness);
                    else
                        olderCache.Add((cache, repository, freshness));
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or DecoderFallbackException)
                {
                    failures.Add(repository + ": повреждённый кэш");
                }
            }
            var temporary = cache + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    "https://codeload.github.com/" + repository + "/zip/refs/heads/" + appId);
                request.Headers.UserAgent.ParseAdd("FST/0.1");
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode)
                {
                    failures.Add(repository + ": HTTP " + (int)response.StatusCode);
                    continue;
                }
                if (response.Content.Headers.ContentType?.MediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true)
                    throw new InvalidDataException("Источник вернул HTML вместо ZIP.");
                if (response.Content.Headers.ContentLength > MaximumDownload)
                    throw new InvalidDataException("ZIP превышает допустимый размер.");
                await using (var input = await response.Content.ReadAsStreamAsync(ct))
                await using (var output = File.Create(temporary))
                    await CopyBoundedAsync(input, output, MaximumDownload, ct);
                var freshness = Freshness(ValidateArchive(temporary, appId), expected);
                olderCache.RemoveAll(item => item.Path == cache);
                File.Move(temporary, cache, true);
                if (freshness != "stale")
                    return CopyResult(cache, target, repository, false, freshness);
                stale.RemoveAll(item => item.Path == cache);
                stale.Add((cache, repository, false));
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or DecoderFallbackException ||
                                       ex is OperationCanceledException && !ct.IsCancellationRequested)
            {
                failures.Add(repository + ": " + ex.Message);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        // An expired archive can still be used offline; the metadata label stays explicit.
        if (olderCache.Count > 0)
        {
            var cached = olderCache[0];
            return CopyResult(cached.Path, target, cached.Source, true, cached.Freshness);
        }
        if (stale.Count > 0)
        {
            if (allowStale)
            {
                var candidate = stale[0];
                return CopyResult(candidate.Path, target, candidate.Source, candidate.Cached, "stale");
            }
            throw new InvalidDataException("Найденные манифесты устарели: в ZIP нет текущих public GID из метаданных SteamCMD. " +
                "Установка отменена. Источники: " + string.Join(", ", stale.Select(item => item.Source).Distinct()));
        }
        throw new HttpRequestException("Публичный ZIP для AppID " + appId + " не найден. " + string.Join("; ", failures));
    }

    private async Task<Dictionary<string, string>?> ReadPublicManifestsAsync(string appId, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.steamcmd.net/v1/info/" + appId);
            request.Headers.UserAgent.ParseAdd("FST/0.1");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var bytes = new MemoryStream();
            await CopyBoundedAsync(input, bytes, 16L * 1024 * 1024, timeout.Token);
            using var json = JsonDocument.Parse(bytes.ToArray());
            if (!json.RootElement.TryGetProperty("data", out var data) || !data.TryGetProperty(appId, out var app) ||
                !app.TryGetProperty("depots", out var depots) || depots.ValueKind != JsonValueKind.Object)
                return null;
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var depot in depots.EnumerateObject())
            {
                if (!Regex.IsMatch(depot.Name, "^[0-9]+$") || depot.Value.ValueKind != JsonValueKind.Object ||
                    !depot.Value.TryGetProperty("manifests", out var manifests) || manifests.ValueKind != JsonValueKind.Object ||
                    !manifests.TryGetProperty("public", out var branch) || branch.ValueKind != JsonValueKind.Object ||
                    !branch.TryGetProperty("gid", out var gid))
                    continue;
                var value = gid.ToString();
                if (Regex.IsMatch(value, "^[1-9][0-9]*$"))
                    result[depot.Name] = value;
            }
            return result.Count == 0 ? null : result;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidDataException ||
                                   ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private static HashSet<string> ValidateArchive(string path, string appId)
    {
        using var file = File.OpenRead(path);
        if (file.Length > MaximumDownload || file.ReadByte() != 'P' || file.ReadByte() != 'K')
            throw new InvalidDataException("Источник не вернул допустимый ZIP.");
        file.Position = 0;
        using var zip = new ZipArchive(file, ZipArchiveMode.Read);
        if (zip.Entries.Count > 4096)
            throw new InvalidDataException("В ZIP слишком много файлов.");
        long total = 0;
        ZipArchiveEntry? lua = null;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            var normalized = entry.FullName.Replace('\\', '/');
            if (normalized.StartsWith('/') || normalized.Split('/').Any(part => part == ".." || part.Contains(':')))
                throw new InvalidDataException("Небезопасный путь в ZIP.");
            if (entry.Length > 64L * 1024 * 1024 || (total += entry.Length) > MaximumDownload)
                throw new InvalidDataException("Распакованный ZIP превышает допустимый размер.");
            if (entry.Name.Length == 0)
                continue;
            if (!names.Add(entry.Name))
                throw new InvalidDataException("Повторяющееся имя файла в ZIP.");
            if (entry.Name == appId + ".lua")
                lua = entry;
        }
        if (lua == null || lua.Length > 4L * 1024 * 1024)
            throw new InvalidDataException("В ZIP нет Lua запрошенной игры.");
        using var stream = lua.Open();
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true);
        var text = reader.ReadToEnd();
        if (!Regex.IsMatch(text, @"(?im)^\s*addappid\s*\(\s*" + appId + @"\s*[,)]"))
            throw new InvalidDataException("Lua не содержит запрошенный AppID.");
        return names;
    }

    private static string Freshness(HashSet<string> names, Dictionary<string, string>? expected) => expected == null
        ? "unknown" : expected.All(item => names.Contains(item.Key + "_" + item.Value + ".manifest")) ? "fresh" : "stale";

    private static DownloadResult CopyResult(string archive, string target, string source, bool cached, string freshness)
    {
        var fullTarget = Path.GetFullPath(target);
        Directory.CreateDirectory(Path.GetDirectoryName(fullTarget)!);
        if (!Path.GetFullPath(archive).Equals(fullTarget, StringComparison.OrdinalIgnoreCase))
        {
            var temporary = fullTarget + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.Copy(archive, temporary);
                File.Move(temporary, fullTarget, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        return new(source, cached, freshness);
    }

    private static async Task CopyBoundedAsync(Stream input, Stream output, long maximum, CancellationToken ct)
    {
        var buffer = new byte[65536];
        long total = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, ct)) != 0)
        {
            if ((total += count) > maximum)
                throw new InvalidDataException("Превышен допустимый размер загрузки.");
            await output.WriteAsync(buffer.AsMemory(0, count), ct);
        }
    }

    public void Dispose() => client.Dispose();
}
