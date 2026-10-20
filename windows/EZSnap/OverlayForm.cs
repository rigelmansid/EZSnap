using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Windows.Forms;

namespace EZSnap
{
    /// <summary>
    /// 一块屏幕上的截图遮罩：画冻结的截图、遮罩、选区和标注里落在本屏的部分、放大镜、工具条。
    /// 选区等状态在 CaptureSession 里（全局坐标）；这里把鼠标键盘事件换成全局坐标交给它。
    /// 本地坐标加上 GlobalBounds.Location 就是全局坐标。
    /// </summary>
    internal sealed class OverlayForm : Form
    {
        private readonly CaptureSession session;
        public readonly ScreenShot Shot;
        public readonly MosaicSource Mosaic;
        /// <summary>鼠标在本屏上的位置（本地坐标），不在本屏时为 null。由会话按全局位置统一设置。</summary>
        public Point? Mouse { get; set; }
        private readonly ToolTip tip = new ToolTip { ShowAlways = true };
        private string lastTip;
        private bool shiftHeld;

        public Rectangle GlobalBounds => Shot.Bounds;
        public float DpiScale => Shot.Scale;

        public OverlayForm(CaptureSession session, ScreenShot shot)
        {
            this.session = session;
            Shot = shot;
            Mosaic = new MosaicSource { Small = MakeMosaic(shot.Image, Math.Max(1, (int)Math.Round(10 * shot.Scale))), Bounds = shot.Bounds };
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            Bounds = shot.Bounds;
            BackColor = Color.Black;
            Cursor = Cursors.Cross;
            Text = "EZSnap";
            KeyPreview = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque, true);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_TOOLWINDOW; // 不出现在 Alt+Tab 里
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            // 跨到另一块 DPI 不同的屏幕时系统会建议改窗口大小；遮罩要一直和这块屏幕一样大，忽略它。
            if (m.Msg == Native.WM_DPICHANGED)
            {
                Bounds = Shot.Bounds;
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Bounds = Shot.Bounds;
        }

        private PointF Global(Point local) => new PointF(local.X + GlobalBounds.X, local.Y + GlobalBounds.Y);

        // MARK: 事件（换成全局坐标交给会话）

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (e.Button == MouseButtons.Left) session.MouseDragged(Global(e.Location));
            else session.MouseMoved(Global(e.Location));
            UpdateTip(e.Location);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            session.MouseExited(this);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Activate();
            if (e.Button == MouseButtons.Right)
            {
                session.RightMouseDown();
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            if (session.ToolbarHost == this && session.Toolbar.HitTest(e.Location))
            {
                Capture = false;
                session.Toolbar.MouseDown(e.Location);
                Invalidate();
                return;
            }
            session.MouseDown(Global(e.Location), e.Clicks, this);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left && !IsDisposed) session.MouseUp(Global(e.Location));
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (session.IsEditingText) return base.ProcessCmdKey(ref msg, keyData);
            return session.HandleKey(keyData) || base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            // 只认 Shift 键本身按下的那一下，按住不放的自动重复不算。
            if (e.KeyCode == Keys.ShiftKey && !shiftHeld && !session.IsEditingText)
            {
                shiftHeld = true;
                session.ShowHex = !session.ShowHex;
            }
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            if (e.KeyCode == Keys.ShiftKey) shiftHeld = false;
        }

