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
        public float FontSize => new float[] { 14, 18, 26 }[SizeIndex];
        public float Width(Tool tool) => tool == Tool.Mosaic ? new float[] { 12, 22, 34 }[SizeIndex] : new float[] { 2, 4, 6 }[SizeIndex];
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
        /// <summary>画下时所在屏幕的缩放，箭头头部的固定长度按它放大。</summary>
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
