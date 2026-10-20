import AppKit

/// 一块屏幕上的截图遮罩：选窗口或拖选区、调整选区、标注、放大镜。
/// 坐标系是翻转的（左上角原点，单位为点），和 CG 全局坐标方向一致。
final class OverlayView: NSView, NSTextFieldDelegate, NSMenuItemValidation {
    weak var session: CaptureSession?
    /// 另一块屏幕已经有选区时，这块屏幕只显示遮罩，不响应鼠标。
    var locked = false {
        didSet {
            if locked {
                hoverRect = nil
            } else if phase == .choosing, let mouse {
                // 解锁后马上恢复窗口高亮和光标，不等鼠标再动。
                hoverRect = windowRect(at: mouse)
                updateCursor(at: mouse)
            }
            needsDisplay = true
        }
    }
    private(set) var mouse: CGPoint?

    private let image: CGImage
    private let scale: CGFloat
    private let windowRects: [CGRect]
    private lazy var mosaicImage: NSImage? = Self.makeMosaic(from: image, block: Int(10 * scale), size: bounds.size)

    private enum Phase { case choosing, dragging, selected }
    private enum Drag {
        case none
        case creating(CGPoint)
        case moving(CGPoint, CGRect)
        case resizing(Handle, CGRect)
        case drawing
    }
    private enum Finish { case copy, save, pin }

    private var phase = Phase.choosing
    private var drag = Drag.none
    private var selection = CGRect.zero
    private var hoverRect: CGRect?
    private var tool: Tool?
    private var annotations: [Annotation] = [] { didSet { updateUndoButton() } }
    private var current: Annotation?
    private var textField: NSTextField? { didSet { updateUndoButton() } }
    private let toolbar = ToolbarView()
    private let style = AnnotationStyle.shared

    override var isFlipped: Bool { true }
    override var acceptsFirstResponder: Bool { true }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    init(frame: CGRect, image: CGImage, windowRects: [CGRect]) {
        self.image = image
        self.scale = CGFloat(image.width) / frame.width
        self.windowRects = windowRects
        super.init(frame: frame)
        toolbar.isHidden = true
        addSubview(toolbar)
        toolbar.onTool = { [weak self] t in self?.toggleTool(t) }
        toolbar.onUndo = { [weak self] in self?.undo() }
        toolbar.onSave = { [weak self] in self?.finish(.save) }
        toolbar.onPin = { [weak self] in self?.finish(.pin) }
        toolbar.onCancel = { [weak self] in self?.session?.finish(.cancelled) }
        toolbar.onDone = { [weak self] in self?.finish(.copy) }
        toolbar.onStyleChange = { [weak self] in self?.applyStyleToTextField() }
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        trackingAreas.forEach(removeTrackingArea)
        addTrackingArea(NSTrackingArea(rect: .zero,
                                       options: [.mouseMoved, .mouseEnteredAndExited, .activeAlways, .inVisibleRect],
                                       owner: self))
    }

    func updateMouse(screenPoint: NSPoint) {
        guard let window else { return }
        let p = convert(window.convertPoint(fromScreen: screenPoint), from: nil)
        mouse = bounds.contains(p) ? p : nil
        if let mouse, phase == .choosing, !locked { hoverRect = windowRect(at: mouse) }
        needsDisplay = true
    }

    // MARK: - 鼠标

    override func mouseEntered(with event: NSEvent) { mouseMoved(with: event) }

    override func mouseExited(with event: NSEvent) {
        mouse = nil
        if phase == .choosing { hoverRect = nil }
        needsDisplay = true
    }

    override func mouseMoved(with event: NSEvent) {
        let p = location(event)
        // key 窗口在鼠标移到别的屏幕后可能还会收到移动事件，这时按移出处理。
        guard bounds.contains(p) else {
            mouseExited(with: event)
            return
        }
        mouse = p
        updateCursor(at: p)
        guard phase != .selected else { return }
        if phase == .choosing, !locked { hoverRect = windowRect(at: p) }
        needsDisplay = true
    }

