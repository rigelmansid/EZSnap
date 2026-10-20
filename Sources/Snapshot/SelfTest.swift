import AppKit

/// 渲染自检：用合成图片驱动截图会话和遮罩视图（不建窗口），把界面和导出结果写成 PNG 供人工查看。
/// 不截屏、不需要屏幕录制权限，也不覆盖鼠标键盘交互。
enum SelfTest {
    static func run(outputDir: String) {
        let size = CGSize(width: 800, height: 500)
        let scale: CGFloat = 2
        guard let image = sampleImage(width: Int(size.width * scale), height: Int(size.height * scale)) else {
            print("自检失败：无法生成样例图")
            exit(1)
        }
        let shot = ScreenShot(screen: nil, image: image, displayBounds: CGRect(origin: .zero, size: size))
        let windows = [CGRect(x: 60, y: 40, width: 360, height: 260), CGRect(x: 380, y: 160, width: 360, height: 280)]

        let choosing = CaptureSession(shots: [shot], windowRects: windows, makesWindows: false)
        choosing.simulateHover(at: CGPoint(x: 200, y: 150))
        write(snapshot(choosing.views[0], image: image), to: "\(outputDir)/1-choosing.png")

        let style = AnnotationStyle.shared
        let red = AnnotationStyle.colors[0], blue = AnnotationStyle.colors[3]
        let wave = (0...40).map { i in CGPoint(x: 140 + CGFloat(i) * 5, y: 300 + sin(CGFloat(i) / 4) * 15) }
        let annotations = [
            Annotation(tool: .rect, color: red, width: 4, points: [CGPoint(x: 120, y: 100), CGPoint(x: 260, y: 180)]),
            Annotation(tool: .ellipse, color: blue, width: 4, points: [CGPoint(x: 300, y: 100), CGPoint(x: 420, y: 180)]),
            Annotation(tool: .arrow, color: red, width: 4, points: [CGPoint(x: 460, y: 320), CGPoint(x: 540, y: 220)]),
            Annotation(tool: .pen, color: AnnotationStyle.colors[2], width: 4, points: wave),
            Annotation(tool: .mosaic, color: red, width: style.width(for: .mosaic),
                       points: [CGPoint(x: 300, y: 250), CGPoint(x: 440, y: 250)]),
            Annotation(tool: .text, color: red, width: 0, points: [CGPoint(x: 120, y: 200)], text: "文字标注 Text", fontSize: 18),
        ]
        let selected = CaptureSession(shots: [shot], windowRects: windows, makesWindows: false)
        selected.simulateSelection(CGRect(x: 100, y: 80, width: 480, height: 280), tool: .arrow, annotations: annotations)
        write(snapshot(selected.views[0], image: image), to: "\(outputDir)/2-selected.png")

        if let out = selected.renderSelection() {
            write(out.pngData, to: "\(outputDir)/3-export.png")
            print("导出尺寸：\(out.cgImage.width)×\(out.cgImage.height) 像素，\(out.pointSize.width)×\(out.pointSize.height) 点")
        } else {
            print("自检失败：renderSelection 返回 nil")
            exit(1)
        }
    }

    private static func snapshot(_ overlay: OverlayView, image: CGImage) -> Data? {
        let container = FrozenImageView(frame: overlay.frame, image: image)
        container.addSubview(overlay)
        guard let rep = container.bitmapImageRepForCachingDisplay(in: container.bounds) else { return nil }
        container.cacheDisplay(in: container.bounds, to: rep)
        return rep.representation(using: .png, properties: [:])
    }

    private static func write(_ data: Data?, to path: String) {
        guard let data else {
            print("自检失败：\(path) 没有数据")
            exit(1)
        }
        do {
            try data.write(to: URL(fileURLWithPath: path))
            print("已写入 \(path)")
        } catch {
            print("自检失败：\(error)")
            exit(1)
        }
    }

    /// 彩色色块加文字，方便看清马赛克、放大镜和方向是否正确。
    private static func sampleImage(width: Int, height: Int) -> CGImage? {
        guard let space = CGColorSpace(name: CGColorSpace.sRGB),
              let ctx = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0, space: space,
                                  bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
        let w = CGFloat(width), h = CGFloat(height)
        let blocks: [(CGFloat, CGFloat, CGFloat)] = [(0.95, 0.85, 0.75), (0.75, 0.88, 0.95), (0.85, 0.95, 0.78), (0.95, 0.80, 0.90)]
        for (i, c) in blocks.enumerated() {
            ctx.setFillColor(CGColor(srgbRed: c.0, green: c.1, blue: c.2, alpha: 1))
            ctx.fill(CGRect(x: w / 2 * CGFloat(i % 2), y: h / 2 * CGFloat(i / 2), width: w / 2, height: h / 2))
        }
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(cgContext: ctx, flipped: false)
        let attrs: [NSAttributedString.Key: Any] = [.font: NSFont.boldSystemFont(ofSize: 56), .foregroundColor: NSColor.black]
        // CG 坐标原点在左下，所以这行字应出现在图片上方。
        ("TOP 上方 Snapshot" as NSString).draw(at: CGPoint(x: 80, y: h - 140), withAttributes: attrs)
        ("Mosaic 马赛克区域" as NSString).draw(at: CGPoint(x: 600, y: h - 540), withAttributes: attrs)
        NSGraphicsContext.restoreGraphicsState()
        return ctx.makeImage()
    }
}
