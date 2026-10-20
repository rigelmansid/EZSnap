import AppKit
import Carbon

struct HotKeyOption {
    let title: String
    let keyCode: UInt32
    let carbonModifiers: UInt32
    let menuModifiers: NSEvent.ModifierFlags
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    static let hotKeyOptions = [
        HotKeyOption(title: "⌃⌘A", keyCode: UInt32(kVK_ANSI_A), carbonModifiers: UInt32(controlKey | cmdKey),
                     menuModifiers: [.control, .command]),
        HotKeyOption(title: "⌥A", keyCode: UInt32(kVK_ANSI_A), carbonModifiers: UInt32(optionKey),
                     menuModifiers: [.option]),
        HotKeyOption(title: "⇧⌘A", keyCode: UInt32(kVK_ANSI_A), carbonModifiers: UInt32(shiftKey | cmdKey),
                     menuModifiers: [.shift, .command]),
    ]

    private var statusItem: NSStatusItem!
    private let hotKey = HotKey()
    private var hotKeyRegistered = true
    private var session: CaptureSession?
    private var isCapturing = false
    private var pins: [PinWindow] = []

    private var hotKeyIndex: Int {
        get { min(max(UserDefaults.standard.integer(forKey: "hotKeyIndex"), 0), Self.hotKeyOptions.count - 1) }
        set { UserDefaults.standard.set(newValue, forKey: "hotKeyIndex") }
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.mainMenu = Self.makeMainMenu()
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        let icon = NSImage(systemSymbolName: "scissors", accessibilityDescription: "截图")
        icon?.isTemplate = true
        statusItem.button?.image = icon
        HotKey.onPress = { [weak self] in self?.startCapture() }
        applyHotKey()
        if !CGPreflightScreenCaptureAccess() {
            CGRequestScreenCaptureAccess()
        }
    }

    // MARK: - 菜单

    private func applyHotKey() {
        let option = Self.hotKeyOptions[hotKeyIndex]
        hotKeyRegistered = hotKey.register(keyCode: option.keyCode, modifiers: option.carbonModifiers)
        rebuildMenu()
    }

    private func rebuildMenu() {
        let menu = NSMenu()
        let option = Self.hotKeyOptions[hotKeyIndex]

        let capture = NSMenuItem(title: "截图", action: #selector(captureFromMenu), keyEquivalent: "a")
        capture.keyEquivalentModifierMask = option.menuModifiers
        capture.target = self
        menu.addItem(capture)

        let hotKeyMenu = NSMenu()
        for (i, o) in Self.hotKeyOptions.enumerated() {
            let item = NSMenuItem(title: o.title, action: #selector(chooseHotKey(_:)), keyEquivalent: "")
            item.tag = i
            item.target = self
            item.state = i == hotKeyIndex ? .on : .off
            hotKeyMenu.addItem(item)
        }
        let hotKeyItem = NSMenuItem(title: hotKeyRegistered ? "快捷键" : "快捷键（注册失败，可能被占用）",
                                    action: nil, keyEquivalent: "")
        hotKeyItem.submenu = hotKeyMenu
        menu.addItem(hotKeyItem)

        let closePins = NSMenuItem(title: "关闭所有钉图", action: #selector(closeAllPins), keyEquivalent: "")
        closePins.target = self
        menu.addItem(closePins)

        menu.addItem(.separator())
        let permission = NSMenuItem(title: "屏幕录制权限设置…", action: #selector(openPermissionSettings), keyEquivalent: "")
        permission.target = self
        menu.addItem(permission)
        menu.addItem(NSMenuItem(title: "退出", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q"))

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
        main.addItem(submenu("Snapshot", []))
        main.addItem(submenu("文件", [
            NSMenuItem(title: "保存…", action: #selector(NSDocument.save(_:)), keyEquivalent: "s"), // 即 saveDocument:
        ]))
        main.addItem(submenu("编辑", [
            NSMenuItem(title: "撤销", action: Selector(("undo:")), keyEquivalent: "z"),
            NSMenuItem(title: "重做", action: Selector(("redo:")), keyEquivalent: "Z"),
            .separator(),
            NSMenuItem(title: "剪切", action: #selector(NSText.cut(_:)), keyEquivalent: "x"),
            NSMenuItem(title: "拷贝", action: #selector(NSText.copy(_:)), keyEquivalent: "c"),
            NSMenuItem(title: "粘贴", action: #selector(NSText.paste(_:)), keyEquivalent: "v"),
            NSMenuItem(title: "全选", action: #selector(NSText.selectAll(_:)), keyEquivalent: "a"),
        ]))
        return main
    }

    @objc private func chooseHotKey(_ sender: NSMenuItem) {
        hotKeyIndex = sender.tag
        applyHotKey()
    }

    @objc private func closeAllPins() {
        for pin in pins { pin.close() }
    }

    @objc private func openPermissionSettings() {
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture") {
            NSWorkspace.shared.open(url)
        }
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
                self.showAlert(title: "截图失败", message: error.localizedDescription)
            }
        }
    }

    private func handle(_ result: CaptureResult, previousApp: NSRunningApplication?) {
        // 遮罩窗口还在处理当前事件，下一轮再释放。
        DispatchQueue.main.async { self.session = nil }
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
        alert.messageText = "需要屏幕录制权限"
        alert.informativeText = "请在“系统设置 → 隐私与安全性 → 屏幕与系统录音”中打开 Snapshot，然后退出并重新打开 Snapshot。"
        alert.addButton(withTitle: "打开系统设置")
        alert.addButton(withTitle: "取消")
        NSApp.activate()
        if alert.runModal() == .alertFirstButtonReturn { openPermissionSettings() }
        // 提示框开着时按下的快捷键会排到它关掉以后才处理，这些按键不能再弹一次。
        HotKey.ignoreBefore = GetCurrentEventTime()
    }

    private func showAlert(title: String, message: String) {
        let alert = NSAlert()
        alert.messageText = title
        alert.informativeText = message
        NSApp.activate()
        alert.runModal()
    }
}
