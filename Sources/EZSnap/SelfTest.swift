import AppKit

/// 渲染自检：用合成图片驱动截图会话和遮罩视图（不建窗口），把界面和导出结果写成 PNG 供人工查看。
/// 另外检查调整已画标注的逻辑（D-20）：直接调用会话的鼠标、键盘方法。
/// 不截屏、不需要屏幕录制权限，不经过真实的鼠标键盘事件，也不覆盖手感。
enum SelfTest {
    static func run(outputDir: String) {
        // 固定用简体中文，输出的图不随系统语言变，能和以前的结果逐字节比较。
        L10n.forced = .zhHans
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
        checkEditing(shot: shot, image: image, windows: windows, outputDir: outputDir)
        checkTextAndPen(shot: shot, image: image, windows: windows, outputDir: outputDir)
        checkUpdateLogic()
    }

    /// 版本号比较和 GitHub 返回内容的解析（D-22）。不联网。
    private static func checkUpdateLogic() {
        var failures = 0
        func check(_ ok: Bool, _ name: String) {
            print(ok ? "通过：\(name)" : "失败：\(name)")
            if !ok { failures += 1 }
        }
        let newer: [(String, String, Bool)] = [
            ("0.2.0", "0.1.1", true), ("0.1.1", "0.1.1", false), ("0.1.0", "0.1.1", false), ("0.1.10", "0.1.9", true),
            ("1.0", "0.9.9", true), ("0.1", "0.1.0", false), ("v0.1.2", "0.1.1", true), ("0.1.1", "v0.1.2", false),
        ]
        for (a, b, expected) in newer {
            check(UpdateChecker.isNewer(a, than: b) == expected, "\(a) 比 \(b) 新：\(expected ? "是" : "否")")
        }
        let page = "https://github.com/rigelmansid/EZSnap/releases/tag/v0.2.0"
        let good = try? UpdateChecker.parse(Data(#"{"tag_name":"v0.2.0","html_url":"\#(page)","draft":false}"#.utf8))
        check(good == UpdateChecker.Release(version: "0.2.0", page: URL(string: page)!), "解析 GitHub 返回内容：去掉 v，取发布页")
        let bad = [#"{"tag_name":"v0.2.0","html_url":"https://example.com/x"}"#, #"{"message":"Not Found"}"#, "not json"]
        for body in bad {
            check((try? UpdateChecker.parse(Data(body.utf8))) == nil, "无法识别的返回内容不当作新版本：\(body.prefix(30))")
        }
        if failures > 0 {
            print("自检失败：检查更新有 \(failures) 项不对")
            exit(1)
        }
    }

    /// 选中、挪动、改大小、改样式、删除已画的标注，再逐步撤销回原样（D-20）。
    private static func checkEditing(shot: ScreenShot, image: CGImage, windows: [CGRect], outputDir: String) {
        let colors = AnnotationStyle.colors
        func pt(_ x: CGFloat, _ y: CGFloat) -> CGPoint { CGPoint(x: x, y: y) }
        let original = [
            Annotation(tool: .rect, color: colors[0], width: 4, points: [pt(120, 100), pt(260, 180)]),
            Annotation(tool: .ellipse, color: colors[3], width: 4, points: [pt(300, 100), pt(420, 180)]),
            Annotation(tool: .arrow, color: colors[0], width: 4, points: [pt(460, 320), pt(540, 220)]),
        ]
        let s = CaptureSession(shots: [shot], windowRects: windows, makesWindows: false)
        s.simulateSelection(CGRect(x: 100, y: 80, width: 480, height: 280), tool: nil, annotations: original)
        let view = s.views[0]
        var failures = 0
        func check(_ ok: Bool, _ name: String) {
            print(ok ? "通过：\(name)" : "失败：\(name)")
            if !ok { failures += 1 }
        }
        func drag(_ a: CGPoint, _ b: CGPoint) {
            s.mouseDown(at: a, clickCount: 1, in: view)
            s.mouseDragged(to: b)
            s.mouseUp(at: b)
        }

        drag(pt(120, 140), pt(150, 160))
        check(s.annotations[0].points == [pt(150, 120), pt(290, 200)] && s.selectedAnnotation == 0, "拖方框的线条：选中并挪动")
        let moved = s.annotations
        drag(pt(290, 200), pt(320, 230))
        check(s.annotations[0].points == [pt(150, 120), pt(320, 230)], "拖方框右下角的控制点：改大小")
        let resized = s.annotations
        drag(pt(360, 140), pt(360, 140))
        check(s.selectedAnnotation == nil && s.annotations == resized, "没选工具时点圆形框内空白处：取消选中，不改标注")
        drag(pt(300, 140), pt(300, 140))
        check(s.selectedAnnotation == 1 && s.annotations == resized, "点圆形的线条：选中，不改标注")
        drag(pt(340, 125), pt(380, 155))
        check(s.annotations.count == 4 && s.annotations[1] == resized[1] && s.annotations[3].tool == .ellipse
              && s.selectedAnnotation == 3, "选中圆形后在框内空白处拖：画新圆形并选中，原来的不变")
        write(snapshot(view, image: image), to: "\(outputDir)/4-edit.png")
        let drawn = s.annotations
        drag(pt(500, 270), pt(500, 270))
        check(s.selectedAnnotation == 2, "点箭头中段：选中箭头")
        drag(pt(540, 220), pt(560, 200))
        check(s.annotations[2].points == [pt(460, 320), pt(560, 200)], "拖箭头终点")
        let arrowMoved = s.annotations
        AnnotationStyle.shared.colorIndex = 2
        AnnotationStyle.shared.sizeIndex = 2
        s.styleChanged()
        check(s.annotations[2].color == colors[2] && s.annotations[2].width == 6, "工具条改颜色和粗细：改到选中的箭头上")
        let styled = s.annotations
        if let key = NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: [], timestamp: 0, windowNumber: 0,
                                      context: nil, characters: "\u{7F}", charactersIgnoringModifiers: "\u{7F}",
                                      isARepeat: false, keyCode: 51) {
            s.handleKey(key)
        }
        check(s.annotations.count == 3 && !s.annotations.contains { $0.tool == .arrow } && s.selectedAnnotation == nil,
              "按 Delete：删掉选中的箭头")

        let steps: [([Annotation], String)] = [
            (styled, "删除"), (arrowMoved, "改样式"), (drawn, "拖箭头终点"), (resized, "画新圆形"),
            (moved, "改大小"), (original, "挪动"),
            (Array(original.prefix(2)), "画箭头"), (Array(original.prefix(1)), "画圆形"), ([], "画方框"),
        ]
        for (expected, name) in steps {
            s.undoCommand()
            check(s.annotations == expected, "撤销\(name)")
        }
        check(!s.validate(#selector(OverlayView.undo(_:))), "全部撤销后不能再撤销")
        if failures > 0 {
            print("自检失败：调整标注有 \(failures) 项不对")
            exit(1)
        }
    }

    /// 挪动文字和画笔线条、改样式、双击重新编辑文字；画笔工具开着时点不中旧线条（D-21）。
    private static func checkTextAndPen(shot: ScreenShot, image: CGImage, windows: [CGRect], outputDir: String) {
        let colors = AnnotationStyle.colors
        let style = AnnotationStyle.shared
        func pt(_ x: CGFloat, _ y: CGFloat) -> CGPoint { CGPoint(x: x, y: y) }
        let text = Annotation(tool: .text, color: colors[0], width: 0, points: [pt(150, 250)], text: "文字 Text", fontSize: 18)
        let pen = Annotation(tool: .pen, color: colors[2], width: 4,
                             points: [pt(200, 320), pt(250, 330), pt(300, 320), pt(350, 330), pt(400, 320)])
        let original = [text, pen]
        let region = CGRect(x: 100, y: 80, width: 480, height: 280)
        let s = CaptureSession(shots: [shot], windowRects: windows, makesWindows: false)
        s.simulateSelection(region, tool: nil, annotations: original)
        let view = s.views[0]
        var failures = 0
        func check(_ ok: Bool, _ name: String) {
            print(ok ? "通过：\(name)" : "失败：\(name)")
            if !ok { failures += 1 }
        }
        func drag(_ a: CGPoint, _ b: CGPoint) {
            s.mouseDown(at: a, clickCount: 1, in: view)
            s.mouseDragged(to: b)
            s.mouseUp(at: b)
        }
        func doubleClick(_ p: CGPoint) {
            s.mouseDown(at: p, clickCount: 1, in: view)
            s.mouseUp(at: p)
            s.mouseDown(at: p, clickCount: 2, in: view)
            s.mouseUp(at: p)
        }
        func clickOutside() {
            s.mouseDown(at: pt(520, 120), clickCount: 1, in: view)
            s.mouseUp(at: pt(520, 120))
        }

        drag(pt(170, 260), pt(190, 270))
        check(s.annotations[0].points == [pt(170, 260)] && s.selectedAnnotation == 0, "拖文字：选中并挪动")
        let textMoved = s.annotations
        drag(pt(300, 320), pt(300, 300))
        check(s.annotations[1].points == pen.points.map { pt($0.x, $0.y - 20) } && s.selectedAnnotation == 1,
              "拖画笔线条：选中并挪动")
        let penMoved = s.annotations
        style.colorIndex = 3
        style.sizeIndex = 2
        s.styleChanged()
        check(s.annotations[1].color == colors[3] && s.annotations[1].width == 6, "工具条改样式：改到选中的线条上")
        write(snapshot(view, image: image), to: "\(outputDir)/5-text-pen.png")
        let penStyled = s.annotations
        drag(pt(180, 272), pt(180, 272))
        check(s.selectedAnnotation == 0 && style.colorIndex == 0 && style.sizeIndex == 1, "点文字：选中，工具条显示它的颜色和字号")
        style.colorIndex = 4
        style.sizeIndex = 2
        s.styleChanged()
        check(s.annotations[0].color == colors[4] && s.annotations[0].fontSize == 26, "工具条改样式：改文字的颜色和字号")
        let textStyled = s.annotations
        doubleClick(pt(180, 272))
        check(s.editingText == 0 && s.annotations == textStyled, "双击文字：进入重新编辑")
        clickOutside()
        check(s.editingText == nil && s.annotations == textStyled, "不改内容就结束编辑：文字原样，位置不变")
        doubleClick(pt(180, 272))
        s.simulateTyping("改过的文字")
        clickOutside()
        check(s.annotations[0].text == "改过的文字" && s.annotations[0].points == [pt(170, 260)]
              && s.annotations[0].color == colors[4] && s.annotations[0].fontSize == 26, "改字后结束编辑：内容更新，位置和样式不变")
        let edited = s.annotations
        doubleClick(pt(180, 272))
        s.simulateTyping("")
        clickOutside()
        check(s.annotations == [penStyled[1]], "把字删光再结束编辑：删掉这个文字")

        let steps: [([Annotation], String)] = [
            (edited, "删光文字"), (textStyled, "改文字内容"), (penStyled, "改文字样式"), (penMoved, "改线条样式"),
            (textMoved, "挪线条"), (original, "挪文字"), ([text], "画线条"), ([], "写文字"),
        ]
        for (expected, name) in steps {
            s.undoCommand()
            check(s.annotations == expected, "撤销\(name)")
        }

        let penTool = CaptureSession(shots: [shot], windowRects: windows, makesWindows: false)
        penTool.simulateSelection(region, tool: .pen, annotations: [pen])
        penTool.mouseDown(at: pt(300, 320), clickCount: 1, in: penTool.views[0])
        penTool.mouseDragged(to: pt(320, 340))
        penTool.mouseUp(at: pt(320, 340))
        check(penTool.annotations.count == 2 && penTool.annotations[0] == pen && penTool.selectedAnnotation == nil,
              "画笔工具开着时在旧线条上拖：画新线条，旧的不动")
        if failures > 0 {
            print("自检失败：文字和画笔有 \(failures) 项不对")
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
