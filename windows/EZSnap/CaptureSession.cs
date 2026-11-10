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
        private enum DragKind { None, Creating, Moving, Resizing, Drawing, Editing }
        private enum Finish { Copy, Save, Pin }
        private enum Handle { TopLeft, Top, TopRight, Right, BottomRight, Bottom, BottomLeft, Left }

        /// <summary>标注的控制点：矩形、椭圆是外接矩形的 8 个点（Box），箭头是两端（End 为 0 或 1）（D-20）。</summary>
        private struct ShapeHandle
        {
            public bool IsEnd;
            public int End;
            public Handle Box;
        }

        public event Action<CaptureResult> Finished;
        /// <summary>
        /// 写剪贴板（参数是写入动作和提示框的父窗口）：成功返回 true；别的程序占着剪贴板时提示用户并返回 false，截图不结束（D-25）。
        /// 自检换成不碰真实剪贴板、不弹对话框的版本。
        /// </summary>
        public Func<Action, IWin32Window, bool> WriteClipboard = ClipboardHelper.TryWrite;
        public readonly List<OverlayForm> Forms;
        public readonly List<Annotation> Annotations = new List<Annotation>();
        public readonly Toolbar Toolbar = new Toolbar();

        public Phase CurrentPhase { get; private set; } = Phase.Choosing;
        public RectangleF Selection { get; private set; }
        public RectangleF? Hover { get; private set; }
        public Annotation Current { get; private set; }
        public OverlayForm ToolbarHost { get; private set; }
        public bool IsEditingText => textBox != null;

        /// <summary>选中的标注（Annotations 的下标）：矩形、椭圆、箭头、文字或画笔（D-20、D-21）。工具条的样式面板跟着显示它的样式。</summary>
        public int? SelectedAnnotation
        {
            get => selectedAnnotation;
            private set
            {
                selectedAnnotation = value;
                Toolbar.PanelTool = value.HasValue ? Annotations[value.Value].Tool : (Tool?)null;
            }
        }
        private int? selectedAnnotation;

        /// <summary>正在重新编辑的文字标注（下标）。编辑期间不画它，由输入框代替（D-21）。</summary>
        public int? EditingText { get; private set; }

        /// <summary>每次改动标注前的整组标注（深拷贝），撤销时取回（D-20）。</summary>
        private readonly List<List<Annotation>> history = new List<List<Annotation>>();
        private int editIndex;
        private ShapeHandle? editHandle;
        private Annotation editOriginal;
        private List<Annotation> editBefore;

        private bool showHex;
        /// <summary>放大镜里的颜色格式，各块屏幕共用，按 Shift 切换。</summary>
        public bool ShowHex { get => showHex; set { showHex = value; Redraw(); } }

        private DragKind drag;
        private PointF dragStart;
        private RectangleF dragOriginal;
        private Handle dragHandle;
        private Tool? tool;
        private TextBox textBox;
        /// <summary>输入框当前用的字体，由这里创建和释放（坑 10）。</summary>
        private Font textFont;
        /// <summary>输入框字号按哪个缩放换算：新建时是输入框所在屏的，重新编辑时是这个文字自己的 Scale（D-25）。</summary>
        private float textScale = 1;
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
            Toolbar.StyleChanged += ApplyStyle;
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
                    case DragKind.Editing: return editHandle != null;
                    default: return CurrentPhase != Phase.Selected;
                }
            }
        }

        public IEnumerable<PointF> HandlePoints =>
            CurrentPhase == Phase.Selected ? AllHandles.Select(h => HandlePoint(h, Selection)) : Enumerable.Empty<PointF>();

        /// <summary>选中的标注；画控制点用。</summary>
        public Annotation SelectedShape => CurrentPhase == Phase.Selected && SelectedAnnotation.HasValue ? Annotations[SelectedAnnotation.Value] : null;

        public IEnumerable<PointF> ShapeHandlePoints =>
            SelectedShape != null ? ShapeHandles(SelectedShape).Select(x => x.Point) : Enumerable.Empty<PointF>();

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
            // 上一次按下没收到松开时，先当作在那里松开了（D-25）。
            EndGesture();
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
                        // 双击文字：重新编辑（D-21），第一下已经选中了它；双击别处：完成。
                        if (AnnotationIndexAt(p) is int t && Annotations[t].Tool == Tool.Text) EditText(t, form);
                        else DoFinish(Finish.Copy);
                        return;
                    }
                    var h = HandleAt(p);
                    if (ShapeHandleAt(p) is { } sh)
                    {
                        BeginEditing(sh.Item1, sh.Item2, p);
                    }
                    else if (h != null)
                    {
                        SelectedAnnotation = null;
                        drag = DragKind.Resizing;
                        dragHandle = h.Value;
                        dragOriginal = Selection;
                    }
                    else if (Selection.Contains(p) && AnnotationIndexAt(p) is int hit)
                    {
                        SelectAnnotation(hit);
                        BeginEditing(hit, null, p);
                    }
                    else if (Selection.Contains(p) && tool != null)
                    {
                        SelectedAnnotation = null;
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
                    else
                    {
                        SelectedAnnotation = null;
                    }
                    break;
            }
            LayoutToolbar();
            Redraw();
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
                case DragKind.Editing:
                    Annotations[editIndex] = Edited(editOriginal, editHandle, dragStart, p);
                    break;
            }
            LayoutToolbar();
            Redraw();
        }

        public void MouseUp(PointF point)
        {
            if (finished) return;
            EndGesture();
            LayoutToolbar();
            UpdateCursor(Clamp(point));
            Redraw();
        }

        /// <summary>
        /// 拖动途中失去了鼠标捕获（切到别的窗口、弹出系统对话框等），之后收不到松开：当作在最后的位置松开了（D-25）。
        /// 正常松开时 WinForms 在 MouseUp 之后也会报一次，那时手势已经结束，什么都不做。
        /// </summary>
        public void MouseCaptureLost()
        {
            if (finished || drag == DragKind.None) return;
            EndGesture();
            LayoutToolbar();
            if (mouse.HasValue) UpdateCursor(mouse.Value);
            Redraw();
        }

        /// <summary>
        /// 结束正在进行的鼠标手势，就当鼠标在最后的位置松开了：拖出的选区定下来，正在画的一笔入库，挪动、拉伸记进撤销。
        /// 鼠标还按着时也可能来 Ctrl+Z、Delete、Enter 这些命令（坑 13），所以会改标注或要导出的命令都先调用它（D-25）；
        /// 按下鼠标、失去鼠标捕获时也调用，补上没收到的松开。
        /// </summary>
        private void EndGesture()
        {
            var ended = drag;
            drag = DragKind.None;
            switch (ended)
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
                    if (Current != null && Current.IsMeaningful)
                    {
                        history.Add(Snapshot());
                        Annotations.Add(Current);
                        // 刚画完的矩形、椭圆、箭头直接选中，可以马上调整（D-20）；画笔不选中，接着画下一笔（D-21）。
                        if (Current.IsShape) SelectedAnnotation = Annotations.Count - 1;
                    }
                    Current = null;
                    break;
                case DragKind.Editing:
                    if (!SameList(Annotations, editBefore)) history.Add(editBefore);
                    editOriginal = null;
                    editBefore = null;
                    break;
                case DragKind.Resizing:
                    if (Selection.Width < 4 || Selection.Height < 4)
                        Selection = new RectangleF(Selection.X, Selection.Y, Math.Max(Selection.Width, 4), Math.Max(Selection.Height, 4));
                    break;
            }
            UpdateUndoButton();
        }

        /// <summary>正在进行的手势已经改了标注：画出了有效的一笔，或者挪动、拉伸过。这时也能撤销，撤销的就是这次手势（D-25）。</summary>
        private bool GestureChangedAnnotations =>
            drag == DragKind.Drawing ? Current != null && Current.IsMeaningful
            : drag == DragKind.Editing && !SameList(Annotations, editBefore);

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
                if (mods == Keys.None && (key == Keys.Delete || key == Keys.Back) && SelectedAnnotation != null)
                {
                    DeleteSelectedAnnotation();
                    return true;
                }
                return false;
            }
            if (mods == Keys.None && key == Keys.C)
            {
                var f = Forms.FirstOrDefault(x => x.Mouse != null);
                var color = f?.ColorUnderMouse();
                if (color == null) return false;
                // 复制失败时不退出截图，可以再按一次 C（D-25）。
                if (WriteClipboard(() => Clipboard.SetText(ColorText(color.Value)), f)) Close(CaptureKind.Cancelled);
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
            history.Clear();
            SelectedAnnotation = null;
            EditingText = null;
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
            EndGesture();
            CommitText();
            SelectedAnnotation = null;
            tool = tool == t ? (Tool?)null : t;
            Toolbar.SelectedTool = tool;
            LayoutToolbar();
            Redraw();
        }

        private void UpdateUndoButton() => Toolbar.CanUndo = CanUndo;

        /// <summary>有改动记录、正在输入文字，或者正在进行的手势改了标注时可以撤销；撤销正在输入的文字框会把它整个去掉。</summary>
        public bool CanUndo => textBox != null || history.Count > 0 || GestureChangedAnnotations;

        private void Undo()
        {
            // 没有可撤销的就什么都不做，也不打断正在进行的拖动。
            if (!CanUndo) return;
            // 拖动或画到一半时撤销：先把这次手势记下来再撤销，等于取消它（D-25）。
            EndGesture();
            if (textBox != null)
            {
                RemoveTextBox();
                // 撤销重新编辑：丢掉改动，原来的文字照旧显示。
                EditingText = null;
            }
            else if (history.Count > 0)
            {
                // 撤销的可能是添加、挪动、改大小、改样式或删除，一律换回改动前的整组标注（D-20）。
                var previous = history[history.Count - 1];
                history.RemoveAt(history.Count - 1);
                Annotations.Clear();
                Annotations.AddRange(previous);
                SelectedAnnotation = null;
            }
            UpdateUndoButton();
            LayoutToolbar();
            // 撤销完可能又能拖动选区了，光标马上跟着变，不等鼠标再动。
            if (mouse.HasValue) UpdateCursor(mouse.Value);
            Redraw();
        }

        private void DoFinish(Finish action)
        {
            // 鼠标还按着就完成：先把这一笔、这次拖动定下来，导出的图和屏幕上看到的一样（D-25）。
            EndGesture();
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
                    if (!WriteClipboard(shot.CopyToClipboard, ToolbarHostFor(Selection)))
                    {
                        // 剪贴板被别的程序占着：不结束截图，选区和标注都留着，可以再试或改为保存（D-25）。
                        shot.Image.Dispose();
                        // 提示框开着时别的屏上的遮罩还能操作，截图可能已经在那边结束了。
                        if (finished) return;
                        LayoutToolbar();
                        if (mouse.HasValue) UpdateCursor(mouse.Value);
                        Redraw();
                        return;
                    }
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

        // MARK: 调整已画的标注（D-20、D-21）

        private List<Annotation> Snapshot() => Annotations.Select(a => a.Clone()).ToList();

        private static bool SameList(List<Annotation> a, List<Annotation> b) =>
            a.Count == b.Count && a.Zip(b, (x, y) => x.SameAs(y)).All(same => same);

        /// <summary>
        /// 当前工具下能不能点选这个标注（D-21）：没选工具或选的是矩形、椭圆、箭头时都能点；
        /// 文字工具下只能点文字；画笔、马赛克工具下都点不中，点击照常画。
        /// </summary>
        private bool CanSelect(Annotation a)
        {
            if (!a.IsEditable) return false;
            switch (tool)
            {
                case null:
                case Tool.Rect:
                case Tool.Ellipse:
                case Tool.Arrow: return true;
                case Tool.Text: return a.Tool == Tool.Text;
                default: return false;
            }
        }

        /// <summary>点中的标注，后画的在上面，优先。</summary>
        private int? AnnotationIndexAt(PointF p)
        {
            var tolerance = 6 * ScaleAt(p);
            for (var i = Annotations.Count - 1; i >= 0; i--)
                if (CanSelect(Annotations[i]) && Annotations[i].HitTest(p, tolerance)) return i;
            return null;
        }

        private static IEnumerable<(ShapeHandle Handle, PointF Point)> ShapeHandles(Annotation a)
        {
            if (!a.IsShape || a.Points.Count < 2) yield break;
            if (a.Tool == Tool.Arrow)
            {
                yield return (new ShapeHandle { IsEnd = true, End = 0 }, a.Points[0]);
                yield return (new ShapeHandle { IsEnd = true, End = 1 }, a.Points[1]);
                yield break;
            }
            var r = Annotation.RectFrom(a.Points[0], a.Points[1]);
            foreach (var h in AllHandles) yield return (new ShapeHandle { Box = h }, HandlePoint(h, r));
        }

        /// <summary>选中标注的控制点。比选区的控制点优先：两者重叠时先调整标注。</summary>
        private (int, ShapeHandle)? ShapeHandleAt(PointF p)
        {
            if (CurrentPhase != Phase.Selected || !SelectedAnnotation.HasValue) return null;
            var tolerance = 6 * ScaleAt(p);
            var i = SelectedAnnotation.Value;
            foreach (var (handle, point) in ShapeHandles(Annotations[i]))
                if (Math.Abs(point.X - p.X) <= tolerance && Math.Abs(point.Y - p.Y) <= tolerance) return (i, handle);
            return null;
        }

        /// <summary>
        /// 选中标注时，工具条显示它的颜色和粗细，方便直接改。矩形、椭圆、箭头还会切到它的工具，框里接着画的是同一种（D-20）；
        /// 文字、画笔不切，否则下一次点击就会新建文字或画新线（D-21）。线宽、字号存的是乘过缩放的像素，按画下时的缩放换算回去。
        /// </summary>
        private void SelectAnnotation(int i)
        {
            var a = Annotations[i];
            SelectedAnnotation = i;
            if (a.IsShape)
            {
                tool = a.Tool;
                Toolbar.SelectedTool = a.Tool;
            }
            var c = Array.FindIndex(AnnotationStyle.Colors, x => x.ToArgb() == a.Color.ToArgb());
            if (c >= 0) style.ColorIndex = c;
            var scale = a.Scale > 0 ? a.Scale : 1;
            for (var s = 0; s < 3; s++)
            {
                var size = a.Tool == Tool.Text ? AnnotationStyle.FontSizes[s] : AnnotationStyle.WidthFor(a.Tool, s);
                var actual = a.Tool == Tool.Text ? a.FontSize : a.Width;
                if (Math.Abs(size * scale - actual) < 0.01f) style.SizeIndex = s;
            }
        }

        private void BeginEditing(int i, ShapeHandle? handle, PointF p)
        {
            drag = DragKind.Editing;
            editIndex = i;
            editHandle = handle;
            editOriginal = Annotations[i].Clone();
            editBefore = Snapshot();
            dragStart = p;
            if (handle == null) SetCursor(p, Cursors.SizeAll);
        }

        private static Annotation Edited(Annotation a, ShapeHandle? handle, PointF start, PointF p)
        {
            var b = a.Clone();
            if (handle == null)
                b.Points = a.Points.Select(q => new PointF(q.X + p.X - start.X, q.Y + p.Y - start.Y)).ToList();
            else if (handle.Value.IsEnd)
                b.Points[handle.Value.End] = p;
            else
            {
                var r = Resize(handle.Value.Box, Annotation.RectFrom(a.Points[0], a.Points[1]), p);
                b.Points = new List<PointF> { r.Location, new PointF(r.Right, r.Bottom) };
            }
            return b;
        }

        private void DeleteSelectedAnnotation()
        {
            if (!SelectedAnnotation.HasValue) return;
            // 正在拖的就是它：先把这次拖动记进撤销，再删（D-25）。
            EndGesture();
            if (SelectedAnnotation is not int i) return;
            history.Add(Snapshot());
            Annotations.RemoveAt(i);
            SelectedAnnotation = null;
            UpdateUndoButton();
            LayoutToolbar();
            if (mouse.HasValue) UpdateCursor(mouse.Value);
            Redraw();
        }

        /// <summary>工具条上改了颜色或粗细：改正在输入的文字，或者改选中的标注。</summary>
        public void ApplyStyle()
        {
            EndGesture();
            ApplyStyleToTextBox();
            if (SelectedAnnotation is int i)
            {
                var a = Annotations[i].Clone();
                a.Color = style.Color;
                if (a.Tool == Tool.Text) a.FontSize = style.FontSize * a.Scale; else a.Width = style.Width(a.Tool) * a.Scale;
                if (!a.SameAs(Annotations[i]))
                {
                    history.Add(Snapshot());
                    Annotations[i] = a;
                    UpdateUndoButton();
                }
            }
            Redraw();
        }

        /// <summary>重新编辑文字：在原处打开输入框，填上原来的字（D-21）。</summary>
        private void EditText(int i, OverlayForm form)
        {
            SelectAnnotation(i);
            SelectedAnnotation = null;
            EditingText = i;
            drag = DragKind.None;
            var a = Annotations[i];
            BeginText(a.Points[0], form, a);
            LayoutToolbar();
            Redraw();
        }

        // MARK: 文字

        /// <summary>
        /// editing 为 null 时是新建：输入框竖直居中在点击处，字号按这块屏的缩放。否则是重新编辑：p 是原来文字的左上角，
        /// 输入框放回原处，沿用这个文字原来的字号、颜色和缩放，挪到缩放不同的屏上也一样（D-25）。
        /// </summary>
        private void BeginText(PointF p, OverlayForm form, Annotation editing = null)
        {
            var box = new TextBox
            {
                BorderStyle = BorderStyle.None,
                // WinForms 的输入框不能透明；用深灰底，提交后文字直接画在截图上。
                BackColor = Color.FromArgb(48, 48, 48),
                Multiline = false,
                Text = editing?.Text ?? "",
            };
            textBox = box;
            textHost = form;
            textScale = editing == null ? form.DpiScale : editing.Scale > 0 ? editing.Scale : 1;
            SetTextBoxStyle(editing?.FontSize ?? style.FontSize * textScale, editing?.Color ?? style.Color);
            box.Location = editing == null
                ? new Point((int)p.X - form.GlobalBounds.X, (int)p.Y - form.GlobalBounds.Y - box.Height / 2)
                // 和 CommitText 的换算相反：左边留 1 像素。
                : new Point((int)Math.Round(p.X) - 1 - form.GlobalBounds.X, (int)Math.Round(p.Y) - form.GlobalBounds.Y);
            box.SelectionStart = box.Text.Length;
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

        /// <summary>工具条上改了样式：输入框换成新的颜色和字号，字号按 textScale 换算，和 ApplyStyle 改选中的文字一样。</summary>
        private void ApplyStyleToTextBox()
        {
            if (textBox != null) SetTextBoxStyle(style.FontSize * textScale, style.Color);
        }

        private void SetTextBoxStyle(float fontSize, Color color)
        {
            // 只释放自己建的字体：新输入框的 Font 是全进程共用的 Control.DefaultFont，释放了以后再建控件都会报错（坑 10）。
            var font = new Font(Theme.FontName, fontSize, FontStyle.Regular, GraphicsUnit.Pixel);
            textBox.Font = font;
            textFont?.Dispose();
            textFont = font;
            textBox.ForeColor = color;
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
            textFont?.Dispose();
            textFont = null;
            textHost = null;
            host?.Focus();
        }

        private void CommitText()
        {
            if (textBox == null) return;
            var box = textBox;
            var host = textHost;
            var scale = textScale;
            var text = box.Text;
            // 输入框在宿主屏的坐标里，换回全局坐标；左边留 1 像素对齐输入框里的文字。
            var origin = new PointF(box.Left + 1 + host.GlobalBounds.X, box.Top + host.GlobalBounds.Y);
            var color = box.ForeColor;
            var fontSize = box.Font.Size;
            RemoveTextBox();
            if (EditingText is int i)
            {
                // 重新编辑（D-21）：输入框没动过，位置沿用原来的，不经过来回换算；字删光了就删掉这个标注。
                EditingText = null;
                var a = Annotations[i].Clone();
                a.Text = text;
                a.Color = color;
                // 字号没改时沿用原值（Font 回读的字号可能有浮点误差）。改了也是按这个文字自己的缩放算的，Scale 不变（D-25）。
                if (Math.Abs(fontSize - a.FontSize) >= 0.01f) a.FontSize = fontSize;
                if (text.Length == 0)
                {
                    history.Add(Snapshot());
                    Annotations.RemoveAt(i);
                }
                else if (!a.SameAs(Annotations[i]))
                {
                    history.Add(Snapshot());
                    Annotations[i] = a;
                }
            }
            else if (text.Length > 0)
            {
                history.Add(Snapshot());
                Annotations.Add(new Annotation { Tool = Tool.Text, Color = color, Width = 0, Points = { origin }, Text = text, FontSize = fontSize, Scale = scale });
            }
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
                else if (ShapeHandleAt(p) is { } sh)
                    cursor = sh.Item2.IsEnd ? Cursors.Cross : HandleCursor(sh.Item2.Box);
                else if (h != null)
                    cursor = HandleCursor(h.Value);
                else if (Selection.Contains(p) && AnnotationIndexAt(p) != null)
                    cursor = Cursors.SizeAll;
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
            // 当作逐个画上去的：每个标注一条撤销记录。
            history.Clear();
            for (var i = 0; i < Annotations.Count; i++) history.Add(Annotations.Take(i).Select(a => a.Clone()).ToList());
            SelectedAnnotation = null;
            EditingText = null;
            Toolbar.SelectedTool = selectedTool;
            UpdateUndoButton();
            LayoutToolbar();
        }
    }
}
