using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace EZSnap
{
    /// <summary>一块屏幕的冻结画面。Bounds 是全局坐标（虚拟屏幕的物理像素），图片和 Bounds 一样大。</summary>
    internal sealed class ScreenShot
    {
        public Rectangle Bounds;
        public Bitmap Image;
        /// <summary>这块屏幕的缩放（96 DPI 为 1），界面元素按它放大。</summary>
        public float Scale = 1;
    }

    /// <summary>截图结果。</summary>
    internal sealed class CapturedImage
    {
        public Bitmap Image;

        public void CopyToClipboard()
        {
            var data = new DataObject();
            data.SetData(DataFormats.Bitmap, true, Image);
            // 再放一份 PNG，支持透明的程序（如 Office、浏览器）会优先用它。
            var png = new MemoryStream();
            Image.Save(png, ImageFormat.Png);
            data.SetData("PNG", false, png);
            Clipboard.SetDataObject(data, true);
        }

        /// <summary>默认文件名。固定格式的日期用不变文化，不跟着系统日历变（同 Mac 版）。</summary>
        public static string DefaultFileName(DateTime time) => L.T("Screenshot ", "截图 ", "截圖 ") + time.ToString("yyyy-MM-dd HH.mm.ss", CultureInfo.InvariantCulture) + ".png";

        public void SaveWithDialog(Settings settings)
        {
            using (var dialog = new SaveFileDialog
            {
                Filter = L.T("PNG image|*.png", "PNG 图片|*.png", "PNG 圖片|*.png"),
                FileName = DefaultFileName(DateTime.Now),
                AddExtension = true,
                DefaultExt = "png",
            })
            {
                var dir = settings.Get("lastSaveDir");
                dialog.InitialDirectory = !string.IsNullOrEmpty(dir) && Directory.Exists(dir)
                    ? dir : Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                if (dialog.ShowDialog() != DialogResult.OK) return;
                try
                {
                    Image.Save(dialog.FileName, ImageFormat.Png);
                    settings.Set("lastSaveDir", Path.GetDirectoryName(dialog.FileName));
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ExternalException)
                {
                    MessageBox.Show(e.Message, L.T("Save Failed", "保存失败", "儲存失敗"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }
    }

    internal static class Capturer
    {
        /// <summary>按物理像素截下每块屏幕。</summary>
        public static List<ScreenShot> CaptureAll()
        {
            var shots = new List<ScreenShot>();
            foreach (var screen in Screen.AllScreens)
            {
                var b = screen.Bounds;
                // 不用 Graphics.CopyFromScreen：.NET Framework 会校验参数，SourceCopy | CaptureBlt 这个组合直接报错（坑 6）。
                // 直接调 BitBlt；CAPTUREBLT 让半透明（分层）窗口也截进来。不带透明通道，GDI 不会往里写透明度。
                var image = new Bitmap(b.Width, b.Height, PixelFormat.Format32bppRgb);
                using (var g = Graphics.FromImage(image))
                {
                    var dest = g.GetHdc();
                    var screenDC = Native.GetDC(IntPtr.Zero);
                    try
                    {
                        if (!Native.BitBlt(dest, 0, 0, b.Width, b.Height, screenDC, b.X, b.Y, Native.SRCCOPY | Native.CAPTUREBLT))
                            throw new System.ComponentModel.Win32Exception();
                    }
                    finally
                    {
                        Native.ReleaseDC(IntPtr.Zero, screenDC);
                        g.ReleaseHdc(dest);
                    }
                }
                shots.Add(new ScreenShot
                {
                    Bounds = b,
                    Image = image,
                    Scale = Native.ScaleAt(new Point(b.X + b.Width / 2, b.Y + b.Height / 2)),
                });
            }
            return shots;
        }

        /// <summary>屏幕上可见的窗口，从前到后，全局坐标。去掉最小化、被系统隐藏（cloaked）、太小的和本程序自己的窗口。</summary>
        public static List<Rectangle> VisibleWindowRects()
        {
            var rects = new List<Rectangle>();
            var self = Process.GetCurrentProcess().Id;
            Native.EnumWindows((hwnd, _) =>
            {
                if (!Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd)) return true;
                Native.GetWindowThreadProcessId(hwnd, out var pid);
                if (pid == self) return true;
                if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
                var exStyle = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
                if ((exStyle & Native.WS_EX_TOOLWINDOW) != 0 && !IsTaskbar(hwnd)) return true;
                // 用 DWM 的边框：不含 Windows 10 窗口四周看不见的拖动边。
                Rectangle r;
                if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out Native.RECT frame, Marshal.SizeOf(typeof(Native.RECT))) == 0)
                    r = frame.ToRectangle();
                else if (Native.GetWindowRect(hwnd, out var wr))
                    r = wr.ToRectangle();
                else
                    return true;
                if (r.Width >= 10 && r.Height >= 10) rects.Add(r);
                return true;
            }, IntPtr.Zero);
            return rects;
        }

        private static bool IsTaskbar(IntPtr hwnd)
        {
            var name = new StringBuilder(64);
            Native.GetClassName(hwnd, name, name.Capacity);
            return name.ToString() == "Shell_TrayWnd" || name.ToString() == "Shell_SecondaryTrayWnd";
        }
    }
}
