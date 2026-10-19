using System.IO.Compression;
using System.Text;
using Fst.Core;
using System.Net;
using System.Text.Json;

var testRoot = Path.Combine(Path.GetTempPath(), "fst-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testRoot);
var passed = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); passed++; }
void Reject(Action action, string name) { try { action(); } catch (InvalidDataException) { Check(true, name); return; } throw new Exception("Expected rejection: " + name); }
string Zip(string name, params (string, byte[])[] entries)
{
    var path = Path.Combine(testRoot, name + ".zip");
    using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
    foreach (var (entry, bytes) in entries) { using var stream = zip.CreateEntry(entry).Open(); stream.Write(bytes); }
    return path;
}
byte[] Text(string text) => Encoding.UTF8.GetBytes(text);
var manifest = new byte[16]; BitConverter.GetBytes(0x71F617D0u).CopyTo(manifest, 0);
var lua = Text("addappid(440)\naddappid(441,0,\"" + new string('a', 64) + "\")\nsetManifestid(441,\"123\")\n");
var steam = Path.Combine(testRoot, "steam");
var primary = Path.Combine(steam, "config", "lua");
var active = Path.Combine(steam, "config", "stplug-in");
var depot = Path.Combine(steam, "config", "depotcache");
var plan = new InstallPlan(steam, primary, depot, true, true, [active]);
InstallResult Install(string zip) => ManifestInstaller.Install(zip, "440", plan, Path.Combine(testRoot, "backup"));
var valid = Zip("valid", ("root/440.lua", lua), ("root/441_123.manifest", manifest));
var result = Install(valid);
Check(result.LuaFiles == 1 && result.ManifestFiles == 1, "install counts");
Check(File.ReadAllText(Path.Combine(primary, "440.lua")).Contains("-- setManifestid"), "Ryuu auto-update comments");
Check(File.Exists(Path.Combine(active, "440.lua")), "active hook path mirror");
Check(File.Exists(Path.Combine(depot, "441_123.manifest")) && File.Exists(Path.Combine(steam, "depotcache", "441_123.manifest")), "both depotcache copies");
var old = File.ReadAllBytes(Path.Combine(primary, "440.lua"));
Install(valid);
Check(Directory.EnumerateFiles(Path.Combine(testRoot, "backup"), "*.bak", SearchOption.AllDirectories).Any(), "overwrite backups");
Reject(() => Install(Zip("traversal", ("../440.lua", lua))), "zip traversal rejected");
Reject(() => Install(Zip("wrongapp", ("441.lua", lua))), "wrong app rejected");
Reject(() => Install(Zip("malicious", ("440.lua", Text("addappid(440)\nos.execute('bad')")))), "executable Lua rejected");
Reject(() => Install(Zip("longcomment", ("440.lua", Text("addappid(440)\n--[[]] os.execute('bad')")))), "Lua comment escape rejected");
Reject(() => Install(Zip("duplicate", ("a/440.lua", lua), ("b/440.lua", lua))), "duplicate names rejected");
Reject(() => Install(Zip("html", ("440.lua", lua), ("441_123.manifest", Text("<html>login</html>")))), "fake manifest rejected");
Reject(() => ManifestInstaller.ValidateAppId("../../1"), "invalid app id rejected");
Check(old.SequenceEqual(File.ReadAllBytes(Path.Combine(primary, "440.lua"))), "failed validation preserves existing files");
Check(ManifestInstaller.TransformLua("-- setManifestid(1,\"2\")", false) == "setManifestid(1,\"2\")", "pinned version restores manifest call");
// Force a write failure after writing primary Lua, then prove rollback.
var blocked = Path.Combine(testRoot, "blocked"); File.WriteAllText(blocked, "file");
try { ManifestInstaller.Install(valid, "440", plan with { AdditionalLuaDirectories = [Path.Combine(blocked, "child")] }, Path.Combine(testRoot, "backup")); throw new Exception("Expected write failure"); }
catch (IOException) { Check(old.SequenceEqual(File.ReadAllBytes(Path.Combine(primary, "440.lua"))), "transaction rollback on later write failure"); }
if (args.Contains("--live"))
{
    using var client = new PublicManifestClient();
    var zip = Path.Combine(testRoot, "live.zip");
    var live = await client.DownloadAsync("756800", zip, Path.Combine(testRoot, "cache"), allowStale: true);
    Console.WriteLine("LIVE " + live);
    Check(File.Exists(zip), "public anonymous archive download");
    var liveResult = ManifestInstaller.Install(zip, "756800", plan, Path.Combine(testRoot, "backup"));
    Check(liveResult.LuaFiles >= 1, "public archive accepted by installer in sandbox");
    Reject(() => client.DownloadAsync("756800", zip, Path.Combine(testRoot, "cache")).GetAwaiter().GetResult(), "known stale public archive blocked by default");
    var freshZip = Path.Combine(testRoot, "portal.zip");
    var fresh = await client.DownloadAsync("400", freshZip, Path.Combine(testRoot, "cache"));
    Check(fresh.Freshness == "fresh", "current public archive without authentication");
    var freshInstall = ManifestInstaller.Install(freshZip, "400", plan, Path.Combine(testRoot, "backup"));
    Check(freshInstall.ManifestFiles == 10, "all current Portal manifests installed in sandbox");
}
// Authentication failures, limits and transport errors are distinct; no remote response body is logged.
var secret = "test-secret-not-for-logs";
using (var client = new RyuuClient(new HttpClient(new FakeHandler((request) =>
{
    if (request.RequestUri!.AbsolutePath.EndsWith("verify_auth_key")) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"username\":\"tester\",\"premium\":true}") };
    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"auth_key\":\"test-secret-not-for-logs\",\"user\":{\"username\":\"tester\"}}") };
}))))
{
    var login = await client.LoginAsync("one-time-code");
    Check(login.AuthKey == secret && login.Identity.Username == "tester", "login exchange verifies resulting key");
}
foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests, HttpStatusCode.ServiceUnavailable })
{
    using var client = new RyuuClient(new HttpClient(new FakeHandler(_ => new HttpResponseMessage(status))));
    try { await client.VerifyAsync(secret); throw new Exception("Expected HTTP failure"); }
    catch (Exception e) when (e is RyuuAuthException or RyuuLimitException or HttpRequestException)
    {
        Check(status == HttpStatusCode.TooManyRequests ? e is RyuuLimitException : status == HttpStatusCode.ServiceUnavailable ? e is HttpRequestException : e is RyuuAuthException, "Ryuu HTTP state " + (int)status);
        Check(!e.Message.Contains(secret), "auth error does not expose key " + (int)status);
    }
}
using (var client = new RyuuClient(new HttpClient(new FakeHandler(request =>
{
    Check(request.RequestUri!.Query == "?file_type=manifest" && request.Headers.GetValues("X-Auth-Key").Single() == secret, "download key stays in header, not URL");
    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>login</html>", Encoding.UTF8, "text/html") };
}))))
{
    try { await client.DownloadAsync("400", secret, Path.Combine(testRoot, "fake-download.zip")); throw new Exception("Expected HTML rejection"); }
    catch (InvalidDataException) { Check(true, "Ryuu login HTML rejected before installation"); }
}
if (args.Contains("--ryuu-live"))
{
    using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "RyuuManifestTool", "config.json")));
    var savedKey = config.RootElement.GetProperty("auth_key").GetString()!;
    using var client = new RyuuClient();
    var login = await client.LoginAsync(savedKey);
    Check(!string.IsNullOrEmpty(login.Identity.Username), "real Ryuu login exchange and key verification");
    var archive = Path.Combine(testRoot, "ryuu-3219030.zip");
    await client.DownloadAsync("3219030", login.AuthKey, archive);
    var downloaded = ManifestInstaller.Install(archive, "3219030", plan, Path.Combine(testRoot, "backup"));
    Check(downloaded.LuaFiles > 0 && downloaded.ManifestFiles > 0, "real Ryuu 3219030 download and sandbox installation");
}
using (var client = new RyuuClient(new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)))))
    Check(await client.FindSessionKeyAsync() == null, "no Ryuu browser session is a normal disconnected state");
