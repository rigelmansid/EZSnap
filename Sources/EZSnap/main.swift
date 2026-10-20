import AppKit

let app = NSApplication.shared

// --selftest <目录>：不需要屏幕录制权限，用合成图片渲染界面和导出结果，输出 PNG。
if let i = CommandLine.arguments.firstIndex(of: "--selftest") {
    let dir = CommandLine.arguments.count > i + 1 ? CommandLine.arguments[i + 1] : NSTemporaryDirectory()
    SelfTest.run(outputDir: dir)
    exit(0)
}

let delegate = AppDelegate()
app.delegate = delegate
app.setActivationPolicy(.accessory)
app.run()
