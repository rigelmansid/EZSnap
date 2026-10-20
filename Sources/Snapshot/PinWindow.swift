import AppKit

/// 钉在屏幕上的截图：可拖动，滚轮缩放，双击或 Esc 关闭，右键菜单。
final class PinWindow: NSWindow {
    var onClose: ((PinWindow) -> Void)?
    let captured: CapturedImage

    init(image: CapturedImage, frame: NSRect) {
        captured = image
        super.init(contentRect: frame, styleMask: .borderless, backing: .buffered, defer: false)
        level = .floating
        hasShadow = true
        isOpaque = false
        backgroundColor = .clear
        isReleasedWhenClosed = false
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        let view = PinView(frame: CGRect(origin: .zero, size: frame.size))
        view.pin = self
        contentView = view
        setFrame(frame, display: false)
    }

    override var canBecomeKey: Bool { true }

    override func becomeKey() {
        super.becomeKey()
        contentView?.needsDisplay = true
    }

    override func resignKey() {
        super.resignKey()
        contentView?.needsDisplay = true
    }

    override func close() {
        super.close()
        onClose?(self)
    }
}

private final class PinView: NSView {
    weak var pin: PinWindow?
    private var zoom: CGFloat = 1

    override init(frame: NSRect) {
        super.init(frame: frame)
        autoresizingMask = [.width, .height]
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override var acceptsFirstResponder: Bool { true }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    override func draw(_ dirtyRect: NSRect) {
        guard let pin else { return }
        pin.captured.nsImage.draw(in: bounds)
        (pin.isKeyWindow ? Theme.accent : NSColor(white: 0.5, alpha: 0.6)).setStroke()
        NSBezierPath(rect: bounds.insetBy(dx: 0.5, dy: 0.5)).stroke()
    }

    override func mouseDown(with event: NSEvent) {
        guard let pin else { return }
        if event.clickCount == 2 {
            pin.close()
        } else {
            pin.performDrag(with: event)
        }
    }

    override func scrollWheel(with event: NSEvent) {
        guard let pin else { return }
        let step = event.hasPreciseScrollingDeltas ? event.scrollingDeltaY * 0.01 : event.scrollingDeltaY * 0.1
        zoom = min(max(zoom * exp(step), 0.2), 5)
        let size = CGSize(width: pin.captured.pointSize.width * zoom, height: pin.captured.pointSize.height * zoom)
        let f = pin.frame
        pin.setFrame(CGRect(x: f.midX - size.width / 2, y: f.midY - size.height / 2, width: size.width, height: size.height),
                     display: true)
    }

    // ⌘C、⌘S 由主菜单分发到 copy(_:) 和 saveDocument(_:)，见 AppDelegate.makeMainMenu。
    override func keyDown(with event: NSEvent) {
        if event.keyCode == 53 { pin?.close() } else { super.keyDown(with: event) }
    }

    @objc func copy(_ sender: Any?) { copyImage() }
    @objc func saveDocument(_ sender: Any?) { saveImage() }

    override func menu(for event: NSEvent) -> NSMenu? {
        let menu = NSMenu()
        menu.addItem(withTitle: "复制", action: #selector(copyImage), keyEquivalent: "").target = self
        menu.addItem(withTitle: "另存为…", action: #selector(saveImage), keyEquivalent: "").target = self
        menu.addItem(withTitle: "恢复原始大小", action: #selector(resetZoom), keyEquivalent: "").target = self
        menu.addItem(.separator())
        menu.addItem(withTitle: "关闭", action: #selector(closePin), keyEquivalent: "").target = self
        return menu
    }

    @objc private func copyImage() { pin?.captured.copyToPasteboard() }
    @objc private func saveImage() { pin?.captured.saveWithPanel() }
    @objc private func closePin() { pin?.close() }

    @objc private func resetZoom() {
        guard let pin else { return }
        zoom = 1
        let f = pin.frame
        let size = pin.captured.pointSize
        pin.setFrame(CGRect(x: f.midX - size.width / 2, y: f.midY - size.height / 2, width: size.width, height: size.height),
                     display: true)
    }
}
