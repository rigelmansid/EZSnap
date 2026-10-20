using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Linq;
using System.Windows.Forms;

namespace EZSnap
{
    internal enum CaptureKind { Cancelled, Copied, Save, Pin }

    internal sealed class CaptureResult
    {
        public CaptureKind Kind;
        public CapturedImage Image;
        /// <summary>钉图窗口的位置（屏幕坐标，物理像素）。</summary>
        public Rectangle PinBounds;
    }

    /// <summary>
    /// 一次截图。每块屏幕一个遮罩窗口；选区、标注、工具条和文字框由会话统一管理，可以跨屏（同 Mac 版 D-8）。
    /// 坐标都是全局坐标：虚拟屏幕的物理像素（主屏左上角为原点）。各屏的遮罩只画落在本屏的部分，
    /// 鼠标键盘事件换成全局坐标后交给会话。
    /// </summary>
    internal sealed class CaptureSession
    {
        public enum Phase { Choosing, Dragging, Selected }
        private enum DragKind { None, Creating, Moving, Resizing, Drawing }
        private enum Finish { Copy, Save, Pin }
        private enum Handle { TopLeft, Top, TopRight, Right, BottomRight, Bottom, BottomLeft, Left }

        public event Action<CaptureResult> Finished;
        public readonly List<OverlayForm> Forms;
        public readonly List<Annotation> Annotations = new List<Annotation>();
        public readonly Toolbar Toolbar = new Toolbar();

        public Phase CurrentPhase { get; private set; } = Phase.Choosing;
        public RectangleF Selection { get; private set; }
        public RectangleF? Hover { get; private set; }
        public Annotation Current { get; private set; }
        public OverlayForm ToolbarHost { get; private set; }
        public bool IsEditingText => textBox != null;

        private bool showHex;
        /// <summary>放大镜里的颜色格式，各块屏幕共用，按 Shift 切换。</summary>
        public bool ShowHex { get => showHex; set { showHex = value; Redraw(); } }

        private DragKind drag;
        private PointF dragStart;
        private RectangleF dragOriginal;
        private Handle dragHandle;
        private Tool? tool;
        private TextBox textBox;
        private OverlayForm textHost;
        private bool finished;
        private PointF? mouse;
        private readonly List<Rectangle> windowRects;
        /// <summary>所有屏幕的外接矩形。选区和拖动都限制在这里面，屏幕之间的空隙导出时是透明的。</summary>
        private readonly RectangleF desktop;
        private readonly AnnotationStyle style = AnnotationStyle.Shared;

        public CaptureSession(List<ScreenShot> shots, List<Rectangle> windowRects)
        {
            this.windowRects = windowRects;
            Forms = shots.Select(s => new OverlayForm(this, s)).ToList();
            var union = shots[0].Bounds;
            foreach (var s in shots) union = Rectangle.Union(union, s.Bounds);
            desktop = union;
            Toolbar.ToolClicked += ToggleTool;
            Toolbar.UndoClicked += Undo;
            Toolbar.SaveClicked += () => DoFinish(Finish.Save);
            Toolbar.PinClicked += () => DoFinish(Finish.Pin);
            Toolbar.CancelClicked += () => Close(CaptureKind.Cancelled);
            Toolbar.DoneClicked += () => DoFinish(Finish.Copy);
            Toolbar.StyleChanged += () => { ApplyStyleToTextBox(); Redraw(); };
        }

        public void Begin()
        {
            foreach (var f in Forms) f.Show();
            var p = Cursor.Position;
            MouseMoved(p);
            var under = Forms.FirstOrDefault(f => f.GlobalBounds.Contains(p)) ?? Forms[0];
            under.Activate();
            Native.SetForegroundWindow(under.Handle);
        }

        private void Close(CaptureKind kind, CapturedImage image = null, Rectangle pin = default)
        {
            if (finished) return;
            finished = true;
            foreach (var f in Forms)
            {
                f.Hide();
                // 遮罩可能还在处理当前事件，下一轮再释放（同 Mac 版）。
                if (f.IsHandleCreated) f.BeginInvoke((Action)f.Dispose); else f.Dispose();
            }
            Finished?.Invoke(new CaptureResult { Kind = kind, Image = image, PinBounds = pin });
        }

        public void Redraw()
        {
            foreach (var f in Forms) f.Invalidate();
        }

