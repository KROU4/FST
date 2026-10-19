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
// Lua loader: stock Steam has none, a tampered archive never reaches the Steam folder, foreign DLLs are kept.
var loaderSteam=Path.Combine(testRoot,"loader-steam");Directory.CreateDirectory(loaderSteam);File.WriteAllText(Path.Combine(loaderSteam,"steam.exe"),"");
Check(LuaLoader.Find(loaderSteam)==null&&LuaLoader.Find("")==null,"stock Steam has no Lua loader");
string LoaderZip(string name,char fill)=>Zip(name,LuaLoader.Files.Select(f=>(f,Text("MZ"+new string(fill,100)))).ToArray());
string Sha(string path)=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
Reject(()=>LuaLoader.Install(LoaderZip("fake-loader",'x'),loaderSteam,Path.Combine(testRoot,"unused-state.json")),"loader archive with wrong checksum rejected");
// Upgrade only what FST installed and nobody changed since.
var upgradeSteam=Path.Combine(testRoot,"upgrade-steam");Directory.CreateDirectory(upgradeSteam);File.WriteAllText(Path.Combine(upgradeSteam,"steam.exe"),"");
var upgradeState=Path.Combine(testRoot,"upgrade-state.json");
var v1=LoaderZip("loader-v1",'a');Check(LuaLoader.Install(v1,upgradeSteam,upgradeState,Sha(v1),"1.0").Length==3,"loader installed into clean Steam");
Check(LuaLoader.NeedsInstall(upgradeSteam,upgradeState),"older FST-installed loader scheduled for upgrade");
var v2=LoaderZip("loader-v2",'b');Check(LuaLoader.Install(v2,upgradeSteam,upgradeState,Sha(v2),"2.0").Length==3&&File.ReadAllText(Path.Combine(upgradeSteam,"OpenSteamTool.dll")).EndsWith("b"),"FST-installed loader upgraded");
Check(!Directory.EnumerateFiles(upgradeSteam,"*.fst-*").Any(),"upgrade leaves no replaced or temporary files");
Check(LuaLoader.Install(v2,upgradeSteam,upgradeState,Sha(v2),"2.0").Length==0,"same loader version is not reinstalled");
File.WriteAllText(Path.Combine(upgradeSteam,"dwmapi.dll"),"MZ user build");
var v3=LoaderZip("loader-v3",'c');var upgraded=LuaLoader.Install(v3,upgradeSteam,upgradeState,Sha(v3),"3.0");
Check(upgraded.Length==2&&File.ReadAllText(Path.Combine(upgradeSteam,"dwmapi.dll"))=="MZ user build","loader file changed by the user is never replaced");
File.Delete(Path.Combine(upgradeSteam,"OpenSteamTool.dll"));
Check(!LuaLoader.NeedsInstall(upgradeSteam,upgradeState),"loader with a user-provided DLL is not reinstalled");
File.Delete(Path.Combine(upgradeSteam,"dwmapi.dll"));
Check(LuaLoader.NeedsInstall(upgradeSteam,upgradeState),"missing FST-installed loader file detected despite remaining proxies");
Check(LuaLoader.Install(v3,upgradeSteam,upgradeState,Sha(v3),"3.0").Contains(Path.Combine(upgradeSteam,"OpenSteamTool.dll"))&&File.ReadAllText(upgradeState).Contains("OpenSteamTool.dll"),"missing loader file restored");
File.Delete(Path.Combine(upgradeSteam,"OpenSteamTool.dll"));File.Delete(Path.Combine(upgradeSteam,"dwmapi.dll"));File.Delete(Path.Combine(upgradeSteam,"xinput1_4.dll"));File.WriteAllText(Path.Combine(upgradeSteam,"hid.dll"),"MZ SteamTools");
Check(!LuaLoader.NeedsInstall(upgradeSteam,upgradeState),"FST does not install OpenSteamTool next to another loader");
var foreignSteam=Path.Combine(testRoot,"foreign-steam");Directory.CreateDirectory(foreignSteam);File.WriteAllText(Path.Combine(foreignSteam,"steam.exe"),"");File.WriteAllText(Path.Combine(foreignSteam,"xinput1_4.dll"),"MZ other loader");
Check(!LuaLoader.NeedsInstall(foreignSteam,Path.Combine(testRoot,"foreign-state.json")),"another loader is used as is");
LuaLoader.Install(v1,foreignSteam,Path.Combine(testRoot,"foreign-state.json"),Sha(v1),"1.0");
Check(File.ReadAllText(Path.Combine(foreignSteam,"xinput1_4.dll"))=="MZ other loader","foreign loader DLL preserved");
// Signature tracker: 404 means the Steam build is not supported yet; network errors are not reported.
var trackerSteam=Path.Combine(testRoot,"tracker-steam");Directory.CreateDirectory(trackerSteam);
foreach(var f in new[]{"OpenSteamTool.dll","steamclient64.dll","steamui.dll"})File.WriteAllText(Path.Combine(trackerSteam,f),f);
Check((await LuaLoader.UnsupportedSteamFilesAsync(trackerSteam,new HttpClient(new FakeHandler(r=>new HttpResponseMessage(r.RequestUri!.AbsolutePath.Contains("/steamclient/")?HttpStatusCode.NotFound:HttpStatusCode.OK))))).SequenceEqual(["steamclient64.dll"]),"unsupported Steam build detected");
Check((await LuaLoader.UnsupportedSteamFilesAsync(trackerSteam,new HttpClient(new FakeHandler(_=>throw new HttpRequestException("offline"))))).Length==0,"offline tracker is not reported as unsupported");
var cachedPattern=Path.Combine(trackerSteam,"opensteamtool","pattern","steamclient",Sha(Path.Combine(trackerSteam,"steamclient64.dll")).ToLowerInvariant()+".toml");Directory.CreateDirectory(Path.GetDirectoryName(cachedPattern)!);File.WriteAllText(cachedPattern,"");
Check((await LuaLoader.UnsupportedSteamFilesAsync(trackerSteam,new HttpClient(new FakeHandler(r=>new HttpResponseMessage(r.RequestUri!.AbsolutePath.Contains("pattern/steamclient/")?HttpStatusCode.NotFound:HttpStatusCode.OK))))).Length==0,"cached signatures count as supported");
Check((await LuaLoader.UnsupportedSteamFilesAsync(trackerSteam,new HttpClient(new FakeHandler(r=>new HttpResponseMessage(r.RequestUri!.AbsolutePath.Contains("/ipc/")?HttpStatusCode.NotFound:HttpStatusCode.OK))))).SequenceEqual(["steamclient64.dll"]),"missing IPC spec detected");
Check((await LuaLoader.UnsupportedSteamFilesAsync(loaderSteam)).Length==0,"tracker is not queried without OpenSteamTool");
var loaderState=Path.Combine(testRoot,"loader-state.json");
try{await LuaLoader.DownloadAndInstallAsync(loaderSteam,Path.Combine(testRoot,"loader-temp"),loaderState,new HttpClient(new FakeHandler(request=>
{
    Check(request.RequestUri!.ToString()==LuaLoader.ArchiveUrl,"loader downloaded from pinned release");
    return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("<html>login</html>")};
})));throw new Exception("Expected loader checksum failure");}
catch(InvalidDataException){Check(LuaLoader.Markers.All(m=>!File.Exists(Path.Combine(loaderSteam,m))),"failed loader download writes nothing into Steam");}
Check(!Directory.EnumerateFiles(Path.Combine(testRoot,"loader-temp")).Any(),"loader download leaves no temporary archive");
try{await LuaLoader.DownloadAndInstallAsync(Path.Combine(testRoot,"no-steam"),Path.Combine(testRoot,"loader-temp"),loaderState,new HttpClient(new FakeHandler(_=>throw new Exception("Must not download without Steam"))));throw new Exception("Expected missing Steam");}
catch(FileNotFoundException){Check(true,"loader is not downloaded without steam.exe");}
var otherLoader=Path.Combine(testRoot,"other-loader");Directory.CreateDirectory(otherLoader);File.WriteAllText(Path.Combine(otherLoader,"xinput1_4.dll"),"");
Check(LuaLoader.Find(otherLoader)=="xinput1_4.dll","existing proxy loader detected");
if(args.Contains("--live"))
{
    var liveFiles=await LuaLoader.DownloadAndInstallAsync(loaderSteam,Path.Combine(testRoot,"loader-temp"),loaderState);
    Check(liveFiles.Length==3&&LuaLoader.Find(loaderSteam)=="OpenSteamTool.dll","OpenSteamTool release verified and installed in sandbox");
    Check((await LuaLoader.DownloadAndInstallAsync(loaderSteam,Path.Combine(testRoot,"loader-temp"),loaderState)).Length==0&&!LuaLoader.NeedsInstall(loaderSteam,loaderState),"current loader is not reinstalled");
}
Localization.Language="en";
Check(Localization.T(LuaLoader.MissingMessage).StartsWith("No Lua loader"),"missing loader message translated");
Check(Localization.T("Подключить Ryuu")=="Connect Ryuu","English UI translation");
Check(Localization.F("Удалено Lua: {0}",2)=="Lua scripts removed: 2","English structured log translation");
Localization.Language="ru";
Check(Localization.T("Connect Ryuu")=="Подключить Ryuu","runtime language switch translates existing controls");
Console.WriteLine($"{passed} checks passed; sandbox: {testRoot}");

sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(reply(request));
}