    override func mouseDown(with event: NSEvent) {
        guard !locked else { return }
        window?.makeKey()
        let p = clamp(location(event))
        if textField != nil {
            // 正在输入文字时，点一下只结束输入。
            commitText()
            return
        }
        switch phase {
        case .choosing, .dragging:
            drag = .creating(p)
        case .selected:
            if event.clickCount == 2, selection.contains(p) {
                finish(.copy)
                return
            }
            if let h = handle(at: p) {
                drag = .resizing(h, selection)
            } else if selection.contains(p), let tool {
                if tool == .text {
                    beginText(at: p)
                } else {
                    current = Annotation(tool: tool, color: style.color, width: style.width(for: tool), points: [p])
                    drag = .drawing
                }
            } else if selection.contains(p), annotations.isEmpty {
                drag = .moving(p, selection)
                NSCursor.closedHand.set()
            }
        }
        layoutToolbar()
    }

    override func mouseDragged(with event: NSEvent) {
        guard !locked else { return }
        let p = clamp(location(event))
        mouse = p
        switch drag {
        case .none:
            return
        case .creating(let start):
            if phase != .dragging {
                guard hypot(p.x - start.x, p.y - start.y) > 3 else { return }
                phase = .dragging
                hoverRect = nil
                session?.didStartSelection(in: self)
            }
            selection = CGRect(from: start, to: p)
        case .moving(let start, let original):
            var r = original.offsetBy(dx: p.x - start.x, dy: p.y - start.y)
            r.origin.x = min(max(r.minX, 0), bounds.width - r.width)
            r.origin.y = min(max(r.minY, 0), bounds.height - r.height)
            selection = r
        case .resizing(let h, let original):
            selection = h.resize(original, to: p)
        case .drawing:
            current?.extend(to: p)
        }
        layoutToolbar()
        needsDisplay = true
    }

    override func mouseUp(with event: NSEvent) {
        guard !locked else { return }
        switch drag {
        case .creating(let start):
            if phase == .dragging {
                if selection.width >= 4, selection.height >= 4 { select() } else { reset() }
            } else {
                // 没有拖动：选中按下处的窗口，没有窗口就选整个屏幕。
                selection = windowRect(at: start)
                select()
            }
        case .drawing:
            if let c = current, c.isMeaningful { annotations.append(c) }
            current = nil
        case .resizing:
            if selection.width < 4 || selection.height < 4 {
                selection.size = CGSize(width: max(selection.width, 4), height: max(selection.height, 4))
            }
        case .moving, .none:
            break
        }
        drag = .none
        layoutToolbar()
        updateCursor(at: clamp(location(event)))
        needsDisplay = true
    }

    override func rightMouseDown(with event: NSEvent) {
        // 右键：有选区时取消选区，没有选区时退出截图。
        if locked {
            session?.activeView?.reset()
        } else if phase == .choosing {
            session?.finish(.cancelled)
        } else {
            reset()
        }
    }

    // MARK: - 键盘

    override func keyDown(with event: NSEvent) {
        (session?.keyTarget(fallback: self) ?? self).handleKey(event)
    }

    override func flagsChanged(with event: NSEvent) {
        (session?.keyTarget(fallback: self) ?? self).handleFlags(event)
    }

    func handleKey(_ event: NSEvent) {
        let cmd = event.modifierFlags.contains(.command)
        // 按 ⌘ 键位表取字符：俄文等非拉丁布局下也能认出 C 键，和菜单快捷键的认法一致。
        let key = event.characters(byApplyingModifiers: .command)?.lowercased() ?? ""
        if event.keyCode == 53 { // Esc
            session?.finish(.cancelled)
            return
        }
        // ⌘C、⌘S、⌘Z 由主菜单分发，见下面的菜单命令。
        if phase == .selected {
            if event.keyCode == 36 || event.keyCode == 76 { finish(.copy) }
        } else if !cmd, key == "c", let mouse, let color = pixelColor(at: mouse) {
            let pb = NSPasteboard.general
            pb.clearContents()
            pb.setString(colorText(color), forType: .string)
            session?.finish(.cancelled)
        }
    }