        private void UpdateTip(Point p)
        {
            var text = session.ToolbarHost == this ? session.Toolbar.TipAt(p) : null;
            if (text == lastTip) return;
            lastTip = text;
            if (text == null) tip.Hide(this);
            else tip.Show(text, this, p.X + 12, p.Y + (int)(20 * DpiScale), 3000);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                tip.Dispose();
                Mosaic.Small?.Dispose();
            }
            base.Dispose(disposing);
        }

        // MARK: 绘制

        protected override void OnPaint(PaintEventArgs e) => Render(e.Graphics);

        /// <summary>画整块遮罩。自检直接拿它往图片上画，所以不依赖窗口有没有显示。</summary>
        public void Render(Graphics g)
        {
            var client = new Rectangle(Point.Empty, GlobalBounds.Size);
            var origin = GlobalBounds.Location;
            g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawImage(Shot.Image, client, client, GraphicsUnit.Pixel);
            g.CompositingMode = CompositingMode.SourceOver;

            var phase = session.CurrentPhase;
            RectangleF? holeGlobal = phase == CaptureSession.Phase.Choosing ? session.Hover : session.Selection;
            RectangleF? hole = holeGlobal.HasValue
                ? new RectangleF(holeGlobal.Value.X - origin.X, holeGlobal.Value.Y - origin.Y, holeGlobal.Value.Width, holeGlobal.Value.Height)
                : (RectangleF?)null;

            using (var dim = new Region(client))
            using (var dimBrush = new SolidBrush(Theme.Dim))
            {
                if (hole.HasValue) dim.Exclude(hole.Value);
                g.FillRegion(dimBrush, dim);
            }

            if (phase != CaptureSession.Phase.Choosing)
            {
                var state = g.Save();
                g.SetClip(new RectangleF(session.Selection.X - origin.X, session.Selection.Y - origin.Y, session.Selection.Width, session.Selection.Height));
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                // 标注是全局坐标，平移到本屏来画；马赛克用本屏自己的底图。
                g.TranslateTransform(-origin.X, -origin.Y);
                var mosaics = new[] { Mosaic };
                foreach (var a in session.Annotations) a.Draw(g, mosaics);
                session.Current?.Draw(g, mosaics);
                g.Restore(state);
            }

            if (hole.HasValue)
            {
                var h = hole.Value;
                var thick = phase == CaptureSession.Phase.Choosing;
                using (var pen = new Pen(Theme.Accent, thick ? 3 * DpiScale : 1))
                {
                    if (thick)
                    {
                        var inset = 1.5f * DpiScale;
                        g.DrawRectangle(pen, h.X + inset, h.Y + inset, h.Width - inset * 2, h.Height - inset * 2);
                    }
                    else
                    {
                        g.DrawRectangle(pen, h.X - 1, h.Y - 1, h.Width + 1, h.Height + 1);
                    }
                }
                // 尺寸标签只画在左上角所在的那块屏幕上。
                if (client.Contains(Point.Truncate(h.Location))) DrawSizeLabel(g, h, session.SizeText(holeGlobal.Value));
            }
            if (phase == CaptureSession.Phase.Selected)
            {
                var s = 3 * DpiScale;
                using (var brush = new SolidBrush(Theme.Accent))
                    foreach (var p in session.HandlePoints)
                        g.FillRectangle(brush, p.X - origin.X - s, p.Y - origin.Y - s, s * 2, s * 2);
            }
            if (Mouse.HasValue && session.ShowsMagnifier) DrawMagnifier(g, Mouse.Value);
            if (session.ToolbarHost == this) session.Toolbar.Paint(g);
        }

        private void DrawSizeLabel(Graphics g, RectangleF r, string text)
        {
            using (var font = new Font("Segoe UI", 12 * DpiScale, GraphicsUnit.Pixel))
            using (var bg = new SolidBrush(Color.FromArgb(191, 0, 0, 0)))
            {
                var size = g.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic);
                var box = new RectangleF(r.X, r.Y - size.Height - 10 * DpiScale, (float)Math.Ceiling(size.Width) + 12 * DpiScale, (float)Math.Ceiling(size.Height) + 6 * DpiScale);
                if (box.Y < 2) box.Location = new PointF(r.X + 4 * DpiScale, r.Y + 4 * DpiScale);
                box.X = Math.Min(box.X, Width - box.Width - 2);
                var smoothing = g.SmoothingMode;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var path = Toolbar.Rounded(box, 4 * DpiScale)) g.FillPath(bg, path);
                g.SmoothingMode = smoothing;
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                g.DrawString(text, font, Brushes.White, box.X + 6 * DpiScale, box.Y + 3 * DpiScale, StringFormat.GenericTypographic);
            }
        }

        private void DrawMagnifier(Graphics g, Point p)
        {
            const int cols = 23, rows = 15;
            var zoom = (float)Math.Round(6 * DpiScale);
            var boxSize = new SizeF(cols * zoom, rows * zoom);
            var infoHeight = 54 * DpiScale;
            var origin = new PointF(p.X + 20 * DpiScale, p.Y + 24 * DpiScale);
            if (origin.X + boxSize.Width > Width) origin.X = p.X - 20 * DpiScale - boxSize.Width;
            if (origin.Y + boxSize.Height + infoHeight > Height) origin.Y = p.Y - 24 * DpiScale - boxSize.Height - infoHeight;
            var box = new RectangleF(origin, boxSize);

            g.FillRectangle(Brushes.Black, box);
            var source = new Rectangle(p.X - cols / 2, p.Y - rows / 2, cols, rows);
            var visible = Rectangle.Intersect(source, new Rectangle(Point.Empty, GlobalBounds.Size));
            if (visible.Width > 0 && visible.Height > 0)
            {
                var dest = new RectangleF(box.X + (visible.X - source.X) * zoom, box.Y + (visible.Y - source.Y) * zoom, visible.Width * zoom, visible.Height * zoom);
                var state = g.Save();
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(Shot.Image, dest, visible, GraphicsUnit.Pixel);
                g.Restore(state);
            }
            using (var cross = new SolidBrush(Color.FromArgb(115, Theme.Accent)))
            {
                g.FillRectangle(cross, box.X, box.Y + rows / 2 * zoom, box.Width, zoom);
                g.FillRectangle(cross, box.X + cols / 2 * zoom, box.Y, zoom, box.Height);
            }
            g.DrawRectangle(Pens.White, box.X, box.Y, box.Width - 1, box.Height - 1);

            var info = new RectangleF(box.X, box.Bottom, box.Width, infoHeight);
            using (var bg = new SolidBrush(Color.FromArgb(204, 0, 0, 0))) g.FillRectangle(bg, info);
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            using (var mono = new Font("Consolas", 11 * DpiScale, GraphicsUnit.Pixel))
            using (var ui = new Font(Theme.FontName, 11 * DpiScale, GraphicsUnit.Pixel))
            using (var hint = new SolidBrush(Color.FromArgb(179, 179, 179)))
            {
                g.DrawString($"({p.X}, {p.Y})", mono, Brushes.White, info.X + 6 * DpiScale, info.Y + 4 * DpiScale);
                var color = PixelColor(p);
                if (color.HasValue)
                {
                    var swatch = new RectangleF(info.X + 6 * DpiScale, info.Y + 22 * DpiScale, 10 * DpiScale, 10 * DpiScale);
                    using (var fill = new SolidBrush(color.Value)) g.FillRectangle(fill, swatch);
                    g.DrawRectangle(Pens.White, swatch.X, swatch.Y, swatch.Width, swatch.Height);
                    g.DrawString(session.ColorText(color.Value), mono, Brushes.White, info.X + 21 * DpiScale, info.Y + 19 * DpiScale);
                }
                g.DrawString("C 复制颜色  Shift 切换格式", ui, hint, info.X + 6 * DpiScale, info.Y + 36 * DpiScale);
            }
        }

        // MARK: 取色

        /// <summary>鼠标所在像素的颜色；鼠标不在本屏时为 null。</summary>
        public Color? ColorUnderMouse() => Mouse.HasValue ? PixelColor(Mouse.Value) : null;

        private Color? PixelColor(Point p)
        {
            if (p.X < 0 || p.Y < 0 || p.X >= Shot.Image.Width || p.Y >= Shot.Image.Height) return null;
            var c = Shot.Image.GetPixel(p.X, p.Y);
            return Color.FromArgb(c.R, c.G, c.B);
        }

        private static Bitmap MakeMosaic(Bitmap image, int block)
        {
            var small = new Bitmap(Math.Max(1, image.Width / block), Math.Max(1, image.Height / block));
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.DrawImage(image, new Rectangle(Point.Empty, small.Size));
            }
            return small;
        }
    }
}
