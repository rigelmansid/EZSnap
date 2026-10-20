import AppKit

/// 选区下方的工具条。选中绘图工具后，下面多出一行粗细和颜色。
final class ToolbarView: NSView {
    var onTool: ((Tool) -> Void)?
    var onUndo: (() -> Void)?
    var onSave: (() -> Void)?
    var onPin: (() -> Void)?
    var onCancel: (() -> Void)?
    var onDone: (() -> Void)?
    var onStyleChange: (() -> Void)?

    var selectedTool: Tool? { didSet { updateState() } }
    var canUndo = false { didSet { updateState() } }

    static let rowHeight: CGFloat = 40
    static let panelHeight: CGFloat = 34
    static let gap: CGFloat = 4

    private let bar = RoundedPanel()
    private let panel = StylePanel()
    private var toolButtons: [(Tool, ToolButton)] = []
    private var undoButton: ToolButton!
    private var barWidth: CGFloat = 0

    override var isFlipped: Bool { true }

    init() {
        super.init(frame: .zero)
        addSubview(bar)
        addSubview(panel)

        var x: CGFloat = 4
        func add(_ button: ToolButton) {
            button.frame = CGRect(x: x, y: 4, width: 32, height: 32)
            bar.addSubview(button)
            x += 32
        }
        func separator() {
            bar.addSubview(Separator(frame: CGRect(x: x + 4, y: 11, width: 1, height: 18)))
            x += 9
        }

        let tools: [(Tool, String, String)] = [
            (.rect, "square", "矩形"),
            (.ellipse, "circle", "椭圆"),
            (.arrow, "arrow.up.right", "箭头"),
            (.pen, "scribble", "画笔"),
            (.text, "textformat", "文字"),
            (.mosaic, "checkerboard.rectangle", "马赛克"),
        ]
        for (tool, symbol, tip) in tools {
            let button = ToolButton(symbol: symbol, tip: tip) { [weak self] in self?.onTool?(tool) }
            toolButtons.append((tool, button))
            add(button)
        }
        separator()
        undoButton = ToolButton(symbol: "arrow.uturn.backward", tip: "撤销 ⌘Z") { [weak self] in self?.onUndo?() }
        add(undoButton)
        separator()
        add(ToolButton(symbol: "square.and.arrow.down", tip: "保存 ⌘S") { [weak self] in self?.onSave?() })
        add(ToolButton(symbol: "pin", tip: "钉在屏幕上") { [weak self] in self?.onPin?() })
        separator()
        add(ToolButton(symbol: "xmark", tip: "退出截图 Esc",
                       tint: NSColor(srgbRed: 0.90, green: 0.25, blue: 0.22, alpha: 1)) { [weak self] in self?.onCancel?() })
        add(ToolButton(symbol: "checkmark", tip: "完成 Enter",
                       tint: NSColor(srgbRed: 0.16, green: 0.68, blue: 0.30, alpha: 1)) { [weak self] in self?.onDone?() })
        barWidth = x + 4

        panel.onChange = { [weak self] in self?.onStyleChange?() }
        updateState()
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    var preferredSize: CGSize {
        CGSize(width: barWidth,
               height: selectedTool == nil ? Self.rowHeight : Self.rowHeight + Self.gap + Self.panelHeight)
    }

    private func updateState() {
        for (tool, button) in toolButtons { button.isSelected = tool == selectedTool }
        undoButton.isEnabled = canUndo
        bar.frame = CGRect(x: 0, y: 0, width: barWidth, height: Self.rowHeight)
        panel.isHidden = selectedTool == nil
        panel.showsColors = selectedTool != .mosaic
        panel.frame = CGRect(x: 0, y: Self.rowHeight + Self.gap, width: panel.preferredWidth, height: Self.panelHeight)
    }

    // 吞掉工具条空白处的点击，免得传给下面的遮罩。
    override func mouseDown(with event: NSEvent) {}
    override func rightMouseDown(with event: NSEvent) {}
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
}

class RoundedPanel: NSView {
    override var isFlipped: Bool { true }

    override init(frame: NSRect) {
        super.init(frame: frame)
        wantsLayer = true
        layer?.shadowColor = NSColor.black.cgColor
        layer?.shadowOpacity = 0.25
        layer?.shadowRadius = 4
        layer?.shadowOffset = CGSize(width: 0, height: -1)
    }

    convenience init() { self.init(frame: .zero) }

    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override func draw(_ dirtyRect: NSRect) {
        NSColor(white: 0.98, alpha: 1).setFill()
        NSBezierPath(roundedRect: bounds, xRadius: 6, yRadius: 6).fill()
    }

    override func mouseDown(with event: NSEvent) {}
    override func rightMouseDown(with event: NSEvent) {}
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
}

private final class Separator: NSView {
    override func draw(_ dirtyRect: NSRect) {
        NSColor(white: 0.82, alpha: 1).setFill()
        bounds.fill()
    }
}

final class ToolButton: NSView {
    var isSelected = false { didSet { needsDisplay = true } }
    var isEnabled = true { didSet { needsDisplay = true } }