        private OverlayForm FormAt(PointF p) => Forms.FirstOrDefault(f => f.GlobalBounds.Contains(Point.Truncate(p)));

        private float ScaleAt(PointF p) => FormAt(p)?.DpiScale ?? Forms[0].DpiScale;

        // MARK: 给各屏遮罩画图用

        public bool ShowsMagnifier
        {
            get
            {
                switch (drag)
                {
                    case DragKind.Resizing: return true;
                    case DragKind.Moving:
                    case DragKind.Drawing: return false;
                    default: return CurrentPhase != Phase.Selected;
                }
            }
        }

        public IEnumerable<PointF> HandlePoints =>
            CurrentPhase == Phase.Selected ? AllHandles.Select(h => HandlePoint(h, Selection)) : Enumerable.Empty<PointF>();

        /// <summary>尺寸标签上的像素数，和导出一致。</summary>
        public string SizeText(RectangleF r)
        {
            var px = PixelRect(r);
            return px.Width + " × " + px.Height;
        }

        public string ColorText(Color c) => ShowHex ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : $"RGB({c.R},{c.G},{c.B})";

        // MARK: 鼠标（全局坐标）

        public void MouseMoved(PointF p)
        {
            if (finished) return;
            TrackMouse(p, CurrentPhase != Phase.Selected);
            UpdateCursor(p);
            if (CurrentPhase != Phase.Choosing) return;
            var hover = WindowRect(p);
            if (hover != Hover)
            {
                Hover = hover;
                Redraw();
            }
        }

        public void MouseExited(OverlayForm form)
        {
            form.Mouse = null;
            form.Invalidate();
            // 移到另一块屏幕时，那块屏幕的移动事件会接着更新高亮；所有屏幕上都没有鼠标了才清掉。
            if (Forms.Any(f => f.Mouse != null)) return;
            mouse = null;
            if (CurrentPhase == Phase.Choosing)
            {
                Hover = null;
                Redraw();
            }
        }

        public void MouseDown(PointF point, int clicks, OverlayForm form)
        {
            if (finished) return;
            var p = Clamp(point);
            if (textBox != null)
            {
                // 正在输入文字时，点一下只结束输入（点在另一块屏幕上也一样）。
                CommitText();
                return;
            }
            switch (CurrentPhase)
            {
                case Phase.Choosing:
                case Phase.Dragging:
                    drag = DragKind.Creating;
                    dragStart = p;
                    break;
                case Phase.Selected:
                    if (clicks == 2 && Selection.Contains(p))
                    {
                        DoFinish(Finish.Copy);
                        return;
                    }
                    var h = HandleAt(p);
                    if (h != null)
                    {
                        drag = DragKind.Resizing;
                        dragHandle = h.Value;
                        dragOriginal = Selection;
                    }
                    else if (Selection.Contains(p) && tool != null)
                    {
                        if (tool == Tool.Text)
                        {
                            BeginText(p, form);
                        }
                        else
                        {
                            var scale = ScaleAt(p);
                            Current = new Annotation
                            {
                                Tool = tool.Value,
                                Color = style.Color,
                                Width = style.Width(tool.Value) * scale,
                                Scale = scale,
                                Points = { p },
                            };
                            drag = DragKind.Drawing;
                        }
                    }
                    else if (Selection.Contains(p) && Annotations.Count == 0)
                    {
                        drag = DragKind.Moving;
                        dragStart = p;
                        dragOriginal = Selection;
                        SetCursor(p, Cursors.SizeAll);
                    }
                    break;
            }
            LayoutToolbar();
        }

        public void MouseDragged(PointF point)
        {
            if (finished) return;
            var p = Clamp(point);
            // 拖动的事件一直发给按下时那块屏幕的窗口；放大镜要跟着鼠标到它实际所在的屏幕。
            TrackMouse(p, true);
            switch (drag)
            {
                case DragKind.None:
                    return;
                case DragKind.Creating:
                    if (CurrentPhase != Phase.Dragging)
                    {
                        if (Distance(p, dragStart) <= 3) return;
                        CurrentPhase = Phase.Dragging;
                        Hover = null;
                    }
                    Selection = Annotation.RectFrom(dragStart, p);
                    break;
                case DragKind.Moving:
                    var r = dragOriginal;
                    r.Offset(p.X - dragStart.X, p.Y - dragStart.Y);
                    r.X = Math.Min(Math.Max(r.X, desktop.Left), desktop.Right - r.Width);
                    r.Y = Math.Min(Math.Max(r.Y, desktop.Top), desktop.Bottom - r.Height);
                    Selection = r;
                    break;
                case DragKind.Resizing:
                    Selection = Resize(dragHandle, dragOriginal, p);
                    break;
                case DragKind.Drawing:
                    Current?.Extend(p);
                    break;
            }
            LayoutToolbar();
            Redraw();
        }

