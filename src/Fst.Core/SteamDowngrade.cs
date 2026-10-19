using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fst.Core;

/// <summary>Rolls the Steam client back to the newest stable build OpenSteamTool publishes signatures for.</summary>
public static class SteamDowngrade
{
    public const string ManifestName = "steam_client_win64";
    public const string PackageCdn = "https://client-update.steamstatic.com/";
    // Written to steam.cfg; FST only removes a steam.cfg whose content is exactly this.
    public const string UpdateBlock = "BootStrapperInhibitAll=enable\r\nBootStrapperForceSelfUpdate=disable\r\n";
    // steam-monitor states the newest stable build it covers in each channel's README.
    public static readonly string[] TrackerReadmes = ["https://raw.githubusercontent.com/OpenSteam001/steam-monitor/pattern/README.md", "https://raw.githubusercontent.com/OpenSteam001/steam-monitor/ipc/README.md"];
    // Valve serves only the current manifest; older ones come from the Wayback Machine while packages still come from Valve.
    private const string ArchiveIndex = "https://web.archive.org/cdx/search/cdx?url=media.steampowered.com/client/steam_client_win64&output=json&fl=timestamp&filter=statuscode:200&collapse=digest&from={0}&limit=12";
    private const string ArchiveFile = "https://web.archive.org/web/{0}id_/http://media.steampowered.com/client/steam_client_win64";

    public static string? InstalledVersion(string steamRoot)
    {
        var path = Path.Combine(steamRoot, "package", ManifestName + ".manifest");
        return File.Exists(path) ? ManifestVersion(File.ReadAllText(path)) : null;
    }
    public static string? ManifestVersion(string manifest) => Regex.Match(manifest, "\"version\"\\s+\"(\\d+)\"") is { Success: true } m ? m.Groups[1].Value : null;
    public static string[] ManifestPackages(string manifest) => Regex.Matches(manifest, "\"(?:file|zipvz)\"\\s+\"([^\"]+)\"").Select(m => m.Groups[1].Value).Distinct().ToArray();
    public static string? StableFromReadme(string readme) => Regex.Match(readme, @"(?im)^\|\s*Stable\s*\|\s*(\d+)\s*\|") is { Success: true } m ? m.Groups[1].Value : null;

    public static async Task<string> SupportedStableAsync(HttpClient client, CancellationToken ct = default)
    {
        var builds = new List<ulong>();
        foreach (var url in TrackerReadmes)
            builds.Add(ulong.Parse(StableFromReadme(await GetStringAsync(client, url, ct)) ?? throw new InvalidDataException("Трекер OpenSteamTool не сообщил поддерживаемую версию Steam.")));
        // Signatures and IPC specs are published per build, so the older of the two newest builds is covered by both.
        return builds.Min().ToString();
    }

    /// <summary>Finds the archived client manifest of a build and checks that Valve still serves every package it lists.</summary>
    public static async Task<string> FindManifestAsync(string version, HttpClient client, CancellationToken ct = default)
    {
        var released = DateTimeOffset.FromUnixTimeSeconds(long.Parse(version)).UtcDateTime;
        using var index = JsonDocument.Parse(await GetStringAsync(client, string.Format(ArchiveIndex, released.ToString("yyyyMMdd")), ct));
        foreach (var row in index.RootElement.EnumerateArray().Skip(1))
        {
            var manifest = await GetStringAsync(client, string.Format(ArchiveFile, row[0].GetString()), ct);
            var found = ManifestVersion(manifest);
            if (found == version) { await RequirePackagesAsync(manifest, client, ct); return manifest; }
            // Snapshots are in ascending order: once a newer build shows up, this one was never archived.
            if (found != null && ulong.Parse(found) > ulong.Parse(version)) break;
        }
        throw new InvalidDataException("Список файлов этой версии Steam не найден в архиве.");
    }

    private static async Task RequirePackagesAsync(string manifest, HttpClient client, CancellationToken ct)
    {
        var packages = ManifestPackages(manifest);
        if (packages.Length == 0 || packages.Any(p => !Regex.IsMatch(p, @"^[\w.\-]+$"))) throw new InvalidDataException("Повреждён список файлов Steam.");
        foreach (var package in packages)
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, PackageCdn + package);
            using var response = await client.SendAsync(request, ct);
            if (response.StatusCode != HttpStatusCode.OK) throw new InvalidDataException("Valve больше не раздаёт файлы этой версии Steam.");
        }
    }

    private static async Task<string> GetStringAsync(HttpClient client, string url, CancellationToken ct)
    {
        // The Wayback Machine briefly refuses connections under load; retry a few times before giving up.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd("FST/0.1");
                using var response = await client.SendAsync(request, ct);
                response.EnsureSuccessStatusCode();
                var bytes = await response.Content.ReadAsByteArrayAsync(ct);
                // Wayback replays the original gzip body of Valve's response even when the client did not ask for it.
                if (bytes is [0x1F, 0x8B, ..])
                {
                    using var gzip = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
                    using var plain = new MemoryStream();
                    await gzip.CopyToAsync(plain, ct);
                    bytes = plain.ToArray();
                }
                return Encoding.UTF8.GetString(bytes);
            }
            catch (HttpRequestException) when (attempt < 4) { await Task.Delay(TimeSpan.FromSeconds(5 * attempt), ct); }
        }
    }

    public static string ConfigPath(string steamRoot) => Path.Combine(steamRoot, "steam.cfg");
    public static bool UpdatesBlocked(string steamRoot) => File.Exists(ConfigPath(steamRoot)) && Regex.IsMatch(File.ReadAllText(ConfigPath(steamRoot)), @"(?im)^\s*BootStrapperInhibitAll\s*=\s*enable");
    public static bool UpdatesBlockedByFst(string steamRoot) => File.Exists(ConfigPath(steamRoot)) && File.ReadAllText(ConfigPath(steamRoot)) == UpdateBlock;
    /// <summary>Writes FST's steam.cfg. A steam.cfg written by someone else is left as is.</summary>
    public static bool BlockUpdates(string steamRoot)
    {
        if (File.Exists(ConfigPath(steamRoot))) return UpdatesBlockedByFst(steamRoot);
        File.WriteAllText(ConfigPath(steamRoot), UpdateBlock);
        return true;
    }
    /// <summary>Removes steam.cfg only if FST wrote it.</summary>
    public static bool AllowUpdates(string steamRoot)
    {
        if (!UpdatesBlockedByFst(steamRoot)) return false;
        File.Delete(ConfigPath(steamRoot));
        return true;
    }
}