using (var client = new RyuuClient(new HttpClient(new FakeHandler(request =>
{
    Check(request.RequestUri!.AbsolutePath == "/api/check_session" && request.RequestUri.Query.Length == 0, "session discovery sends no key in URL");
    return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("{\"auth_key\":\"test-session-key\"}")};
}))))
    Check(await client.FindSessionKeyAsync() == "test-session-key", "browser session key discovered automatically");
if(args.Contains("--ryuu-session"))
{
    using var client=new RyuuClient();var discovered=await client.FindSessionKeyAsync();
    Check(!string.IsNullOrWhiteSpace(discovered),"real Ryuu browser session discovered without manual key");
    var identity=await client.VerifyAsync(discovered!);Check(!string.IsNullOrWhiteSpace(identity.Username),"discovered session key verified by server");
}
Check(LuaCatalog.List(new[]{primary,active}).Single(e=>e.AppId=="440").Paths.Length==2,"Lua catalog groups mirrored copies");
Reject(()=>LuaCatalog.Remove("../440",new[]{primary,active},Path.Combine(testRoot,"backup")),"Lua removal rejects arbitrary paths");
LuaCatalog.Remove("440",new[]{primary,active},Path.Combine(testRoot,"backup"));
Check(!File.Exists(Path.Combine(primary,"440.lua"))&&!File.Exists(Path.Combine(active,"440.lua")),"Lua removal deletes both configured copies");
Check(File.Exists(Path.Combine(depot,"441_123.manifest")),"Lua removal preserves depot manifests");
Check(Directory.EnumerateDirectories(Path.Combine(testRoot,"backup"),"removed-440-*").Any(),"Lua removal preserves recovery backup");
Localization.Language="en";
Check(Localization.T("Подключить Ryuu")=="Connect Ryuu","English UI translation");
Check(Localization.F("Удалено Lua: {0}",2)=="Lua scripts removed: 2","English structured log translation");
Localization.Language="ru";
Check(Localization.T("Connect Ryuu")=="Подключить Ryuu","runtime language switch translates existing controls");
Console.WriteLine($"{passed} checks passed; sandbox: {testRoot}");

sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(reply(request));
}
