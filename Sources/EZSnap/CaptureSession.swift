import AppKit

enum CaptureResult {
    case cancelled
    case copied
    case save(CapturedImage)
    case pin(CapturedImage, NSRect)
}

/// 一次截图。每块屏幕一个遮罩窗口；选区、标注、工具条和文字框由会话统一管理，可以跨屏（D-8）。
/// 坐标都是 CG 全局坐标：主屏左上角为原点，y 向下，单位为点。各屏的 OverlayView 只画落在本屏的部分，
/// 鼠标键盘事件换成全局坐标后交给会话。
final class CaptureSession: NSObject, NSTextFieldDelegate {
    enum Phase { case choosing, dragging, selected }
    private enum Drag {
        case none
        case creating(CGPoint)
        case moving(CGPoint, CGRect)
        case resizing(Handle, CGRect)
        case drawing
        /// 调整第 n 个标注：拖控制点或挪动（控制点为 nil），记下按下处、原来的标注和整组标注（撤销用）。
        case editing(Int, ShapeHandle?, CGPoint, Annotation, [Annotation])
    }
    private enum Finish { case copy, save, pin }

    var onFinish: ((CaptureResult) -> Void)?
    /// 放大镜里的颜色格式，各块屏幕共用，按 Shift 切换。
    var showHex = false { didSet { redraw() } }
    private(set) var views: [OverlayView] = []
    private var windows: [OverlayWindow] = []
    private var finished = false

    private(set) var phase = Phase.choosing
    private var drag = Drag.none
    private(set) var selection = CGRect.zero
    private(set) var hoverRect: CGRect?
    private(set) var annotations: [Annotation] = []
    /// 每次改动标注前的整组标注，撤销时取回（D-20）。
    private var history: [[Annotation]] = [] { didSet { updateUndoButton() } }
    /// 选中的标注（annotations 的下标）：方框、圆形、箭头、文字或画笔（D-20、D-21）。
    /// 工具条的样式面板跟着显示它的颜色和粗细。
    private(set) var selectedAnnotation: Int? {
        didSet { toolbar.panelTool = selectedAnnotation.map { annotations[$0].tool } }
    }
    /// 正在重新编辑的文字标注（下标）。编辑期间不画它，由文字框代替（D-21）。
    private(set) var editingText: Int?
    private(set) var current: Annotation?
    private var tool: Tool?
    private var textField: NSTextField? { didSet { updateUndoButton() } }
    private weak var textHost: OverlayView?
    private let toolbar = ToolbarView()
    private let style = AnnotationStyle.shared
    private let windowRects: [CGRect]
    /// 所有屏幕的外接矩形。选区和拖动都限制在这里面，屏幕之间的空隙导出时是透明的。
    private let desktop: CGRect
    /// 鼠标最后在哪（全局坐标），不在任何屏幕上时为 nil。
    private var mouse: CGPoint?

