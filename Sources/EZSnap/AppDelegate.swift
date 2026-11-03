import AppKit
import Carbon

final class AppDelegate: NSObject, NSApplicationDelegate {
    private var statusItem: NSStatusItem!
    private let hotKey = HotKey()
    private var hotKeyRegistered = true
    private var recorder: HotKeyRecorder?
    private var session: CaptureSession?
    private var isCapturing = false
    private var pins: [PinWindow] = []
    /// GitHub 上比当前新的版本（D-22）；有就在菜单最上方显示下载项。
    private var availableUpdate: UpdateChecker.Release?
    /// 新版本的提示框因为正在截图或别的提示框开着而推迟了。
    private var pendingUpdateAlert = false
    private var checkingUpdate = false
    private var updateTimer: Timer?

    /// 选中的预设序号；-1 表示用自定义快捷键（D-6）。
    private var hotKeyIndex: Int {
        get {
            let i = UserDefaults.standard.integer(forKey: "hotKeyIndex")
            return i == -1 && customShortcut != nil ? -1 : min(max(i, 0), Shortcut.presets.count - 1)
        }
        set { UserDefaults.standard.set(newValue, forKey: "hotKeyIndex") }
    }

    private var customShortcut: Shortcut? {
        get {
            guard let d = UserDefaults.standard.dictionary(forKey: "customHotKey"),
                  let keyCode = d["keyCode"] as? Int, let modifiers = d["modifiers"] as? Int else { return nil }
            return Shortcut(keyCode: UInt32(keyCode), modifiers: NSEvent.ModifierFlags(rawValue: UInt(modifiers)))
        }
        set {
            if let s = newValue {
                UserDefaults.standard.set(["keyCode": Int(s.keyCode), "modifiers": Int(s.modifiers.rawValue)],
                                          forKey: "customHotKey")
            } else {
                UserDefaults.standard.removeObject(forKey: "customHotKey")
            }
        }
    }

    private var currentShortcut: Shortcut {
        hotKeyIndex < 0 ? customShortcut ?? Shortcut.presets[0] : Shortcut.presets[hotKeyIndex]
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.mainMenu = Self.makeMainMenu()
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        let icon = NSImage(systemSymbolName: "scissors", accessibilityDescription: L("Capture", "截图", "截圖"))
        icon?.isTemplate = true
        statusItem.button?.image = icon
        HotKey.onPress = { [weak self] in self?.startCapture() }
        applyHotKey()
        DistributedNotificationCenter.default().addObserver(
            self, selector: #selector(inputSourceChanged),
            name: NSNotification.Name(kTISNotifySelectedKeyboardInputSourceChanged as String), object: nil)
        // 菜单栏应用没有窗口和 Dock 图标，第一次打开时说一声它在哪（D-9）。
        if !UserDefaults.standard.bool(forKey: "didShowWelcome") {
            UserDefaults.standard.set(true, forKey: "didShowWelcome")
            showWelcome()
        }
        if !CGPreflightScreenCaptureAccess() {
            CGRequestScreenCaptureAccess()
        }
        startUpdateChecks()
    }

