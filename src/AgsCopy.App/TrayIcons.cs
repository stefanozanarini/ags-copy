using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace AgsCopy.App;

public enum TrayState
{
    Offline,
    Idle,
    Running,
    Error,
    Paused,
}

/// <summary>Icons drawn at runtime: two arrows on a coloured badge, one colour per state.</summary>
public static class TrayIcons
{
    private static readonly Dictionary<TrayState, Icon> Cache = [];

    public static Icon Get(TrayState state)
    {
        if (Cache.TryGetValue(state, out var icon)) return icon;
        var color = state switch
        {
            TrayState.Idle => Color.FromArgb(46, 125, 50),
            TrayState.Running => Color.FromArgb(31, 111, 178),
            TrayState.Error => Color.FromArgb(198, 40, 40),
            TrayState.Paused => Color.FromArgb(224, 138, 0),
            _ => Color.FromArgb(120, 120, 120),
        };
        icon = Draw(color, 32);
        Cache[state] = icon;
        return icon;
    }

    public static System.Windows.Media.ImageSource WindowIcon()
    {
        var icon = Get(TrayState.Running);
        return Imaging.CreateBitmapSourceFromHIcon(icon.Handle, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
    }

    private static Icon Draw(Color color, int size)
    {
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using (var path = RoundedRect(new RectangleF(1, 1, size - 2, size - 2), size / 5f))
            using (var fill = new SolidBrush(color))
                g.FillPath(fill, path);

            using var pen = new Pen(Color.White, size / 10f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            float s = size;
            // Upper arrow pointing right, lower arrow pointing left.
            g.DrawLine(pen, s * .22f, s * .36f, s * .76f, s * .36f);
            g.DrawLines(pen, [new PointF(s * .60f, s * .22f), new PointF(s * .76f, s * .36f), new PointF(s * .60f, s * .50f)]);
            g.DrawLine(pen, s * .78f, s * .66f, s * .24f, s * .66f);
            g.DrawLines(pen, [new PointF(s * .40f, s * .52f), new PointF(s * .24f, s * .66f), new PointF(s * .40f, s * .80f)]);
        }

        var handle = bmp.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(handle).Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
