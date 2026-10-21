using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace EZSnap
{
    /// <summary>托盘程序（对应 Mac 版的 AppDelegate）：托盘图标和菜单、快捷键、截图流程、钉图管理。</summary>
    internal sealed class TrayApp : ApplicationContext
    {
        public const string ReopenEventName = @"Local\EZSnap.Reopen";

        private readonly NotifyIcon tray;
        private readonly HotKey hotKey = new HotKey();
        private readonly Settings settings = new Settings();
        private readonly Control invoker = new Control();
        private readonly List<PinForm> pins = new List<PinForm>();
        private readonly EventWaitHandle reopen;
        private CaptureSession session;
        private bool hotKeyRegistered = true;
        private bool recording;

        public TrayApp()
        {
            invoker.CreateControl();
            tray = new NotifyIcon { Icon = LoadIcon(), Text = "EZSnap", Visible = true };
            // 左键也弹出菜单，和 Mac 版点菜单栏图标一样。
            tray.MouseUp += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                    typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(tray, null);
            };
            L.Choice = L.Parse(settings.Get("language"));
            hotKey.Pressed += StartCapture;
            ApplyHotKey();

            // 托盘程序没有窗口，第一次打开时说一声它在哪（同 Mac 版 D-9）。
            if (!settings.GetBool("didShowWelcome"))
            {
                settings.Set("didShowWelcome", "1");
                ShowWelcome();
            }
            // 已经在运行时再双击图标：第二个进程发信号过来，再提示一次。
            reopen = new EventWaitHandle(false, EventResetMode.AutoReset, ReopenEventName);
            var listener = new Thread(() =>
            {
                while (reopen.WaitOne())
                {
                    if (invoker.IsDisposed) return;
                    invoker.BeginInvoke((Action)ShowWelcome);
                }
            }) { IsBackground = true };
            listener.Start();
        }

        public static Icon LoadIcon()
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("EZSnap.AppIcon.ico"))
                return stream != null ? new Icon(stream) : SystemIcons.Application;
        }

        // MARK: 快捷键

        /// <summary>选中的预设序号；-1 表示用自定义快捷键。</summary>
        private int HotKeyIndex
        {
            get
            {
                var i = settings.GetInt("hotKeyIndex", 0);
                return i == -1 && CustomShortcut != null ? -1 : Math.Min(Math.Max(i, 0), Shortcut.Presets.Length - 1);
            }
            set => settings.Set("hotKeyIndex", value.ToString());
        }

        private Shortcut? CustomShortcut
        {
            get => Shortcut.Parse(settings.Get("customHotKey"));
            set => settings.Set("customHotKey", value?.Serialize());
        }

        private Shortcut CurrentShortcut => HotKeyIndex < 0 ? CustomShortcut ?? Shortcut.Presets[0] : Shortcut.Presets[HotKeyIndex];

        private void ApplyHotKey()
        {
            hotKeyRegistered = hotKey.Register(CurrentShortcut);
            RebuildMenu();
        }

        private void RebuildMenu()
        {
            var shortcut = CurrentShortcut;
            var menu = new ContextMenuStrip();
            menu.Items.Add(new ToolStripMenuItem(L.T("Capture", "截图", "截圖"), null, (s, e) => CaptureFromMenu()) { ShortcutKeyDisplayString = shortcut.Title });

            var hotKeyItem = new ToolStripMenuItem(hotKeyRegistered ? L.T("Shortcut", "快捷键", "快速鍵")
                : L.T("Shortcut (registration failed, may be in use)", "快捷键（注册失败，可能被占用）", "快速鍵（註冊失敗，可能已被佔用）"));
            for (var i = 0; i < Shortcut.Presets.Length; i++)
            {
                var index = i;
                hotKeyItem.DropDownItems.Add(new ToolStripMenuItem(Shortcut.Presets[i].Title, null, (s, e) => ChooseHotKey(index))
                {
                    Checked = i == HotKeyIndex,
                });
            }
            hotKeyItem.DropDownItems.Add(new ToolStripSeparator());
            hotKeyItem.DropDownItems.Add(new ToolStripMenuItem(HotKeyIndex < 0 ? L.T($"Custom: {shortcut.Title}…", $"自定义：{shortcut.Title}…", $"自訂：{shortcut.Title}…") : L.T("Custom…", "自定义…", "自訂…"), null, (s, e) => RecordHotKey())
            {
                Checked = HotKeyIndex < 0,
            });
            menu.Items.Add(hotKeyItem);

            // 语言（D-12）：标题带上英文，看不懂当前语言的人也找得到。
            var languageItem = new ToolStripMenuItem(L.T("Language", "语言 / Language", "語言 / Language"));
            foreach (var language in new[] { Language.System, Language.En, Language.ZhHans, Language.ZhHant })
            {
                var chosen = language;
                languageItem.DropDownItems.Add(new ToolStripMenuItem(L.Title(chosen), null, (s, e) => ChooseLanguage(chosen)) { Checked = L.Choice == chosen });
                if (chosen == Language.System) languageItem.DropDownItems.Add(new ToolStripSeparator());
            }
            menu.Items.Add(languageItem);
            menu.Items.Add(L.T("Close All Pins", "关闭所有钉图", "關閉所有釘圖"), null, (s, e) => CloseAllPins());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(L.T("Exit", "退出", "結束"), null, (s, e) => Quit());

            var old = tray.ContextMenuStrip;
            tray.ContextMenuStrip = menu;
            old?.Dispose();
        }

        private void ChooseLanguage(Language language)
        {
            L.Choice = language;
            settings.Set("language", L.Key(language));
            // 菜单立即重建；截图界面、提示等下次打开时按新语言显示。
            RebuildMenu();
        }

        private void ChooseHotKey(int index)
        {
            HotKeyIndex = index;
            CustomShortcut = null;
            ApplyHotKey();
        }

        private void RecordHotKey()
        {
            if (recording) return;
            recording = true;
            // 录制期间停用全局快捷键，免得按到现在的组合键就开始截图。
            hotKey.Unregister();
            using (var form = new HotKeyRecorderForm(CurrentShortcut))
            {
                if (form.ShowDialog() == DialogResult.OK && form.Recorded is Shortcut shortcut)
                {
                    var oldIndex = HotKeyIndex;
                    var oldCustom = CustomShortcut;
                    CustomShortcut = shortcut;
                    HotKeyIndex = -1;
                    ApplyHotKey();
                    if (!hotKeyRegistered)
                    {
                        CustomShortcut = oldCustom;
                        HotKeyIndex = oldIndex;
                        ApplyHotKey();
                        MessageBox.Show(L.T($"{shortcut.Title} couldn't be registered; another program may be using it. Still using {CurrentShortcut.Title}.",
                                $"{shortcut.Title} 注册失败，可能被其他程序占用了。仍使用 {CurrentShortcut.Title}。",
                                $"{shortcut.Title} 註冊失敗，可能已被其他程式佔用。仍使用 {CurrentShortcut.Title}。"),
                            L.T("Couldn't Set Shortcut", "快捷键设置失败", "快速鍵設定失敗"),
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
                else
                {
                    ApplyHotKey();
                }
            }
            recording = false;
        }

        // MARK: 截图

        private void CaptureFromMenu()
        {
            // 等菜单收起，免得菜单被截进去。
            var timer = new System.Windows.Forms.Timer { Interval = 150 };
            timer.Tick += (s, e) =>
            {
                timer.Dispose();
                StartCapture();
            };
            timer.Start();
        }

        private void StartCapture()
        {
            if (session != null || recording) return;
            var previous = Native.GetForegroundWindow();
            try
            {
                var windows = Capturer.VisibleWindowRects();
                var shots = Capturer.CaptureAll();
                if (shots.Count == 0) return;
                var s = new CaptureSession(shots, windows);
                s.Finished += r => Handle(r, shots, previous);
                session = s;
                s.Begin();
            }
            catch (Exception e)
            {
                session = null;
                MessageBox.Show(e.Message, L.T("Capture Failed", "截图失败", "截圖失敗"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void Handle(CaptureResult result, List<ScreenShot> shots, IntPtr previous)
        {
            session = null;
            foreach (var shot in shots) shot.Image.Dispose();
            switch (result.Kind)
            {
                case CaptureKind.Cancelled:
                case CaptureKind.Copied:
                    result.Image?.Image.Dispose();
                    Native.SetForegroundWindow(previous);
                    break;
                case CaptureKind.Save:
                    result.Image.SaveWithDialog(settings);
                    result.Image.Image.Dispose();
                    Native.SetForegroundWindow(previous);
                    break;
                case CaptureKind.Pin:
                    var pin = new PinForm(result.Image, result.PinBounds, settings);
                    pin.FormClosed += (s, e) => pins.Remove(pin);
                    pins.Add(pin);
                    pin.Show();
                    break;
            }
        }

        private void CloseAllPins()
        {
            foreach (var pin in pins.ToArray()) pin.Close();
        }

        private void ShowWelcome()
        {
            tray.ShowBalloonTip(6000, L.T("EZSnap is running in the notification area", "EZSnap 在托盘里运行", "EZSnap 在通知區域中執行"),
                L.T($"Click the scissors icon in the notification area to open the menu (it may be under ^); press {CurrentShortcut.Title} to capture.",
                    $"点右下角托盘里的剪刀图标打开菜单（可能收在 ^ 里）；按 {CurrentShortcut.Title} 开始截图。",
                    $"點右下角通知區域中的剪刀圖示打開選單（可能收在 ^ 裡）；按 {CurrentShortcut.Title} 開始截圖。"), ToolTipIcon.Info);
        }

        private void Quit()
        {
            CloseAllPins();
            hotKey.Dispose();
            tray.Visible = false;
            tray.Dispose();
            reopen.Dispose();
            invoker.Dispose();
            ExitThread();
        }
    }
}
