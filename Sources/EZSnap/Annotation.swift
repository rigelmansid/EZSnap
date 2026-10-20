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
    var fontSize: CGFloat { [14, 18, 26][sizeIndex] }

    func width(for tool: Tool) -> CGFloat {
        tool == .mosaic ? [12, 22, 34][sizeIndex] : [2, 4, 6][sizeIndex]
    }
}

/// 一个标注。坐标是遮罩视图的坐标（左上角原点，单位为点）。
struct Annotation {
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
            let attrs: [NSAttributedString.Key: Any] = [
                .font: NSFont.systemFont(ofSize: fontSize, weight: .medium),
                .foregroundColor: color,
            ]
            (text as NSString).draw(at: start, withAttributes: attrs)
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
