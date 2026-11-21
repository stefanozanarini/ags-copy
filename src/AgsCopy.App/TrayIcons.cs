using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace AgsCopy.App;

public enum TrayState
{
    Offline,
    Idle,
    Running,
    Error,
    Paused,
}

/// <summary>Tray icons: the AGS-Copy mark with a dot in the corner, one colour per state.</summary>
public static class TrayIcons
{
    private static readonly Dictionary<TrayState, Icon> Cache = [];
    private static Bitmap? _mark;

    public static Icon Get(TrayState state)
    {
        if (Cache.TryGetValue(state, out var icon)) return icon;
        var color = state switch
        {
            TrayState.Idle => Color.FromArgb(46, 160, 60),
            TrayState.Running => Color.FromArgb(40, 130, 220),
            TrayState.Error => Color.FromArgb(230, 40, 40),
            TrayState.Paused => Color.FromArgb(240, 150, 0),
            _ => Color.FromArgb(150, 150, 150),
        };
        icon = Draw(color, 32);
        Cache[state] = icon;
        return icon;
    }

    private static Bitmap Mark()
    {
        if (_mark != null) return _mark;
        using var stream = typeof(TrayIcons).Assembly.GetManifestResourceStream("Tray.png")
                           ?? throw new InvalidOperationException("Tray.png resource missing");
        return _mark = new Bitmap(stream);
    }

    private static Icon Draw(Color color, int size)
    {
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.Clear(Color.Transparent);
            g.DrawImage(Mark(), 0, 0, size, size);

            // State dot, bottom right, ringed in white so it stands out on the black tile.
            var d = size * 0.46f;
            var r = new RectangleF(size - d, size - d, d - 0.5f, d - 0.5f);
            using (var ring = new SolidBrush(Color.White))
                g.FillEllipse(ring, r);
            r.Inflate(-size * 0.07f, -size * 0.07f);
            using (var fill = new SolidBrush(color))
                g.FillEllipse(fill, r);
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

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
