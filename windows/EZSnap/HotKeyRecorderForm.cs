using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace EZSnap
{
    /// <summary>录制自定义快捷键的小窗口（同 Mac 版 D-6）。按下新组合键后按回车或点“确定”生效；Esc、“取消”或关掉窗口则放弃。</summary>
    internal sealed class HotKeyRecorderForm : Form
    {
        private readonly Shortcut current;
        private readonly Label message = new Label { AutoSize = false };
        private readonly Button ok = new Button { Text = "确定" };
        private readonly Panel box = new Panel();
        public Shortcut? Recorded { get; private set; }

        public HotKeyRecorderForm(Shortcut current)
        {
            this.current = current;
            Text = "自定义截图快捷键";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = true;
            TopMost = true;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96, 96);
            Font = new Font(Theme.FontName, 9);
            ClientSize = new Size(340, 168);
            KeyPreview = true;
            Icon = TrayApp.LoadIcon();

            var prompt = new Label { Text = "按下新的截图快捷键：", Location = new Point(20, 16), AutoSize = true };
            box.SetBounds(20, 42, 300, 44);
            box.Paint += PaintBox;
            message.SetBounds(20, 92, 300, 32);
            message.Font = new Font(Theme.FontName, 8.25f);
            var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel };
            cancel.SetBounds(146, 128, 84, 28);
            ok.SetBounds(236, 128, 84, 28);
            ok.Click += (s, e) => Confirm();
            Controls.AddRange(new Control[] { prompt, box, message, cancel, ok });
            UpdateState();
        }

        /// <summary>截下所有按键，包括带 Ctrl、Alt 的组合（它们在 ProcessCmdKey 这一层就能拿到）。</summary>
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            var key = keyData & Keys.KeyCode;
            var modifiers = keyData & Keys.Modifiers;
            // 只按了修饰键本身，等它和别的键一起按。
            if (key == Keys.ShiftKey || key == Keys.ControlKey || key == Keys.Menu || key == Keys.LWin || key == Keys.RWin) return true;
            var win = (Native.GetKeyState(Native.VK_LWIN) & 0x8000) != 0 || (Native.GetKeyState(Native.VK_RWIN) & 0x8000) != 0;
            if (modifiers == Keys.None && !win && key == Keys.Escape)
            {
                DialogResult = DialogResult.Cancel;
                return true;
            }
            if (modifiers == Keys.None && !win && key == Keys.Return)
            {
                Confirm();
                return true;
            }
            var mods = Mods.None;
            if (modifiers.HasFlag(Keys.Control)) mods |= Mods.Control;
            if (modifiers.HasFlag(Keys.Alt)) mods |= Mods.Alt;
            if (modifiers.HasFlag(Keys.Shift)) mods |= Mods.Shift;
            if (win) mods |= Mods.Win;
            Recorded = new Shortcut(key, mods);
            UpdateState();
            return true;
        }

        private void Confirm()
        {
            if (Recorded == null || Recorded.Value.Problem != null) return;
            DialogResult = DialogResult.OK;
        }

        private void UpdateState()
        {
            var problem = Recorded?.Problem;
            if (problem != null)
            {
                message.Text = problem;
                message.ForeColor = Color.FromArgb(220, 40, 40);
            }
            else
            {
                message.Text = Recorded == null ? "Esc 取消" : "按回车或点“确定”生效";
                message.ForeColor = SystemColors.GrayText;
            }
            ok.Enabled = Recorded != null && problem == null;
            box.Invalidate();
        }

        private void PaintBox(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(1, 1, box.Width - 3, box.Height - 3);
            using (var path = Toolbar.Rounded(r, 6))
            using (var fill = new SolidBrush(SystemColors.Window))
            using (var border = new Pen(Recorded?.Problem == null ? Theme.Accent : Color.FromArgb(220, 40, 40), 2))
            {
                g.FillPath(fill, path);
                g.DrawPath(border, path);
            }
            // 还没按新组合键时，灰色显示现在用的。
            var text = (Recorded ?? current).Title;
            using (var font = new Font(Theme.FontName, 14))
                TextRenderer.DrawText(g, text, font, box.ClientRectangle, Recorded == null ? SystemColors.GrayText : SystemColors.WindowText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }
}
