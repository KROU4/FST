using Fst.Core;
namespace Fst.App;

internal static class Shell
{
    public static void OpenLogs(){var path=Path.Combine(Settings.DataRoot,"fst.log");if(!File.Exists(path))File.WriteAllText(path,"");var start=new System.Diagnostics.ProcessStartInfo("notepad.exe"){UseShellExecute=false};start.ArgumentList.Add(path);System.Diagnostics.Process.Start(start);}
}
internal sealed class LibraryForm : FstWindow
{
    private readonly Settings settings;
    private readonly ListBox list=new(){BorderStyle=BorderStyle.None,BackColor=UiTheme.Surface,ForeColor=UiTheme.Ink,Font=UiTheme.Font(11),DrawMode=DrawMode.OwnerDrawFixed,ItemHeight=42,IntegralHeight=false,SelectionMode=SelectionMode.MultiExtended};
    private readonly Label status;
    private readonly FstInput search;
    private LuaEntry[] entries=[];
    private readonly Dictionary<string,string> names=new();
    private readonly CancellationTokenSource lifetime=new();
    public LibraryForm(Settings settings):base("fst / добавленные lua",new Size(660,540))
    {
        this.settings=settings;
        Controls.Add(UiTheme.Label("Поиск по AppID или названию",new Rectangle(22,64,618,22),9,muted:true));
        search=UiTheme.Input("",new Rectangle(20,91,406,40));Controls.Add(search);
        var selectAll=UiTheme.Button("Выбрать все",new Rectangle(440,91,200,40));selectAll.Click+=(_,_)=>{for(var i=0;i<list.Items.Count;i++)list.SetSelected(i,true);};Controls.Add(selectAll);
        var card=UiTheme.Card(new Rectangle(20,145,620,293));Controls.Add(card);list.SetBounds(1,1,618,291);card.Controls.Add(list);
        list.HandleCreated+=(_,_)=>SetWindowTheme(list.Handle,"DarkMode_Explorer",null);
        list.DrawItem+=(_,e)=>{if(e.Index<0)return;using var bg=new SolidBrush((e.State&DrawItemState.Selected)!=0?UiTheme.Hover:UiTheme.Surface);e.Graphics.FillRectangle(bg,e.Bounds);var entry=(LuaEntry)list.Items[e.Index];TextRenderer.DrawText(e.Graphics,names.GetValueOrDefault(entry.AppId,entry.AppId+".lua"),UiTheme.Font(11),new Rectangle(e.Bounds.X+15,e.Bounds.Y,e.Bounds.Width-160,e.Bounds.Height),UiTheme.Ink,TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);TextRenderer.DrawText(e.Graphics,entry.AppId,UiTheme.Font(9),new Rectangle(e.Bounds.Right-140,e.Bounds.Y,120,e.Bounds.Height),UiTheme.Muted,TextFormatFlags.VerticalCenter|TextFormatFlags.Right);};
        status=UiTheme.Label("",new Rectangle(22,458,385,60),9,muted:true);Controls.Add(status);
        search.TextChanged+=(_,_)=>ApplyFilter();
        var remove=UiTheme.Button("Удалить выбранные",new Rectangle(428,458,212,38));Controls.Add(remove);
        remove.Click+=(_,_)=>
        {
            if(JobService.InstallationActive){status.Text=L.T("Дождитесь окончания установки.");return;}
            var selected=list.SelectedItems.Cast<LuaEntry>().ToArray();if(selected.Length==0)return;
            var removed=0;var failed=0;foreach(var entry in selected){try{LuaCatalog.Remove(entry.AppId,settings.LuaDirectories(),Path.Combine(Settings.DataRoot,"backups"));removed++;}catch{failed++;}}
            Reload();status.Text=failed==0?L.F("Удалено: {0}. Перезапустите Steam.",removed):L.F("Удалено: {0}; ошибок: {1}.",removed,failed);JobService.Log("Удалено Lua: {0}",removed);
        };
        list.SelectedIndexChanged+=(_,_)=>UpdateCount();
        list.KeyDown+=(_,e)=>{if(e.Control&&e.KeyCode==Keys.A){for(var i=0;i<list.Items.Count;i++)list.SetSelected(i,true);e.Handled=true;e.SuppressKeyPress=true;}};
        L.Changed+=UpdateCount;FormClosed+=(_,_)=>L.Changed-=UpdateCount;
        Reload();
        Shown+=async(_,_)=>await LoadNamesAsync();FormClosed+=(_,_)=>lifetime.Cancel();
    }
    private void Reload(){entries=LuaCatalog.List(settings.LuaDirectories()).ToArray();ApplyFilter();}
    private void ApplyFilter()
    {
        var selected=list.SelectedItems.Cast<LuaEntry>().Select(e=>e.AppId).ToHashSet();
        var terms=search.Text.Split(' ',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
        list.BeginUpdate();
        try
        {
            list.Items.Clear();
            foreach(var entry in entries)
                if(terms.All(term=>entry.AppId.Contains(term,StringComparison.OrdinalIgnoreCase)||names.GetValueOrDefault(entry.AppId,"").Contains(term,StringComparison.OrdinalIgnoreCase)))
                {var index=list.Items.Add(entry);if(selected.Contains(entry.AppId))list.SetSelected(index,true);}
        }
        finally{list.EndUpdate();}
        UpdateCount();
    }
    private void UpdateCount()=>status.Text=list.Items.Count==0?L.T("Lua не найдены"):L.F("Добавлено: {0} · выбрано: {1}",list.Items.Count,list.SelectedItems.Count);
    internal void SelectForPreview()
    {
        if(entries.Length>0)
        {
            var entry=entries[0];search.Text=entry.AppId;
            if(!list.Items.Cast<LuaEntry>().Any(e=>e.AppId==entry.AppId))throw new InvalidOperationException("AppID search failed.");
            if(names.TryGetValue(entry.AppId,out var name))
            {
                search.Text=entry.AppId+" "+name;
                if(list.Items.Count!=1||((LuaEntry)list.Items[0]).AppId!=entry.AppId)throw new InvalidOperationException("Combined search failed.");
            }
            search.Text="__fst_no_such_game__";if(list.Items.Count!=0)throw new InvalidOperationException("Empty search failed.");
            search.Text="";if(list.Items.Count!=entries.Length)throw new InvalidOperationException("Search reset failed.");
        }
        if(list.Items.Count>=2){list.SetSelected(0,true);list.SetSelected(1,true);if(list.SelectedItems.Count!=2)throw new InvalidOperationException("Multi-selection failed.");}
    }
    private async Task LoadNamesAsync()
    {
        using var http=new HttpClient{Timeout=TimeSpan.FromSeconds(8),MaxResponseContentBufferSize=4*1024*1024};using var gate=new SemaphoreSlim(3);
        await Task.WhenAll(entries.Select(async entry=>
        {
            try
            {
                await gate.WaitAsync(lifetime.Token);
                try
                {
                    var content=await http.GetStringAsync("https://store.steampowered.com/api/appdetails?appids="+entry.AppId+"&filters=basic&l="+(L.Language=="en"?"english":"russian"),lifetime.Token);
                    using var json=System.Text.Json.JsonDocument.Parse(content);
                    if(json.RootElement.TryGetProperty(entry.AppId,out var app)&&app.TryGetProperty("data",out var data)&&data.TryGetProperty("name",out var name))
                        if(!IsDisposed){names[entry.AppId]=name.GetString()??entry.AppId;if(string.IsNullOrWhiteSpace(search.Text))list.Invalidate();else ApplyFilter();}
                }finally{gate.Release();}
            }catch{}
        }));
    }
    [System.Runtime.InteropServices.DllImport("uxtheme.dll",CharSet=System.Runtime.InteropServices.CharSet.Unicode)] private static extern int SetWindowTheme(IntPtr window,string theme,string? classes);
}
