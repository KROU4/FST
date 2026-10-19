using System.Diagnostics;
using System.Text.Json;
using Fst.Core;
namespace Fst.App;

internal sealed class UninstallForm : FstWindow
{
    public UninstallForm(Settings settings, RyuuSession session) : base("fst / удаление", new Size(600, 300))
    {
        Controls.Add(UiTheme.Label("Удалить FST из системы?", new Rectangle(22, 72, 555, 34), 17, bold: true));
        var lua = new FstCheckBox { Text = "Также удалить все добавленные Lua", Bounds = new Rectangle(22, 128, 555, 32), Checked = false };
        Controls.Add(lua);
        var status = UiTheme.Label("", new Rectangle(22, 177, 555, 36), 9, muted: true);
        Controls.Add(status);
        var cancel = UiTheme.Button("Отмена", new Rectangle(22, 232, 265, 40));
        cancel.Click += (_, _) => Close();
        Controls.Add(cancel);
        var remove = UiTheme.Button("Удалить FST", new Rectangle(303, 232, 275, 40), true);
        Controls.Add(remove);
        remove.Click += async (_, _) =>
        {
            if (JobService.InstallationActive)
            {
                status.Text = L.T("Дождитесь окончания установки.");
                return;
            }
            remove.Enabled = false;
            cancel.Enabled = false;
            try
            {
                // Resolve and validate the helper before changing account or Lua state.
                var executable = Path.GetFullPath(Environment.ProcessPath!);
                if (!Path.GetFileName(executable).Equals("FST.exe", StringComparison.OrdinalIgnoreCase))
                    throw new IOException();
                var id = Guid.NewGuid().ToString("N");
                var script = Path.Combine(Path.GetTempPath(), "fst-uninstall-" + id + ".ps1");
                var plan = Path.ChangeExtension(script, ".json");
                using (var stream = typeof(UninstallForm).Assembly.GetManifestResourceStream("Fst.App.Finish-Uninstall.ps1")!)
                using (var reader = new StreamReader(stream))
                    File.WriteAllText(script, await reader.ReadToEndAsync());
                File.WriteAllText(plan, JsonSerializer.Serialize(new
                {
                    ProcessId = Environment.ProcessId,
                    Executable = executable,
                    DataRoot = Settings.DataRoot
                }));
                if (lua.Checked)
                    foreach (var entry in LuaCatalog.List(settings.LuaDirectories()))
                        LuaCatalog.Remove(entry.AppId, settings.LuaDirectories(), Path.Combine(Settings.DataRoot, "backups"));
                await session.DisconnectAsync();
                Startup.Set(false);
                var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
                foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-PlanFile", plan })
                    start.ArgumentList.Add(arg);
                _ = Process.Start(start) ?? throw new IOException();
                Application.Exit();
            }
            catch { status.Text = L.T("Удаление не завершено. Проверьте доступ к файлам."); remove.Enabled = true; cancel.Enabled = true; }
        };
    }
}