/// <summary>
/// Loopback package server for steam.exe -overridepackageurl: serves the pinned client manifest and streams every other
/// file from Valve's CDN. Packages are named by their hash, so the Steam bootstrapper still verifies what it receives.
/// </summary>
public sealed class PackageMirror : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly byte[] manifest;
    private readonly HttpClient client;
    private readonly string upstream;
    private readonly Task loop;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Task, byte> active = new();
    private int requests;
    public int Requests => Volatile.Read(ref requests);
    public string Url => "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/";

    public PackageMirror(string manifest, HttpClient client, string upstream = SteamDowngrade.PackageCdn)
    {
        this.manifest = Encoding.UTF8.GetBytes(manifest);
        this.client = client;
        this.upstream = upstream;
        listener.Start();
        loop = AcceptAsync();
    }

    private async Task AcceptAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient connection;
            try { connection = await listener.AcceptTcpClientAsync(stop.Token); }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException) { break; }
            var task = ServeAsync(connection);
            active[task] = 0;
            _ = task.ContinueWith(t => active.TryRemove(t, out _), TaskScheduler.Default);
        }
    }

    private async Task ServeAsync(TcpClient connection)
    {
        using (connection)
        {
            try
            {
                var stream = connection.GetStream();
                string head;
                using (var headBudget = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
                {
                    headBudget.CancelAfter(TimeSpan.FromSeconds(30));
                    head = await ReadHeadAsync(stream, headBudget.Token);
                }
                var parts = head.Split(' ', 3);
                if (parts.Length < 3) return;
                Interlocked.Increment(ref requests);
                var method = parts[0];
                // Steam asks for files right under the override URL; anything with path segments is refused.
                var name = Uri.UnescapeDataString(parts[1].Split('?')[0]).TrimStart('/');
                if (method is not "GET" and not "HEAD" || !Regex.IsMatch(name, @"^[\w.\-]+$")) { await WriteHeadAsync(stream, 404, "Not Found", 0); return; }
                if (name == SteamDowngrade.ManifestName)
                {
                    await WriteHeadAsync(stream, 200, "OK", manifest.Length);
                    if (method == "GET") await stream.WriteAsync(manifest, stop.Token);
                    return;
                }
                using var request = new HttpRequestMessage(method == "HEAD" ? HttpMethod.Head : HttpMethod.Get, upstream + name);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stop.Token);
                await WriteHeadAsync(stream, (int)response.StatusCode, response.ReasonPhrase ?? "", response.Content.Headers.ContentLength);
                if (method == "GET") await (await response.Content.ReadAsStreamAsync(stop.Token)).CopyToAsync(stream, stop.Token);
            }
            // A connection handler runs unobserved: any failure only ends this one transfer, and Steam retries or reports it.
            catch (Exception) { }
        }
    }

    private static async Task<string> ReadHeadAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new List<byte>();
        var one = new byte[1];
        while (buffer.Count < 16 * 1024)
        {
            if (await stream.ReadAsync(one, ct) == 0) break;
            buffer.Add(one[0]);
            if (buffer.Count >= 4 && buffer[^4] == '\r' && buffer[^3] == '\n' && buffer[^2] == '\r' && buffer[^1] == '\n') return Encoding.ASCII.GetString(buffer.ToArray());
        }
        throw new InvalidDataException("Malformed HTTP request.");
    }

    private Task WriteHeadAsync(NetworkStream stream, int status, string reason, long? length)
    {
        var head = "HTTP/1.1 " + status + " " + reason + "\r\nContent-Type: application/octet-stream\r\n" + (length is { } l ? "Content-Length: " + l + "\r\n" : "") + "Connection: close\r\n\r\n";
        return stream.WriteAsync(Encoding.ASCII.GetBytes(head), stop.Token).AsTask();
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        listener.Stop();
        try { await loop; } catch { }
        // Let cancelled transfers finish before their token source and the shared HttpClient go away.
        try { await Task.WhenAll(active.Keys); } catch { }
        stop.Dispose();
    }
}
