using System.Runtime.InteropServices;

namespace Fst.App;

internal static class UiTheme
{
    public static readonly Color Background = Color.FromArgb(17, 17, 17), Surface = Color.FromArgb(23, 23, 23), Elevated = Color.FromArgb(36, 36, 36), Hover = Color.FromArgb(43, 43, 43), Ink = Color.FromArgb(230, 230, 222), Muted = Color.FromArgb(143, 143, 138), Border = Color.FromArgb(62, 62, 59);
    public static Font Font(float size = 10, FontStyle style = FontStyle.Regular) => new("Consolas", size, style);
    public static Label Label(string text, Rectangle bounds, float size = 10, bool muted = false, bool bold = false) => new() { Text = L.T(text), Bounds = bounds, Font = Font(size, bold ? FontStyle.Bold : FontStyle.Regular), ForeColor = muted ? Muted : Ink, BackColor = Color.Transparent };
    public static FstButton Button(string text, Rectangle bounds, bool primary = false) => new() { Text = L.T(text), Bounds = bounds, Primary = primary };
    public static FstCard Card(Rectangle bounds) => new() { Bounds = bounds };
    public static FstInput Input(string value, Rectangle bounds) => new() { Text = value, Bounds = bounds };
}
internal sealed class FstButton : Button
{
    [System.ComponentModel.DefaultValue(false)]
    public bool Primary
    {
        get; set;
    }
    private bool hovered;
    public FstButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
        Font = UiTheme.Font(10, FontStyle.Bold);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }
    protected override void OnMouseEnter(EventArgs e)
    {
        hovered = true;
        Invalidate();
        base.OnMouseEnter(e);
    }
    protected override void OnMouseLeave(EventArgs e)
    {
        hovered = false;
        Invalidate();
        base.OnMouseLeave(e);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var fill = !Enabled ? UiTheme.Elevated : Primary ? (hovered ? Color.FromArgb(241, 241, 234) : UiTheme.Ink) : hovered ? UiTheme.Hover : UiTheme.Elevated;
        e.Graphics.Clear(fill);
        if (!Primary)
        {
            using var pen = new Pen(UiTheme.Border);
            e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, !Enabled ? UiTheme.Muted : Primary ? UiTheme.Background : UiTheme.Ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        if (Focused && ShowFocusCues)
            ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -4, -4));
    }
}
internal sealed class FstCard : Panel
{
    public FstCard()
    {
        DoubleBuffered = true;
        BackColor = UiTheme.Surface;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        using var pen = new Pen(UiTheme.Border);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        base.OnPaint(e);
    }
}
internal class FstWindow : Form
{
    private readonly FstButton languageButton;
    public FstWindow(string title, Size size)
    {
        Text = "Fast Steam Tools";
        ClientSize = size;
        FormBorderStyle = FormBorderStyle.None;
        AutoScaleMode = AutoScaleMode.None;
        Font = UiTheme.Font();
        BackColor = UiTheme.Background;
        ForeColor = UiTheme.Ink;
        DoubleBuffered = true;
        StartPosition = FormStartPosition.CenterScreen;
        Icon = AppIcon.Load();
        var mark = new PictureBox { Bounds = new Rectangle(20, 14, 22, 22), Image = Icon.ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom };
        Controls.Add(mark);
        var heading = UiTheme.Label("Fast Steam Tools", new Rectangle(52, 15, size.Width - 220, 25), 12, bold: true);
        Controls.Add(heading);
        languageButton = UiTheme.Button(L.Language == "ru" ? "RU / en" : "ru / EN", new Rectangle(size.Width - 144, 12, 82, 28));
        Controls.Add(languageButton);
        languageButton.Click += (_, _) => L.Language = L.Language == "ru" ? "en" : "ru";
        L.Changed += Translate;
        FormClosed += (_, _) => L.Changed -= Translate;
        var close = UiTheme.Button("×", new Rectangle(size.Width - 48, 12, 28, 28));
        close.Click += (_, _) => Close();
        Controls.Add(close);
        heading.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) Drag(); };
        MouseDown += (_, e) => { if (e.Button == MouseButtons.Left && e.Y < 49) Drag(); };
    }
    protected override void OnShown(EventArgs e)
    {
        Translate();
        base.OnShown(e);
    }
    private void Translate()
    {
        Text = L.T(Text);
        void Visit(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                if (child is not TextBox and not FstInput)
                    child.Text = L.T(child.Text);
                if (child is not FstInput)
                    Visit(child);
            }
        }
        Visit(this);
        languageButton.Text = L.Language == "ru" ? "RU / en" : "ru / EN";
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        using var pen = new Pen(UiTheme.Border);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        e.Graphics.DrawLine(pen, 20, 49, Width - 20, 49);
        base.OnPaint(e);
    }
    private void Drag()
    {
        ReleaseCapture();
        SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero);
    }
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}
internal static class AppIcon
{
    public static Icon Load()
    {
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("Fst.App.Assets.fst.ico")!;
        return new Icon(stream, 32, 32);
    }
}
internal sealed class FstCheckBox : CheckBox
{
    private bool hovered;
    public FstCheckBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        AutoSize = false;
        Font = UiTheme.Font(10);
        ForeColor = UiTheme.Ink;
        Cursor = Cursors.Hand;
    }
    protected override void OnMouseEnter(EventArgs e)
    {
        hovered = true;
        Invalidate();
        base.OnMouseEnter(e);
    }
    protected override void OnMouseLeave(EventArgs e)
    {
        hovered = false;
        Invalidate();
        base.OnMouseLeave(e);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? UiTheme.Surface);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var box = new Rectangle(0, (Height - 20) / 2, 20, 20);
        using var brush = new SolidBrush(Checked ? UiTheme.Ink : hovered ? UiTheme.Hover : UiTheme.Elevated);
        e.Graphics.FillRectangle(brush, box);
        using var border = new Pen(hovered ? UiTheme.Muted : UiTheme.Border);
        e.Graphics.DrawRectangle(border, box);
        if (Checked)
        {
            using var pen = new Pen(UiTheme.Background, 2) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
            e.Graphics.DrawLines(pen, [new Point(5, box.Y + 10), new Point(9, box.Y + 14), new Point(16, box.Y + 6)]);
        }
        TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(33, 0, Width - 33, Height), Enabled ? UiTheme.Ink : UiTheme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        if (Focused && ShowFocusCues)
            ControlPaint.DrawFocusRectangle(e.Graphics, new Rectangle(30, 2, Width - 32, Height - 4));
    }
}
internal sealed class FstInput : UserControl
{
    private readonly TextBox input = new() { BorderStyle = BorderStyle.None, BackColor = UiTheme.Elevated, ForeColor = UiTheme.Ink, Font = UiTheme.Font(10) };
    public FstInput()
    {
        BackColor = UiTheme.Elevated;
        Padding = new Padding(12, 10, 12, 10);
        Controls.Add(input);
        input.TextChanged += (_, _) => OnTextChanged(EventArgs.Empty);
        input.Enter += (_, _) => Invalidate();
        input.Leave += (_, _) => Invalidate();
        Height = 40;
        SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
    }
    [System.Diagnostics.CodeAnalysis.AllowNull, System.ComponentModel.Browsable(true), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Visible)]
    public override string Text
    {
        get => input.Text; set => input.Text = value ?? "";
    }
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        input.SetBounds(12, Math.Max(8, (Height - input.PreferredHeight) / 2), Math.Max(1, Width - 24), input.PreferredHeight);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        using var pen = new Pen(input.Focused ? UiTheme.Muted : UiTheme.Border);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        base.OnPaint(e);
    }
}
