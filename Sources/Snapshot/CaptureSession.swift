import AppKit

enum CaptureResult {
    case cancelled
    case copied
    case save(CapturedImage)
    case pin(CapturedImage, NSRect)
}

/// 一次截图：每块屏幕一个全屏遮罩窗口，同一时间只有一块屏幕能有选区。
final class CaptureSession {
    var onFinish: ((CaptureResult) -> Void)?
    /// 放大镜里的颜色格式，各块屏幕共用，按 Shift 切换。
    var showHex = false {
        didSet { views.forEach { $0.needsDisplay = true } }
    }
    private(set) weak var activeView: OverlayView?
    private var windows: [OverlayWindow] = []
    private var views: [OverlayView] = []
    private var finished = false

    /// windowRects 是 CG 全局坐标。
    init(shots: [ScreenShot], windowRects: [CGRect]) {
        for shot in shots {
            let window = OverlayWindow(screen: shot.screen)
            let frame = CGRect(origin: .zero, size: shot.screen.frame.size)
            let rects = windowRects.map { $0.offsetBy(dx: -shot.displayBounds.minX, dy: -shot.displayBounds.minY) }
            let container = FrozenImageView(frame: frame, image: shot.image)
            let view = OverlayView(frame: frame, image: shot.image, windowRects: rects)
            view.autoresizingMask = [.width, .height]
            view.session = self
            window.overlay = view
            container.addSubview(view)
            window.contentView = container
            windows.append(window)
            views.append(view)
        }
    }

    func begin() {
        let mouse = NSEvent.mouseLocation
        for (window, view) in zip(windows, views) {
            window.orderFrontRegardless()
            view.updateMouse(screenPoint: mouse)
        }
        NSApp.activate()
        // 鼠标顶在屏幕最上沿时 y 正好等于 maxY：NSMouseInRect 算在里面，frame.contains 不算。
        let index = windows.firstIndex { NSMouseInRect(mouse, $0.frame, false) } ?? 0
        windows[index].makeKeyAndOrderFront(nil)
        windows[index].makeFirstResponder(views[index])
        NSCursor.crosshair.set()
    }

    func didStartSelection(in view: OverlayView) {
        activeView = view
        for other in views where other !== view { other.locked = true }
    }

    func didResetSelection(in view: OverlayView) {
        if activeView === view { activeView = nil }
        views.forEach { $0.locked = false }
    }

    /// 键盘事件交给有选区的屏幕；没有选区时交给鼠标所在的屏幕。
    func keyTarget(fallback: OverlayView) -> OverlayView {
        activeView ?? views.first { $0.mouse != nil } ?? fallback
    }

    func finish(_ result: CaptureResult) {
        guard !finished else { return }
        finished = true
        windows.forEach { $0.orderOut(nil) }
        NSCursor.arrow.set()
        onFinish?(result)
    }
}

final class OverlayWindow: NSWindow {
    weak var overlay: OverlayView?

    init(screen: NSScreen) {
        super.init(contentRect: screen.frame, styleMask: .borderless, backing: .buffered, defer: false)
        level = .screenSaver
        isOpaque = true
        backgroundColor = .black
        hasShadow = false
        isReleasedWhenClosed = false
        animationBehavior = .none
        acceptsMouseMovedEvents = true
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
        setFrame(screen.frame, display: false)
    }

    // 被锁定的屏幕不能成为 key 窗口，免得在上面点一下就把正在输入的文字和 Esc 带走。
    override var canBecomeKey: Bool { !(overlay?.locked ?? false) }
    override var canBecomeMain: Bool { true }
}

/// 冻结的屏幕画面。单独一层，遮罩重绘时不用重画整张截图。
final class FrozenImageView: NSView {
    private let image: NSImage

    init(frame: CGRect, image: CGImage) {
        self.image = NSImage(cgImage: image, size: frame.size)
        super.init(frame: frame)
        wantsLayer = true
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override var isFlipped: Bool { true }
    override var isOpaque: Bool { true }

    override func draw(_ dirtyRect: NSRect) {
        image.draw(in: bounds, from: .zero, operation: .copy, fraction: 1, respectFlipped: true, hints: nil)
    }
}
