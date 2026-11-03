import AppKit

enum Tool: CaseIterable {
    case rect, ellipse, arrow, pen, text, mosaic
}

enum Theme {
    static let accent = NSColor(srgbRed: 0.11, green: 0.56, blue: 0.98, alpha: 1)
    static let dim = NSColor(white: 0, alpha: 0.42)
}

/// 当前画笔样式。整个应用共用一份，下次截图沿用上次的选择。
final class AnnotationStyle {
    static let shared = AnnotationStyle()
    static let colors: [NSColor] = [
        NSColor(srgbRed: 0.95, green: 0.23, blue: 0.21, alpha: 1),
        NSColor(srgbRed: 1.00, green: 0.78, blue: 0.10, alpha: 1),
        NSColor(srgbRed: 0.20, green: 0.78, blue: 0.35, alpha: 1),
        NSColor(srgbRed: 0.11, green: 0.56, blue: 0.98, alpha: 1),
        NSColor(srgbRed: 0.69, green: 0.32, blue: 0.87, alpha: 1),
        NSColor(srgbRed: 0.10, green: 0.10, blue: 0.10, alpha: 1),
        NSColor(srgbRed: 0.55, green: 0.55, blue: 0.55, alpha: 1),
        NSColor(srgbRed: 1.00, green: 1.00, blue: 1.00, alpha: 1),
    ]

    var colorIndex = 0
    var sizeIndex = 1

    var color: NSColor { Self.colors[colorIndex] }
    var fontSize: CGFloat { Self.fontSizes[sizeIndex] }

    static let fontSizes: [CGFloat] = [14, 18, 26]

    func width(for tool: Tool) -> CGFloat { Self.width(for: tool, sizeIndex: sizeIndex) }

    static func width(for tool: Tool, sizeIndex: Int) -> CGFloat {
        tool == .mosaic ? [12, 22, 34][sizeIndex] : [2, 4, 6][sizeIndex]
    }
}

/// 一个标注。坐标是遮罩视图的坐标（左上角原点，单位为点）。
struct Annotation: Equatable {
    var tool: Tool
    var color: NSColor
    var width: CGFloat
    /// 矩形、椭圆、箭头：起点和终点；画笔、马赛克：轨迹；文字：左上角。
    var points: [CGPoint]
    var text = ""
    var fontSize: CGFloat = 18

    mutating func extend(to p: CGPoint) {
        switch tool {
        case .pen, .mosaic:
            points.append(p)
        default:
            if points.count > 1 { points[1] = p } else { points.append(p) }
        }
    }

    /// 有控制点、能拉伸的标注：方框、圆形、箭头（D-20）。
    var isShape: Bool { tool == .rect || tool == .ellipse || tool == .arrow }

    /// 画完后还能选中挪动的标注：方框、圆形、箭头，加上文字和画笔（D-21）。马赛克不行。
    var isEditable: Bool { isShape || tool == .text || tool == .pen }

    private var font: NSFont { NSFont.systemFont(ofSize: fontSize, weight: .medium) }

    /// 文字占的范围，左上角是 points[0]。
    var textBounds: CGRect {
        CGRect(origin: points.first ?? .zero, size: (text as NSString).size(withAttributes: [.font: font]))
    }

    /// 选中时画的虚线外框：圆形是外接矩形，文字、画笔是外围留一点空的范围。方框、箭头不画。
    var selectionBox: CGRect? {
        switch tool {
        case .ellipse where points.count > 1: return CGRect(from: points[0], to: points[1])
        case .text: return textBounds.insetBy(dx: -3, dy: -3)
        case .pen:
            let box = points.reduce(CGRect.null) { $0.union(CGRect(origin: $1, size: .zero)) }
            return box.insetBy(dx: -(width / 2 + 3), dy: -(width / 2 + 3))
        default: return nil
        }
    }

