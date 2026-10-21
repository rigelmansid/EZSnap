import AppKit

/// 一块屏幕上的截图遮罩：画遮罩、选区和标注里落在本屏的部分、放大镜。
/// 选区等状态在 CaptureSession 里（全局坐标，D-8）；这里把鼠标键盘事件换成全局坐标交给它。
/// 视图坐标是翻转的（左上角原点，单位为点），本屏坐标加上 origin 就是全局坐标。
final class OverlayView: NSView, NSMenuItemValidation {
    weak var session: CaptureSession?
    /// 本屏左上角的全局坐标。
    let origin: CGPoint
    /// 鼠标在本屏上的位置（本屏坐标），不在本屏时为 nil。由会话按全局位置统一设置。
    var mouse: CGPoint?

    let image: CGImage
    let scale: CGFloat
    private(set) lazy var mosaicImage: NSImage? = Self.makeMosaic(from: image, block: Int(10 * scale), size: bounds.size)

    var globalFrame: CGRect { CGRect(origin: origin, size: bounds.size) }

    override var isFlipped: Bool { true }
    override var acceptsFirstResponder: Bool { true }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    init(frame: CGRect, image: CGImage, origin: CGPoint) {
        self.image = image
        self.scale = CGFloat(image.width) / frame.width
        self.origin = origin
        super.init(frame: frame)
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
        if bounds.contains(p) { session?.mouseMoved(to: global(p)) }
    }

    private func global(_ p: CGPoint) -> CGPoint { CGPoint(x: p.x + origin.x, y: p.y + origin.y) }

    private func global(_ event: NSEvent) -> CGPoint { global(convert(event.locationInWindow, from: nil)) }

    private func local(_ r: CGRect) -> CGRect { r.offsetBy(dx: -origin.x, dy: -origin.y) }

    // MARK: - 事件（换成全局坐标交给会话）

    override func mouseEntered(with event: NSEvent) { mouseMoved(with: event) }

    override func mouseExited(with event: NSEvent) { session?.mouseExited(from: self) }

    // key 窗口在鼠标移到别的屏幕后可能还会收到移动事件；换成全局坐标后由会话按实际位置处理。
    override func mouseMoved(with event: NSEvent) { session?.mouseMoved(to: global(event)) }

    override func mouseDown(with event: NSEvent) {
        window?.makeKey()
        session?.mouseDown(at: global(event), clickCount: event.clickCount, in: self)
    }

    override func mouseDragged(with event: NSEvent) { session?.mouseDragged(to: global(event)) }

    override func mouseUp(with event: NSEvent) { session?.mouseUp(at: global(event)) }

    override func rightMouseDown(with event: NSEvent) { session?.rightMouseDown() }

    override func keyDown(with event: NSEvent) { session?.handleKey(event) }

    override func flagsChanged(with event: NSEvent) { session?.handleFlags(event) }

    // 菜单命令（⌘C、⌘S、⌘Z，见 AppDelegate.makeMainMenu）。哪块屏是 key 窗口都一样，都转给会话。
    @objc func copy(_ sender: Any?) { session?.copySelection() }

    @objc func saveDocument(_ sender: Any?) { session?.saveSelection() }

    @objc func undo(_ sender: Any?) { session?.undoCommand() }

    func validateMenuItem(_ item: NSMenuItem) -> Bool { session?.validate(item.action) ?? false }

    // MARK: - 绘制

    override func draw(_ dirtyRect: NSRect) {
        guard let session else { return }
        let hole: CGRect? = (session.phase == .choosing ? session.hoverRect : session.selection).map(local)

        let dim = NSBezierPath(rect: bounds)
        // 跨屏时洞会超出本屏；只挖本屏里的部分，否则奇偶填充会把超出的那块反过来涂上。
        if let visible = hole?.intersection(bounds), !visible.isNull {
            dim.appendRect(visible)
            dim.windingRule = .evenOdd
        }
        Theme.dim.setFill()
        dim.fill()

        if session.phase != .choosing {
            NSGraphicsContext.saveGraphicsState()
            NSBezierPath(rect: local(session.selection)).addClip()
            // 标注是全局坐标，平移到本屏来画；马赛克用本屏自己的像素化底图。
            let shift = NSAffineTransform()
            shift.translateX(by: -origin.x, yBy: -origin.y)
            shift.concat()
            for a in session.annotations { a.draw(mosaic: a.tool == .mosaic ? mosaicImage : nil, canvas: globalFrame) }
            if let c = session.current { c.draw(mosaic: c.tool == .mosaic ? mosaicImage : nil, canvas: globalFrame) }
            NSGraphicsContext.restoreGraphicsState()
        }

        if let hole {
            Theme.accent.setStroke()
            let thick = session.phase == .choosing
            let border = NSBezierPath(rect: thick ? hole.insetBy(dx: 1.5, dy: 1.5) : hole.insetBy(dx: -0.5, dy: -0.5))
            border.lineWidth = thick ? 3 : 1
            border.stroke()
            // 尺寸标签只画在左上角所在的那块屏幕上。
            if bounds.contains(hole.origin) {
                drawSizeLabel(for: hole, text: session.sizeText(for: hole.offsetBy(dx: origin.x, dy: origin.y)))
            }
        }
        if session.phase == .selected { drawHandles(session.handlePoints) }
        if let mouse, session.showsMagnifier { drawMagnifier(at: mouse) }
    }

    private func drawHandles(_ points: [CGPoint]) {
        Theme.accent.setFill()
        for p in points {
            CGRect(x: p.x - origin.x - 3, y: p.y - origin.y - 3, width: 6, height: 6).fill()
        }
    }

    private func drawSizeLabel(for r: CGRect, text: String) {
        let text = text as NSString
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
            let text = session?.colorText(color) ?? "RGB(\(color.r),\(color.g),\(color.b))"
            (text as NSString).draw(at: CGPoint(x: info.minX + 21, y: info.minY + 20), withAttributes: attrs)
        }
        var hint = attrs
        hint[.foregroundColor] = NSColor(white: 0.7, alpha: 1)
        (L("C copy  ⇧ RGB/HEX", "C 复制颜色  ⇧ 切换格式", "C 拷貝顏色  ⇧ 切換格式") as NSString).draw(at: CGPoint(x: info.minX + 6, y: info.minY + 36), withAttributes: hint)
    }

    // MARK: - 取色

    /// 鼠标所在像素的颜色；鼠标不在本屏时为 nil。
    func colorUnderMouse() -> (r: UInt8, g: UInt8, b: UInt8)? { mouse.flatMap(pixelColor) }

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
}