    func handleFlags(_ event: NSEvent) {
        // 只认 Shift 键本身按下（56 左、60 右）：先松开 ⌘ 等别的键时，flags 里也可能还带着 Shift。
        guard event.keyCode == 56 || event.keyCode == 60, event.modifierFlags.contains(.shift) else { return }
        session?.showHex.toggle()
    }

    // MARK: - 状态

    private func select() {
        phase = .selected
        hoverRect = nil
        session?.didStartSelection(in: self)
        layoutToolbar()
    }

    func reset() {
        textField?.removeFromSuperview()
        textField = nil
        phase = .choosing
        drag = .none
        selection = .zero
        annotations = []
        current = nil
        tool = nil
        toolbar.selectedTool = nil
        hoverRect = mouse.map(windowRect(at:))
        session?.didResetSelection(in: self)
        layoutToolbar()
        window?.makeFirstResponder(self)
        needsDisplay = true
    }

    private func toggleTool(_ t: Tool) {
        commitText()
        tool = tool == t ? nil : t
        toolbar.selectedTool = tool
        layoutToolbar()
    }

    /// 有标注或正在输入文字时可以撤销；撤销正在输入的文字框会把它整个去掉。
    private func updateUndoButton() {
        toolbar.canUndo = textField != nil || !annotations.isEmpty
    }

    private func undo() {
        if let field = textField {
            field.removeFromSuperview()
            textField = nil
            window?.makeFirstResponder(self)
        } else if !annotations.isEmpty {
            annotations.removeLast()
        }
        // 撤销完可能又能拖动选区了，光标马上跟着变，不等鼠标再动。
        if let mouse { updateCursor(at: mouse) }
        needsDisplay = true
    }

    private func finish(_ action: Finish) {
        commitText()
        guard let shot = renderSelection() else {
            session?.finish(.cancelled)
            return
        }
        switch action {
        case .copy:
            shot.copyToPasteboard()
            session?.finish(.copied)
        case .save:
            session?.finish(.save(shot))
        case .pin:
            // 钉图窗口按导出图实际的像素范围摆放，和图片一样大，不会被拉伸。
            let px = pixelRect(selection)
            let rect = CGRect(x: px.minX / scale, y: px.minY / scale, width: shot.pointSize.width, height: shot.pointSize.height)
            let rectInWindow = convert(rect, to: nil)
            session?.finish(.pin(shot, window?.convertToScreen(rectInWindow) ?? rectInWindow))
        }
    }

    // MARK: - 菜单命令（⌘C、⌘S、⌘Z，见 AppDelegate.makeMainMenu）

    @objc func copy(_ sender: Any?) { finish(.copy) }

    @objc func saveDocument(_ sender: Any?) { finish(.save) }

    @objc func undo(_ sender: Any?) {
        // 正在输入时撤销输入的文字。字段编辑器不处理 undo:，会沿响应链传到这里。
        if let editor = textField?.currentEditor() {
            editor.undoManager?.undo()
        } else {
            undo()
        }
    }

    func validateMenuItem(_ item: NSMenuItem) -> Bool {
        switch item.action {
        case #selector(copy(_:)), #selector(saveDocument(_:)):
            return phase == .selected
        case #selector(undo(_:)):
            if let editor = textField?.currentEditor() { return editor.undoManager?.canUndo ?? false }
            return !annotations.isEmpty
        default:
            return true
        }
    }

    // MARK: - 文字

    private func beginText(at p: CGPoint) {
        let field = NSTextField(string: "")
        field.isBordered = false
        field.isBezeled = false
        field.drawsBackground = false
        field.focusRingType = .none
        field.delegate = self
        field.cell?.isScrollable = true
        field.cell?.wraps = false
        textField = field
        applyStyleToTextField()
        let height = field.frame.height
        field.frame.origin = CGPoint(x: p.x, y: p.y - height / 2)
        addSubview(field, positioned: .below, relativeTo: toolbar)
        window?.makeFirstResponder(field)
        applyStyleToTextField()
    }

