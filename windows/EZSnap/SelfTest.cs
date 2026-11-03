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
    /// 自检：不截屏，用合成图片驱动截图会话，把界面和导出结果写成 PNG，并检查跨屏拼接、窗口选择、快捷键、
    /// 调整已画的标注（D-20、D-21）、检查更新的版本比较（D-22）等逻辑。直接调用会话的方法，不经过真实的鼠标键盘事件。
    /// 结果写进 selftest.log；有失败时返回 1。不覆盖真实的全局快捷键和手感。
    /// </summary>
    internal static class SelfTest
    {
        private static readonly List<string> Log = new List<string>();
        private static int failures;

        public static int Run(string dir)
        {
            Directory.CreateDirectory(dir);
            // 固定用简体中文，输出不随系统语言变；语言本身在 Languages() 里单独检查。
            L.Choice = Language.ZhHans;
            try
            {
                SingleScreen(dir);
                EditShapes(dir);
                EditTextAndPen(dir);
                CrossScreen(dir);
                Shortcuts();
                Languages();
                UpdateLogic();
                RealCapture(dir);
                RealUpdateCheck();
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
            // 10.3→10、20.6→21，宽 11；按“起点、宽度分别取整”会得到 10。
            Check("小数坐标的选区按四条边取整", CaptureSession.PixelRect(new RectangleF(10.3f, 10.3f, 10.3f, 10.3f)).Width == 11,
                CaptureSession.PixelRect(new RectangleF(10.3f, 10.3f, 10.3f, 10.3f)).ToString());
        }

        // MARK: 调整已画的标注（D-20、D-21），和 Mac 版自检同样的步骤

        private static PointF P(float x, float y) => new PointF(x, y);

        private static List<Annotation> Copy(List<Annotation> list) => list.Select(a => a.Clone()).ToList();

        private static bool Same(List<Annotation> a, List<Annotation> b) => a.Count == b.Count && a.Zip(b, (x, y) => x.SameAs(y)).All(ok => ok);

        private static string Describe(List<Annotation> list) =>
            string.Join("; ", list.Select(a => a.Tool + " " + string.Join(" ", a.Points.Take(2).Select(q => $"({q.X},{q.Y})"))));

        private static void Drag(CaptureSession s, PointF a, PointF b)
        {
            s.MouseDown(a, 1, s.Forms[0]);
            s.MouseDragged(b);
            s.MouseUp(b);
        }

        private static ScreenShot EditShot() => new ScreenShot { Bounds = new Rectangle(0, 0, 800, 500), Image = SampleImage(800, 500), Scale = 1 };

        private static void EditShapes(string dir)
        {
            var colors = AnnotationStyle.Colors;
            var original = new List<Annotation>
            {
                new Annotation { Tool = Tool.Rect, Color = colors[0], Width = 4, Points = { P(120, 100), P(260, 180) } },
                new Annotation { Tool = Tool.Ellipse, Color = colors[3], Width = 4, Points = { P(300, 100), P(420, 180) } },
                new Annotation { Tool = Tool.Arrow, Color = colors[0], Width = 4, Points = { P(460, 320), P(540, 220) } },
            };
            var s = new CaptureSession(new List<ScreenShot> { EditShot() }, new List<Rectangle>());
            s.SimulateSelection(new RectangleF(100, 80, 480, 280), null, Copy(original));
            var all = s.Annotations;

            Drag(s, P(120, 140), P(150, 160));
            Check("拖矩形的线条：选中并挪动", all[0].Points.SequenceEqual(new[] { P(150, 120), P(290, 200) }) && s.SelectedAnnotation == 0, Describe(all));
            var moved = Copy(all);
            Drag(s, P(290, 200), P(320, 230));
            Check("拖矩形右下角的控制点：改大小", all[0].Points.SequenceEqual(new[] { P(150, 120), P(320, 230) }), Describe(all));
            var resized = Copy(all);
            Drag(s, P(360, 140), P(360, 140));
            Check("没选工具时点椭圆框内空白处：取消选中，不改标注", s.SelectedAnnotation == null && Same(all, resized), Describe(all));
            Drag(s, P(300, 140), P(300, 140));
            Check("点椭圆的线条：选中，不改标注", s.SelectedAnnotation == 1 && Same(all, resized), s.SelectedAnnotation + " " + Describe(all));
            Drag(s, P(340, 125), P(380, 155));
            Check("选中椭圆后在框内空白处拖：画新椭圆并选中，原来的不变",
                all.Count == 4 && all[1].SameAs(resized[1]) && all[3].Tool == Tool.Ellipse && s.SelectedAnnotation == 3, Describe(all));
            using (var image = Render(s.Forms[0])) Save(image, Path.Combine(dir, "8-edit.png"));
            var drawn = Copy(all);
            Drag(s, P(500, 270), P(500, 270));
            Check("点箭头中段：选中箭头", s.SelectedAnnotation == 2, s.SelectedAnnotation.ToString());
            Drag(s, P(540, 220), P(560, 200));
            Check("拖箭头终点", all[2].Points.SequenceEqual(new[] { P(460, 320), P(560, 200) }), Describe(all));
            var arrowMoved = Copy(all);
            AnnotationStyle.Shared.ColorIndex = 2;
            AnnotationStyle.Shared.SizeIndex = 2;
            s.ApplyStyle();
            Check("工具条改颜色和粗细：改到选中的箭头上", all[2].Color.ToArgb() == colors[2].ToArgb() && all[2].Width == 6, all[2].Color + " " + all[2].Width);
            var styled = Copy(all);
            s.HandleKey(Keys.Delete);
            Check("按 Delete：删掉选中的箭头", all.Count == 3 && all.All(a => a.Tool != Tool.Arrow) && s.SelectedAnnotation == null, Describe(all));

            var steps = new (List<Annotation>, string)[]
            {
                (styled, "删除"), (arrowMoved, "改样式"), (drawn, "拖箭头终点"), (resized, "画新椭圆"), (moved, "改大小"), (original, "挪动"),
                (original.Take(2).ToList(), "画箭头"), (original.Take(1).ToList(), "画椭圆"), (new List<Annotation>(), "画矩形"),
            };
            foreach (var (expected, name) in steps)
            {
                s.HandleKey(Keys.Control | Keys.Z);
                Check("撤销" + name, Same(all, expected), Describe(all));
            }
            Check("全部撤销后不能再撤销", !s.CanUndo);
        }

        private static void EditTextAndPen(string dir)
        {
            var colors = AnnotationStyle.Colors;
            var style = AnnotationStyle.Shared;
            var text = new Annotation { Tool = Tool.Text, Color = colors[0], Width = 0, Points = { P(150, 250) }, Text = "文字 Text", FontSize = 18 };
            var pen = new Annotation { Tool = Tool.Pen, Color = colors[2], Width = 4, Points = { P(200, 320), P(250, 330), P(300, 320), P(350, 330), P(400, 320) } };
            var original = new List<Annotation> { text, pen };
            var region = new RectangleF(100, 80, 480, 280);
            var s = new CaptureSession(new List<ScreenShot> { EditShot() }, new List<Rectangle>());
            s.SimulateSelection(region, null, Copy(original));
            var all = s.Annotations;
            void DoubleClick(PointF p)
            {
                s.MouseDown(p, 1, s.Forms[0]);
                s.MouseUp(p);
                s.MouseDown(p, 2, s.Forms[0]);
                s.MouseUp(p);
            }
            void ClickOutside()
            {
                s.MouseDown(P(520, 120), 1, s.Forms[0]);
                s.MouseUp(P(520, 120));
            }
            TextBox Box() => s.Forms[0].Controls.OfType<TextBox>().FirstOrDefault();

            Drag(s, P(170, 260), P(190, 270));
            Check("拖文字：选中并挪动", all[0].Points.SequenceEqual(new[] { P(170, 260) }) && s.SelectedAnnotation == 0, Describe(all));
            var textMoved = Copy(all);
            Drag(s, P(300, 320), P(300, 300));
            Check("拖画笔线条：选中并挪动", all[1].Points.SequenceEqual(pen.Points.Select(q => P(q.X, q.Y - 20))) && s.SelectedAnnotation == 1, Describe(all));
            var penMoved = Copy(all);
            style.ColorIndex = 3;
            style.SizeIndex = 2;
            s.ApplyStyle();
            Check("工具条改样式：改到选中的线条上", all[1].Color.ToArgb() == colors[3].ToArgb() && all[1].Width == 6, all[1].Color + " " + all[1].Width);
            using (var image = Render(s.Forms[0])) Save(image, Path.Combine(dir, "9-text-pen.png"));
            var penStyled = Copy(all);
            Drag(s, P(180, 272), P(180, 272));
            Check("点文字：选中，工具条显示它的颜色和字号", s.SelectedAnnotation == 0 && style.ColorIndex == 0 && style.SizeIndex == 1,
                $"{s.SelectedAnnotation} {style.ColorIndex} {style.SizeIndex}");
            style.ColorIndex = 4;
            style.SizeIndex = 2;
            s.ApplyStyle();
            Check("工具条改样式：改文字的颜色和字号", all[0].Color.ToArgb() == colors[4].ToArgb() && all[0].FontSize == 26, all[0].Color + " " + all[0].FontSize);
            var textStyled = Copy(all);
            DoubleClick(P(180, 272));
            Check("双击文字：进入重新编辑", s.EditingText == 0 && Box()?.Text == "文字 Text" && Same(all, textStyled), Box()?.Text ?? "没有输入框");
            ClickOutside();
            Check("不改内容就结束编辑：文字原样，位置不变", s.EditingText == null && Same(all, textStyled), Describe(all));
            DoubleClick(P(180, 272));
            if (Box() != null) Box().Text = "改过的文字";
            ClickOutside();
            Check("改字后结束编辑：内容更新，位置和样式不变", all[0].Text == "改过的文字" && all[0].Points.SequenceEqual(new[] { P(170, 260) })
                && all[0].Color.ToArgb() == colors[4].ToArgb() && all[0].FontSize == 26, all[0].Text + " " + Describe(all));
            var edited = Copy(all);
            DoubleClick(P(180, 272));
            if (Box() != null) Box().Text = "";
            ClickOutside();
            Check("把字删光再结束编辑：删掉这个文字", all.Count == 1 && all[0].SameAs(penStyled[1]), Describe(all));

            var steps = new (List<Annotation>, string)[]
            {
                (edited, "删光文字"), (textStyled, "改文字内容"), (penStyled, "改文字样式"), (penMoved, "改线条样式"),
                (textMoved, "挪线条"), (original, "挪文字"), (new List<Annotation> { text }, "画线条"), (new List<Annotation>(), "写文字"),
            };
            foreach (var (expected, name) in steps)
            {
                s.HandleKey(Keys.Control | Keys.Z);
                Check("撤销" + name, Same(all, expected), Describe(all));
            }

            var penTool = new CaptureSession(new List<ScreenShot> { EditShot() }, new List<Rectangle>());
            penTool.SimulateSelection(region, Tool.Pen, new[] { pen.Clone() });
            Drag(penTool, P(300, 320), P(320, 340));
            Check("画笔工具开着时在旧线条上拖：画新线条，旧的不动",
                penTool.Annotations.Count == 2 && penTool.Annotations[0].SameAs(pen) && penTool.SelectedAnnotation == null, Describe(penTool.Annotations));
            // 样式是全局共用的，恢复默认，免得影响后面的检查。
            style.ColorIndex = 0;
            style.SizeIndex = 1;
        }

        // MARK: 检查更新（D-22）

        private static void UpdateLogic()
        {
            foreach (var (a, b, expected) in new[] { ("0.2.0", "0.1.1", true), ("0.1.1", "0.1.1", false), ("0.1.0", "0.1.1", false), ("0.1.10", "0.1.9", true),
                                                     ("1.0", "0.9.9", true), ("0.1", "0.1.0", false), ("v0.1.2", "0.1.1", true), ("0.1.1", "v0.1.2", false) })
                Check($"{a} 比 {b} 新：{(expected ? "是" : "否")}", UpdateChecker.IsNewer(a, b) == expected);
            const string page = "https://github.com/rigelmansid/EZSnap/releases/tag/v0.2.0";
            var good = UpdateChecker.Parse("{\"tag_name\":\"v0.2.0\",\"html_url\":\"" + page + "\",\"draft\":false}");
            Check("解析 GitHub 返回内容：去掉 v，取发布页", good?.Version == "0.2.0" && good.Page.AbsoluteUri == page, good?.Version + " " + good?.Page);
            foreach (var body in new[] { "{\"tag_name\":\"v0.2.0\",\"html_url\":\"https://example.com/x\"}", "{\"message\":\"Not Found\"}", "not json" })
                Check("无法识别的返回内容不当作新版本：" + body, UpdateChecker.Parse(body) == null);
            Check("程序版本号取三段：" + UpdateChecker.CurrentVersion, UpdateChecker.CurrentVersion.Split('.').Length == 3);
        }

        /// <summary>真实访问一次 GitHub。CI 机器共用 IP，可能碰到匿名访问的限流，所以只记结果，不算失败。</summary>
        private static void RealUpdateCheck()
        {
            try
            {
                var latest = UpdateChecker.FetchLatest().GetAwaiter().GetResult();
                Log.Add($"真实检查更新：最新正式版 {latest.Version}，当前 {UpdateChecker.CurrentVersion}，"
                        + (UpdateChecker.IsNewer(latest.Version, UpdateChecker.CurrentVersion) ? "有新版本" : "已是最新"));
            }
            catch (Exception e)
            {
                Log.Add("真实检查更新没成功（不算失败）：" + (e.InnerException?.Message ?? e.Message));
            }
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

        // MARK: 真实截屏（坑 6）：在 CI 的 Windows 机器上也真的截一次，至少保证不报错、尺寸对

        private static void RealCapture(string dir)
        {
            var shots = Capturer.CaptureAll();
            Check("真实截屏：每块屏都截到了", shots.Count == Screen.AllScreens.Length && shots.Count > 0, shots.Count + " / " + Screen.AllScreens.Length);
            Check("真实截屏：图片和屏幕一样大",
                shots.All(s => s.Image.Width == s.Bounds.Width && s.Image.Height == s.Bounds.Height),
                string.Join(", ", shots.Select(s => s.Image.Size + " vs " + s.Bounds.Size)));
            if (shots.Count > 0)
            {
                // 截到的内容随机器而定（CI 上可能是一片黑），只存下来看，不判断。
                using (var small = new Bitmap(shots[0].Image, shots[0].Image.Width / 4, shots[0].Image.Height / 4)) Save(small, Path.Combine(dir, "7-real-capture.png"));
            }
            foreach (var s in shots) s.Image.Dispose();
            var windows = Capturer.VisibleWindowRects();
            Check("真实窗口列表：能取到，不报错", windows != null, "null");
            Log.Add("可见窗口数：" + windows.Count);
        }

        // MARK: 界面语言（D-12）

        private static void Languages()
        {
            foreach (var (culture, expected) in new[] { ("zh-CN", Language.ZhHans), ("zh-Hans", Language.ZhHans), ("zh-TW", Language.ZhHant),
                                                         ("zh-HK", Language.ZhHant), ("zh-Hant", Language.ZhHant), ("en-US", Language.En), ("ja-JP", Language.En) })
                Check($"系统语言 {culture} → {expected}", L.FromCulture(culture) == expected, L.FromCulture(culture).ToString());
            var time = new DateTime(2026, 10, 21, 9, 5, 7);
            var names = new List<string>();
            var space = new Shortcut(Keys.Space, Mods.Alt);
            foreach (var language in new[] { Language.En, Language.ZhHans, Language.ZhHant })
            {
                L.Choice = language;
                names.Add(CapturedImage.DefaultFileName(time));
                names.Add(space.Title);
                using (var form = new HotKeyRecorderForm(Shortcut.Presets[0])) names.Add(form.Text);
            }
            L.Choice = Language.ZhHans;
            Check("默认文件名三种语言", names[0] == "Screenshot 2026-10-21 09.05.07.png" && names[3] == "截图 2026-10-21 09.05.07.png"
                && names[6] == "截圖 2026-10-21 09.05.07.png", string.Join(" | ", names));
            Check("按键名跟着语言：Alt+Space / Alt+空格 / Alt+空白鍵", names[1] == "Alt+Space" && names[4] == "Alt+空格" && names[7] == "Alt+空白鍵",
                string.Join(" | ", names));
            Check("录制窗口标题三种语言", names[2] == "Custom Capture Shortcut" && names[5] == "自定义截图快捷键" && names[8] == "自訂截圖快速鍵",
                string.Join(" | ", names));
            Check("语言菜单里的名字用各自的写法", L.Title(Language.En) == "English" && L.Title(Language.ZhHans) == "简体中文" && L.Title(Language.ZhHant) == "繁體中文");
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