        public void MouseUp(PointF point)
        {
            if (finished) return;
            switch (drag)
            {
                case DragKind.Creating:
                    if (CurrentPhase == Phase.Dragging)
                    {
                        if (Selection.Width >= 4 && Selection.Height >= 4) Select(); else Reset();
                    }
                    else
                    {
                        // 没有拖动：选中按下处的窗口（横跨几块屏幕也整个选中），没有窗口就选那块屏幕。
                        Selection = WindowRect(dragStart);
                        Select();
                    }
                    break;
                case DragKind.Drawing:
                    if (Current != null && Current.IsMeaningful) Annotations.Add(Current);
                    Current = null;
                    break;
                case DragKind.Resizing:
                    if (Selection.Width < 4 || Selection.Height < 4)
                        Selection = new RectangleF(Selection.X, Selection.Y, Math.Max(Selection.Width, 4), Math.Max(Selection.Height, 4));
                    break;
            }
            drag = DragKind.None;
            UpdateUndoButton();
            LayoutToolbar();
            UpdateCursor(Clamp(point));
            Redraw();
        }

        public void RightMouseDown()
        {
            // 右键：有选区时取消选区，没有选区时退出截图。
            if (CurrentPhase == Phase.Choosing) Close(CaptureKind.Cancelled); else Reset();
        }

        /// <summary>按全局位置更新各屏遮罩记录的鼠标位置：只有鼠标所在的那块屏有值。</summary>
        private void TrackMouse(PointF p, bool redraws)
        {
            var under = FormAt(p);
            mouse = under != null ? p : (PointF?)null;
            foreach (var f in Forms)
            {
                Point? local = f == under ? new Point((int)Math.Floor(p.X) - f.GlobalBounds.X, (int)Math.Floor(p.Y) - f.GlobalBounds.Y) : (Point?)null;
                if (f.Mouse == local) continue;
                f.Mouse = local;
                if (redraws) f.Invalidate();
            }
        }

        // MARK: 键盘

        /// <summary>遮罩收到的按键（不在输入文字时）。返回是否处理了。</summary>
        public bool HandleKey(Keys keyData)
        {
            var key = keyData & Keys.KeyCode;
            var mods = keyData & Keys.Modifiers;
            if (key == Keys.Escape)
            {
                Close(CaptureKind.Cancelled);
                return true;
            }
            if (mods == Keys.Control)
            {
                switch (key)
                {
                    case Keys.C: if (CurrentPhase == Phase.Selected) DoFinish(Finish.Copy); return true;
                    case Keys.S: if (CurrentPhase == Phase.Selected) DoFinish(Finish.Save); return true;
                    case Keys.Z: Undo(); return true;
                }
                return false;
            }
            if (CurrentPhase == Phase.Selected)
            {
                if (key == Keys.Return) { DoFinish(Finish.Copy); return true; }
                return false;
            }
            if (mods == Keys.None && key == Keys.C)
            {
                var f = Forms.FirstOrDefault(x => x.Mouse != null);
                var color = f?.ColorUnderMouse();
                if (color == null) return false;
                try { Clipboard.SetText(ColorText(color.Value)); } catch (System.Runtime.InteropServices.ExternalException) { }
                Close(CaptureKind.Cancelled);
                return true;
            }
            return false;
        }

        public void SaveCommand() => DoFinish(Finish.Save);

        // MARK: 状态

        private void Select()
        {
            CurrentPhase = Phase.Selected;
            Hover = null;
            LayoutToolbar();
        }

        private void Reset()
        {
            RemoveTextBox();
            CurrentPhase = Phase.Choosing;
            drag = DragKind.None;
            Selection = RectangleF.Empty;
            Annotations.Clear();
            Current = null;
            tool = null;
            Toolbar.SelectedTool = null;
            UpdateUndoButton();
            Hover = mouse.HasValue ? WindowRect(mouse.Value) : (RectangleF?)null;
            LayoutToolbar();
            if (mouse.HasValue) UpdateCursor(mouse.Value);
            Redraw();
        }

