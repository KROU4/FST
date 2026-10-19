namespace Fst.App;

internal sealed class FilesForm : FstWindow
{
    private readonly ListBox entries=new(){BorderStyle=BorderStyle.None,BackColor=UiTheme.Surface,ForeColor=UiTheme.Ink,Font=UiTheme.Font(11),ItemHeight=30,IntegralHeight=false};
    private readonly Label location;
    private string folder=Settings.DataRoot;
    public FilesForm():base("fst / файлы",new Size(660,540))
    {
        location=UiTheme.Label("",new Rectangle(22,68,615,35),9,muted:true);Controls.Add(location);
        var card=UiTheme.Card(new Rectangle(20,108,620,328));Controls.Add(card);entries.SetBounds(1,1,618,326);card.Controls.Add(entries);
        var back=UiTheme.Button("Назад",new Rectangle(20,458,190,38));back.Click+=(_,_)=>{if(folder!=Settings.DataRoot){folder=Path.GetDirectoryName(folder)!;LoadFolder();}};Controls.Add(back);
        var copy=UiTheme.Button("Скопировать путь",new Rectangle(224,458,212,38));copy.Click+=(_,_)=>Clipboard.SetText(folder);Controls.Add(copy);
        var open=UiTheme.Button("Открыть",new Rectangle(450,458,190,38),true);open.Click+=(_,_)=>OpenSelected();Controls.Add(open);entries.DoubleClick+=(_,_)=>OpenSelected();LoadFolder();
    }
    private sealed record FileEntry(string Path,bool Directory){public override string ToString()=>(Directory?"[+]  ":"     ")+System.IO.Path.GetFileName(Path);}
    private void LoadFolder()
    {
        Directory.CreateDirectory(Settings.DataRoot);entries.Items.Clear();location.Text=folder;
        try{foreach(var item in new DirectoryInfo(folder).EnumerateFileSystemInfos().Where(i=>(i.Attributes&FileAttributes.ReparsePoint)==0).OrderBy(i=>i is not DirectoryInfo).ThenBy(i=>i.Name,StringComparer.OrdinalIgnoreCase))entries.Items.Add(new FileEntry(item.FullName,item is DirectoryInfo));}
        catch{location.Text=L.T("Не удалось прочитать файлы.");}
    }
    private void OpenSelected()
    {
        if(entries.SelectedItem is not FileEntry item)return;
        if(item.Directory){folder=item.Path;LoadFolder();return;}
        if(!new[]{".json",".jsonl",".txt",".log",".lua",".toml",".vdf"}.Contains(Path.GetExtension(item.Path),StringComparer.OrdinalIgnoreCase)){location.Text=L.T("Выберите текстовый файл или папку.");return;}
        try
        {
            using var reader=new StreamReader(item.Path);var buffer=new char[100000];var read=reader.ReadBlock(buffer,0,buffer.Length);
            using var viewer=new FstWindow("fst / "+Path.GetFileName(item.Path),new Size(820,600));
            viewer.Controls.Add(new RichTextBox{Text=new string(buffer,0,read),ReadOnly=true,DetectUrls=false,BorderStyle=BorderStyle.None,BackColor=UiTheme.Surface,ForeColor=UiTheme.Ink,Font=UiTheme.Font(10),Bounds=new Rectangle(20,68,780,512),WordWrap=false});viewer.ShowDialog(this);
        }catch{location.Text=L.T("Не удалось прочитать файлы.");}
    }
}
