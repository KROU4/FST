using System.Diagnostics;
namespace Fst.App;
internal sealed class ConnectForm : FstWindow
{
    private readonly RyuuSession session;
    private readonly Label title, detail;
    private readonly FstButton submit, disconnect;
    private readonly CancellationTokenSource lifetime = new();
    private bool busy;
    private string lastStatus = "";
    public ConnectForm(RyuuSession session, bool automatic = true) : base("fst / подключение", new Size(600,280))
    {
        this.session = session;
        var card = UiTheme.Card(new Rectangle(20,68,560,190)); Controls.Add(card);
        title = UiTheme.Label("Ryuu", new Rectangle(18,17,520,32),19,bold:true); card.Controls.Add(title);
        detail = UiTheme.Label("Войдите через Discord",new Rectangle(18,58,520,48),10,muted:true);card.Controls.Add(detail);
        submit = UiTheme.Button("Подключить",new Rectangle(18,127,250,40),true);
        disconnect = UiTheme.Button("Отключить",new Rectangle(282,127,258,40));
        card.Controls.Add(submit);card.Controls.Add(disconnect);
        submit.Click += async (_,_) => await AutoConnectAsync();
        disconnect.Click += async (_,_) => await RunAsync(session.DisconnectAsync);
        FormClosed += (_,_) => lifetime.Cancel(); RefreshStatus(true);
        if(automatic)Shown+=async(_,_)=>{if(!session.Connected)await AutoConnectAsync();};
    }
    internal async Task VerifyReconnectAsync()
    {
        await RunAsync(session.DisconnectAsync);
        if(session.Connected||SecretStore.Read().Length!=0)throw new InvalidOperationException("Disconnect did not clear credentials.");
        await AutoConnectAsync();
        if(!session.Connected||SecretStore.Read().Length==0)throw new InvalidOperationException("Automatic reconnect failed.");
        RefreshStatus(true);
    }
    private async Task AutoConnectAsync()
    {
        await RunAsync(async () =>
        {
            detail.Text=L.T("Ищем сохранённое подключение…");
            if(await session.DiscoverAsync(searchLocal:false,ct:lifetime.Token))return;
            Process.Start(new ProcessStartInfo("https://generator.ryuu.lol/login?next=/"){UseShellExecute=true});
            detail.Text=L.T("Войдите через Discord в браузере");
            var until=DateTime.UtcNow.AddMinutes(3);
            while(DateTime.UtcNow<until)
            {
                await Task.Delay(TimeSpan.FromSeconds(4),lifetime.Token);
                if(await session.DiscoverAsync(searchLocal:false,ct:lifetime.Token))return;
            }
            throw new TimeoutException();
        });
    }
    private async Task RunAsync(Func<Task> action)
    {
        if(busy)return;busy=true;submit.Enabled=false;disconnect.Enabled=false;
        try { await action();if(!IsDisposed){lastStatus="";RefreshStatus(true);} }
        catch(Exception e){if(!IsDisposed){detail.Text=e is TimeoutException?L.T("Завершите вход в браузере\nи нажмите «Подключить» снова."):RyuuSession.SafeError(e);}}
        finally {busy=false;if(!IsDisposed){submit.Enabled=true;disconnect.Enabled=session.Connected;}}
    }
    public void RefreshStatus(bool force=false)
    {
        if(busy&&!force)return;
        if(!force&&lastStatus==session.Status)return;lastStatus=session.Status;
        title.Text=session.Connected?(string.IsNullOrWhiteSpace(session.Username)?L.T("Ryuu подключён"):session.Username):L.T("Подключить Ryuu");
        title.SetBounds(18,session.Connected?40:17,524,32);
        title.TextAlign=session.Connected?ContentAlignment.MiddleCenter:ContentAlignment.TopLeft;
        detail.Text=session.Connected?"":L.T("Войдите через Discord");
        submit.Text=L.T(session.Connected?"Проверить подключение":"Подключить");disconnect.Enabled=session.Connected;
    }
}
