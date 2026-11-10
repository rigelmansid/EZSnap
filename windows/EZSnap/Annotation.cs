using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;

namespace EZSnap
{
    internal enum Tool { Rect, Ellipse, Arrow, Pen, Text, Mosaic }

    internal static class Theme
    {
        public static readonly Color Accent = Color.FromArgb(28, 143, 250);
        public static readonly Color Dim = Color.FromArgb(107, 0, 0, 0);
        public const string FontName = "Microsoft YaHei UI";
    }

    /// <summary>当前画笔样式。整个应用共用一份，下次截图沿用上次的选择。</summary>
    internal sealed class AnnotationStyle
    {
        public static readonly AnnotationStyle Shared = new AnnotationStyle();
        public static readonly Color[] Colors =
        {
            Color.FromArgb(242, 59, 54), Color.FromArgb(255, 199, 26), Color.FromArgb(51, 199, 89), Color.FromArgb(28, 143, 250),
            Color.FromArgb(176, 82, 222), Color.FromArgb(26, 26, 26), Color.FromArgb(140, 140, 140), Color.FromArgb(255, 255, 255),
        };

        public int ColorIndex;
        public int SizeIndex = 1;

        public Color Color => Colors[ColorIndex];
        /// <summary>字号，单位是 96 DPI 下的像素，用时乘以屏幕缩放。</summary>
        public float FontSize => FontSizes[SizeIndex];
        public float Width(Tool tool) => WidthFor(tool, SizeIndex);

        public static readonly float[] FontSizes = { 14, 18, 26 };
        public static float WidthFor(Tool tool, int sizeIndex) =>
            tool == Tool.Mosaic ? new float[] { 12, 22, 34 }[sizeIndex] : new float[] { 2, 4, 6 }[sizeIndex];
    }

    /// <summary>一块屏幕的马赛克底图：整屏缩小后的图，画的时候放大铺满这块屏幕。</summary>
    internal sealed class MosaicSource
    {
        public Bitmap Small;
        public RectangleF Bounds;
    }

    /// <summary>一个标注。坐标是全局坐标（虚拟屏幕的物理像素），线宽和字号也是像素。</summary>
    internal sealed class Annotation
    {
        public Tool Tool;
        public Color Color;
        public float Width;
        /// <summary>矩形、椭圆、箭头：起点和终点；画笔、马赛克：轨迹；文字：左上角。</summary>
        public List<PointF> Points = new List<PointF>();
        public string Text = "";
        public float FontSize = 18;
        /// <summary>
        /// 画下时所在屏幕的缩放：箭头头部的固定长度按它放大；之后改粗细、字号（包括重新编辑文字）也按它换算，
        /// 挪到缩放不同的屏上也不变（D-25）。
        /// </summary>
        public float Scale = 1;

        public void Extend(PointF p)
        {
            if (Tool == Tool.Pen || Tool == Tool.Mosaic) Points.Add(p);
            else if (Points.Count > 1) Points[1] = p;
            else Points.Add(p);
        }

