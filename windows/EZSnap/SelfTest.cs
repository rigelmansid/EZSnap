using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace EZSnap
{
    /// <summary>
    /// 自检：不截屏，用合成图片驱动截图会话，把界面和导出结果写成 PNG，并检查跨屏拼接、窗口选择、快捷键等逻辑。
    /// 结果写进 selftest.log；有失败时返回 1。不覆盖真实的截屏、全局快捷键和鼠标键盘交互。
    /// </summary>
    internal static class SelfTest
    {
        private static readonly List<string> Log = new List<string>();
        private static int failures;

        public static int Run(string dir)
        {
            Directory.CreateDirectory(dir);
            try
            {
                SingleScreen(dir);
                CrossScreen(dir);
                Shortcuts();
            }
            catch (Exception e)
            {
                failures++;
                Log.Add("FAIL  异常：" + e);
            }
            Log.Add(failures == 0 ? "ALL PASS" : failures + " FAILED");
            File.WriteAllLines(Path.Combine(dir, "selftest.log"), Log, new UTF8Encoding(true));
            return failures == 0 ? 0 : 1;
        }

        private static void Check(string name, bool ok, string detail = "")
        {
            Log.Add((ok ? "PASS  " : "FAIL  ") + name + (ok ? "" : "   [" + detail + "]"));
            if (!ok) failures++;
        }

        private static void Save(Bitmap image, string path)
        {
            image.Save(path, ImageFormat.Png);
            Log.Add("已写入 " + path);
        }

        private static Bitmap Render(OverlayForm form)
        {
            var image = new Bitmap(form.GlobalBounds.Width, form.GlobalBounds.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(image)) form.Render(g);
            return image;
        }

        // MARK: 单屏，和 Mac 版自检的三张图对应

        private static void SingleScreen(string dir)
        {
            var shot = new ScreenShot { Bounds = new Rectangle(0, 0, 800, 500), Image = SampleImage(800, 500), Scale = 1 };
            var windows = new List<Rectangle> { new Rectangle(60, 40, 360, 260), new Rectangle(380, 160, 360, 280) };

            var choosing = new CaptureSession(new List<ScreenShot> { shot }, windows);
            choosing.SimulateHover(new PointF(200, 150));
            Check("悬停在窗口上：高亮这个窗口", choosing.Hover == windows[0], choosing.Hover.ToString());
            using (var image = Render(choosing.Forms[0])) Save(image, Path.Combine(dir, "1-choosing.png"));

            var style = AnnotationStyle.Shared;
            Color red = AnnotationStyle.Colors[0], blue = AnnotationStyle.Colors[3];
            var wave = Enumerable.Range(0, 41).Select(i => new PointF(140 + i * 5, 300 + (float)Math.Sin(i / 4.0) * 15)).ToList();
            var annotations = new List<Annotation>
            {
                new Annotation { Tool = Tool.Rect, Color = red, Width = 4, Points = { new PointF(120, 100), new PointF(260, 180) } },
                new Annotation { Tool = Tool.Ellipse, Color = blue, Width = 4, Points = { new PointF(300, 100), new PointF(420, 180) } },
                new Annotation { Tool = Tool.Arrow, Color = red, Width = 4, Points = { new PointF(460, 320), new PointF(540, 220) } },
                new Annotation { Tool = Tool.Pen, Color = AnnotationStyle.Colors[2], Width = 4, Points = wave },
                new Annotation { Tool = Tool.Mosaic, Color = red, Width = style.Width(Tool.Mosaic), Points = { new PointF(300, 250), new PointF(440, 250) } },
                new Annotation { Tool = Tool.Text, Color = red, Width = 0, Points = { new PointF(120, 200) }, Text = "文字标注 Text", FontSize = 18 },
            };
            var selected = new CaptureSession(new List<ScreenShot> { shot }, windows);
            selected.SimulateSelection(new RectangleF(100, 80, 480, 280), Tool.Arrow, annotations);
            Check("工具条显示在选区下方", selected.ToolbarHost == selected.Forms[0] && selected.Toolbar.Visible && selected.Toolbar.Location.Y >= 360,
                selected.Toolbar.Location.ToString());
            using (var image = Render(selected.Forms[0])) Save(image, Path.Combine(dir, "2-selected.png"));

            var export = selected.RenderSelection();
            Check("导出尺寸和选区一致：480×280", export != null && export.Image.Width == 480 && export.Image.Height == 280,
                export == null ? "null" : export.Image.Size.ToString());
            if (export != null) Save(export.Image, Path.Combine(dir, "3-export.png"));
            Check("小数坐标的选区按四条边取整", CaptureSession.PixelRect(new RectangleF(10.3f, 10.3f, 10.3f, 10.3f)).Width == 10,
                CaptureSession.PixelRect(new RectangleF(10.3f, 10.3f, 10.3f, 10.3f)).ToString());
        }

        // MARK: 跨屏：A 在左（蓝），B 在右边偏下（红），两屏之间右上方有一块空隙

        private static void CrossScreen(string dir)
        {
            var a = new ScreenShot { Bounds = new Rectangle(0, 0, 800, 500), Image = Solid(800, 500, Color.Blue), Scale = 1 };
            var b = new ScreenShot { Bounds = new Rectangle(800, 100, 600, 400), Image = Solid(600, 400, Color.Red), Scale = 1.5f };
            var spanning = new Rectangle(700, 200, 300, 150);
            var shots = new List<ScreenShot> { a, b };
            var windows = new List<Rectangle> { spanning };

            var s = new CaptureSession(shots, windows);
            s.MouseMoved(new PointF(750, 250));
            Check("悬停横跨两屏的窗口：高亮整个窗口", s.Hover == spanning, s.Hover.ToString());
            s.MouseDown(new PointF(750, 250), 1, s.Forms[0]);
            s.MouseUp(new PointF(750, 250));
            Check("单击横跨两屏的窗口：整个选中", s.CurrentPhase == CaptureSession.Phase.Selected && s.Selection == spanning, s.Selection.ToString());
            Check("工具条在选区右下角所在的屏（B）上，按 B 的缩放", s.ToolbarHost == s.Forms[1] && Math.Abs(s.Toolbar.Scale - 1.5f) < 0.01f,
                s.ToolbarHost == s.Forms[0] ? "A" : "B");
            s.RightMouseDown();
            Check("右键取消选区，恢复鼠标下窗口的高亮", s.CurrentPhase == CaptureSession.Phase.Choosing && s.Hover == spanning, s.Hover.ToString());

            var d = new CaptureSession(shots, windows);
            d.MouseDown(new PointF(700, 50), 1, d.Forms[0]);
            d.MouseDragged(new PointF(800, 150));
            d.MouseDragged(new PointF(900, 250));
            d.MouseUp(new PointF(900, 250));
            Check("从 A 拖到 B：选区横跨两屏", d.Selection == new RectangleF(700, 50, 200, 200), d.Selection.ToString());
            Check("拖动时放大镜跟着鼠标到 B", d.Forms[1].Mouse == new Point(100, 150) && d.Forms[0].Mouse == null, d.Forms[1].Mouse.ToString());
            var export = d.RenderSelection();
            Color pa = export.Image.GetPixel(50, 100), pb = export.Image.GetPixel(150, 150), gap = export.Image.GetPixel(150, 20);
            Check("导出：A 那部分取 A 的截图（蓝）", pa.B == 255 && pa.R == 0 && pa.A == 255, pa.ToString());
            Check("导出：B 那部分取 B 的截图（红）", pb.R == 255 && pb.B == 0 && pb.A == 255, pb.ToString());
            Check("导出：两屏之间的空隙是透明的", gap.A == 0, gap.ToString());
            Save(export.Image, Path.Combine(dir, "4-cross-export.png"));
            using (var image = Render(d.Forms[0])) Save(image, Path.Combine(dir, "5-cross-A.png"));
            using (var image = Render(d.Forms[1])) Save(image, Path.Combine(dir, "6-cross-B.png"));

            // 文字框落在点下去的那块屏上；输入时点另一块屏只结束输入，文字提交为全局坐标。
            d.SimulateSelection(d.Selection, Tool.Text, new Annotation[0]);
            d.MouseDown(new PointF(850, 200), 1, d.Forms[1]);
            var box = d.Forms[1].Controls.OfType<TextBox>().FirstOrDefault();
            Check("在 B 上点：文字框放在 B 上", box != null && d.IsEditingText);
            if (box != null)
            {
                box.Text = "跨屏";
                d.MouseDown(new PointF(750, 200), 1, d.Forms[0]);
                var mark = d.Annotations.LastOrDefault();
                Check("点另一块屏：文字提交了，坐标是全局坐标", !d.IsEditingText && mark?.Text == "跨屏" && Math.Abs(mark.Points[0].X - 851) < 0.01,
                    mark == null ? "null" : mark.Points[0].ToString());
            }

            // 马赛克横跨两屏：每块屏用自己的底图，导出不出错。
            d.Annotations.Add(new Annotation { Tool = Tool.Mosaic, Color = Color.Red, Width = 20, Points = { new PointF(720, 230), new PointF(880, 230) } });
            var mosaic = d.RenderSelection();
            Check("跨屏马赛克：左边是蓝，右边是红", mosaic.Image.GetPixel(30, 180).B > 200 && mosaic.Image.GetPixel(170, 180).R > 200,
                mosaic.Image.GetPixel(30, 180) + " / " + mosaic.Image.GetPixel(170, 180));
        }

        // MARK: 快捷键、文件名

        private static void Shortcuts()
        {
            Check("预设：Ctrl+Alt+A、Alt+A、Ctrl+Shift+A", string.Join(",", Shortcut.Presets.Select(p => p.Title)) == "Ctrl+Alt+A,Alt+A,Ctrl+Shift+A",
                string.Join(",", Shortcut.Presets.Select(p => p.Title)));
            Check("不带修饰键的字母不能用", new Shortcut(Keys.A, Mods.None).Problem != null);
            Check("只带 Shift 的不能用", new Shortcut(Keys.A, Mods.Shift).Problem != null);
            Check("只带 Ctrl 的不能用（会抢走 Ctrl+C 这类）", new Shortcut(Keys.C, Mods.Control).Problem != null);
            Check("Ctrl+Alt+S 可以用", new Shortcut(Keys.S, Mods.Control | Mods.Alt).Problem == null);
            Check("F9 可以单独用", new Shortcut(Keys.F9, Mods.None).Problem == null && new Shortcut(Keys.F9, Mods.None).Title == "F9");
            var custom = new Shortcut(Keys.D5, Mods.Control | Mods.Shift);
            Check("自定义快捷键存了能读回来", Shortcut.Parse(custom.Serialize()) == custom && custom.Title == "Ctrl+Shift+5", custom.Title);
            var name = CapturedImage.DefaultFileName(new DateTime(2026, 10, 21, 9, 5, 7));
            Check("默认文件名：截图 2026-10-21 09.05.07.png（公历、阿拉伯数字）", name == "截图 2026-10-21 09.05.07.png", name);
        }

        // MARK: 合成图片

        private static Bitmap Solid(int w, int h, Color c)
        {
            var image = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(image)) g.Clear(c);
            return image;
        }

        /// <summary>彩色色块加文字，方便看清马赛克、放大镜和方向是否正确。</summary>
        private static Bitmap SampleImage(int w, int h)
        {
            var image = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(image))
            {
                var blocks = new[] { Color.FromArgb(242, 217, 191), Color.FromArgb(191, 224, 242), Color.FromArgb(217, 242, 199), Color.FromArgb(242, 204, 230) };
                for (var i = 0; i < 4; i++)
                    using (var brush = new SolidBrush(blocks[i]))
                        g.FillRectangle(brush, w / 2 * (i % 2), h / 2 * (i / 2), w / 2, h / 2);
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                using (var font = new Font(Theme.FontName, 28, FontStyle.Bold, GraphicsUnit.Pixel))
                {
                    g.DrawString("TOP 上方 EZSnap", font, Brushes.Black, 40, 30);
                    g.DrawString("Mosaic 马赛克区域", font, Brushes.Black, 300, 240);
                }
            }
            return image;
        }
    }
}
