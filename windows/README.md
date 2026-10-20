# EZSnap（Windows 版）

和 Mac 版功能对应的 Windows 10 / 11 截图工具。C# + WinForms，目标框架 .NET Framework 4.8（Windows 10 1903 起自带，不用另装运行时）。来由和取舍见 [D-11](../docs/decisions.md)。

## 安装

在 GitHub 仓库的 Actions → Windows → 最近一次成功的运行里下载 `EZSnap-windows`，解压后运行 `EZSnap-Setup-版本号.exe`。

- 装到当前用户自己的目录（`%LOCALAPPDATA%\Programs\EZSnap`），不需要管理员权限；开始菜单里有 EZSnap，卸载在“设置 → 应用”里。
- 安装包没有代码签名，Windows 会提示“Windows 已保护你的电脑”：点“更多信息 → 仍要运行”。正式分发要买代码签名证书。
- 设置保存在 `%APPDATA%\EZSnap\settings.txt`，卸载时不删。

## 用法

和 Mac 版一样，几处按 Windows 习惯换了：

| Mac 版 | Windows 版 |
|---|---|
| 菜单栏剪刀图标 | 右下角托盘里的剪刀图标（可能收在 ^ 里），左键右键都弹菜单 |
| `⌃⌘A` / `⌥A` / `⇧⌘A` | `Ctrl+Alt+A` / `Alt+A` / `Ctrl+Shift+A`，也可以“自定义…” |
| `⌘C` / `⌘S` / `⌘Z` | `Ctrl+C` / `Ctrl+S` / `Ctrl+Z` |
| 屏幕录制权限 | 不需要 |

悬停识别窗口、拖动框选（可以跨屏）、放大镜取色（`C` 复制颜色，`Shift` 切换格式）、矩形 / 椭圆 / 箭头 / 画笔 / 文字 / 马赛克、撤销、双击或 `Enter` 完成、`Esc` 退出、右键取消、钉图，都和 Mac 版相同。

和 Mac 版不同的地方：

- 文字输入框是深灰底（WinForms 的输入框不能透明），提交后文字直接画在截图上。
- 安装程序界面是英文的（Inno Setup 官方没有简体中文语言包）。

## 构建

```bash
dotnet build windows/EZSnap/EZSnap.csproj -c Release     # 在 macOS 上也能编译，但只能在 Windows 上运行
```

- 自检：`EZSnap.exe --selftest <目录>`，用合成图片渲染界面、检查跨屏拼接等，结果写进 `<目录>\selftest.log`，失败时退出码非 0。不覆盖真实截屏和鼠标键盘交互。
- 安装包：Windows 上装 Inno Setup 6，运行 `ISCC windows\installer\EZSnap.iss`，生成到 `windows\dist\`。
- 图标：`swift scripts/make-icon.swift windows/EZSnap/AppIcon.ico`（在 Mac 上生成）。
- 推送到 GitHub 后，`.github/workflows/windows.yml` 会在 Windows 机器上自动做以上三步。

## 结构

| 文件 | 内容 | 对应 Mac 版 |
|---|---|---|
| `Program.cs` | 入口、单实例、`--selftest` | `main.swift` |
| `TrayApp.cs` | 托盘图标和菜单、快捷键选择、截图流程、钉图管理 | `AppDelegate.swift` |
| `HotKey.cs` | 全局快捷键（`RegisterHotKey`）、快捷键模型 | `HotKey.swift` |
| `HotKeyRecorderForm.cs` | 录制自定义快捷键 | `HotKeyRecorder.swift` |
| `Capture.cs` | 截屏、窗口列表、复制和保存 | `Capture.swift` |
| `CaptureSession.cs` | 一次截图：选区、标注、工具条、文字框统一管理，导出拼接 | `CaptureSession.swift` |
| `OverlayForm.cs` | 一块屏幕的遮罩 | `OverlayView.swift` |
| `Annotation.cs` | 标注模型和绘制、画笔样式 | `Annotation.swift` |
| `Toolbar.cs` | 工具条和粗细/颜色面板 | `ToolbarView.swift` |
| `PinForm.cs` | 钉图窗口 | `PinWindow.swift` |
| `SelfTest.cs` | 自检 | `SelfTest.swift` |
| `Native.cs` | Win32 接口 | — |