    /// 点在线条上（线宽两侧各加 6 点余量）算点中。方框、圆形只认线条，框里的空白处可以接着画新的（D-20）。
    /// 文字点在字的范围里就算（D-21）。
    func hitTest(_ p: CGPoint) -> Bool {
        guard isEditable, !points.isEmpty else { return false }
        if tool == .text { return textBounds.insetBy(dx: -3, dy: -3).contains(p) }
        guard points.count > 1 else { return false }
        let path = CGMutablePath()
        switch tool {
        case .rect: path.addRect(CGRect(from: points[0], to: points[1]))
        case .ellipse: path.addEllipse(in: CGRect(from: points[0], to: points[1]))
        case .pen: path.addLines(between: points)
        default: path.addLines(between: [points[0], points[1]])
        }
        return path.copy(strokingWithWidth: width + 12, lineCap: .round, lineJoin: .round, miterLimit: 10).contains(p)
    }

    var isMeaningful: Bool {
        guard let first = points.first, let last = points.last else { return false }
        switch tool {
        case .text: return !text.isEmpty
        case .pen, .mosaic: return points.count > 1
        default: return hypot(last.x - first.x, last.y - first.y) > 3
        }
    }

    /// 在当前 NSGraphicsContext 中绘制。上下文必须是翻转的（左上角原点）。
    /// mosaic 是覆盖整个 canvas 的像素化底图，只有马赛克需要。
    func draw(mosaic: NSImage?, canvas: CGRect) {
        guard let start = points.first else { return }
        let end = points.last ?? start
        switch tool {
        case .rect:
            color.setStroke()
            let path = NSBezierPath(rect: CGRect(from: start, to: end))
            path.lineWidth = width
            path.stroke()
        case .ellipse:
            color.setStroke()
            let path = NSBezierPath(ovalIn: CGRect(from: start, to: end))
            path.lineWidth = width
            path.stroke()
        case .arrow:
            color.setFill()
            arrowPath(from: start, to: end)?.fill()
        case .pen:
            color.setStroke()
            let path = NSBezierPath()
            path.move(to: start)
            points.dropFirst().forEach { path.line(to: $0) }
            path.lineWidth = width
            path.lineCapStyle = .round
            path.lineJoinStyle = .round
            path.stroke()
        case .mosaic:
            guard let mosaic, let context = NSGraphicsContext.current else { return }
            let line = CGMutablePath()
            line.addLines(between: points)
            let area = line.copy(strokingWithWidth: width, lineCap: .round, lineJoin: .round, miterLimit: 10)
            context.saveGraphicsState()
            context.cgContext.addPath(area)
            context.cgContext.clip()
            context.imageInterpolation = .none
            mosaic.draw(in: canvas, from: .zero, operation: .sourceOver, fraction: 1, respectFlipped: true,
                        hints: [.interpolation: NSNumber(value: NSImageInterpolation.none.rawValue)])
            context.restoreGraphicsState()
        case .text:
            (text as NSString).draw(at: start, withAttributes: [.font: font, .foregroundColor: color])
        }
    }

    /// 尾部收成尖、头部是三角形的实心箭头。
    private func arrowPath(from a: CGPoint, to b: CGPoint) -> NSBezierPath? {
        let dx = b.x - a.x, dy = b.y - a.y
        let length = hypot(dx, dy)
        guard length > 1 else { return nil }
        let ux = dx / length, uy = dy / length
        let nx = -uy, ny = ux
        let headLength = min(length, 10 + width * 3.5)
        let headHalf = headLength * 0.45
        let shaftHalf = width * 0.6
        func at(_ distanceFromTip: CGFloat, _ side: CGFloat) -> CGPoint {
            CGPoint(x: b.x - ux * distanceFromTip + nx * side, y: b.y - uy * distanceFromTip + ny * side)
        }
        let path = NSBezierPath()
        path.move(to: a)
        path.line(to: at(headLength * 0.8, shaftHalf))
        path.line(to: at(headLength, headHalf))
        path.line(to: b)
        path.line(to: at(headLength, -headHalf))
        path.line(to: at(headLength * 0.8, -shaftHalf))
        path.close()
        return path
    }
}

extension CGRect {
    init(from a: CGPoint, to b: CGPoint) {
        self.init(x: min(a.x, b.x), y: min(a.y, b.y), width: abs(a.x - b.x), height: abs(a.y - b.y))
    }
}
