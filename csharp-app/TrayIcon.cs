using System.Drawing;
using System.Drawing.Drawing2D;
using WinForms = System.Windows.Forms;

namespace Typryt;

/// <summary>
/// The notification-area icon ("T|") and its right-click menu. The icon is drawn in code, so
/// no image file is needed. Events tell the app what the user asked for.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ToolStripMenuItem _status;
    private readonly WinForms.ToolStripMenuItem _toggle;

    public event Action? OpenDashboardRequested;
    public event Action? OpenHistoryRequested;
    public event Action? ToggleRequested;
    public event Action? ExitRequested;

    public TrayIcon(bool isOn)
    {
        _status = new WinForms.ToolStripMenuItem { Enabled = false };
        _toggle = new WinForms.ToolStripMenuItem();
        _toggle.Click += (_, _) => ToggleRequested?.Invoke();

        var dashboard = new WinForms.ToolStripMenuItem("Open Dashboard");
        dashboard.Click += (_, _) => OpenDashboardRequested?.Invoke();

        var history = new WinForms.ToolStripMenuItem("Correction History");
        history.Click += (_, _) => OpenHistoryRequested?.Invoke();

        var exit = new WinForms.ToolStripMenuItem("Exit");
        exit.Click += (_, _) => ExitRequested?.Invoke();

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add(_status);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(_toggle);
        menu.Items.Add(dashboard);
        menu.Items.Add(history);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(exit);

        _icon = new WinForms.NotifyIcon
        {
            Icon = DrawIcon(),
            Text = "Typryt",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => OpenDashboardRequested?.Invoke();

        SetOn(isOn);
    }

    /// <summary>Shows whether corrections are on, in the status line and the toggle item.</summary>
    public void SetOn(bool isOn)
    {
        _status.Text = isOn ? "● Typryt is ON" : "○ Typryt is OFF";
        _toggle.Text = isOn ? "Disable" : "Enable";
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }

    /// <summary>"T" in white with an orange bar after it, the logo's cursor.</summary>
    private static Icon DrawIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            graphics.Clear(Color.Transparent);
            using var font = new Font("Segoe UI Semibold", 17, FontStyle.Regular, GraphicsUnit.Pixel);
            graphics.DrawString("T", font, Brushes.White, new PointF(2, 3));
            using var bar = new SolidBrush(Color.FromArgb(0xE8, 0x62, 0x0A));
            graphics.FillRectangle(bar, 22, 6, 3, 20);
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }
}