        private void ToggleTool(Tool t)
        {
            CommitText();
            tool = tool == t ? (Tool?)null : t;
            Toolbar.SelectedTool = tool;
            LayoutToolbar();
            Redraw();
        }

        /// <summary>有标注或正在输入文字时可以撤销；撤销正在输入的文字框会把它整个去掉。</summary>
        private void UpdateUndoButton() => Toolbar.CanUndo = textBox != null || Annotations.Count > 0;

        private void Undo()
        {
            if (textBox != null) RemoveTextBox();
            else if (Annotations.Count > 0) Annotations.RemoveAt(Annotations.Count - 1);
            UpdateUndoButton();
            // 撤销完可能又能拖动选区了，光标马上跟着变，不等鼠标再动。
            if (mouse.HasValue) UpdateCursor(mouse.Value);
            Redraw();
        }

        private void DoFinish(Finish action)
        {
            CommitText();
            var shot = RenderSelection();
            if (shot == null)
            {
                Close(CaptureKind.Cancelled);
                return;
            }
            switch (action)
            {
                case Finish.Copy:
                    try { shot.CopyToClipboard(); } catch (System.Runtime.InteropServices.ExternalException) { }
                    Close(CaptureKind.Copied, shot);
                    break;
                case Finish.Save:
                    Close(CaptureKind.Save, shot);
                    break;
                case Finish.Pin:
                    // 钉图窗口按导出图实际的像素范围摆放，和图片一样大。
                    Close(CaptureKind.Pin, shot, PixelRect(Selection));
                    break;
            }
        }

        // MARK: 文字

