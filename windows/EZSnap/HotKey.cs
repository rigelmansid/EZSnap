using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace EZSnap
{
    /// <summary>修饰键，取值和 RegisterHotKey 的 MOD_* 一致。</summary>
    [Flags]
    internal enum Mods { None = 0, Alt = 1, Control = 2, Shift = 4, Win = 8 }

    /// <summary>一个快捷键：虚拟键加修饰键。Windows 的虚拟键本来就跟着键盘布局走，AZERTY 上的 A 也是 Keys.A。</summary>
    internal struct Shortcut : IEquatable<Shortcut>
    {
        public readonly Keys Key;
        public readonly Mods Mods;

        public Shortcut(Keys key, Mods mods)
        {
            Key = key & Keys.KeyCode;
            Mods = mods;
        }

        /// <summary>菜单里的三个预设，和 Mac 版对应：⌃⌘A → Ctrl+Alt+A（Windows 上常用的截图快捷键），⌥A → Alt+A，⇧⌘A → Ctrl+Shift+A。</summary>
        public static readonly Shortcut[] Presets =
        {
            new Shortcut(Keys.A, Mods.Control | Mods.Alt),
            new Shortcut(Keys.A, Mods.Alt),
            new Shortcut(Keys.A, Mods.Control | Mods.Shift),
        };

        public static bool IsFunctionKey(Keys k) => k >= Keys.F1 && k <= Keys.F24;

        public string Title
        {
            get
            {
                var parts = new List<string>();
                if (Mods.HasFlag(Mods.Control)) parts.Add("Ctrl");
                if (Mods.HasFlag(Mods.Alt)) parts.Add("Alt");
                if (Mods.HasFlag(Mods.Shift)) parts.Add("Shift");
                if (Mods.HasFlag(Mods.Win)) parts.Add("Win");
                parts.Add(KeyName(Key));
                return string.Join("+", parts);
            }
        }

        /// <summary>不能用作全局快捷键的原因；能用时为 null。规则同 Mac 版（D-6）：只带 Ctrl 的会抢走各个程序自己的快捷键。</summary>
        public string Problem
        {
            get
            {
                if (IsFunctionKey(Key)) return null;
                if ((Mods & (Mods.Control | Mods.Alt | Mods.Win)) == 0) return L.T("Include at least one of Ctrl, Alt, Win (F1–F24 can be used alone)", "要带 Ctrl、Alt、Win 中的至少一个（F1–F24 可以单独用）", "要包含 Ctrl、Alt、Win 其中至少一個（F1–F24 可單獨使用）");
                if (Mods == Mods.Control) return L.T("Ctrl alone would take over other programs' shortcuts (like Ctrl+C); add another modifier", "只带 Ctrl 会抢走各个程序自己的快捷键（如 Ctrl+C），请再加一个修饰键", "只用 Ctrl 會搶走各個程式自己的快速鍵（如 Ctrl+C），請再加一個修飾鍵");
                return null;
            }
        }

        public static string KeyName(Keys k)
        {
            if (k >= Keys.A && k <= Keys.Z) return k.ToString();
            if (k >= Keys.D0 && k <= Keys.D9) return ((int)(k - Keys.D0)).ToString();
            if (k >= Keys.NumPad0 && k <= Keys.NumPad9) return L.T("Num ", "小键盘", "數字鍵盤") + (int)(k - Keys.NumPad0);
            if (IsFunctionKey(k)) return k.ToString();
            switch (k)
            {
                case Keys.Space: return L.T("Space", "空格", "空白鍵");
                case Keys.Return: return "Enter";
                case Keys.Tab: return "Tab";
                case Keys.Back: return "Backspace";
                case Keys.Delete: return "Delete";
                case Keys.Insert: return "Insert";
                case Keys.Escape: return "Esc";
                case Keys.Left: return "←";
                case Keys.Right: return "→";
                case Keys.Up: return "↑";
                case Keys.Down: return "↓";
                case Keys.Home: return "Home";
                case Keys.End: return "End";
                case Keys.PageUp: return "PageUp";
                case Keys.PageDown: return "PageDown";
                case Keys.PrintScreen: return "PrintScreen";
            }
            // 标点键按当前键盘布局取字符。
            var ch = Native.MapVirtualKey((uint)k, 2 /* MAPVK_VK_TO_CHAR */) & 0xFFFF;
            return ch > 0x20 ? ((char)ch).ToString().ToUpperInvariant() : k.ToString();
        }

        /// <summary>存进设置的格式：“虚拟键,修饰键”。</summary>
        public string Serialize() => (int)Key + "," + (int)Mods;

        public static Shortcut? Parse(string s)
        {
            var parts = s?.Split(',');
            if (parts == null || parts.Length != 2 || !int.TryParse(parts[0], out var k) || !int.TryParse(parts[1], out var m)) return null;
            return new Shortcut((Keys)k, (Mods)m);
        }

        public bool Equals(Shortcut other) => Key == other.Key && Mods == other.Mods;
        public override bool Equals(object obj) => obj is Shortcut s && Equals(s);
        public override int GetHashCode() => ((int)Key << 4) ^ (int)Mods;
        public static bool operator ==(Shortcut a, Shortcut b) => a.Equals(b);
        public static bool operator !=(Shortcut a, Shortcut b) => !a.Equals(b);
        public override string ToString() => Title;
    }

    /// <summary>全局快捷键：用一个只收消息的隐藏窗口接 WM_HOTKEY。</summary>
    internal sealed class HotKey : NativeWindow, IDisposable
    {
        private const int Id = 0x534E; // "SN"
        private bool registered;

        public event Action Pressed;

        public HotKey()
        {
            CreateHandle(new CreateParams { Parent = new IntPtr(-3) /* HWND_MESSAGE */ });
        }

        public bool Register(Shortcut s)
        {
            Unregister();
            registered = Native.RegisterHotKey(Handle, Id, (int)s.Mods | Native.MOD_NOREPEAT, (int)s.Key);
            return registered;
        }

        public void Unregister()
        {
            if (registered) Native.UnregisterHotKey(Handle, Id);
            registered = false;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY && m.WParam.ToInt32() == Id) Pressed?.Invoke();
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            Unregister();
            DestroyHandle();
        }
    }
}
