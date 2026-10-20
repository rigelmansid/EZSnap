import AppKit
import Carbon

/// 录制自定义快捷键的小窗口（D-6）。按下新组合键后按回车或点“确定”生效；Esc、“取消”或关掉窗口则放弃。
final class HotKeyRecorder: NSObject, NSWindowDelegate {
    private let panel = NSPanel(contentRect: NSRect(x: 0, y: 0, width: 340, height: 160),
                                styleMask: [.titled, .closable], backing: .buffered, defer: false)
    private let field: RecorderField
    private let message = NSTextField(wrappingLabelWithString: "")
    private let okButton = NSButton(title: "确定", target: nil, action: nil)
    private var completion: ((Shortcut?) -> Void)?
    private var monitor: Any?

    init(current: Shortcut, completion: @escaping (Shortcut?) -> Void) {
        field = RecorderField(current: current)
        self.completion = completion
        super.init()
        panel.title = "自定义截图快捷键"
        panel.isReleasedWhenClosed = false
        // 菜单栏应用没有 Dock 图标，窗口被盖住就不好找回来。
        panel.level = .floating
        panel.delegate = self

        let prompt = NSTextField(labelWithString: "按下新的截图快捷键：")
        prompt.frame = NSRect(x: 20, y: 122, width: 300, height: 18)
        field.frame = NSRect(x: 20, y: 72, width: 300, height: 42)
        message.frame = NSRect(x: 20, y: 42, width: 300, height: 28)
        message.font = .systemFont(ofSize: 11)
        let cancelButton = NSButton(title: "取消", target: self, action: #selector(cancel))
        cancelButton.frame = NSRect(x: 146, y: 12, width: 84, height: 28)
        okButton.frame = NSRect(x: 236, y: 12, width: 84, height: 28)
        okButton.target = self
        okButton.action = #selector(confirm)
        for view in [prompt, field, message, cancelButton, okButton] { panel.contentView?.addSubview(view) }

        field.onChange = { [weak self] in self?.update() }
        field.onConfirm = { [weak self] in self?.confirm() }
        field.onCancel = { [weak self] in self?.cancel() }
        update()
        // 在事件分发之前截下这个窗口的所有按键：主菜单里有的快捷键（如 ⌘C）没人响应也会被吞掉（坑 1）。
        monitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] event in
            guard let self, event.window === self.panel else { return event }
            self.field.handle(event)
            return nil
        }
    }

    deinit {
        if let monitor { NSEvent.removeMonitor(monitor) }
    }

    func show() {
        NSApp.activate()
        panel.center()
        panel.makeKeyAndOrderFront(nil)
        panel.makeFirstResponder(field)
    }

    private func update() {
        if let problem = field.recorded?.problem {
            message.stringValue = problem
            message.textColor = .systemRed
        } else {
            message.stringValue = field.recorded == nil ? "Esc 取消" : "按回车或点“确定”生效"
            message.textColor = .secondaryLabelColor
        }
        okButton.isEnabled = field.recorded != nil && field.recorded?.problem == nil
    }

    @objc private func confirm() {
        guard let shortcut = field.recorded, shortcut.problem == nil else { return }
        finish(shortcut)
    }

    @objc private func cancel() { finish(nil) }

    private func finish(_ shortcut: Shortcut?) {
        guard let completion else { return }
        self.completion = nil
        if let monitor { NSEvent.removeMonitor(monitor) }
        monitor = nil
        panel.orderOut(nil)
        completion(shortcut)
    }

    func windowWillClose(_ notification: Notification) { finish(nil) }
}

/// 显示并接收快捷键的框。按键由 HotKeyRecorder 的事件监视器交给 handle(_:)。
private final class RecorderField: NSView {
    var onChange: (() -> Void)?
    var onConfirm: (() -> Void)?
    var onCancel: (() -> Void)?
    private(set) var recorded: Shortcut?
    private let current: Shortcut

    init(current: Shortcut) {
        self.current = current
        super.init(frame: .zero)
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override var acceptsFirstResponder: Bool { true }

    override func keyDown(with event: NSEvent) { handle(event) }

    func handle(_ event: NSEvent) {
        let modifiers = event.modifierFlags.intersection([.control, .option, .shift, .command])
        let code = Int(event.keyCode)
        if modifiers.isEmpty, code == kVK_Escape {
            onCancel?()
        } else if modifiers.isEmpty, code == kVK_Return || code == kVK_ANSI_KeypadEnter {
            onConfirm?()
        } else {
            recorded = Shortcut(keyCode: UInt32(code), modifiers: modifiers)
            needsDisplay = true
            onChange?()
        }
    }

    override func draw(_ dirtyRect: NSRect) {
        let path = NSBezierPath(roundedRect: bounds.insetBy(dx: 1, dy: 1), xRadius: 6, yRadius: 6)
        NSColor.controlBackgroundColor.setFill()
        path.fill()
        (recorded?.problem == nil ? NSColor.controlAccentColor : NSColor.systemRed).setStroke()
        path.lineWidth = 2
        path.stroke()
        // 还没按新组合键时，灰色显示现在用的。
        let text = (recorded ?? current).title as NSString
        let attrs: [NSAttributedString.Key: Any] = [
            .font: NSFont.systemFont(ofSize: 20, weight: .medium),
            .foregroundColor: recorded == nil ? NSColor.secondaryLabelColor : NSColor.labelColor,
        ]
        let size = text.size(withAttributes: attrs)
        text.draw(at: NSPoint(x: bounds.midX - size.width / 2, y: bounds.midY - size.height / 2), withAttributes: attrs)
    }
}