    private func applyStyleToTextField() {
        guard let field = textField else { return }
        let font = NSFont.systemFont(ofSize: style.fontSize, weight: .medium)
        field.font = font
        field.textColor = style.color
        (field.currentEditor() as? NSTextView)?.insertionPointColor = style.color
        field.frame.size.height = ceil(font.ascender - font.descender + font.leading) + 2
        fitTextField()
    }

    private func fitTextField() {
        guard let field = textField else { return }
        let width = field.attributedStringValue.size().width + 14
        field.frame.size.width = max(40, width)
    }

    func controlTextDidChange(_ obj: Notification) { fitTextField() }

    func control(_ control: NSControl, textView: NSTextView, doCommandBy selector: Selector) -> Bool {
        if selector == #selector(NSResponder.insertNewline(_:)) || selector == #selector(NSResponder.cancelOperation(_:)) {
            commitText()
            return true
        }
        return false
    }

    private func commitText() {
        guard let field = textField else { return }
        textField = nil
        let text = field.stringValue
        // NSTextField 的文字左边有 2 点内边距。
        let origin = CGPoint(x: field.frame.minX + 2, y: field.frame.minY)
        field.removeFromSuperview()
        if !text.isEmpty {
            annotations.append(Annotation(tool: .text, color: field.textColor ?? style.color, width: 0,
                                          points: [origin], text: text, fontSize: field.font?.pointSize ?? style.fontSize))
        }
        window?.makeFirstResponder(self)
        needsDisplay = true
    }

    // MARK: - 布局与光标

    private func layoutToolbar() {
        let visible: Bool
        switch drag {
        case .moving, .resizing: visible = false
        default: visible = phase == .selected
        }
        toolbar.isHidden = !visible
        guard visible else { return }
        let size = toolbar.preferredSize
        let margin: CGFloat = 6
        let x = min(max(selection.maxX - size.width, margin), bounds.width - size.width - margin)
        var y = selection.maxY + margin
        if y + size.height > bounds.height - margin {
            y = selection.minY - margin - size.height
            if y < margin { y = selection.maxY - size.height - margin }
        }
        toolbar.frame = CGRect(x: x, y: y, width: size.width, height: size.height)
    }

    private func updateCursor(at p: CGPoint) {
        if locked {
            NSCursor.arrow.set()
            return
        }
        switch phase {
        case .choosing, .dragging:
            NSCursor.crosshair.set()
        case .selected:
            if toolbar.hitTest(p) != nil {
                NSCursor.arrow.set()
            } else if let h = handle(at: p) {
                h.cursor.set()
            } else if selection.contains(p), let tool {
                (tool == .text ? NSCursor.iBeam : NSCursor.crosshair).set()
            } else if selection.contains(p), annotations.isEmpty {
                NSCursor.openHand.set()
            } else {
                NSCursor.arrow.set()
            }
        }
    }

    private func handle(at p: CGPoint) -> Handle? {
        guard phase == .selected else { return nil }
        return Handle.allCases.first { h in
            let c = h.point(in: selection)
            return abs(c.x - p.x) <= 6 && abs(c.y - p.y) <= 6
        }
    }

    private func windowRect(at p: CGPoint) -> CGRect {
        for r in windowRects where r.contains(p) {
            let clipped = r.intersection(bounds)
            if !clipped.isNull, clipped.width > 1, clipped.height > 1 { return clipped }
        }
        return bounds
    }

    private func location(_ event: NSEvent) -> CGPoint {
        convert(event.locationInWindow, from: nil)
    }

    private func clamp(_ p: CGPoint) -> CGPoint {
        CGPoint(x: min(max(p.x, 0), bounds.width), y: min(max(p.y, 0), bounds.height))
    }

    // MARK: - 绘制

    override func draw(_ dirtyRect: NSRect) {
        let hole: CGRect? = phase == .choosing ? hoverRect : selection

        let dim = NSBezierPath(rect: bounds)
        if let hole {
            dim.appendRect(hole)
            dim.windingRule = .evenOdd
        }
        Theme.dim.setFill()
        dim.fill()

        if phase != .choosing {
            NSGraphicsContext.saveGraphicsState()
            NSBezierPath(rect: selection).addClip()
            for a in annotations { a.draw(mosaic: a.tool == .mosaic ? mosaicImage : nil, canvas: bounds) }
            if let c = current { c.draw(mosaic: c.tool == .mosaic ? mosaicImage : nil, canvas: bounds) }
            NSGraphicsContext.restoreGraphicsState()
        }

        if let hole {
            Theme.accent.setStroke()
            let thick = phase == .choosing
            let border = NSBezierPath(rect: thick ? hole.insetBy(dx: 1.5, dy: 1.5) : hole.insetBy(dx: -0.5, dy: -0.5))
            border.lineWidth = thick ? 3 : 1
            border.stroke()
            drawSizeLabel(for: hole)
        }
        if phase == .selected { drawHandles() }
        if let mouse, !locked, showsMagnifier { drawMagnifier(at: mouse) }
    }

    private var showsMagnifier: Bool {
        switch drag {
        case .resizing: return true
        case .moving, .drawing: return false
        default: return phase != .selected
        }
    }

    private func drawHandles() {
        Theme.accent.setFill()
        for h in Handle.allCases {
            let c = h.point(in: selection)
            CGRect(x: c.x - 3, y: c.y - 3, width: 6, height: 6).fill()
        }
    }

    private func drawSizeLabel(for r: CGRect) {
        let px = pixelRect(r)
        let text = "\(Int(px.width)) × \(Int(px.height))" as NSString
        let attrs: [NSAttributedString.Key: Any] = [
            .font: NSFont.monospacedDigitSystemFont(ofSize: 12, weight: .regular),
            .foregroundColor: NSColor.white,
        ]
        let size = text.size(withAttributes: attrs)
        var box = CGRect(x: r.minX, y: r.minY - size.height - 10, width: ceil(size.width) + 12, height: ceil(size.height) + 6)
        if box.minY < 2 {
            box.origin = CGPoint(x: r.minX + 4, y: r.minY + 4)
        }
        box.origin.x = min(box.minX, bounds.width - box.width - 2)
        NSColor(white: 0, alpha: 0.75).setFill()
        NSBezierPath(roundedRect: box, xRadius: 4, yRadius: 4).fill()
        text.draw(at: CGPoint(x: box.minX + 6, y: box.minY + 3), withAttributes: attrs)
    }

    private func drawMagnifier(at p: CGPoint) {
        let cols = 23, rows = 15
        let zoom: CGFloat = 6
        let boxSize = CGSize(width: CGFloat(cols) * zoom, height: CGFloat(rows) * zoom)
        let infoHeight: CGFloat = 54
        var origin = CGPoint(x: p.x + 20, y: p.y + 24)
        if origin.x + boxSize.width > bounds.maxX { origin.x = p.x - 20 - boxSize.width }
        if origin.y + boxSize.height + infoHeight > bounds.maxY { origin.y = p.y - 24 - boxSize.height - infoHeight }
        let box = CGRect(origin: origin, size: boxSize)

        let px = Int(floor(p.x * scale)), py = Int(floor(p.y * scale))
        let source = CGRect(x: px - cols / 2, y: py - rows / 2, width: cols, height: rows)
        let visible = source.intersection(CGRect(x: 0, y: 0, width: image.width, height: image.height))

        NSColor.black.setFill()
        box.fill()
        if !visible.isNull, let crop = image.cropping(to: visible) {
            let dest = CGRect(x: box.minX + (visible.minX - source.minX) * zoom,
                              y: box.minY + (visible.minY - source.minY) * zoom,
                              width: visible.width * zoom, height: visible.height * zoom)
            NSGraphicsContext.current?.imageInterpolation = .none
            NSImage(cgImage: crop, size: dest.size).draw(
                in: dest, from: .zero, operation: .sourceOver, fraction: 1, respectFlipped: true,
                hints: [.interpolation: NSNumber(value: NSImageInterpolation.none.rawValue)])
        }
        Theme.accent.withAlphaComponent(0.45).setFill()
        CGRect(x: box.minX, y: box.minY + CGFloat(rows / 2) * zoom, width: box.width, height: zoom).fill()
        CGRect(x: box.minX + CGFloat(cols / 2) * zoom, y: box.minY, width: zoom, height: box.height).fill()
        NSColor.white.setStroke()
        NSBezierPath(rect: box.insetBy(dx: 0.5, dy: 0.5)).stroke()

        let info = CGRect(x: box.minX, y: box.maxY, width: box.width, height: infoHeight)
        NSColor(white: 0, alpha: 0.8).setFill()
        info.fill()
        let attrs: [NSAttributedString.Key: Any] = [
            .font: NSFont.monospacedDigitSystemFont(ofSize: 11, weight: .regular),
            .foregroundColor: NSColor.white,
        ]
        let color = pixelColor(at: p)
        ("(\(px), \(py))" as NSString).draw(at: CGPoint(x: info.minX + 6, y: info.minY + 4), withAttributes: attrs)
        if let color {
            let swatch = CGRect(x: info.minX + 6, y: info.minY + 22, width: 10, height: 10)
            NSColor(srgbRed: CGFloat(color.r) / 255, green: CGFloat(color.g) / 255, blue: CGFloat(color.b) / 255, alpha: 1).setFill()
            swatch.fill()
            NSColor.white.setStroke()
            NSBezierPath(rect: swatch.insetBy(dx: -0.5, dy: -0.5)).stroke()
            (colorText(color) as NSString).draw(at: CGPoint(x: info.minX + 21, y: info.minY + 20), withAttributes: attrs)
        }
        var hint = attrs
        hint[.foregroundColor] = NSColor(white: 0.7, alpha: 1)
        ("C 复制颜色  ⇧ 切换格式" as NSString).draw(at: CGPoint(x: info.minX + 6, y: info.minY + 36), withAttributes: hint)
    }

    // MARK: - 取色与导出

    private func pixelColor(at p: CGPoint) -> (r: UInt8, g: UInt8, b: UInt8)? {
        let px = Int(floor(p.x * scale)), py = Int(floor(p.y * scale))
        guard px >= 0, py >= 0, px < image.width, py < image.height,
              let crop = image.cropping(to: CGRect(x: px, y: py, width: 1, height: 1)),
              let space = CGColorSpace(name: CGColorSpace.sRGB) else { return nil }
        var data = [UInt8](repeating: 0, count: 4)
        let drawn: Bool = data.withUnsafeMutableBytes { buffer in
            guard let ctx = CGContext(data: buffer.baseAddress, width: 1, height: 1, bitsPerComponent: 8, bytesPerRow: 4,
                                      space: space, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return false }
            ctx.draw(crop, in: CGRect(x: 0, y: 0, width: 1, height: 1))
            return true
        }
        return drawn ? (data[0], data[1], data[2]) : nil
    }

    private func colorText(_ c: (r: UInt8, g: UInt8, b: UInt8)) -> String {
        session?.showHex == true ? String(format: "#%02X%02X%02X", c.r, c.g, c.b) : "RGB(\(c.r),\(c.g),\(c.b))"
    }

    /// 选区对应的原生像素范围。四条边各自取整，导出的图和屏幕上看到的选区边界一致。
    private func pixelRect(_ r: CGRect) -> CGRect {
        let x0 = (r.minX * scale).rounded(), x1 = (r.maxX * scale).rounded()
        let y0 = (r.minY * scale).rounded(), y1 = (r.maxY * scale).rounded()
        return CGRect(x: x0, y: y0, width: x1 - x0, height: y1 - y0)
    }

    /// 按原生像素裁出选区并画上标注。
    func renderSelection() -> CapturedImage? {
        let imageRect = CGRect(x: 0, y: 0, width: image.width, height: image.height)
        let px = pixelRect(selection).intersection(imageRect)
        guard !px.isNull, !px.isEmpty, let base = image.cropping(to: px) else { return nil }
        let w = Int(px.width), h = Int(px.height)
        let srgb = CGColorSpace(name: CGColorSpace.sRGB)!
        let info = CGImageAlphaInfo.premultipliedLast.rawValue
        guard let ctx = CGContext(data: nil, width: w, height: h, bitsPerComponent: 8, bytesPerRow: 0,
                                  space: image.colorSpace ?? srgb, bitmapInfo: info)
                ?? CGContext(data: nil, width: w, height: h, bitsPerComponent: 8, bytesPerRow: 0,
                             space: srgb, bitmapInfo: info) else { return nil }
        ctx.draw(base, in: CGRect(x: 0, y: 0, width: w, height: h))
        if !annotations.isEmpty {
            // 换成和视图一致的坐标：左上角原点、单位为点。
            ctx.translateBy(x: 0, y: CGFloat(h))
            ctx.scaleBy(x: scale, y: -scale)
            ctx.translateBy(x: -px.minX / scale, y: -px.minY / scale)
            NSGraphicsContext.saveGraphicsState()
            NSGraphicsContext.current = NSGraphicsContext(cgContext: ctx, flipped: true)
            for a in annotations { a.draw(mosaic: a.tool == .mosaic ? mosaicImage : nil, canvas: bounds) }
            NSGraphicsContext.restoreGraphicsState()
        }
        guard let out = ctx.makeImage() else { return nil }
        return CapturedImage(cgImage: out, pointSize: CGSize(width: CGFloat(w) / scale, height: CGFloat(h) / scale))
    }

    private static func makeMosaic(from image: CGImage, block: Int, size: CGSize) -> NSImage? {
        let w = max(1, image.width / max(block, 1)), h = max(1, image.height / max(block, 1))
        guard let space = CGColorSpace(name: CGColorSpace.sRGB),
              let ctx = CGContext(data: nil, width: w, height: h, bitsPerComponent: 8, bytesPerRow: 0, space: space,
                                  bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
        ctx.interpolationQuality = .medium
        ctx.draw(image, in: CGRect(x: 0, y: 0, width: w, height: h))
        guard let small = ctx.makeImage() else { return nil }
        return NSImage(cgImage: small, size: size)
    }

    // MARK: - 自检用

    func simulateHover(at p: CGPoint) {
        mouse = p
        hoverRect = windowRect(at: p)
    }

    func simulateSelection(_ rect: CGRect, tool: Tool?, annotations: [Annotation]) {
        selection = rect
        phase = .selected
        self.tool = tool
        self.annotations = annotations
        toolbar.selectedTool = tool
        layoutToolbar()
    }
}

