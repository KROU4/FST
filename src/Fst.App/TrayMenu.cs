using System.Runtime.InteropServices;
namespace Fst.App;

internal sealed class TrayMenu : IDisposable
{
    private readonly ContextMenuStrip menu;
    private readonly Form owner = new() { FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, Opacity = 0, StartPosition = FormStartPosition.Manual, Size = new Size(1, 1), Text = "FST menu owner" };
    private readonly System.Windows.Forms.Timer watch = new() { Interval = 100 };
    private IntPtr initialForeground, overflow;
    public TrayMenu(ContextMenuStrip menu)
    {
        this.menu = menu;
        menu.AutoClose = true;
        menu.Closed += (_, _) => { watch.Stop(); owner.Hide(); };
        watch.Tick += (_, _) =>
        {
            var foreground = GetForegroundWindow();
            if (menu.Visible && ((overflow != IntPtr.Zero && !IsWindowVisible(overflow)) || (foreground != owner.Handle && foreground != menu.Handle && foreground != initialForeground)))
                menu.Close();
        };
    }
    public void Show()
    {
        if (menu.Visible)
        {
            menu.Close();
            return;
        }
        initialForeground = GetForegroundWindow();
        overflow = FindWindow("TopLevelWindowForOverflowXamlIsland", null);
        if (overflow == IntPtr.Zero || !IsWindowVisible(overflow))
            overflow = FindWindow("NotifyIconOverflowWindow", null);
        if (overflow != IntPtr.Zero && !IsWindowVisible(overflow))
            overflow = IntPtr.Zero;
        var location = Cursor.Position;
        owner.Location = new Point(location.X, location.Y);
        owner.Show();
        SetForegroundWindow(owner.Handle);
        menu.Show(owner, Point.Empty);
        watch.Start();
    }
    public void Dispose()
    {
        watch.Dispose();
        menu.Dispose();
        owner.Dispose();
    }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string? title);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
}