    private let symbol: String
    private let tint: NSColor
    private let action: () -> Void

    init(symbol: String, tip: String, tint: NSColor = NSColor(white: 0.22, alpha: 1), action: @escaping () -> Void) {
        self.symbol = symbol
        self.tint = tint
        self.action = action
        super.init(frame: .zero)
        toolTip = tip
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override var isFlipped: Bool { true }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    override func mouseDown(with event: NSEvent) {
        if isEnabled { action() }
    }

    override func draw(_ dirtyRect: NSRect) {
        if isSelected {
            NSColor(srgbRed: 0.87, green: 0.93, blue: 1, alpha: 1).setFill()
            NSBezierPath(roundedRect: bounds.insetBy(dx: 3, dy: 3), xRadius: 5, yRadius: 5).fill()
        }
        let color = !isEnabled ? NSColor(white: 0.75, alpha: 1) : (isSelected ? Theme.accent : tint)
        guard let image = Symbols.image(symbol, color: color, size: 15) else { return }
        let s = image.size
        image.draw(in: CGRect(x: (bounds.width - s.width) / 2, y: (bounds.height - s.height) / 2, width: s.width, height: s.height),
                   from: .zero, operation: .sourceOver, fraction: 1, respectFlipped: true, hints: nil)
    }
}

/// 粗细（3 档）和颜色（8 色）。
final class StylePanel: RoundedPanel {
    var onChange: (() -> Void)?
    var showsColors = true { didSet { needsDisplay = true } }

    private let style = AnnotationStyle.shared
    private let sizeSlot: CGFloat = 28
    private let colorSlot: CGFloat = 22

    var preferredWidth: CGFloat {
        let colors = showsColors ? 9 + colorSlot * CGFloat(AnnotationStyle.colors.count) : 0
        return 6 + sizeSlot * 3 + colors + 6
    }

    private func sizeRect(_ i: Int) -> CGRect {
        CGRect(x: 6 + CGFloat(i) * sizeSlot, y: 3, width: sizeSlot, height: 28)
    }

    private func colorRect(_ i: Int) -> CGRect {
        CGRect(x: 6 + sizeSlot * 3 + 9 + CGFloat(i) * colorSlot, y: 6, width: colorSlot, height: 22)
    }

    override func draw(_ dirtyRect: NSRect) {
        super.draw(dirtyRect)
        for i in 0..<3 {
            let slot = sizeRect(i)
            let selected = i == style.sizeIndex
            if selected {
                NSColor(srgbRed: 0.87, green: 0.93, blue: 1, alpha: 1).setFill()
                NSBezierPath(roundedRect: slot.insetBy(dx: 2, dy: 2), xRadius: 5, yRadius: 5).fill()
            }
            let d: CGFloat = [4, 7, 10][i]
            (selected ? Theme.accent : NSColor(white: 0.35, alpha: 1)).setFill()
            NSBezierPath(ovalIn: CGRect(x: slot.midX - d / 2, y: slot.midY - d / 2, width: d, height: d)).fill()
        }
        guard showsColors else { return }
        NSColor(white: 0.82, alpha: 1).setFill()
        CGRect(x: 6 + sizeSlot * 3 + 4, y: 8, width: 1, height: 18).fill()
        for (i, color) in AnnotationStyle.colors.enumerated() {
            let box = colorRect(i).insetBy(dx: 3, dy: 3)
            color.setFill()
            box.fill()
            NSColor(white: 0.7, alpha: 1).setStroke()
            NSBezierPath(rect: box.insetBy(dx: 0.5, dy: 0.5)).stroke()
            if i == style.colorIndex {
                Theme.accent.setStroke()
                let ring = NSBezierPath(rect: box.insetBy(dx: -2, dy: -2))
                ring.lineWidth = 2
                ring.stroke()
            }
        }
    }

    override func mouseDown(with event: NSEvent) {
        let p = convert(event.locationInWindow, from: nil)
        if let i = (0..<3).first(where: { sizeRect($0).contains(p) }) {
            style.sizeIndex = i
        } else if showsColors, let i = AnnotationStyle.colors.indices.first(where: { colorRect($0).contains(p) }) {
            style.colorIndex = i
        } else {
            return
        }
        needsDisplay = true
        onChange?()
    }
}

enum Symbols {
    static func image(_ name: String, color: NSColor, size: CGFloat) -> NSImage? {
        let base = NSImage(systemSymbolName: name, accessibilityDescription: nil)
            ?? NSImage(systemSymbolName: "questionmark", accessibilityDescription: nil)
        let config = NSImage.SymbolConfiguration(pointSize: size, weight: .medium)
            .applying(NSImage.SymbolConfiguration(paletteColors: [color]))
        return base?.withSymbolConfiguration(config)
    }
}
