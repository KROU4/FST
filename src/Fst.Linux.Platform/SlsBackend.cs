using System.Text.Json;
using System.Text.RegularExpressions;
using Fst.Core;
using YamlDotNet.RepresentationModel;

namespace Fst.App;

public static class SlsBackend
{
    private sealed record Override(string Value,string? Previous);
    private sealed record Owned(string[] Apps,Dictionary<string,Override> Manifests,bool ManagedUpdates=false,string? PreviousUpdates=null);
    private static string Ownership=>Path.Combine(Settings.DataRoot,"sls-owned.json");
    public static void RequireReady(Settings settings)
    {
        if(!File.Exists(settings.SlsLibrary))throw new IOException(L.T("Установите SLSsteam и выберите SLSsteam.so в каталогах."));
        if(!File.Exists(settings.LumaLibrary))throw new IOException(L.T("Установите lumalinux и выберите liblumalinux.so в каталогах."));
        if(!File.Exists(Path.Combine(settings.SteamRoot,"steam.sh")))throw new IOException(L.T("Выберите папку Steam с steam.sh."));
        Parse(settings.SlsConfig);
        LumaBackend.Render(settings);
    }
    private static (YamlStream Stream,YamlMappingNode Root) Parse(string path)
    {
        RejectLinks(path);var stream=new YamlStream();
        if(File.Exists(path))stream.Load(new StringReader(File.ReadAllText(path)));
        if(stream.Documents.Count==0)stream.Add(new YamlDocument(new YamlMappingNode()));
        if(stream.Documents.Count!=1||stream.Documents[0].RootNode is not YamlMappingNode root)throw new InvalidDataException("Invalid SLSsteam config.");
        var apps=Get(root,"AdditionalApps");var manifests=Get(root,"ManifestIds");
        if(!Empty(apps)&&apps is not YamlSequenceNode||!Empty(manifests)&&manifests is not YamlMappingNode)throw new InvalidDataException("Invalid SLSsteam app/manifest configuration.");
        return(stream,root);
    }
    private static YamlNode? Get(YamlMappingNode map,string name)=>map.Children.TryGetValue(new YamlScalarNode(name),out var value)?value:null;
    private static bool Empty(YamlNode? node)=>node==null||node is YamlScalarNode scalar&&string.IsNullOrWhiteSpace(scalar.Value);
    private static string? Scalar(YamlNode? node)=>node is YamlScalarNode scalar?scalar.Value:null;
    public static void Synchronize(Settings settings)
    {
        var(stream,root)=Parse(settings.SlsConfig);
        var previous=File.Exists(Ownership)?JsonSerializer.Deserialize<Owned>(File.ReadAllText(Ownership))??new([],[]):new Owned([],[]);
        var entries=LuaCatalog.List(settings.LuaDirectories());
        var desired=entries.Select(e=>e.AppId).ToHashSet(StringComparer.Ordinal);
        var apps=Get(root,"AdditionalApps") as YamlSequenceNode??new YamlSequenceNode();
        foreach(var node in apps.Children.Where(n=>previous.Apps.Contains(Scalar(n))).ToArray())apps.Children.Remove(node);
        var ownedApps=new List<string>();
        foreach(var id in desired.Order(StringComparer.Ordinal))if(!apps.Children.Any(n=>Scalar(n)==id)){apps.Add(new YamlScalarNode(id));ownedApps.Add(id);}
        root.Children[new YamlScalarNode("AdditionalApps")]=apps;
        if(previous.ManagedUpdates&&Scalar(Get(root,"DisableUpdates")) is "false" or "no")
        {
            if(previous.PreviousUpdates is null)root.Children.Remove(new YamlScalarNode("DisableUpdates"));
            else root.Children[new YamlScalarNode("DisableUpdates")]=new YamlScalarNode(previous.PreviousUpdates);
        }
        var originalUpdates=Scalar(Get(root,"DisableUpdates"));var manageUpdates=desired.Count>0;
        if(manageUpdates)root.Children[new YamlScalarNode("DisableUpdates")]=new YamlScalarNode("false");
        var manifests=Get(root,"ManifestIds") as YamlMappingNode??new YamlMappingNode();
        foreach(var pair in previous.Manifests)
        {
            if(Scalar(Get(manifests,pair.Key))!=pair.Value.Value)continue;
            if(pair.Value.Previous is null)manifests.Children.Remove(new YamlScalarNode(pair.Key));else manifests.Children[new YamlScalarNode(pair.Key)]=new YamlScalarNode(pair.Value.Previous);
        }
        var ownedManifests=new Dictionary<string,Override>();
        foreach(var entry in entries)foreach(var match in Regex.Matches(File.ReadAllText(entry.Paths[0]),"(?im)^\\s*setmanifestid\\s*\\(\\s*([0-9]+)\\s*,\\s*\"([0-9]+)\"" ).Cast<Match>())
        {
            var depot=match.Groups[1].Value;var gid=match.Groups[2].Value;
            var old=Scalar(Get(manifests,depot));
            // Explicit user pins take precedence over FST.
            if(old!=null)continue;
            manifests.Children[new YamlScalarNode(depot)]=new YamlScalarNode(gid);
            ownedManifests[depot]=new(gid,old);
        }
        root.Children[new YamlScalarNode("ManifestIds")]=manifests;
        var writer=new StringWriter(System.Globalization.CultureInfo.InvariantCulture);stream.Save(writer,false);
        var oldConfig=File.Exists(settings.SlsConfig)?File.ReadAllBytes(settings.SlsConfig):null;
        var oldOwnership=File.Exists(Ownership)?File.ReadAllBytes(Ownership):null;
        RejectLinks(settings.LumaKeys);
        var oldKeys=File.Exists(settings.LumaKeys)?File.ReadAllBytes(settings.LumaKeys):null;
        var newKeys=System.Text.Encoding.UTF8.GetBytes(LumaBackend.Render(settings));
        var backup=Path.Combine(Settings.DataRoot,"backups","sls-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(backup);
        if(oldConfig!=null)File.WriteAllBytes(Path.Combine(backup,"config.yaml"),oldConfig);
        if(oldKeys!=null)File.WriteAllBytes(Path.Combine(backup,"keys.txt"),oldKeys);
        try
        {
            Write(settings.SlsConfig,System.Text.Encoding.UTF8.GetBytes(writer.ToString()));
            Write(Ownership,JsonSerializer.SerializeToUtf8Bytes(new Owned(ownedApps.ToArray(),ownedManifests,manageUpdates,originalUpdates)));
            Write(settings.LumaKeys,newKeys);
            if(OperatingSystem.IsLinux())File.SetUnixFileMode(settings.LumaKeys,UnixFileMode.UserRead|UnixFileMode.UserWrite);
        }
        catch{Restore(settings.SlsConfig,oldConfig);Restore(Ownership,oldOwnership);Restore(settings.LumaKeys,oldKeys);throw;}
    }
    public static InstallResult Install(string archive,string appId,Settings settings)
    {
        RequireReady(settings);var result=ManifestInstaller.Install(archive,appId,settings.Plan(),Path.Combine(Settings.DataRoot,"backups"));
        try{Synchronize(settings);return result;}
        catch{Rollback(result.BackupDirectory);throw;}
    }
    public static void Remove(string[] ids,Settings settings)
    {
        var originals=ids.SelectMany(id=>LuaCatalog.List(settings.LuaDirectories()).Where(e=>e.AppId==id).SelectMany(e=>e.Paths)).Distinct().ToDictionary(p=>p,File.ReadAllBytes);
        try
        {
            foreach(var id in ids)LuaCatalog.Remove(id,settings.LuaDirectories(),Path.Combine(Settings.DataRoot,"backups"));
            Synchronize(settings);
        }
        catch{foreach(var pair in originals)Write(pair.Key,pair.Value);throw;}
    }
    private static void Rollback(string backup)
    {
        using var json=JsonDocument.Parse(File.ReadAllText(Path.Combine(backup,"restore.json")));
        foreach(var item in json.RootElement.EnumerateArray().Reverse())
        {var target=item.GetProperty("target").GetString()!;var old=item.GetProperty("backup");Restore(target,old.ValueKind==JsonValueKind.Null?null:File.ReadAllBytes(old.GetString()!));}
    }
    private static void Restore(string path,byte[]? bytes){if(bytes is null){if(File.Exists(path))File.Delete(path);}else Write(path,bytes);}
    private static void RejectLinks(string path)
    {for(string? part=Path.GetFullPath(path);part!=null;part=Path.GetDirectoryName(part))if((File.Exists(part)||Directory.Exists(part))&&(File.GetAttributes(part)&FileAttributes.ReparsePoint)!=0)throw new IOException("Symlink in SLSsteam config path.");}
    private static void Write(string path,byte[] bytes)
    {RejectLinks(path);Directory.CreateDirectory(Path.GetDirectoryName(path)!);var temp=path+".fst-"+Guid.NewGuid().ToString("N");try{File.WriteAllBytes(temp,bytes);File.Move(temp,path,true);}finally{File.Delete(temp);}}
}
