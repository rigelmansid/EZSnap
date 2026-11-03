using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        /// <summary>GitHub 上比当前新的版本（D-22）；有就在菜单最上方显示下载项。</summary>
        private UpdateChecker.Release availableUpdate;
        /// <summary>新版本的提示因为正在截图或录快捷键而推迟了。</summary>
        private bool pendingUpdateAlert;
        private bool checkingUpdate;
        private bool showingUpdateAlert;
        private readonly System.Windows.Forms.Timer updateTimer = new System.Windows.Forms.Timer();

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
            StartUpdateChecks();
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
            // 有新版本时放在最上方（D-22）。
            if (availableUpdate != null)
            {
                var update = availableUpdate;
                menu.Items.Add(L.T($"Download EZSnap {update.Version}…", $"下载新版本 {update.Version}…", $"下載新版本 {update.Version}…"), null,
                    (s, e) => OpenPage(update.Page));
                menu.Items.Add(new ToolStripSeparator());
            }
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
            menu.Items.Add(L.T("Check for Updates…", "检查更新…", "檢查更新…"), null, (s, e) => CheckForUpdates(true));
            menu.Items.Add(new ToolStripMenuItem(L.T("Check for Updates Automatically", "自动检查更新", "自動檢查更新"), null, (s, e) => ToggleAutoCheck())
            {
                Checked = UpdateChecker.AutoCheck(settings),
            });
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

        // MARK: 检查更新（D-22）

        /// <summary>
        /// 启动后几秒查一次（自动检查打开时，等托盘图标和消息循环就绪）；之后每小时看一眼，距上次成功检查满 24 小时再查。
        /// 睡眠期间错过的，醒来后的下一个整点会补上。
        /// </summary>
        private void StartUpdateChecks()
        {
            var first = new System.Windows.Forms.Timer { Interval = 5000 };
            first.Tick += (s, e) =>
            {
                first.Dispose();
                if (UpdateChecker.AutoCheck(settings)) CheckForUpdates(false);
            };
            first.Start();
            updateTimer.Interval = 60 * 60 * 1000;
            updateTimer.Tick += (s, e) =>
            {
                if (UpdateChecker.IsDue(settings)) CheckForUpdates(false);
                ShowPendingUpdateAlert();
            };
            updateTimer.Start();
        }

        private void ToggleAutoCheck()
        {
            UpdateChecker.SetAutoCheck(settings, !UpdateChecker.AutoCheck(settings));
            if (UpdateChecker.IsDue(settings)) CheckForUpdates(false);
            RebuildMenu();
        }

        private static void OpenPage(Uri page)
        {
            try { Process.Start(new ProcessStartInfo(page.AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception || e is InvalidOperationException) { }
        }

        /// <summary>
        /// manual 为 true 时是用户点了“检查更新…”：结果总会告诉用户，包括已是最新和失败。
        /// 自动检查只在发现没提示过的新版本时提示一次，失败不打扰，下一轮再试。await 之后回到界面线程继续。
        /// </summary>
        private async void CheckForUpdates(bool manual)
        {
            if (checkingUpdate) return;
            checkingUpdate = true;
            var current = UpdateChecker.CurrentVersion;
            try
            {
                var latest = await UpdateChecker.FetchLatest();
                UpdateChecker.SetLastCheck(settings, DateTime.UtcNow);
                var newer = UpdateChecker.IsNewer(latest.Version, current);
                availableUpdate = newer ? latest : null;
                RebuildMenu();
                if (newer && (manual || settings.Get("notifiedUpdateVersion") != latest.Version))
                    ShowUpdateAlert(latest, manual);
                else if (manual)
                    MessageBox.Show(L.T($"Version {current} is the latest.", $"当前版本 {current} 是最新的。", $"目前版本 {current} 是最新的。"),
                        L.T("EZSnap Is Up to Date", "已是最新版本", "已是最新版本"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception e)
            {
                if (manual)
                    // HttpRequestException 的消息只有一句“发送请求时出错”，具体原因在 InnerException 里。
                    MessageBox.Show(e is System.Threading.Tasks.TaskCanceledException ? L.T("The request timed out.", "连接超时。", "連線逾時。")
                        : e is System.Net.Http.HttpRequestException && e.InnerException != null ? e.InnerException.Message : e.Message,
                        L.T("Couldn't Check for Updates", "检查更新失败", "檢查更新失敗"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                checkingUpdate = false;
            }
        }

        private void ShowUpdateAlert(UpdateChecker.Release release, bool manual)
        {
            // 截图进行中、录快捷键时或提示已经开着时不弹，等截图结束或下一个整点再弹。
            if (!manual && (session != null || recording || showingUpdateAlert))
            {
                pendingUpdateAlert = true;
                return;
            }
            pendingUpdateAlert = false;
            settings.Set("notifiedUpdateVersion", release.Version);
            showingUpdateAlert = true;
            var answer = MessageBox.Show(
                L.T($"EZSnap {release.Version} is available (you have {UpdateChecker.CurrentVersion}). Open the GitHub release page to download it? It can also be found later in the tray menu.",
                    $"EZSnap {release.Version} 已发布（当前版本 {UpdateChecker.CurrentVersion}）。要打开 GitHub 发布页下载吗？之后也能在托盘菜单里找到下载项。",
                    $"EZSnap {release.Version} 已發佈（目前版本 {UpdateChecker.CurrentVersion}）。要打開 GitHub 發佈頁下載嗎？之後也能在通知區域選單裡找到下載項。"),
                L.T("New Version Available", "有新版本", "有新版本"), MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            showingUpdateAlert = false;
            if (answer == DialogResult.Yes) OpenPage(release.Page);
        }

        private void ShowPendingUpdateAlert()
        {
            if (pendingUpdateAlert && availableUpdate != null) ShowUpdateAlert(availableUpdate, false);
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
            // 截图时推迟的新版本提示，等遮罩收起后再弹（D-22）。
            if (pendingUpdateAlert) invoker.BeginInvoke((Action)ShowPendingUpdateAlert);
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
            updateTimer.Dispose();
            hotKey.Dispose();
            tray.Visible = false;
            tray.Dispose();
            reopen.Dispose();
            invoker.Dispose();
            ExitThread();
        }
    }
}
