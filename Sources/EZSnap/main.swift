import AppKit

let app = NSApplication.shared

// --selftest <目录>：不需要屏幕录制权限，用合成图片渲染界面和导出结果，输出 PNG。
if let i = CommandLine.arguments.firstIndex(of: "--selftest") {
    let dir = CommandLine.arguments.count > i + 1 ? CommandLine.arguments[i + 1] : NSTemporaryDirectory()
    SelfTest.run(outputDir: dir)
    exit(0)
}

// --check-update：查一次 GitHub 上的最新正式版并打印结果，不弹窗、不改设置（D-22）。
if CommandLine.arguments.contains("--check-update") {
    Task {
        let current = UpdateChecker.currentVersion
        do {
            let latest = try await UpdateChecker.fetchLatest()
            let newer = UpdateChecker.isNewer(latest.version, than: current)
            print("最新正式版 \(latest.version)（\(latest.page.absoluteString)），当前 \(current)，\(newer ? "有新版本" : "已是最新")")
            exit(0)
        } catch {
            print("检查失败：\(error.localizedDescription)")
            exit(1)
        }
    }
    dispatchMain()
}

let delegate = AppDelegate()
app.delegate = delegate
app.setActivationPolicy(.accessory)
app.run()
