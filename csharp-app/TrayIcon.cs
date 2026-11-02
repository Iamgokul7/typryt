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
    private readonly Icon _onIcon = DrawIcon(on: true);
    private readonly Icon _offIcon = DrawIcon(on: false);

    public event Action? OpenDashboardRequested;
    public event Action? OpenHistoryRequested;
    public event Action? OpenLogFolderRequested;
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

        var logs = new WinForms.ToolStripMenuItem("Open Log Folder");
        logs.Click += (_, _) => OpenLogFolderRequested?.Invoke();

        var exit = new WinForms.ToolStripMenuItem("Exit");
        exit.Click += (_, _) => ExitRequested?.Invoke();

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add(_status);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(_toggle);
        menu.Items.Add(dashboard);
        menu.Items.Add(history);
        menu.Items.Add(logs);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(exit);

        _icon = new WinForms.NotifyIcon
        {
            Icon = _onIcon,
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
        _icon.Text = "Typryt";
        _icon.Icon = isOn ? _onIcon : _offIcon;
        _status.Text = isOn ? "● Typryt is ON" : "○ Typryt is OFF";
        _toggle.Text = isOn ? "Disable" : "Enable";
    }

    /// <summary>Shows that Typryt paused itself, with the reason, in the tooltip and the status line.</summary>
    public void SetPaused(string reason)
    {
        string text = $"Typryt paused: {reason}";
        _icon.Icon = _offIcon;
        _status.Text = "○ " + text;
        _toggle.Text = "Enable";
        _icon.Text = text.Length > 63 ? text[..63] : text;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }

    /// <summary>The app mark: an accent square (grey when off) with a black border and a black "T|".</summary>
    private static Icon DrawIcon(bool on)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.None;
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            graphics.Clear(Color.Transparent);
            using var fill = new SolidBrush(on ? Color.FromArgb(0xDE, 0x5C, 0x0A) : Color.FromArgb(0x6E, 0x6E, 0x6E));
            graphics.FillRectangle(fill, 1, 1, 30, 30);
            using var border = new Pen(Color.Black, 3) { Alignment = PenAlignment.Inset };
            graphics.DrawRectangle(border, 1, 1, 30, 30);
            using var font = new Font("Consolas", 15, FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            graphics.DrawString("T|", font, Brushes.Black, new RectangleF(1, 2, 30, 28), format);
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }
}
