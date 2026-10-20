import AppKit
import ScreenCaptureKit
import UniformTypeIdentifiers

/// 一块屏幕的冻结画面。
struct ScreenShot {
    let screen: NSScreen
    let image: CGImage
    /// CG 全局坐标（主屏左上角为原点，单位为点）。
    let displayBounds: CGRect
}

/// 截图结果：像素图加上它在屏幕上对应的点尺寸。
struct CapturedImage {
    let cgImage: CGImage
    let pointSize: CGSize

    var nsImage: NSImage { NSImage(cgImage: cgImage, size: pointSize) }

    private var bitmap: NSBitmapImageRep {
        let rep = NSBitmapImageRep(cgImage: cgImage)
        rep.size = pointSize
        return rep
    }

    var pngData: Data? { bitmap.representation(using: .png, properties: [:]) }

    func copyToPasteboard() {
        let rep = bitmap
        let pb = NSPasteboard.general
        pb.clearContents()
        if let png = rep.representation(using: .png, properties: [:]) { pb.setData(png, forType: .png) }
        if let tiff = rep.tiffRepresentation { pb.setData(tiff, forType: .tiff) }
    }

    func saveWithPanel(completion: (() -> Void)? = nil) {
        let panel = NSSavePanel()
        panel.allowedContentTypes = [.png]
        panel.canCreateDirectories = true
        panel.level = .modalPanel
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX") // 固定格式，不跟着系统日历（如农历）和数字形式变
        formatter.dateFormat = "yyyy-MM-dd HH.mm.ss"
        panel.nameFieldStringValue = "截图 \(formatter.string(from: Date())).png"
        let defaults = UserDefaults.standard
        if let dir = defaults.string(forKey: "lastSaveDir") {
            panel.directoryURL = URL(fileURLWithPath: dir)
        } else {
            panel.directoryURL = FileManager.default.urls(for: .desktopDirectory, in: .userDomainMask).first
        }
        let data = pngData
        NSApp.activate()
        panel.begin { response in
            if response == .OK, let url = panel.url, let data {
                do {
                    try data.write(to: url)
                    defaults.set(url.deletingLastPathComponent().path, forKey: "lastSaveDir")
                } catch {
                    NSAlert(error: error).runModal()
                }
            }
            completion?()
        }
    }
}

extension NSScreen {
    var displayID: CGDirectDisplayID? {
        (deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber)?.uint32Value
    }
}

enum ScreenCapturer {
    /// 按原生分辨率截下每块屏幕。
    @MainActor
    static func captureAll() async throws -> [ScreenShot] {
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
        var shots: [ScreenShot] = []
        for screen in NSScreen.screens {
            guard let id = screen.displayID,
                  let display = content.displays.first(where: { $0.displayID == id }) else { continue }
            let filter = SCContentFilter(display: display, excludingWindows: [])
            let config = SCStreamConfiguration()
            let scale = CGFloat(filter.pointPixelScale)
            config.width = Int(filter.contentRect.width * scale)
            config.height = Int(filter.contentRect.height * scale)
            config.showsCursor = false
            config.captureResolution = .best
            let image = try await SCScreenshotManager.captureImage(contentFilter: filter, configuration: config)
            shots.append(ScreenShot(screen: screen, image: image, displayBounds: CGDisplayBounds(id)))
        }
        return shots
    }

    /// 屏幕上可见的窗口，按从前到后排列，CG 全局坐标。只取普通窗口和菜单栏。
    static func visibleWindowRects() -> [CGRect] {
        let options: CGWindowListOption = [.optionOnScreenOnly, .excludeDesktopElements]
        guard let list = CGWindowListCopyWindowInfo(options, kCGNullWindowID) as? [[String: Any]] else { return [] }
        let pid = ProcessInfo.processInfo.processIdentifier
        return list.compactMap { info in
            guard let layer = info[kCGWindowLayer as String] as? Int, [0, 24, 25].contains(layer),
                  (info[kCGWindowOwnerPID as String] as? pid_t) != pid,
                  (info[kCGWindowAlpha as String] as? Double ?? 1) > 0.01,
                  let dict = info[kCGWindowBounds as String] as? NSDictionary,
                  let rect = CGRect(dictionaryRepresentation: dict as CFDictionary),
                  rect.width >= 10, rect.height >= 10 else { return nil }
            return rect
        }
    }
}
