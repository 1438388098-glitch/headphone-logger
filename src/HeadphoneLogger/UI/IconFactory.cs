using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace HeadphoneLogger.UI;

/// <summary>程序运行时绘制的耳机图标（托盘与窗口共用）。</summary>
internal static class IconFactory
{
    public static Icon CreateHeadphoneIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // 圆角底
            using var bg = new GraphicsPath();
            bg.AddArc(1, 1, 8, 8, 180, 90);
            bg.AddArc(23, 1, 8, 8, 270, 90);
            bg.AddArc(23, 23, 8, 8, 0, 90);
            bg.AddArc(1, 23, 8, 8, 90, 90);
            bg.CloseFigure();
            using var brush = new SolidBrush(Color.FromArgb(63, 81, 140));
            g.FillPath(brush, bg);

            // 头梁弧线
            using var pen = new Pen(Color.White, 4f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(pen, 8, 6, 16, 20, 180, 180);

            // 左右耳罩
            using var cup = new SolidBrush(Color.White);
            FillRoundRect(g, cup, 5, 17, 8, 10, 3);
            FillRoundRect(g, cup, 19, 17, 8, 10, 3);
        }

        var hicon = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(hicon);
            return (Icon)temp.Clone(); // Clone 持有独立句柄，随 NotifyIcon 生命周期释放
        }
        finally
        {
            DestroyIcon(hicon);
        }
    }

    private static void FillRoundRect(Graphics g, Brush brush, int x, int y, int w, int h, int r)
    {
        using var path = new GraphicsPath();
        path.AddArc(x, y, r * 2, r * 2, 180, 90);
        path.AddArc(x + w - r * 2, y, r * 2, r * 2, 270, 90);
        path.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2, 0, 90);
        path.AddArc(x, y + h - r * 2, r * 2, r * 2, 90, 90);
        path.CloseFigure();
        g.FillPath(brush, path);
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