        public bool IsMeaningful
        {
            get
            {
                if (Points.Count == 0) return false;
                var a = Points[0];
                var b = Points[Points.Count - 1];
                switch (Tool)
                {
                    case Tool.Text: return Text.Length > 0;
                    case Tool.Pen:
                    case Tool.Mosaic: return Points.Count > 1;
                    default: return Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y)) > 3;
                }
            }
        }

        /// <summary>有控制点、能拉伸的标注：矩形、椭圆、箭头（同 Mac 版 D-20）。</summary>
        public bool IsShape => Tool == Tool.Rect || Tool == Tool.Ellipse || Tool == Tool.Arrow;

        /// <summary>画完后还能选中挪动的标注：矩形、椭圆、箭头，加上文字和画笔（D-21）。马赛克不行。</summary>
        public bool IsEditable => IsShape || Tool == Tool.Text || Tool == Tool.Pen;

        /// <summary>深拷贝。撤销记录存的是拷贝，之后改动标注不会改到记录里（D-20）。</summary>
        public Annotation Clone() => new Annotation
        {
            Tool = Tool, Color = Color, Width = Width, Points = new List<PointF>(Points), Text = Text, FontSize = FontSize, Scale = Scale,
        };

        public bool SameAs(Annotation o) =>
            o != null && Tool == o.Tool && Color.ToArgb() == o.Color.ToArgb() && Width == o.Width && Text == o.Text
            && FontSize == o.FontSize && Scale == o.Scale && Points.SequenceEqual(o.Points);

        private static readonly Bitmap MeasureImage = new Bitmap(1, 1);

        /// <summary>文字占的范围，左上角是 Points[0]，和 Draw 里的画法一致。</summary>
        public RectangleF TextBounds
        {
            get
            {
                var origin = Points.Count > 0 ? Points[0] : PointF.Empty;
                using (var g = Graphics.FromImage(MeasureImage))
                using (var font = new Font(Theme.FontName, FontSize, FontStyle.Regular, GraphicsUnit.Pixel))
                {
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                    return new RectangleF(origin, g.MeasureString(Text, font, PointF.Empty, StringFormat.GenericTypographic));
                }
            }
        }

        /// <summary>选中时画的虚线外框：椭圆是外接矩形，文字、画笔是外围留一点空的范围。矩形、箭头不画。</summary>
        public RectangleF? SelectionBox
        {
            get
            {
                var pad = 3 * Scale;
                switch (Tool)
                {
                    case Tool.Ellipse when Points.Count > 1:
                        return RectFrom(Points[0], Points[1]);
                    case Tool.Text:
                        var t = TextBounds;
                        t.Inflate(pad, pad);
                        return t;
                    case Tool.Pen when Points.Count > 0:
                        var box = RectangleF.FromLTRB(Points.Min(q => q.X), Points.Min(q => q.Y), Points.Max(q => q.X), Points.Max(q => q.Y));
                        box.Inflate(Width / 2 + pad, Width / 2 + pad);
                        return box;
                    default:
                        return null;
                }
            }
        }

        /// <summary>
        /// 点在线条上（线宽两侧各加 tolerance）算点中。矩形、椭圆只认线条，框里的空白处可以接着画新的（D-20）；
        /// 文字点在字的范围里就算（D-21）。按几何距离算，不用 GraphicsPath.Widen：退化成一条线的矩形会让它抛异常。
        /// </summary>
        public bool HitTest(PointF p, float tolerance)
        {
            if (!IsEditable || Points.Count == 0) return false;
            if (Tool == Tool.Text)
            {
                var t = TextBounds;
                t.Inflate(tolerance / 2, tolerance / 2);
                return t.Contains(p);
            }
            if (Points.Count < 2) return false;
            var reach = Width / 2 + tolerance;
            switch (Tool)
            {
                case Tool.Rect:
                    var r = RectFrom(Points[0], Points[1]);
                    var outer = RectangleF.Inflate(r, reach, reach);
                    var inner = RectangleF.Inflate(r, -reach, -reach);
                    return outer.Contains(p) && !(inner.Width > 0 && inner.Height > 0 && inner.Contains(p));
                case Tool.Ellipse:
                    return NearPolyline(EllipsePoints(RectFrom(Points[0], Points[1])), p, reach);
                case Tool.Pen:
                    return NearPolyline(Points, p, reach);
                default:
                    return NearPolyline(new List<PointF> { Points[0], Points[1] }, p, reach);
            }
        }

        private static List<PointF> EllipsePoints(RectangleF r)
        {
            float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
            return Enumerable.Range(0, 97).Select(i => i * Math.PI * 2 / 96)
                .Select(t => new PointF(cx + r.Width / 2 * (float)Math.Cos(t), cy + r.Height / 2 * (float)Math.Sin(t))).ToList();
        }

        private static bool NearPolyline(List<PointF> points, PointF p, float reach)
        {
            for (var i = 0; i + 1 < points.Count; i++)
                if (DistanceToSegment(p, points[i], points[i + 1]) <= reach) return true;
            return false;
        }

        private static double DistanceToSegment(PointF p, PointF a, PointF b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y, len2 = dx * dx + dy * dy;
            var t = len2 == 0 ? 0 : Math.Max(0, Math.Min(1, ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2));
            double x = a.X + t * dx - p.X, y = a.Y + t * dy - p.Y;
            return Math.Sqrt(x * x + y * y);
        }

        public static RectangleF RectFrom(PointF a, PointF b) =>
            RectangleF.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));

        /// <summary>在 g 上画（g 已经是全局坐标）。马赛克用给出的各屏底图，每块屏只画在自己范围内。</summary>
        public void Draw(Graphics g, IEnumerable<MosaicSource> mosaics)
        {
            if (Points.Count == 0) return;
            var start = Points[0];
            var end = Points[Points.Count - 1];
            switch (Tool)
            {
                case Tool.Rect:
                    using (var pen = new Pen(Color, Width) { LineJoin = LineJoin.Miter })
                    {
                        var r = RectFrom(start, end);
                        g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
                    }
                    break;
                case Tool.Ellipse:
                    using (var pen = new Pen(Color, Width))
                        g.DrawEllipse(pen, RectFrom(start, end));
                    break;
                case Tool.Arrow:
                    using (var path = ArrowPath(start, end))
                    using (var brush = new SolidBrush(Color))
                        if (path != null) g.FillPath(brush, path);
                    break;
                case Tool.Pen:
                    using (var pen = new Pen(Color, Width) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                    {
                        if (Points.Count > 1) g.DrawLines(pen, Points.ToArray());
                    }
                    break;
                case Tool.Mosaic:
                    DrawMosaic(g, mosaics);
                    break;
                case Tool.Text:
                    using (var font = new Font(Theme.FontName, FontSize, FontStyle.Regular, GraphicsUnit.Pixel))
                    using (var brush = new SolidBrush(Color))
                        g.DrawString(Text, font, brush, start, StringFormat.GenericTypographic);
                    break;
            }
        }

        private void DrawMosaic(Graphics g, IEnumerable<MosaicSource> mosaics)
        {
            if (Points.Count < 2 || mosaics == null) return;
            using (var line = new GraphicsPath())
            using (var widen = new Pen(Color.Black, Width) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                line.AddLines(Points.ToArray());
                line.Widen(widen);
                foreach (var m in mosaics)
                {
                    if (m?.Small == null) continue;
                    var state = g.Save();
                    g.SetClip(line, CombineMode.Intersect);
                    g.SetClip(m.Bounds, CombineMode.Intersect);
                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.DrawImage(m.Small, m.Bounds);
                    g.Restore(state);
                }
            }
        }

        /// <summary>尾部收成尖、头部是三角形的实心箭头。</summary>
        private GraphicsPath ArrowPath(PointF a, PointF b)
        {
            float dx = b.X - a.X, dy = b.Y - a.Y;
            var length = (float)Math.Sqrt(dx * dx + dy * dy);
            if (length <= 1) return null;
            float ux = dx / length, uy = dy / length, nx = -uy, ny = ux;
            var headLength = Math.Min(length, 10 * Scale + Width * 3.5f);
            var headHalf = headLength * 0.45f;
            var shaftHalf = Width * 0.6f;
            PointF At(float fromTip, float side) => new PointF(b.X - ux * fromTip + nx * side, b.Y - uy * fromTip + ny * side);
            var path = new GraphicsPath();
            path.AddPolygon(new[] { a, At(headLength * 0.8f, shaftHalf), At(headLength, headHalf), b, At(headLength, -headHalf), At(headLength * 0.8f, -shaftHalf) });
            return path;
        }
    }
}