    /// windowRects 是 CG 全局坐标。makesWindows 为 false 时只建视图不建窗口，供渲染自检用。
    init(shots: [ScreenShot], windowRects: [CGRect], makesWindows: Bool = true) {
        self.windowRects = windowRects
        desktop = shots.reduce(CGRect.null) { $0.union($1.displayBounds) }
        super.init()
        for shot in shots {
            let frame = CGRect(origin: .zero, size: shot.displayBounds.size)
            let view = OverlayView(frame: frame, image: shot.image, origin: shot.displayBounds.origin)
            view.session = self
            views.append(view)
            guard makesWindows, let screen = shot.screen else { continue }
            let window = OverlayWindow(screen: screen)
            let container = FrozenImageView(frame: frame, image: shot.image)
            view.autoresizingMask = [.width, .height]
            container.addSubview(view)
            window.contentView = container
            windows.append(window)
        }
        toolbar.isHidden = true
        toolbar.onTool = { [weak self] t in self?.toggleTool(t) }
        toolbar.onUndo = { [weak self] in self?.undo() }
        toolbar.onSave = { [weak self] in self?.finish(.save) }
        toolbar.onPin = { [weak self] in self?.finish(.pin) }
        toolbar.onCancel = { [weak self] in self?.close(.cancelled) }
        toolbar.onDone = { [weak self] in self?.finish(.copy) }
        toolbar.onStyleChange = { [weak self] in self?.styleChanged() }
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

    private func close(_ result: CaptureResult) {
        guard !finished else { return }
        finished = true
        windows.forEach { $0.orderOut(nil) }
        NSCursor.arrow.set()
        onFinish?(result)
    }

    private func redraw() { views.forEach { $0.needsDisplay = true } }

    // MARK: - 给各屏遮罩画图用

    var showsMagnifier: Bool {
        switch drag {
        case .resizing: return true
        case .moving, .drawing: return false
        case .editing(_, let h, _, _, _): return h != nil
        default: return phase != .selected
        }
    }

    var handlePoints: [CGPoint] { phase == .selected ? Handle.allCases.map { $0.point(in: selection) } : [] }

    /// 选中的标注；画控制点用。
    var selectedShape: Annotation? { phase == .selected ? selectedAnnotation.map { annotations[$0] } : nil }

    var shapeHandlePoints: [CGPoint] { selectedShape.map { shapeHandles($0).map(\.point) } ?? [] }

    /// 尺寸标签上的像素数，按矩形涉及的屏幕里最高的分辨率算，和导出一致。
    func sizeText(for r: CGRect) -> String {
        let px = Self.pixelRect(r, scale: scale(for: r))
        return "\(Int(px.width)) × \(Int(px.height))"
    }

    func colorText(_ c: (r: UInt8, g: UInt8, b: UInt8)) -> String {
        showHex ? String(format: "#%02X%02X%02X", c.r, c.g, c.b) : "RGB(\(c.r),\(c.g),\(c.b))"
    }

    // MARK: - 鼠标（全局坐标）

    func mouseMoved(to p: CGPoint) {
        trackMouse(p, redraws: phase != .selected)
        updateCursor(at: p)
        if phase == .choosing {
            let hover = windowRect(at: p)
            if hover != hoverRect {
                hoverRect = hover
                redraw()
            }
        }
    }

    func mouseExited(from view: OverlayView) {
        view.mouse = nil
        view.needsDisplay = true
        // 移到另一块屏幕时，那块屏幕的移动事件会接着更新高亮；所有屏幕上都没有鼠标了才清掉。
        guard views.allSatisfy({ $0.mouse == nil }) else { return }
        mouse = nil
        if phase == .choosing {
            hoverRect = nil
            redraw()
        }
    }

    func mouseDown(at point: CGPoint, clickCount: Int, in view: OverlayView) {
        let p = clamp(point)
        if textField != nil {
            // 正在输入文字时，点一下只结束输入（点在另一块屏幕上也一样）。
            commitText()
            return
        }
        switch phase {
        case .choosing, .dragging:
            drag = .creating(p)
        case .selected:
            // 双击文字：重新编辑（D-21）。第一下已经选中了它。
            if clickCount == 2, selection.contains(p), let i = annotationIndex(at: p), annotations[i].tool == .text {
                editText(i, in: view)
                return
            }
            if clickCount == 2, selection.contains(p) {
                finish(.copy)
                return
            }
            if let (i, h) = shapeHandle(at: p) {
                beginEditing(i, handle: h, at: p)
            } else if let h = handle(at: p) {
                selectedAnnotation = nil
                drag = .resizing(h, selection)
            } else if selection.contains(p), let i = annotationIndex(at: p) {
                selectAnnotation(i)
                beginEditing(i, handle: nil, at: p)
            } else if selection.contains(p), let tool {
                selectedAnnotation = nil
                if tool == .text {
                    beginText(at: p, in: view)
                } else {
                    current = Annotation(tool: tool, color: style.color, width: style.width(for: tool), points: [p])
                    drag = .drawing
                }
            } else if selection.contains(p), annotations.isEmpty {
                drag = .moving(p, selection)
                NSCursor.closedHand.set()
            } else {
                selectedAnnotation = nil
            }
        }
        layoutToolbar()
        redraw()
    }

    func mouseDragged(to point: CGPoint) {
        let p = clamp(point)
        // 拖动的事件一直发给按下时那块屏幕的视图；放大镜要跟着鼠标到它实际所在的屏幕。
        trackMouse(p, redraws: true)
        switch drag {
        case .none:
            return
        case .creating(let start):
            if phase != .dragging {
                guard hypot(p.x - start.x, p.y - start.y) > 3 else { return }
                phase = .dragging
                hoverRect = nil
            }
            selection = CGRect(from: start, to: p)
        case .moving(let start, let original):
            var r = original.offsetBy(dx: p.x - start.x, dy: p.y - start.y)
            r.origin.x = min(max(r.minX, desktop.minX), desktop.maxX - r.width)
            r.origin.y = min(max(r.minY, desktop.minY), desktop.maxY - r.height)
            selection = r
        case .resizing(let h, let original):
            selection = h.resize(original, to: p)
        case .drawing:
            current?.extend(to: p)
        case .editing(let i, let h, let start, let original, _):
            annotations[i] = edited(original, handle: h, from: start, to: p)
        }
        layoutToolbar()
        redraw()
    }

    func mouseUp(at point: CGPoint) {
        switch drag {
        case .creating(let start):
            if phase == .dragging {
                if selection.width >= 4, selection.height >= 4 { select() } else { reset() }
            } else {
                // 没有拖动：选中按下处的窗口（横跨几块屏幕也整个选中），没有窗口就选那块屏幕。
                selection = windowRect(at: start)
                select()
            }
        case .drawing:
            if let c = current, c.isMeaningful {
                history.append(annotations)
                annotations.append(c)
                // 刚画完的方框、圆形、箭头直接选中，可以马上调整（D-20）。画笔不选中，接着画下一笔（D-21）。
                if c.isShape { selectedAnnotation = annotations.count - 1 }
            }
            current = nil
        case .editing(_, _, _, _, let before):
            if annotations != before { history.append(before) }
        case .resizing:
            if selection.width < 4 || selection.height < 4 {
                selection.size = CGSize(width: max(selection.width, 4), height: max(selection.height, 4))
            }
        case .moving, .none:
            break
        }
        drag = .none
        layoutToolbar()
        updateCursor(at: clamp(point))
        redraw()
    }

    func rightMouseDown() {
        // 右键：有选区时取消选区，没有选区时退出截图。
        if phase == .choosing { close(.cancelled) } else { reset() }
    }

    /// 按全局位置更新各屏视图记录的鼠标位置：只有鼠标所在的那块屏有值。
    private func trackMouse(_ p: CGPoint, redraws: Bool) {
        mouse = views.contains { $0.globalFrame.contains(p) } ? p : nil
        for view in views {
            let local = view.globalFrame.contains(p) ? CGPoint(x: p.x - view.origin.x, y: p.y - view.origin.y) : nil
            guard view.mouse != local else { continue }
            view.mouse = local
            if redraws { view.needsDisplay = true }
        }
    }

    // MARK: - 键盘

    func handleKey(_ event: NSEvent) {
        let cmd = event.modifierFlags.contains(.command)
        // 按 ⌘ 键位表取字符：俄文等非拉丁布局下也能认出 C 键，和菜单快捷键的认法一致。
        let key = event.characters(byApplyingModifiers: .command)?.lowercased() ?? ""
        if event.keyCode == 53 { // Esc
            close(.cancelled)
            return
        }
        // ⌘C、⌘S、⌘Z 由主菜单分发，见下面的菜单命令。
        if phase == .selected {
            if event.keyCode == 36 || event.keyCode == 76 { finish(.copy) }
            if event.keyCode == 51 || event.keyCode == 117 { deleteSelectedAnnotation() } // Delete、⌦
        } else if !cmd, key == "c", let color = views.lazy.compactMap({ $0.colorUnderMouse() }).first {
            let pb = NSPasteboard.general
            pb.clearContents()
            pb.setString(colorText(color), forType: .string)
            close(.cancelled)
        }
    }

    func handleFlags(_ event: NSEvent) {
        // 只认 Shift 键本身按下（56 左、60 右）：先松开 ⌘ 等别的键时，flags 里也可能还带着 Shift。
        guard event.keyCode == 56 || event.keyCode == 60, event.modifierFlags.contains(.shift) else { return }
        showHex.toggle()
    }

    // MARK: - 菜单命令（由遮罩视图转过来，见 AppDelegate.makeMainMenu）

    func copySelection() { finish(.copy) }

    func saveSelection() { finish(.save) }

    func undoCommand() {
        // 正在输入时撤销输入的文字。字段编辑器不处理 undo:，会沿响应链经遮罩视图转到这里。
        if let editor = textField?.currentEditor() {
            editor.undoManager?.undo()
        } else {
            undo()
        }
    }

    func validate(_ action: Selector?) -> Bool {
        switch action {
        case #selector(OverlayView.copy(_:)), #selector(OverlayView.saveDocument(_:)):
            return phase == .selected
        case #selector(OverlayView.undo(_:)):
            if let editor = textField?.currentEditor() { return editor.undoManager?.canUndo ?? false }
            return !history.isEmpty
        default:
            return true
        }
    }

    // MARK: - 状态

    private func select() {
        phase = .selected
        hoverRect = nil
        layoutToolbar()
    }

    private func reset() {
        textField?.removeFromSuperview()
        textField = nil
        textHost = nil
        editingText = nil
        phase = .choosing
        drag = .none
        selection = .zero
        annotations = []
        history = []
        selectedAnnotation = nil
        current = nil
        tool = nil
        toolbar.selectedTool = nil
        hoverRect = mouse.map(windowRect(at:))
        layoutToolbar()
        if let mouse { updateCursor(at: mouse) }
        for view in views { view.window?.makeFirstResponder(view) }
        redraw()
    }

    private func toggleTool(_ t: Tool) {
        commitText()
        selectedAnnotation = nil
        redraw()
        tool = tool == t ? nil : t
        toolbar.selectedTool = tool
        layoutToolbar()
    }

    /// 有改动记录或正在输入文字时可以撤销；撤销正在输入的文字框会把它整个去掉。
    private func updateUndoButton() {
        toolbar.canUndo = textField != nil || !history.isEmpty
    }

    private func undo() {
        if let field = textField {
            field.removeFromSuperview()
            textField = nil
            if let host = textHost { host.window?.makeFirstResponder(host) }
            textHost = nil
            // 撤销重新编辑：丢掉改动，原来的文字照旧显示。
            editingText = nil
        } else if let previous = history.popLast() {
            // 撤销的可能是添加、挪动、改大小、改样式或删除，一律换回改动前的整组标注（D-20）。
            annotations = previous
            selectedAnnotation = nil
        }
        layoutToolbar()
        // 撤销完可能又能拖动选区了，光标马上跟着变，不等鼠标再动。
        if let mouse { updateCursor(at: mouse) }
        redraw()
    }

    private func finish(_ action: Finish) {
        commitText()
        guard let shot = renderSelection() else {
            close(.cancelled)
            return
        }
        switch action {
        case .copy:
            shot.copyToPasteboard()
            close(.copied)
        case .save:
            close(.save(shot))
        case .pin:
            close(.pin(shot, pinFrame(for: shot)))
        }
    }

    /// 钉图窗口按导出图实际的像素范围摆放，和图片一样大，不会被拉伸。返回 Cocoa 屏幕坐标。
    private func pinFrame(for shot: CapturedImage) -> NSRect {
        let scale = scale(for: selection)
        let px = Self.pixelRect(selection, scale: scale)
        let rect = CGRect(x: px.minX / scale, y: px.minY / scale, width: shot.pointSize.width, height: shot.pointSize.height)
        guard let view = views.first(where: { $0.globalFrame.contains(rect.origin) }) ?? views.first else { return rect }
        let rectInWindow = view.convert(rect.offsetBy(dx: -view.origin.x, dy: -view.origin.y), to: nil)
        return view.window?.convertToScreen(rectInWindow) ?? rectInWindow
    }

    // MARK: - 调整已画的标注（D-20）

    /// 当前工具下能不能点选这个标注（D-21）：没选工具或选的是方框、圆形、箭头时都能点；
    /// 文字工具下只能点文字；画笔、马赛克工具下都点不中，点击照常画。
    private func canSelect(_ a: Annotation) -> Bool {
        guard a.isEditable else { return false }
        switch tool {
        case nil, .rect?, .ellipse?, .arrow?: return true
        case .text?: return a.tool == .text
        default: return false
        }
    }

    /// 点中的标注，后画的在上面，优先。
    private func annotationIndex(at p: CGPoint) -> Int? {
        annotations.indices.last { canSelect(annotations[$0]) && annotations[$0].hitTest(p) }
    }

    private func shapeHandles(_ a: Annotation) -> [(handle: ShapeHandle, point: CGPoint)] {
        guard a.isShape, a.points.count > 1 else { return [] }
        if a.tool == .arrow { return [(.end(0), a.points[0]), (.end(1), a.points[1])] }
        let r = CGRect(from: a.points[0], to: a.points[1])
        return Handle.allCases.map { (.box($0), $0.point(in: r)) }
    }

    /// 选中标注的控制点。比选区的控制点优先：两者重叠时先调整标注。
    private func shapeHandle(at p: CGPoint) -> (Int, ShapeHandle)? {
        guard phase == .selected, let i = selectedAnnotation,
              let hit = shapeHandles(annotations[i]).first(where: { abs($0.point.x - p.x) <= 6 && abs($0.point.y - p.y) <= 6 })
        else { return nil }
        return (i, hit.handle)
    }

    /// 选中标注时，工具条显示它的颜色和粗细，方便直接改。方框、圆形、箭头还会切到它的工具，
    /// 框里接着画的是同一种（D-20）；文字、画笔不切，否则下一次点击就会新建文字或画新线（D-21）。
    private func selectAnnotation(_ i: Int) {
        let a = annotations[i]
        selectedAnnotation = i
        if a.isShape {
            tool = a.tool
            toolbar.selectedTool = a.tool
        }
        if let c = AnnotationStyle.colors.firstIndex(of: a.color) { style.colorIndex = c }
        let size = a.tool == .text
            ? AnnotationStyle.fontSizes.firstIndex(of: a.fontSize)
            : (0..<3).first { AnnotationStyle.width(for: a.tool, sizeIndex: $0) == a.width }
        if let size { style.sizeIndex = size }
        toolbar.refreshStyle()
    }

    /// 重新编辑文字：在原处打开文字框，填上原来的字（D-21）。
    private func editText(_ i: Int, in view: OverlayView) {
        selectAnnotation(i)
        selectedAnnotation = nil
        editingText = i
        drag = .none
        let a = annotations[i]
        beginText(at: a.points[0], in: view, text: a.text)
        layoutToolbar()
        redraw()
    }

    private func beginEditing(_ i: Int, handle: ShapeHandle?, at p: CGPoint) {
        drag = .editing(i, handle, p, annotations[i], annotations)
        if handle == nil { NSCursor.closedHand.set() }
    }

    private func edited(_ a: Annotation, handle: ShapeHandle?, from start: CGPoint, to p: CGPoint) -> Annotation {
        var b = a
        switch handle {
        case nil:
            b.points = a.points.map { CGPoint(x: $0.x + p.x - start.x, y: $0.y + p.y - start.y) }
        case .end(let n)?:
            b.points[n] = p
        case .box(let h)?:
            let r = h.resize(CGRect(from: a.points[0], to: a.points[1]), to: p)
            b.points = [r.origin, CGPoint(x: r.maxX, y: r.maxY)]
        }
        return b
    }

    private func deleteSelectedAnnotation() {
        guard let i = selectedAnnotation else { return }
        history.append(annotations)
        annotations.remove(at: i)
        selectedAnnotation = nil
        layoutToolbar()
        if let mouse { updateCursor(at: mouse) }
        redraw()
    }

    /// 工具条上改了颜色或粗细：改正在输入的文字，或者改选中的标注。
    func styleChanged() {
        applyStyleToTextField()
        guard let i = selectedAnnotation else { return }
        var a = annotations[i]
        a.color = style.color
        if a.tool == .text { a.fontSize = style.fontSize } else { a.width = style.width(for: a.tool) }
        guard a != annotations[i] else { return }
        history.append(annotations)
        annotations[i] = a
        redraw()
    }

    // MARK: - 文字

    /// text 为空时是新建：文字框竖直居中在点击处。否则是重新编辑：p 是原来文字的左上角，文字框放回原处。
    private func beginText(at p: CGPoint, in view: OverlayView, text: String = "") {
        let field = NSTextField(string: text)
        field.isBordered = false
        field.isBezeled = false
        field.drawsBackground = false
        field.focusRingType = .none
        field.delegate = self
        field.cell?.isScrollable = true
        field.cell?.wraps = false
        textField = field
        textHost = view
        applyStyleToTextField()
        let height = field.frame.height
        field.frame.origin = text.isEmpty
            ? CGPoint(x: p.x - view.origin.x, y: p.y - view.origin.y - height / 2)
            // 和 commitText 的换算相反：文字左边有 2 点内边距。
            : CGPoint(x: p.x - 2 - view.origin.x, y: p.y - view.origin.y)
        if toolbar.superview === view {
            view.addSubview(field, positioned: .below, relativeTo: toolbar)
        } else {
            view.addSubview(field)
        }
        view.window?.makeKey()
        view.window?.makeFirstResponder(field)
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
        let host = textHost
        textField = nil
        textHost = nil
        let text = field.stringValue
        // NSTextField 的文字左边有 2 点内边距；文字框在宿主屏的坐标里，换回全局坐标。
        let shift = host?.origin ?? .zero
        let origin = CGPoint(x: field.frame.minX + 2 + shift.x, y: field.frame.minY + shift.y)
        field.removeFromSuperview()
        let color = field.textColor ?? style.color, fontSize = field.font?.pointSize ?? style.fontSize
        if let i = editingText {
            // 重新编辑（D-21）：文字框没动过，位置沿用原来的，不经过来回换算；字删光了就删掉这个标注。
            editingText = nil
            var a = annotations[i]
            a.text = text
            a.color = color
            a.fontSize = fontSize
            if text.isEmpty {
                history.append(annotations)
                annotations.remove(at: i)
            } else if a != annotations[i] {
                history.append(annotations)
                annotations[i] = a
            }
        } else if !text.isEmpty {
            history.append(annotations)
            annotations.append(Annotation(tool: .text, color: color, width: 0,
                                          points: [origin], text: text, fontSize: fontSize))
        }
        if let host { host.window?.makeFirstResponder(host) }
        redraw()
    }

    // MARK: - 布局与光标

    private func layoutToolbar() {
        let visible: Bool
        switch drag {
        case .moving, .resizing: visible = false
        default: visible = phase == .selected
        }
        toolbar.isHidden = !visible
        guard visible, let host = toolbarHost() else { return }
        if toolbar.superview !== host { host.addSubview(toolbar) }
        let sel = selection.offsetBy(dx: -host.origin.x, dy: -host.origin.y)
        let bounds = host.bounds
        let size = toolbar.preferredSize
        let margin: CGFloat = 6
        let x = min(max(sel.maxX - size.width, margin), bounds.width - size.width - margin)
        var y = sel.maxY + margin
        if y + size.height > bounds.height - margin {
            y = sel.minY - margin - size.height
            if y < margin { y = sel.maxY - size.height - margin }
        }
        toolbar.frame = CGRect(x: x, y: y, width: size.width, height: size.height)
    }

    /// 工具条放在选区右下角所在的屏幕；右下角落在屏幕之间的空隙里时，放在选区中心所在的屏幕（D-8）。
    private func toolbarHost() -> OverlayView? {
        let corner = CGPoint(x: selection.maxX - 1, y: selection.maxY - 1)
        let center = CGPoint(x: selection.midX, y: selection.midY)
        return views.first { $0.globalFrame.contains(corner) } ?? views.first { $0.globalFrame.contains(center) }
            ?? views.first { $0.globalFrame.intersects(selection) } ?? views.first
    }

    private func updateCursor(at p: CGPoint) {
        switch phase {
        case .choosing, .dragging:
            NSCursor.crosshair.set()
        case .selected:
            let host = toolbar.superview as? OverlayView
            if let host, toolbar.hitTest(CGPoint(x: p.x - host.origin.x, y: p.y - host.origin.y)) != nil {
                NSCursor.arrow.set()
            } else if let (_, h) = shapeHandle(at: p) {
                h.cursor.set()
            } else if let h = handle(at: p) {
                h.cursor.set()
            } else if selection.contains(p), annotationIndex(at: p) != nil {
                NSCursor.openHand.set()
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
            let clipped = r.intersection(desktop)
            if !clipped.isNull, clipped.width > 1, clipped.height > 1 { return clipped }
        }
        return views.first { $0.globalFrame.contains(p) }?.globalFrame ?? desktop
    }

    private func clamp(_ p: CGPoint) -> CGPoint {
        CGPoint(x: min(max(p.x, desktop.minX), desktop.maxX), y: min(max(p.y, desktop.minY), desktop.maxY))
    }

    // MARK: - 导出

    /// 矩形涉及的屏幕里最高的分辨率（D-8）。
    private func scale(for r: CGRect) -> CGFloat {
        views.filter { $0.globalFrame.intersects(r) }.map(\.scale).max() ?? views.first?.scale ?? 1
    }

    /// 矩形对应的像素范围：四条边各自取整，导出的图和屏幕上看到的选区边界一致。
    static func pixelRect(_ r: CGRect, scale: CGFloat) -> CGRect {
        let x0 = (r.minX * scale).rounded(), x1 = (r.maxX * scale).rounded()
        let y0 = (r.minY * scale).rounded(), y1 = (r.maxY * scale).rounded()
        return CGRect(x: x0, y: y0, width: x1 - x0, height: y1 - y0)
    }

    /// 按原生像素裁出选区并画上标注。跨屏时把各块屏幕的部分拼起来，屏幕之间的空隙留透明（D-8）。
    func renderSelection() -> CapturedImage? {
        let parts = views.filter { $0.globalFrame.intersects(selection) }
        let scale = scale(for: selection)
        let px = Self.pixelRect(selection, scale: scale)
        guard let first = parts.first, px.width >= 1, px.height >= 1 else { return nil }
        let w = Int(px.width), h = Int(px.height)
        let srgb = CGColorSpace(name: CGColorSpace.sRGB)!
        let info = CGImageAlphaInfo.premultipliedLast.rawValue
        guard let ctx = CGContext(data: nil, width: w, height: h, bitsPerComponent: 8, bytesPerRow: 0,
                                  space: first.image.colorSpace ?? srgb, bitmapInfo: info)
                ?? CGContext(data: nil, width: w, height: h, bitsPerComponent: 8, bytesPerRow: 0,
                             space: srgb, bitmapInfo: info) else { return nil }
        for view in parts {
            // 这块屏幕上的那部分：从它自己的截图里按原生像素裁出来，画到输出图里对应的位置（CG 原点在左下）。
            let part = view.globalFrame.intersection(selection)
            let imageRect = CGRect(x: 0, y: 0, width: view.image.width, height: view.image.height)
            let source = Self.pixelRect(part.offsetBy(dx: -view.origin.x, dy: -view.origin.y), scale: view.scale)
                .intersection(imageRect)
            guard !source.isNull, !source.isEmpty, let crop = view.image.cropping(to: source) else { continue }
            let dest = Self.pixelRect(part, scale: scale)
            ctx.draw(crop, in: CGRect(x: dest.minX - px.minX, y: px.maxY - dest.maxY, width: dest.width, height: dest.height))
        }
        if !annotations.isEmpty {
            // 换成全局坐标：左上角原点、单位为点。
            ctx.translateBy(x: 0, y: CGFloat(h))
            ctx.scaleBy(x: scale, y: -scale)
            ctx.translateBy(x: -px.minX / scale, y: -px.minY / scale)
            NSGraphicsContext.saveGraphicsState()
            NSGraphicsContext.current = NSGraphicsContext(cgContext: ctx, flipped: true)
            for a in annotations { draw(a, over: parts) }
            NSGraphicsContext.restoreGraphicsState()
        }
        guard let out = ctx.makeImage() else { return nil }
        return CapturedImage(cgImage: out, pointSize: CGSize(width: CGFloat(w) / scale, height: CGFloat(h) / scale))
    }

    /// 马赛克要用每块屏幕自己的像素化底图，按屏幕分开画；其他标注画一次。
    private func draw(_ a: Annotation, over parts: [OverlayView]) {
        guard a.tool == .mosaic else { return a.draw(mosaic: nil, canvas: desktop) }
        for view in parts {
            NSGraphicsContext.saveGraphicsState()
            if parts.count > 1 { NSBezierPath(rect: view.globalFrame).addClip() }
            a.draw(mosaic: view.mosaicImage, canvas: view.globalFrame)
            NSGraphicsContext.restoreGraphicsState()
        }
    }

    // MARK: - 自检用

    func simulateHover(at p: CGPoint) {
        trackMouse(p, redraws: true)
        hoverRect = windowRect(at: p)
    }

    /// 往正在输入的文字框里填字，代替键盘输入。
    func simulateTyping(_ text: String) { textField?.stringValue = text }

    func simulateSelection(_ rect: CGRect, tool: Tool?, annotations: [Annotation]) {
        selection = rect
        phase = .selected
        self.tool = tool
        self.annotations = annotations
        // 当作逐个画上去的：每个标注一条撤销记录。
        history = annotations.indices.map { Array(annotations.prefix($0)) }
        selectedAnnotation = nil
        toolbar.selectedTool = tool
        layoutToolbar()
    }
}

/// 标注的控制点：方框、圆形是外接矩形的 8 个点，箭头是两端（D-20）。
private enum ShapeHandle {
    case box(Handle)
    case end(Int)

    var cursor: NSCursor {
        switch self {
        case .box(let h): return h.cursor
        case .end: return .crosshair
        }
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

final class OverlayWindow: NSWindow {
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

    override var canBecomeKey: Bool { true }
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
