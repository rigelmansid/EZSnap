using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace EZSnap
{
    /// <summary>钉在屏幕上的截图：可拖动，滚轮缩放，双击或 Esc 关闭，右键菜单。</summary>
    internal sealed class PinForm : Form
    {
        private const int WM_NCLBUTTONDBLCLK = 0x00A3;
        private readonly CapturedImage captured;
        private readonly Settings settings;
        private float zoom = 1;

        public PinForm(CapturedImage captured, Rectangle bounds, Settings settings)
        {
            this.captured = captured;
            this.settings = settings;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            Bounds = bounds;
            Text = L.T("EZSnap Pin", "EZSnap 钉图", "EZSnap 釘圖");
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);

            // 每次弹出时按当前语言重建（D-12）。
            var menu = new ContextMenuStrip();
            menu.Opening += (s, e) =>
            {
                menu.Items.Clear();
                menu.Items.Add(L.T("Copy", "复制", "複製"), null, (a, b) => Copy());
                menu.Items.Add(L.T("Save As…", "另存为…", "另存新檔…"), null, (a, b) => captured.SaveWithDialog(settings));
                menu.Items.Add(L.T("Actual Size", "恢复原始大小", "恢復原始大小"), null, (a, b) => SetZoom(1));
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add(L.T("Close", "关闭", "關閉"), null, (a, b) => Close());
                e.Cancel = false;
            };
            ContextMenuStrip = menu;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ClassStyle |= Native.CS_DROPSHADOW;
                cp.ExStyle |= Native.WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_NCLBUTTONDBLCLK)
            {
                Close();
                return;
            }
            // 拖到 DPI 不同的屏幕上也保持原来的像素大小。
            if (m.Msg == Native.WM_DPICHANGED) return;
            base.WndProc(ref m);
        }

        private void Copy()
        {
            try { captured.CopyToClipboard(); } catch (System.Runtime.InteropServices.ExternalException) { }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.InterpolationMode = Math.Abs(zoom - 1) < 0.001 ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(captured.Image, ClientRectangle);
            using (var pen = new Pen(ContainsFocus ? Theme.Accent : Color.FromArgb(153, 128, 128, 128)))
                g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }

        protected override void OnActivated(EventArgs e) { base.OnActivated(e); Invalidate(); }
        protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); Invalidate(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            // 交给系统按标题栏拖动；双击由 WM_NCLBUTTONDBLCLK 关闭。
            Native.ReleaseCapture();
            Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, (IntPtr)Native.HTCAPTION, IntPtr.Zero);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            SetZoom(Math.Min(Math.Max(zoom * (float)Math.Exp(e.Delta / 120.0 * 0.1), 0.2f), 5f));
        }

        private void SetZoom(float value)
        {
            zoom = value;
            var size = new Size((int)Math.Round(captured.Image.Width * zoom), (int)Math.Round(captured.Image.Height * zoom));
            var center = new Point(Left + Width / 2, Top + Height / 2);
            Bounds = new Rectangle(center.X - size.Width / 2, center.Y - size.Height / 2, size.Width, size.Height);
            Invalidate();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Escape: Close(); return true;
                case Keys.Control | Keys.C: Copy(); return true;
                case Keys.Control | Keys.S: captured.SaveWithDialog(settings); return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            captured.Image.Dispose();
        }
    }
}
