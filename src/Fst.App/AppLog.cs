using System.Text.Json;
namespace Fst.App;

internal static class AppLog
{
    private sealed record Entry(DateTimeOffset Time,string Template,string[] Args);
    private static readonly object gate=new();
    private static bool initialized;
    private static string Events=>Path.Combine(Settings.DataRoot,"log-events.jsonl");
    private static string Output=>Path.Combine(Settings.DataRoot,"fst.log");
    public static void Write(string template,params object?[] args)
    {
        lock(gate)
        {
            Initialize();var entry=new Entry(DateTimeOffset.Now,template,args.Select(a=>a?.ToString()??"").ToArray());
            File.AppendAllText(Events,JsonSerializer.Serialize(entry)+"\n");File.AppendAllText(Output,Render(entry)+"\n");
        }
    }
    private static string Render(Entry entry)=>$"[{entry.Time:O}] "+L.F(entry.Template,entry.Args.Cast<object?>().ToArray());
    private static void Initialize()
    {
        if(initialized)return;Directory.CreateDirectory(Settings.DataRoot);
        if(!File.Exists(Events)&&File.Exists(Output))File.Move(Output,Path.Combine(Settings.DataRoot,"logs-previous-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff")+".txt"));
        initialized=true;L.Changed+=Rebuild;
    }
    public static void Rebuild()
    {
        lock(gate)
        {
            Initialize();if(!File.Exists(Events))return;
            var lines=new List<string>();foreach(var line in File.ReadLines(Events)){try{if(JsonSerializer.Deserialize<Entry>(line) is {} entry)lines.Add(Render(entry));}catch(JsonException){}}
            var temp=Output+".tmp";File.WriteAllLines(temp,lines);File.Move(temp,Output,true);
        }
    }
}