    /// 已经在运行时又双击了图标：再提示一次，免得以为没反应。
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        showWelcome()
        return false
    }

    private func showWelcome() {
        showAlert(title: L("EZSnap is running in the menu bar", "EZSnap 在菜单栏里运行", "EZSnap 在選單列中執行"),
                  message: L("Click the scissors icon at the top right of the screen to open the menu; press \(currentShortcut.title) to capture.",
                             "点屏幕右上角的剪刀图标打开菜单；按 \(currentShortcut.title) 开始截图。",
                             "點螢幕右上角的剪刀圖像打開選單；按 \(currentShortcut.title) 開始截圖。"))
    }

    // MARK: - 菜单

    private func applyHotKey() {
        let shortcut = currentShortcut
        hotKeyRegistered = hotKey.register(keyCode: shortcut.keyCode, modifiers: shortcut.carbonModifiers)
        rebuildMenu()
    }

    private func rebuildMenu() {
        let menu = NSMenu()
        let shortcut = currentShortcut

        // 有新版本时放在最上方（D-22）。不配图标：同组其他项没有图标，配了会让它们空出图标位。
        if let update = availableUpdate {
            let item = NSMenuItem(title: L("Download EZSnap \(update.version)…", "下载新版本 \(update.version)…", "下載新版本 \(update.version)…"),
                                  action: #selector(openUpdatePage), keyEquivalent: "")
            item.target = self
            menu.addItem(item)
            menu.addItem(.separator())
        }

        let capture = NSMenuItem(title: L("Capture", "截图", "截圖"), action: #selector(captureFromMenu),
                                 keyEquivalent: shortcut.menuKeyEquivalent ?? "")
        if shortcut.menuKeyEquivalent == nil { capture.title = L("Capture (\(shortcut.title))", "截图（\(shortcut.title)）", "截圖（\(shortcut.title)）") }
        capture.keyEquivalentModifierMask = shortcut.modifiers
        capture.target = self
        menu.addItem(capture)

        let hotKeyMenu = NSMenu()
        for (i, preset) in Shortcut.presets.enumerated() {
            let item = NSMenuItem(title: preset.title, action: #selector(chooseHotKey(_:)), keyEquivalent: "")
            item.tag = i
            item.target = self
            item.state = i == hotKeyIndex ? .on : .off
            hotKeyMenu.addItem(item)
        }
        hotKeyMenu.addItem(.separator())
        let custom = NSMenuItem(title: hotKeyIndex < 0 ? L("Custom: \(shortcut.title)…", "自定义：\(shortcut.title)…", "自訂：\(shortcut.title)…")
                                                : L("Custom…", "自定义…", "自訂…"),
                                action: #selector(recordHotKey), keyEquivalent: "")
        custom.target = self
        custom.state = hotKeyIndex < 0 ? .on : .off
        hotKeyMenu.addItem(custom)
        let hotKeyItem = NSMenuItem(title: hotKeyRegistered ? L("Shortcut", "快捷键", "快速鍵")
                                    : L("Shortcut (registration failed, may be in use)", "快捷键（注册失败，可能被占用）", "快速鍵（註冊失敗，可能已被佔用）"),
                                    action: nil, keyEquivalent: "")
        hotKeyItem.submenu = hotKeyMenu
        menu.addItem(hotKeyItem)

        // 语言（D-12）：标题带上英文，看不懂当前语言的人也找得到。
        let languageMenu = NSMenu()
        for language in Language.allCases {
            let item = NSMenuItem(title: language.title, action: #selector(chooseLanguage(_:)), keyEquivalent: "")
            item.representedObject = language.rawValue
            item.target = self
            item.state = L10n.choice == language ? .on : .off
            languageMenu.addItem(item)
            if language == .system { languageMenu.addItem(.separator()) }
        }
        let languageItem = NSMenuItem(title: L("Language", "语言 / Language", "語言 / Language"), action: nil, keyEquivalent: "")
        languageItem.submenu = languageMenu
        menu.addItem(languageItem)

        let closePins = NSMenuItem(title: L("Close All Pins", "关闭所有钉图", "關閉所有釘圖"), action: #selector(closeAllPins), keyEquivalent: "")
        closePins.target = self
        menu.addItem(closePins)

        menu.addItem(.separator())
        let check = NSMenuItem(title: L("Check for Updates…", "检查更新…", "檢查更新…"), action: #selector(checkForUpdatesFromMenu), keyEquivalent: "")
        check.target = self
        menu.addItem(check)
        let auto = NSMenuItem(title: L("Check for Updates Automatically", "自动检查更新", "自動檢查更新"), action: #selector(toggleAutoCheck), keyEquivalent: "")
        auto.target = self
        auto.state = UpdateChecker.autoCheck ? .on : .off
        menu.addItem(auto)

        menu.addItem(.separator())
        let permission = NSMenuItem(title: L("Screen Recording Permission…", "屏幕录制权限设置…", "螢幕錄製權限設定…"), action: #selector(openPermissionSettings), keyEquivalent: "")
        permission.target = self
        // macOS 26 会自动给“退出”配图标，同组没图标的项会空出图标位；这里也配一个，两项对齐。
        permission.image = NSImage(systemSymbolName: "lock.shield", accessibilityDescription: nil)
        menu.addItem(permission)
        menu.addItem(NSMenuItem(title: L("Quit", "退出", "結束"), action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q"))

        statusItem.menu = menu
    }

    /// 不显示的主菜单，带 ⌘ 的快捷键都靠它分发（D-1）：文字框里的剪切、拷贝、粘贴、全选、撤销，
    /// 以及遮罩和钉图的 ⌘C、⌘S、⌘Z（OverlayView、PinView 里的同名方法）。
    /// 新加 ⌘ 快捷键要在这里加菜单项，不能写在 keyDown 里（坑 1）。
    static func makeMainMenu() -> NSMenu {
        func submenu(_ title: String, _ items: [NSMenuItem]) -> NSMenuItem {
            let menu = NSMenu(title: title)
            for item in items { menu.addItem(item) }
            let item = NSMenuItem(title: title, action: nil, keyEquivalent: "")
            item.submenu = menu
            return item
        }
        let main = NSMenu()
        main.addItem(submenu("EZSnap", []))
        main.addItem(submenu(L("File", "文件", "檔案"), [
            NSMenuItem(title: L("Save…", "保存…", "儲存…"), action: #selector(NSDocument.save(_:)), keyEquivalent: "s"), // 即 saveDocument:
        ]))
        main.addItem(submenu(L("Edit", "编辑", "編輯"), [
            NSMenuItem(title: L("Undo", "撤销", "還原"), action: Selector(("undo:")), keyEquivalent: "z"),
            NSMenuItem(title: L("Redo", "重做", "重做"), action: Selector(("redo:")), keyEquivalent: "Z"),
            .separator(),
            NSMenuItem(title: L("Cut", "剪切", "剪下"), action: #selector(NSText.cut(_:)), keyEquivalent: "x"),
            NSMenuItem(title: L("Copy", "拷贝", "拷貝"), action: #selector(NSText.copy(_:)), keyEquivalent: "c"),
            NSMenuItem(title: L("Paste", "粘贴", "貼上"), action: #selector(NSText.paste(_:)), keyEquivalent: "v"),
            NSMenuItem(title: L("Select All", "全选", "全選"), action: #selector(NSText.selectAll(_:)), keyEquivalent: "a"),
        ]))
        return main
    }

    @objc private func chooseHotKey(_ sender: NSMenuItem) {
        hotKeyIndex = sender.tag
        customShortcut = nil
        applyHotKey()
    }

    @objc private func recordHotKey() {
        guard recorder == nil else { return }
        // 录制期间停用全局快捷键，免得按到现在的组合键就开始截图。
        hotKey.unregister()
        recorder = HotKeyRecorder(current: currentShortcut) { [weak self] shortcut in
            guard let self else { return }
            self.recorder = nil
            guard let shortcut else { return self.applyHotKey() }
            let oldIndex = self.hotKeyIndex, oldCustom = self.customShortcut
            self.customShortcut = shortcut
            self.hotKeyIndex = -1
            self.applyHotKey()
            if !self.hotKeyRegistered {
                self.customShortcut = oldCustom
                self.hotKeyIndex = oldIndex
                self.applyHotKey()
                self.showAlert(title: L("Couldn't Set Shortcut", "快捷键设置失败", "快速鍵設定失敗"),
                               message: L("\(shortcut.title) couldn't be registered; another app may be using it. Still using \(self.currentShortcut.title).",
                                          "\(shortcut.title) 注册失败，可能被其他程序占用了。仍使用 \(self.currentShortcut.title)。",
                                          "\(shortcut.title) 註冊失敗，可能已被其他程式佔用。仍使用 \(self.currentShortcut.title)。"))
            }
        }
        recorder?.show()
    }

    @objc private func inputSourceChanged() {
        // 预设按字符定义，换了键盘布局键位可能跟着变（D-6）；录制期间不重新注册。
        if recorder == nil { applyHotKey() }
    }

    @objc private func chooseLanguage(_ sender: NSMenuItem) {
        guard let raw = sender.representedObject as? String, let language = Language(rawValue: raw) else { return }
        L10n.choice = language
        // 立即生效：菜单重建；截图界面、提示框等下次打开时按新语言显示。
        NSApp.mainMenu = Self.makeMainMenu()
        rebuildMenu()
    }

    @objc private func closeAllPins() {
        for pin in pins { pin.close() }
    }

    @objc private func openPermissionSettings() {
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture") {
            NSWorkspace.shared.open(url)
        }
    }

    // MARK: - 检查更新（D-22）

    /// 启动时查一次（自动检查打开时）；之后每小时看一眼，距上次成功检查满 24 小时再查。
    /// 睡眠期间错过的，醒来后的下一个整点会补上。
    private func startUpdateChecks() {
        if UpdateChecker.autoCheck { checkForUpdates(manual: false) }
        updateTimer = Timer.scheduledTimer(withTimeInterval: 60 * 60, repeats: true) { [weak self] _ in
            guard let self else { return }
            if UpdateChecker.isDue { self.checkForUpdates(manual: false) }
            self.showPendingUpdateAlert()
        }
    }

    @objc private func checkForUpdatesFromMenu() { checkForUpdates(manual: true) }

    @objc private func toggleAutoCheck() {
        UpdateChecker.autoCheck.toggle()
        if UpdateChecker.isDue { checkForUpdates(manual: false) }
        rebuildMenu()
    }

    @objc private func openUpdatePage() {
        if let page = availableUpdate?.page { NSWorkspace.shared.open(page) }
    }

    /// manual 为 true 时是用户点了“检查更新…”：结果总会告诉用户，包括已是最新和失败。
    /// 自动检查只在发现没提示过的新版本时弹一次，失败不打扰，下一轮再试。
    private func checkForUpdates(manual: Bool) {
        guard !checkingUpdate else { return }
        checkingUpdate = true
        Task { @MainActor in
            defer { self.checkingUpdate = false }
            let current = UpdateChecker.currentVersion
            do {
                let latest = try await UpdateChecker.fetchLatest()
                UpdateChecker.lastCheck = Date()
                let newer = UpdateChecker.isNewer(latest.version, than: current)
                self.availableUpdate = newer ? latest : nil
                self.rebuildMenu()
                if newer, manual || UpdateChecker.notifiedVersion != latest.version {
                    self.showUpdateAlert(latest, manual: manual)
                } else if manual {
                    self.showAlert(title: L("EZSnap Is Up to Date", "已是最新版本", "已是最新版本"),
                                   message: L("Version \(current) is the latest.", "当前版本 \(current) 是最新的。", "目前版本 \(current) 是最新的。"))
                }
            } catch {
                if manual {
                    self.showAlert(title: L("Couldn't Check for Updates", "检查更新失败", "檢查更新失敗"), message: error.localizedDescription)
                }
            }
        }
    }

    private func showUpdateAlert(_ release: UpdateChecker.Release, manual: Bool) {
        // 截图进行中或别的提示框开着时不弹，等截图结束或下一个整点再弹。
        if !manual, session != nil || isCapturing || NSApp.modalWindow != nil {
            pendingUpdateAlert = true
            return
        }
        pendingUpdateAlert = false
        UpdateChecker.notifiedVersion = release.version
        let alert = NSAlert()
        alert.messageText = L("EZSnap \(release.version) Is Available", "EZSnap \(release.version) 已发布", "EZSnap \(release.version) 已發佈")
        alert.informativeText = L("You have \(UpdateChecker.currentVersion). Download the new version from the GitHub release page; it can also be found later in the menu.",
                                  "当前版本 \(UpdateChecker.currentVersion)。可以到 GitHub 发布页下载新版本，之后也能在菜单里找到下载项。",
                                  "目前版本 \(UpdateChecker.currentVersion)。可以到 GitHub 發佈頁下載新版本，之後也能在選單裡找到下載項。")
        alert.addButton(withTitle: L("Download", "下载", "下載"))
        alert.addButton(withTitle: L("Later", "以后再说", "以後再說"))
        NSApp.activate()
        if alert.runModal() == .alertFirstButtonReturn { NSWorkspace.shared.open(release.page) }
        // 和权限提示框一样：提示框开着时按的快捷键不能在关掉后触发截图。
        HotKey.ignoreBefore = GetCurrentEventTime()
    }

    private func showPendingUpdateAlert() {
        guard pendingUpdateAlert, let release = availableUpdate else { return }
        showUpdateAlert(release, manual: false)
    }

    // MARK: - 截图

    @objc private func captureFromMenu() {
        // 等菜单收起，免得菜单被截进去。
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.25) { [weak self] in self?.startCapture() }
    }

    func startCapture() {
        guard session == nil, !isCapturing else { return }
        guard CGPreflightScreenCaptureAccess() else {
            showPermissionAlert()
            return
        }
        isCapturing = true
        let previousApp = NSWorkspace.shared.frontmostApplication
        let windowRects = ScreenCapturer.visibleWindowRects()
        Task { @MainActor in
            defer { self.isCapturing = false }
            do {
                let shots = try await ScreenCapturer.captureAll()
                guard !shots.isEmpty else { return }
                let session = CaptureSession(shots: shots, windowRects: windowRects)
                session.onFinish = { result in self.handle(result, previousApp: previousApp) }
                self.session = session
                session.begin()
            } catch {
                self.showAlert(title: L("Capture Failed", "截图失败", "截圖失敗"), message: error.localizedDescription)
            }
        }
    }

    private func handle(_ result: CaptureResult, previousApp: NSRunningApplication?) {
        // 遮罩窗口还在处理当前事件，下一轮再释放。截图时推迟的新版本提示框，等界面收起后再弹（D-22）。
        DispatchQueue.main.async {
            self.session = nil
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) { self.showPendingUpdateAlert() }
        }
        switch result {
        case .cancelled, .copied:
            previousApp?.activate()
        case .save(let image):
            image.saveWithPanel { previousApp?.activate() }
        case .pin(let image, let frame):
            let pin = PinWindow(image: image, frame: frame)
            pin.onClose = { [weak self] closed in self?.pins.removeAll { $0 === closed } }
            pins.append(pin)
            pin.orderFrontRegardless()
            previousApp?.activate()
        }
    }

    private func showPermissionAlert() {
        let alert = NSAlert()
        alert.messageText = L("Screen Recording Permission Needed", "需要屏幕录制权限", "需要螢幕錄製權限")
        alert.informativeText = L("Turn on EZSnap in System Settings → Privacy & Security → Screen & System Audio Recording, then quit and reopen EZSnap.\n\nIf EZSnap is already in the list and turned on, that entry was left by an earlier version and doesn't work for this one. Click Reset Permission to clear it, then turn EZSnap on again when asked.",
                                  "请在“系统设置 → 隐私与安全性 → 屏幕与系统录音”中打开 EZSnap，然后退出并重新打开 EZSnap。\n\n如果列表里已经有 EZSnap 而且是打开的，那是旧版本留下的记录，对这个版本无效：点“重置权限”清掉它，再按提示重新打开。",
                                  "請在「系統設定 → 隱私權與安全性 → 螢幕與系統錄音」中開啟 EZSnap，然後結束並重新開啟 EZSnap。\n\n如果列表裡已經有 EZSnap 而且是開啟的，那是舊版本留下的記錄，對這個版本無效：點「重置權限」清除它，再依提示重新開啟。")
        alert.addButton(withTitle: L("Open System Settings", "打开系统设置", "打開系統設定"))
        alert.addButton(withTitle: L("Reset Permission", "重置权限", "重置權限"))
        alert.addButton(withTitle: L("Cancel", "取消", "取消"))
        NSApp.activate()
        switch alert.runModal() {
        case .alertFirstButtonReturn: openPermissionSettings()
        case .alertSecondButtonReturn: resetScreenCapturePermission()
        default: break
        }
        // 提示框开着时按下的快捷键会排到它关掉以后才处理，这些按键不能再弹一次。
        HotKey.ignoreBefore = GetCurrentEventTime()
    }

    /// 清掉系统里 EZSnap 的屏幕录制记录，再重新申请（坑 11）。旧版本留下的记录显示为打开，
    /// 但签名对不上新版本，系统不认；清掉后重新申请，列表里会出现对应这个版本的新记录。
    private func resetScreenCapturePermission() {
        let id = Bundle.main.bundleIdentifier ?? "local.ezsnap.app"
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/tccutil")
        process.arguments = ["reset", "ScreenCapture", id]
        let ok: Bool
        do {
            try process.run()
            process.waitUntilExit()
            ok = process.terminationStatus == 0
        } catch {
            ok = false
        }
        guard ok else {
            showAlert(title: L("Couldn't Reset Permission", "重置权限失败", "重置權限失敗"),
                      message: L("In System Settings → Privacy & Security → Screen & System Audio Recording, select EZSnap, click −, then reopen EZSnap.",
                                 "请在“系统设置 → 隐私与安全性 → 屏幕与系统录音”中选中 EZSnap，点“−”删掉，再重新打开 EZSnap。",
                                 "請在「系統設定 → 隱私權與安全性 → 螢幕與系統錄音」中選取 EZSnap，點「−」刪除，再重新開啟 EZSnap。"))
            openPermissionSettings()
            return
        }
        // 重新申请：系统会弹出授权提示，并在列表里加上新记录（默认关着）。打开后系统会要求退出并重新打开 EZSnap。
        CGRequestScreenCaptureAccess()
    }

    private func showAlert(title: String, message: String) {
        let alert = NSAlert()
        alert.messageText = title
        alert.informativeText = message
        NSApp.activate()
        alert.runModal()
    }
}