private enum Handle: CaseIterable {
    case topLeft, top, topRight, right, bottomRight, bottom, bottomLeft, left

    func point(in r: CGRect) -> CGPoint {
        switch self {
        case .topLeft: return CGPoint(x: r.minX, y: r.minY)
        case .top: return CGPoint(x: r.midX, y: r.minY)
        case .topRight: return CGPoint(x: r.maxX, y: r.minY)
        case .right: return CGPoint(x: r.maxX, y: r.midY)
        case .bottomRight: return CGPoint(x: r.maxX, y: r.maxY)
        case .bottom: return CGPoint(x: r.midX, y: r.maxY)
        case .bottomLeft: return CGPoint(x: r.minX, y: r.maxY)
        case .left: return CGPoint(x: r.minX, y: r.midY)
        }
    }

    func resize(_ r: CGRect, to p: CGPoint) -> CGRect {
        var x0 = r.minX, x1 = r.maxX, y0 = r.minY, y1 = r.maxY
        switch self {
        case .topLeft: x0 = p.x; y0 = p.y
        case .top: y0 = p.y
        case .topRight: x1 = p.x; y0 = p.y
        case .right: x1 = p.x
        case .bottomRight: x1 = p.x; y1 = p.y
        case .bottom: y1 = p.y
        case .bottomLeft: x0 = p.x; y1 = p.y
        case .left: x0 = p.x
        }
        return CGRect(from: CGPoint(x: x0, y: y0), to: CGPoint(x: x1, y: y1))
    }

    var cursor: NSCursor {
        switch self {
        case .top, .bottom: return .resizeUpDown
        case .left, .right: return .resizeLeftRight
        default: return .crosshair
        }
    }
}