        private void BeginText(PointF p, OverlayForm form)
        {
            var box = new TextBox
            {
                BorderStyle = BorderStyle.None,
                // WinForms 的输入框不能透明；用深灰底，提交后文字直接画在截图上。
                BackColor = Color.FromArgb(48, 48, 48),
                Multiline = false,
            };
            textBox = box;
            textHost = form;
            ApplyStyleToTextBox();
            box.Location = new Point((int)p.X - form.GlobalBounds.X, (int)p.Y - form.GlobalBounds.Y - box.Height / 2);
            box.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Return || e.KeyCode == Keys.Escape)
                {
                    e.SuppressKeyPress = true;
                    CommitText();
                }
                else if (e.KeyData == (Keys.Control | Keys.S))
                {
                    e.SuppressKeyPress = true;
                    DoFinish(Finish.Save);
                }
            };
            box.TextChanged += (s, e) => FitTextBox();
            form.Controls.Add(box);
            box.BringToFront();
            UpdateUndoButton();
            form.Activate();
            box.Focus();
        }

        private void ApplyStyleToTextBox()
        {
            if (textBox == null) return;
            var scale = textHost?.DpiScale ?? 1;
            textBox.Font?.Dispose();
            textBox.Font = new Font(Theme.FontName, style.FontSize * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            textBox.ForeColor = style.Color;
            FitTextBox();
        }

        private void FitTextBox()
        {
            if (textBox == null) return;
            var size = TextRenderer.MeasureText(textBox.Text.Length > 0 ? textBox.Text : " ", textBox.Font);
            var scale = textHost?.DpiScale ?? 1;
            textBox.Width = Math.Max((int)(40 * scale), size.Width + (int)(14 * scale));
        }

        private void RemoveTextBox()
        {
            if (textBox == null) return;
            var host = textHost;
            textBox.Parent?.Controls.Remove(textBox);
            textBox.Dispose();
            textBox = null;
            textHost = null;
            host?.Focus();
        }

        private void CommitText()
        {
            if (textBox == null) return;
            var box = textBox;
            var host = textHost;
            var text = box.Text;
            // 输入框在宿主屏的坐标里，换回全局坐标；左边留 1 像素对齐输入框里的文字。
            var origin = new PointF(box.Left + 1 + host.GlobalBounds.X, box.Top + host.GlobalBounds.Y);
            var color = box.ForeColor;
            var fontSize = box.Font.Size;
            RemoveTextBox();
            if (text.Length > 0)
                Annotations.Add(new Annotation { Tool = Tool.Text, Color = color, Width = 0, Points = { origin }, Text = text, FontSize = fontSize, Scale = host.DpiScale });
            UpdateUndoButton();
            Redraw();
        }

        // MARK: 布局与光标

        private void LayoutToolbar()
        {
            var visible = drag != DragKind.Moving && drag != DragKind.Resizing && CurrentPhase == Phase.Selected;
            var host = visible ? ToolbarHostFor(Selection) : null;
            if (host != ToolbarHost)
            {
                ToolbarHost?.Invalidate();
                ToolbarHost = host;
            }
            Toolbar.Visible = host != null;
            if (host == null) return;
            Toolbar.Scale = host.DpiScale;
            var sel = Selection;
            sel.Offset(-host.GlobalBounds.X, -host.GlobalBounds.Y);
            var bounds = host.ClientRectangle;
            var size = Toolbar.Size;
            var margin = 6 * host.DpiScale;
            var x = Math.Min(Math.Max(sel.Right - size.Width, margin), bounds.Width - size.Width - margin);
            var y = sel.Bottom + margin;
            if (y + size.Height > bounds.Height - margin)
            {
                y = sel.Top - margin - size.Height;
                if (y < margin) y = sel.Bottom - size.Height - margin;
            }
            Toolbar.Location = new Point((int)Math.Round(x), (int)Math.Round(y));
            host.Invalidate();
        }

        /// <summary>工具条放在选区右下角所在的屏幕；右下角落在屏幕之间的空隙里时，放在选区中心所在的屏幕。</summary>
        private OverlayForm ToolbarHostFor(RectangleF s) =>
            FormAt(new PointF(s.Right - 1, s.Bottom - 1)) ?? FormAt(new PointF(s.X + s.Width / 2, s.Y + s.Height / 2))
            ?? Forms.FirstOrDefault(f => f.GlobalBounds.IntersectsWith(Rectangle.Round(s))) ?? Forms[0];

        private void SetCursor(PointF p, Cursor cursor)
        {
            var f = FormAt(p);
            if (f != null && f.Cursor != cursor) f.Cursor = cursor;
        }

        private void UpdateCursor(PointF p)
        {
            Cursor cursor;
            if (CurrentPhase != Phase.Selected)
            {
                cursor = Cursors.Cross;
            }
            else
            {
                var host = ToolbarHost;
                var h = HandleAt(p);
                if (host != null && Toolbar.HitTest(new Point((int)p.X - host.GlobalBounds.X, (int)p.Y - host.GlobalBounds.Y)))
                    cursor = Cursors.Default;
                else if (h != null)
                    cursor = HandleCursor(h.Value);
                else if (Selection.Contains(p) && tool != null)
                    cursor = tool == Tool.Text ? Cursors.IBeam : Cursors.Cross;
                else if (Selection.Contains(p) && Annotations.Count == 0)
                    cursor = Cursors.SizeAll;
                else
                    cursor = Cursors.Default;
            }
            SetCursor(p, cursor);
        }

        private static readonly Handle[] AllHandles = (Handle[])Enum.GetValues(typeof(Handle));

        private static PointF HandlePoint(Handle h, RectangleF r)
        {
            float midX = r.X + r.Width / 2, midY = r.Y + r.Height / 2;
            switch (h)
            {
                case Handle.TopLeft: return new PointF(r.Left, r.Top);
                case Handle.Top: return new PointF(midX, r.Top);
                case Handle.TopRight: return new PointF(r.Right, r.Top);
                case Handle.Right: return new PointF(r.Right, midY);
                case Handle.BottomRight: return new PointF(r.Right, r.Bottom);
                case Handle.Bottom: return new PointF(midX, r.Bottom);
                case Handle.BottomLeft: return new PointF(r.Left, r.Bottom);
                default: return new PointF(r.Left, midY);
            }
        }

        private Handle? HandleAt(PointF p)
        {
            if (CurrentPhase != Phase.Selected) return null;
            var tolerance = 6 * ScaleAt(p);
            foreach (var h in AllHandles)
            {
                var c = HandlePoint(h, Selection);
                if (Math.Abs(c.X - p.X) <= tolerance && Math.Abs(c.Y - p.Y) <= tolerance) return h;
            }
            return null;
        }

        private static Cursor HandleCursor(Handle h)
        {
            switch (h)
            {
                case Handle.Top:
                case Handle.Bottom: return Cursors.SizeNS;
                case Handle.Left:
                case Handle.Right: return Cursors.SizeWE;
                case Handle.TopLeft:
                case Handle.BottomRight: return Cursors.SizeNWSE;
                default: return Cursors.SizeNESW;
            }
        }

        private static RectangleF Resize(Handle h, RectangleF r, PointF p)
        {
            float x0 = r.Left, x1 = r.Right, y0 = r.Top, y1 = r.Bottom;
            switch (h)
            {
                case Handle.TopLeft: x0 = p.X; y0 = p.Y; break;
                case Handle.Top: y0 = p.Y; break;
                case Handle.TopRight: x1 = p.X; y0 = p.Y; break;
                case Handle.Right: x1 = p.X; break;
                case Handle.BottomRight: x1 = p.X; y1 = p.Y; break;
                case Handle.Bottom: y1 = p.Y; break;
                case Handle.BottomLeft: x0 = p.X; y1 = p.Y; break;
                case Handle.Left: x0 = p.X; break;
            }
            return Annotation.RectFrom(new PointF(x0, y0), new PointF(x1, y1));
        }

        private RectangleF WindowRect(PointF p)
        {
            foreach (var r in windowRects)
            {
                if (!r.Contains(Point.Truncate(p))) continue;
                var clipped = RectangleF.Intersect(r, desktop);
                if (clipped.Width > 1 && clipped.Height > 1) return clipped;
            }
            var f = FormAt(p);
            return f != null ? (RectangleF)f.GlobalBounds : desktop;
        }

        private PointF Clamp(PointF p) =>
            new PointF(Math.Min(Math.Max(p.X, desktop.Left), desktop.Right), Math.Min(Math.Max(p.Y, desktop.Top), desktop.Bottom));

        private static double Distance(PointF a, PointF b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

        // MARK: 导出

        /// <summary>矩形对应的像素范围：四条边各自取整。</summary>
        public static Rectangle PixelRect(RectangleF r)
        {
            int x0 = (int)Math.Round(r.Left), x1 = (int)Math.Round(r.Right);
            int y0 = (int)Math.Round(r.Top), y1 = (int)Math.Round(r.Bottom);
            return Rectangle.FromLTRB(x0, y0, x1, y1);
        }

        public IEnumerable<MosaicSource> MosaicSources => Forms.Select(f => f.Mosaic);

        /// <summary>按物理像素裁出选区并画上标注。跨屏时把各块屏幕的部分拼起来，屏幕之间的空隙留透明。</summary>
        public CapturedImage RenderSelection()
        {
            var px = PixelRect(Selection);
            if (px.Width < 1 || px.Height < 1) return null;
            var image = new Bitmap(px.Width, px.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(image))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                foreach (var f in Forms)
                {
                    var part = Rectangle.Intersect(f.GlobalBounds, px);
                    if (part.Width <= 0 || part.Height <= 0) continue;
                    var dest = new Rectangle(part.X - px.X, part.Y - px.Y, part.Width, part.Height);
                    var src = new Rectangle(part.X - f.GlobalBounds.X, part.Y - f.GlobalBounds.Y, part.Width, part.Height);
                    g.DrawImage(f.Shot.Image, dest, src, GraphicsUnit.Pixel);
                }
                if (Annotations.Count > 0)
                {
                    g.CompositingMode = CompositingMode.SourceOver;
                    g.PixelOffsetMode = PixelOffsetMode.Default;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAlias;
                    g.TranslateTransform(-px.X, -px.Y);
                    foreach (var a in Annotations) a.Draw(g, MosaicSources);
                }
            }
            return new CapturedImage { Image = image };
        }

        // MARK: 自检用

        public void SimulateHover(PointF p)
        {
            TrackMouse(p, true);
            Hover = WindowRect(p);
        }

        public void SimulateSelection(RectangleF rect, Tool? selectedTool, IEnumerable<Annotation> annotations)
        {
            Selection = rect;
            CurrentPhase = Phase.Selected;
            tool = selectedTool;
            Annotations.Clear();
            Annotations.AddRange(annotations);
            Toolbar.SelectedTool = selectedTool;
            UpdateUndoButton();
            LayoutToolbar();
        }
    }
}
