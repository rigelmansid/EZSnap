using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace EZSnap
{
    /// <summary>
    /// 选区下方的工具条，由所在的遮罩直接画（不是子控件）。选中绘图工具后，下面多出一行粗细和颜色。
    /// 尺寸按 96 DPI 设计，乘以 Scale。只有条和面板本身接收点击，两行之间和第二行右边的空白交给遮罩。
    /// </summary>
    internal sealed class Toolbar
    {
        private enum Kind { Tool, Undo, Save, Pin, Cancel, Done }

        private sealed class Button
        {
            public Kind Kind;
            public Tool Tool;
            public float X;
            public string Tip;
        }

        private const float RowHeight = 40, PanelHeight = 34, Gap = 4, ButtonSize = 32, SizeSlot = 28, ColorSlot = 22;

        public Tool? SelectedTool;
        public bool CanUndo;
        public bool Visible;
        public float Scale = 1;
        /// <summary>左上角，所在遮罩的本地坐标（像素）。</summary>
        public Point Location;

        public event Action<Tool> ToolClicked;
        public event Action UndoClicked, SaveClicked, PinClicked, CancelClicked, DoneClicked, StyleChanged;

        private readonly List<Button> buttons = new List<Button>();
        private readonly List<float> separators = new List<float>();
        private readonly float barWidth;
        private readonly AnnotationStyle style = AnnotationStyle.Shared;

        public Toolbar()
        {
            float x = 4;
            void Add(Kind kind, string tip, Tool tool = Tool.Rect)
            {
                buttons.Add(new Button { Kind = kind, Tool = tool, X = x, Tip = tip });
                x += ButtonSize;
            }
            void Separator()
            {
                separators.Add(x + 4);
                x += 9;
            }
            Add(Kind.Tool, "矩形", Tool.Rect);
            Add(Kind.Tool, "椭圆", Tool.Ellipse);
            Add(Kind.Tool, "箭头", Tool.Arrow);
            Add(Kind.Tool, "画笔", Tool.Pen);
            Add(Kind.Tool, "文字", Tool.Text);
            Add(Kind.Tool, "马赛克", Tool.Mosaic);
            Separator();
            Add(Kind.Undo, "撤销 Ctrl+Z");
            Separator();
            Add(Kind.Save, "保存 Ctrl+S");
            Add(Kind.Pin, "钉在屏幕上");
            Separator();
            Add(Kind.Cancel, "退出截图 Esc");
            Add(Kind.Done, "完成 Enter");
            barWidth = x + 4;
        }

        private int Px(float v) => (int)Math.Round(v * Scale);

        private bool ShowsColors => SelectedTool != Tool.Mosaic;
        private float PanelWidth => 6 + SizeSlot * 3 + (ShowsColors ? 9 + ColorSlot * AnnotationStyle.Colors.Length : 0) + 6;

        public Size Size => new Size(Px(barWidth), Px(SelectedTool == null ? RowHeight : RowHeight + Gap + PanelHeight));
        private Rectangle BarRect => new Rectangle(Location, new Size(Px(barWidth), Px(RowHeight)));
        private Rectangle PanelRect => SelectedTool == null ? Rectangle.Empty
            : new Rectangle(Location.X, Location.Y + Px(RowHeight + Gap), Px(PanelWidth), Px(PanelHeight));

        public bool HitTest(Point p) => Visible && (BarRect.Contains(p) || PanelRect.Contains(p));

        /// <summary>换成工具条自己的坐标（96 DPI 单位）。</summary>
        private PointF Local(Point p) => new PointF((p.X - Location.X) / Scale, (p.Y - Location.Y) / Scale);

        private Button ButtonAt(Point p)
        {
            if (!BarRect.Contains(p)) return null;
            var l = Local(p);
            foreach (var b in buttons)
                if (l.X >= b.X && l.X < b.X + ButtonSize && l.Y >= 4 && l.Y < 4 + ButtonSize) return b;
            return null;
        }

        public string TipAt(Point p) => ButtonAt(p)?.Tip;

        public void MouseDown(Point p)
        {
            var b = ButtonAt(p);
            if (b != null)
            {
                switch (b.Kind)
                {
                    case Kind.Tool: ToolClicked?.Invoke(b.Tool); break;
                    case Kind.Undo: if (CanUndo) UndoClicked?.Invoke(); break;
                    case Kind.Save: SaveClicked?.Invoke(); break;
                    case Kind.Pin: PinClicked?.Invoke(); break;
                    case Kind.Cancel: CancelClicked?.Invoke(); break;
                    case Kind.Done: DoneClicked?.Invoke(); break;
                }
                return;
            }
            if (!PanelRect.Contains(p)) return;
            var l = Local(p);
            var y = l.Y - (RowHeight + Gap);
            for (var i = 0; i < 3; i++)
            {
                if (new RectangleF(6 + i * SizeSlot, 3, SizeSlot, 28).Contains(l.X, y))
                {
                    style.SizeIndex = i;
                    StyleChanged?.Invoke();
                    return;
                }
            }
            if (!ShowsColors) return;
            for (var i = 0; i < AnnotationStyle.Colors.Length; i++)
            {
                if (ColorRect(i).Contains(l.X, y))
                {
                    style.ColorIndex = i;
                    StyleChanged?.Invoke();
                    return;
                }
            }
        }

        private static RectangleF ColorRect(int i) => new RectangleF(6 + SizeSlot * 3 + 9 + i * ColorSlot, 6, ColorSlot, 22);

        // MARK: 绘制

        public void Paint(Graphics g)
        {
            if (!Visible) return;
            var state = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TranslateTransform(Location.X, Location.Y);
            g.ScaleTransform(Scale, Scale);
            PaintPanel(g, new RectangleF(0, 0, barWidth, RowHeight));
            using (var sep = new SolidBrush(Color.FromArgb(209, 209, 209)))
                foreach (var x in separators) g.FillRectangle(sep, x, 11, 1, 18);
            foreach (var b in buttons)
            {
                var selected = b.Kind == Kind.Tool && SelectedTool == b.Tool;
                var cell = new RectangleF(b.X, 4, ButtonSize, ButtonSize);
                if (selected)
                    using (var bg = new SolidBrush(Color.FromArgb(222, 237, 255)))
                    using (var path = Rounded(RectangleF.Inflate(cell, -3, -3), 5))
                        g.FillPath(bg, path);
                var enabled = b.Kind != Kind.Undo || CanUndo;
                var tint = b.Kind == Kind.Cancel ? Color.FromArgb(230, 64, 56) : b.Kind == Kind.Done ? Color.FromArgb(41, 173, 77) : Color.FromArgb(56, 56, 56);
                var color = !enabled ? Color.FromArgb(191, 191, 191) : selected ? Theme.Accent : tint;
                DrawIcon(g, b, cell, color);
            }
            if (SelectedTool != null) PaintStyle(g);
            g.Restore(state);
        }

        private static void PaintPanel(Graphics g, RectangleF r)
        {
            using (var shadow = Rounded(new RectangleF(r.X, r.Y + 1, r.Width, r.Height), 6))
            using (var sb = new SolidBrush(Color.FromArgb(50, 0, 0, 0)))
                g.FillPath(sb, shadow);
            using (var path = Rounded(r, 6))
            using (var fill = new SolidBrush(Color.FromArgb(250, 250, 250)))
                g.FillPath(fill, path);
        }

        private void PaintStyle(Graphics g)
        {
            var top = RowHeight + Gap;
            PaintPanel(g, new RectangleF(0, top, PanelWidth, PanelHeight));
            for (var i = 0; i < 3; i++)
            {
                var slot = new RectangleF(6 + i * SizeSlot, top + 3, SizeSlot, 28);
                var selected = i == style.SizeIndex;
                if (selected)
                    using (var bg = new SolidBrush(Color.FromArgb(222, 237, 255)))
                    using (var path = Rounded(RectangleF.Inflate(slot, -2, -2), 5))
                        g.FillPath(bg, path);
                var d = new float[] { 4, 7, 10 }[i];
                using (var dot = new SolidBrush(selected ? Theme.Accent : Color.FromArgb(89, 89, 89)))
                    g.FillEllipse(dot, slot.X + slot.Width / 2 - d / 2, slot.Y + slot.Height / 2 - d / 2, d, d);
            }
            if (!ShowsColors) return;
            using (var sep = new SolidBrush(Color.FromArgb(209, 209, 209)))
                g.FillRectangle(sep, 6 + SizeSlot * 3 + 4, top + 8, 1, 18);
            for (var i = 0; i < AnnotationStyle.Colors.Length; i++)
            {
                var r = ColorRect(i);
                var box = new RectangleF(r.X + 3, top + r.Y + 3, r.Width - 6, r.Height - 6);
                using (var fill = new SolidBrush(AnnotationStyle.Colors[i])) g.FillRectangle(fill, box);
                using (var border = new Pen(Color.FromArgb(179, 179, 179), 1)) g.DrawRectangle(border, box.X, box.Y, box.Width, box.Height);
                if (i == style.ColorIndex)
                    using (var ring = new Pen(Theme.Accent, 2))
                        g.DrawRectangle(ring, box.X - 2, box.Y - 2, box.Width + 4, box.Height + 4);
            }
        }

        private static void DrawIcon(Graphics g, Button b, RectangleF cell, Color color)
        {
            var state = g.Save();
            g.TranslateTransform(cell.X, cell.Y);
            using (var pen = new Pen(color, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            using (var brush = new SolidBrush(color))
            {
                switch (b.Kind)
                {
                    case Kind.Tool:
                        switch (b.Tool)
                        {
                            case Tool.Rect: g.DrawRectangle(pen, 9, 10, 14, 12); break;
                            case Tool.Ellipse: g.DrawEllipse(pen, 8.5f, 10, 15, 12); break;
                            case Tool.Arrow:
                                g.DrawLine(pen, 10, 22, 22, 10);
                                g.DrawLines(pen, new[] { new PointF(15, 10), new PointF(22, 10), new PointF(22, 17) });
                                break;
                            case Tool.Pen:
                                g.DrawBezier(pen, 8, 20, 12, 8, 17, 26, 24, 12);
                                break;
                            case Tool.Text:
                                using (var font = new Font("Segoe UI", 12, FontStyle.Regular, GraphicsUnit.Pixel))
                                using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                                    g.DrawString("Aa", font, brush, new RectangleF(0, 0, 32, 32), format);
                                break;
                            case Tool.Mosaic:
                                g.DrawRectangle(pen, 9, 9, 14, 14);
                                for (var i = 0; i < 3; i++)
                                    for (var j = 0; j < 3; j++)
                                        if ((i + j) % 2 == 0) g.FillRectangle(brush, 9 + i * 14 / 3f, 9 + j * 14 / 3f, 14 / 3f, 14 / 3f);
                                break;
                        }
                        break;
                    case Kind.Undo:
                        g.DrawBezier(pen, 22, 22, 24, 12, 16, 10, 10, 13);
                        g.DrawLines(pen, new[] { new PointF(14, 9), new PointF(10, 13), new PointF(14, 17) });
                        break;
                    case Kind.Save:
                        g.DrawLines(pen, new[] { new PointF(9, 18), new PointF(9, 23), new PointF(23, 23), new PointF(23, 18) });
                        g.DrawLine(pen, 16, 8, 16, 19);
                        g.DrawLines(pen, new[] { new PointF(12, 15), new PointF(16, 19), new PointF(20, 15) });
                        break;
                    case Kind.Pin:
                        g.DrawLines(pen, new[] { new PointF(13, 8), new PointF(19, 8), new PointF(18, 14), new PointF(21, 17), new PointF(11, 17), new PointF(14, 14), new PointF(13, 8) });
                        g.DrawLine(pen, 16, 17, 16, 24);
                        break;
                    case Kind.Cancel:
                        g.DrawLine(pen, 11, 11, 21, 21);
                        g.DrawLine(pen, 21, 11, 11, 21);
                        break;
                    case Kind.Done:
                        g.DrawLines(pen, new[] { new PointF(9, 16.5f), new PointF(14, 21.5f), new PointF(23, 11) });
                        break;
                }
            }
            g.Restore(state);
        }

        public static GraphicsPath Rounded(RectangleF r, float radius)
        {
            var d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
