using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Underlit.UI;

/// <summary>
/// Tray icon (NotifyIcon) + right-click menu.
/// The logo is generated in code — see <see cref="CreateLogoIcon"/> — so we don't
/// need to ship a .ico file. The design: a dark rounded-square ground with a
/// circle whose top is bright white and whose bottom fades into darkness. That
/// gradient is the "underlit" metaphor: a light that's been pushed below its
/// normal range.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    public event Action? OpenSettings;
    public event Action? TogglePaused;
    public event Action? Quit;
    /// <summary>v0.7.0: take back lights Google / the Hue app changed.</summary>
    public event Action? ResumeLights;

    private readonly NotifyIcon _ni;
    private readonly ToolStripMenuItem _toggleItem;
    private readonly ToolStripMenuItem _resumeLightsItem;
    private readonly Icon _icon;

    public TrayIcon()
    {
        _icon = CreateLogoIcon(32);
        _ni = new NotifyIcon
        {
            Icon = _icon,
            Text = "Underlit",
            Visible = true
        };

        var menu = new ContextMenuStrip();
        _toggleItem = new ToolStripMenuItem("Pause dimming", null, (_, _) => TogglePaused?.Invoke());
        menu.Items.Add(_toggleItem);
        _resumeLightsItem = new ToolStripMenuItem("Resume lights schedule", null, (_, _) => ResumeLights?.Invoke())
        {
            ToolTipText = "Take back lights that Google Home, the Hue app or a switch changed.",
        };
        menu.Items.Add(_resumeLightsItem);
        menu.Items.Add(new ToolStripMenuItem("Settings…", null, (_, _) => OpenSettings?.Invoke()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Quit Underlit", null, (_, _) => Quit?.Invoke()));
        _ni.ContextMenuStrip = menu;

        _ni.MouseClick += (s, e) =>
        {
            if (e.Button == MouseButtons.Left) OpenSettings?.Invoke();
        };
    }

    /// <summary>v0.7.0: show how many lights are currently left alone
    /// because something else changed them.</summary>
    private int _lastOverrideCount = -1;
    public void SetLightsOverridden(int count)
    {
        if (count == _lastOverrideCount) return;
        _lastOverrideCount = count;
        _resumeLightsItem.Text = count > 0
            ? $"Resume lights schedule ({count} light{(count == 1 ? "" : "s")} left alone)"
            : "Resume lights schedule";
        _resumeLightsItem.Font = new Font(_resumeLightsItem.Font, count > 0 ? FontStyle.Bold : FontStyle.Regular);
        _ni.Text = count > 0 ? $"Underlit - {count} light{(count == 1 ? "" : "s")} under manual control" : "Underlit";
    }

    public void SetPausedLabel(bool paused)
        => _toggleItem.Text = paused ? "Resume dimming" : "Pause dimming";

    /// <summary>v0.6.55: surface a balloon notification near the tray
    /// icon. Used by the wind-down notification scheduler in
    /// UnderlitHost - clean tray-anchored UX without taking a hard
    /// dependency on the WinRT toast packages.</summary>
    public void ShowBalloon(string title, string body, int timeoutMs = 5000)
    {
        try
        {
            _ni.BalloonTipTitle = title;
            _ni.BalloonTipText  = body;
            _ni.BalloonTipIcon  = ToolTipIcon.Info;
            _ni.ShowBalloonTip(timeoutMs);
        }
        catch (Exception ex) { Underlit.Logger.Warn("TrayIcon.ShowBalloon failed", ex); }
    }

    /// <summary>
    /// Draws the Underlit logo at the requested pixel size and returns a managed Icon.
    /// Public so other UI (e.g. Settings window titlebar) can reuse the same design.
    /// </summary>
    public static Icon CreateLogoIcon(int size)
    {
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.Clear(Color.Transparent);

            // Dark rounded-square ground.
            var bgColor = Color.FromArgb(255, 30, 30, 34);
            float cornerRadius = size * 0.22f;
            FillRoundedRect(g, 0, 0, size, size, cornerRadius, bgColor);

            // Centered circle (~58% of canvas), with a vertical gradient:
            // pure white across the upper ~55%, then fading to near-black across
            // the lower 45%. Reads as "lamp with its bottom dimmed".
            float d = size * 0.58f;
            float offset = (size - d) / 2f;
            var circleRect = new RectangleF(offset, offset, d, d);

            using var gradient = new LinearGradientBrush(
                new PointF(0, offset),
                new PointF(0, offset + d),
                Color.FromArgb(255, 255, 255, 255),
                Color.FromArgb(255, 58, 58, 66))
            {
                Blend = new Blend
                {
                    Positions = new[] { 0f, 0.55f, 1f },
                    Factors   = new[] { 0f, 0f,   1f }
                }
            };
            g.FillEllipse(gradient, circleRect);
        }

        // Convert bitmap → HICON → managed Icon. GetHicon returns a handle we own;
        // cloning makes the managed Icon self-sufficient so we can free the source.
        IntPtr hIcon = bmp.GetHicon();
        Icon icon;
        try
        {
            icon = (Icon)Icon.FromHandle(hIcon).Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
        return icon;
    }

    private static void FillRoundedRect(Graphics g, float x, float y, float w, float h, float r, Color color)
    {
        using var path = new GraphicsPath();
        float d = r * 2;
        path.AddArc(x,         y,         d, d, 180, 90);
        path.AddArc(x + w - d, y,         d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0,   90);
        path.AddArc(x,         y + h - d, d, d, 90,  90);
        path.CloseFigure();
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public void Dispose()
    {
        _ni.Visible = false;
        _ni.Dispose();
        _icon.Dispose();
    }
}
