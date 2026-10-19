namespace Fst.Core;

public sealed record LuaEntry(string AppId, string[] Paths);
public static class LuaCatalog
{
    public static IReadOnlyList<LuaEntry> List(IEnumerable<string> directories) => directories.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists)
        .SelectMany(d=>Directory.EnumerateFiles(d,"*.lua",SearchOption.TopDirectoryOnly)).Where(p=>uint.TryParse(Path.GetFileNameWithoutExtension(p),out var id)&&id>0)
        .GroupBy(Path.GetFileNameWithoutExtension,StringComparer.OrdinalIgnoreCase).Select(g=>new LuaEntry(g.Key!,g.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray())).OrderBy(e=>ulong.Parse(e.AppId)).ToArray();
    public static void Remove(string appId,IEnumerable<string> directories,string backupRoot)
    {
        ManifestInstaller.ValidateAppId(appId);
        var entry=List(directories).SingleOrDefault(e=>e.AppId==appId)??throw new FileNotFoundException("Lua уже удалён.");
        foreach(var path in entry.Paths)
            for(string? p=path;p!=null;p=Path.GetDirectoryName(p))
                if((File.Exists(p)||Directory.Exists(p))&&(File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)throw new IOException("Путь Lua содержит ссылку.");
        var backup=Path.Combine(backupRoot,"removed-"+appId+"-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(backup);
        var copies=entry.Paths.Select((p,i)=>(Target:p,Backup:Path.Combine(backup,i+".lua"))).ToArray();
        foreach(var c in copies)File.Copy(c.Target,c.Backup);
        File.WriteAllText(Path.Combine(backup,"restore.json"),System.Text.Json.JsonSerializer.Serialize(copies.Select(c=>new{target=c.Target,backup=c.Backup})));
        try{foreach(var c in copies)File.Delete(c.Target);}
        catch{foreach(var c in copies)File.Copy(c.Backup,c.Target,true);throw;}
    }
}
