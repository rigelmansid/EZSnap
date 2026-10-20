// 生成应用图标：蓝色圆角方块上一把白色剪刀（SF Symbols 的 scissors）（D-9）。
// 用法：swift scripts/make-icon.swift Resources/AppIcon.icns
//      swift scripts/make-icon.swift windows/EZSnap/AppIcon.ico    （Windows 版，D-11）
import AppKit

let output = CommandLine.arguments.dropFirst().first ?? "Resources/AppIcon.icns"

func render(_ pixels: Int) -> Data {
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: pixels, pixelsHigh: pixels, bitsPerSample: 8,
                               samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB,
                               bytesPerRow: 0, bitsPerPixel: 0)!
    let s = CGFloat(pixels)
    rep.size = NSSize(width: s, height: s)
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)

    // macOS 图标网格：1024 的画布里图形占 824，四周留出阴影的位置。
    let inset = s * 100 / 1024
    let box = NSRect(x: inset, y: inset * 1.1, width: s - inset * 2, height: s - inset * 2)
    let shape = NSBezierPath(roundedRect: box, xRadius: box.width * 0.225, yRadius: box.width * 0.225)
    NSGraphicsContext.saveGraphicsState()
    let shadow = NSShadow()
    shadow.shadowColor = NSColor(white: 0, alpha: 0.3)
    shadow.shadowBlurRadius = s * 12 / 1024
    shadow.shadowOffset = NSSize(width: 0, height: -s * 6 / 1024)
    shadow.set()
    NSColor.white.setFill()
    shape.fill()
    NSGraphicsContext.restoreGraphicsState()
    NSGradient(starting: NSColor(srgbRed: 0.33, green: 0.70, blue: 1.00, alpha: 1),
               ending: NSColor(srgbRed: 0.05, green: 0.42, blue: 0.88, alpha: 1))!.draw(in: shape, angle: -90)

    let config = NSImage.SymbolConfiguration(pointSize: box.width * 0.42, weight: .semibold)
        .applying(NSImage.SymbolConfiguration(paletteColors: [.white]))
    if let symbol = NSImage(systemSymbolName: "scissors", accessibilityDescription: nil)?.withSymbolConfiguration(config) {
        let size = symbol.size
        symbol.draw(in: NSRect(x: box.midX - size.width / 2, y: box.midY - size.height / 2, width: size.width, height: size.height))
    }
    NSGraphicsContext.restoreGraphicsState()
    return rep.representation(using: .png, properties: [:])!
}

/// Windows 的 .ico：每个尺寸一张 PNG（Windows Vista 以后都认）。
func writeIco(_ path: String) throws {
    let sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    let images = sizes.map { render($0) }
    var data = Data()
    func le16(_ v: Int) { data.append(contentsOf: [UInt8(v & 0xFF), UInt8((v >> 8) & 0xFF)]) }
    func le32(_ v: Int) { le16(v & 0xFFFF); le16((v >> 16) & 0xFFFF) }
    le16(0); le16(1); le16(sizes.count)
    var offset = 6 + 16 * sizes.count
    for (size, png) in zip(sizes, images) {
        data.append(UInt8(size >= 256 ? 0 : size))
        data.append(UInt8(size >= 256 ? 0 : size))
        data.append(0); data.append(0)
        le16(1); le16(32); le32(png.count); le32(offset)
        offset += png.count
    }
    for png in images { data.append(png) }
    try data.write(to: URL(fileURLWithPath: path))
}

if output.hasSuffix(".ico") {
    try writeIco(output)
    print("已生成 \(output)")
    exit(0)
}

let iconset = URL(fileURLWithPath: NSTemporaryDirectory()).appendingPathComponent("AppIcon-\(UUID().uuidString).iconset")
try FileManager.default.createDirectory(at: iconset, withIntermediateDirectories: true)
for points in [16, 32, 128, 256, 512] {
    try render(points).write(to: iconset.appendingPathComponent("icon_\(points)x\(points).png"))
    try render(points * 2).write(to: iconset.appendingPathComponent("icon_\(points)x\(points)@2x.png"))
}
let iconutil = Process()
iconutil.executableURL = URL(fileURLWithPath: "/usr/bin/iconutil")
iconutil.arguments = ["-c", "icns", iconset.path, "-o", output]
try iconutil.run()
iconutil.waitUntilExit()
try FileManager.default.removeItem(at: iconset)
guard iconutil.terminationStatus == 0 else { print("iconutil 失败"); exit(1) }
print("已生成 \(output)")
