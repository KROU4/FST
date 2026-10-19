using System.Collections.Concurrent;
using System.Text.Json;
using Fst.Core;

namespace Fst.App;

public sealed record Job(string State, string Message);
public sealed class JobService(Settings settings, RyuuSession session, Action<string> notify, Action connect)
{
    private readonly ConcurrentDictionary<string, Job> jobs = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private static int installing;
    public static bool InstallationActive=>Volatile.Read(ref installing)!=0;
    public bool IsBusy => gate.CurrentCount == 0;
    public object Dispatch(string method, JsonElement args) => method switch
    {
        "status" => new { installed = Installed(), busy = gate.CurrentCount == 0, connected = session.Connected, authStatus = session.Status, restartAfterInstall = settings.RestartAfterInstall, language=L.Language },
        "connect" => OpenConnection(),
        "install" => Start(args.GetProperty("appid").GetString()!),
        "job" => jobs.TryGetValue(args.GetProperty("jobId").GetString()!, out var job) ? job : throw new InvalidOperationException("Задание не найдено."),
        _ => throw new InvalidOperationException("Неизвестный метод FST.")
    };
    private object OpenConnection() { connect(); return new { opened = true }; }
    private string[] Installed() => new[] { settings.LuaDirectory }.Concat(settings.ActiveLuaDirectories()).Where(Directory.Exists).SelectMany(d => Directory.EnumerateFiles(d, "*.lua")).Select(Path.GetFileNameWithoutExtension).OfType<string>().Distinct().ToArray();
    private object Start(string appId)
    {
        ManifestInstaller.ValidateAppId(appId);
        var authKey = session.RequireKey();
        if (!gate.Wait(0)) throw new InvalidOperationException("Установка уже идёт. Дождитесь завершения.");
        Interlocked.Exchange(ref installing,1);
        var id = Guid.NewGuid().ToString("N");
        jobs[id] = new("downloading", L.T("Скачивание из Ryuu…"));
        _ = Task.Run(async () =>
        {
            var archive = Path.Combine(Settings.DataRoot, id + ".zip");
            try
            {
                using var client = new RyuuClient();
                await client.DownloadAsync(appId, authKey, archive);
                Log("Игра {0} скачана из Ryuu.",appId);
                jobs[id] = new("installing", L.T("Установка Lua и манифестов…"));
                var result = ManifestInstaller.Install(archive, appId, settings.Plan(), Path.Combine(Settings.DataRoot, "backups"));
                Log("Установлена игра {0}: Lua — {1}, манифестов — {2}. Копия: {3}",appId,result.LuaFiles,result.ManifestFiles,result.BackupDirectory);
                if (settings.RestartAfterInstall)
                {
                    jobs[id] = new("restarting", L.T("Установлено. Steam перезапускается…"));
                    await Task.Delay(1800);
                    await SteamControl.RestartAsync(settings);
                }
                jobs[id] = new("complete", L.T("Добавлено в библиотеку"));
                notify(L.F("Игра {0} добавлена.",appId));
            }
            catch (Exception e)
            {
                if (e is RyuuAuthException) session.Reject(authKey);
                var message = e is HttpRequestException or OperationCanceledException ? RyuuSession.SafeError(e) : L.T(e.Message);
                jobs[id] = new("failed", message); Log("Ошибка установки: {0}", message); notify(message);
            }
            finally { try { File.Delete(archive); } catch { } Interlocked.Exchange(ref installing,0);gate.Release(); }
        });
        return new { jobId = id };
    }
    public static void Log(string template,params object?[] args)=>AppLog.Write(template,args);
}
