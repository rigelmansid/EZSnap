# Snapshot

截图并标注的 macOS 菜单栏小工具。Swift + AppKit，不依赖第三方库。

要求：macOS 14 及以上；只需 Command Line Tools，不需要 Xcode。

## 构建与运行

```bash
./scripts/build.sh          # 生成 build/Snapshot.app
open build/Snapshot.app
```

首次运行需要授予屏幕录制权限：系统设置 → 隐私与安全性 → 屏幕与系统录音 → 打开 Snapshot，
然后退出 Snapshot（菜单栏剪刀图标 → 退出）再重新打开。

长期使用可以把 `build/Snapshot.app` 拖进“应用程序”，再在“系统设置 → 通用 → 登录项”里添加。

### 重新编译后权限失效

默认用 ad-hoc 签名，每次编译签名都会变，系统会认为是新应用，需要重新授权
（在权限列表里先删掉旧的 Snapshot 再加）。想避免这一点，在“钥匙串访问 → 证书助理 → 创建证书”
建一个“代码签名”类型的自签名证书，然后：

```bash
SIGN_IDENTITY="证书名称" ./scripts/build.sh
```

## 用法

| 操作 | 说明 |
|---|---|
| `⌃⌘A` | 开始截图（菜单栏 → 快捷键 可改成 `⌥A` 或 `⇧⌘A`） |
| 鼠标悬停 | 自动识别窗口，单击选中该窗口；没有窗口时选中整个屏幕 |
| 拖动 | 框选区域 |
| 拖选区内部 / 边角 | 移动 / 调整大小（有标注后不能移动，仍可调整大小） |
| 放大镜 | 显示坐标和颜色；`C` 复制颜色值，`Shift` 切换 RGB/HEX |
| 工具栏 | 矩形、椭圆、箭头、画笔、文字、马赛克；粗细 3 档，颜色 8 种 |
| 双击选区 / `Enter` / `⌘C` | 完成，复制到剪贴板 |
| `⌘S` / 保存按钮 | 另存为 PNG |
| 钉图按钮 | 把截图钉在屏幕最上层：拖动移动，滚轮缩放，双击或 `Esc` 关闭，右键菜单可复制、另存 |
| `⌘Z` | 撤销上一个标注 |
| 文字框里 | `⌘X`、`⌘C`、`⌘V`、`⌘A`、`⌘Z` 是普通的文字编辑；`⌘S` 直接保存；`Enter` 或 `Esc` 结束输入 |
| 右键 | 有选区时取消选区，没有选区时退出 |
| `Esc` | 退出截图 |

## 结构

| 文件 | 内容 |
|---|---|
| `AppDelegate.swift` | 菜单栏图标、快捷键选择、不显示的主菜单（分发 ⌘ 快捷键）、截图流程、钉图管理 |
| `HotKey.swift` | 全局快捷键（Carbon `RegisterEventHotKey`，不需要辅助功能权限） |
| `Capture.swift` | ScreenCaptureKit 截屏、窗口列表、复制和保存 |
| `CaptureSession.swift` | 一次截图：每块屏幕一个遮罩窗口 |
| `OverlayView.swift` | 选区、标注、放大镜、导出 |
| `Annotation.swift` | 标注模型和绘制、画笔样式 |
| `ToolbarView.swift` | 工具栏和粗细/颜色面板 |
| `PinWindow.swift` | 钉图窗口 |
| `SelfTest.swift` | 渲染自检 |

## 自检

不需要屏幕录制权限，用合成图片渲染遮罩界面和导出结果，输出 PNG 供查看：

```bash
build/Snapshot.app/Contents/MacOS/Snapshot --selftest /tmp
```

只覆盖绘制和导出，不覆盖截屏、快捷键和鼠标键盘交互。

## 未做

- 长截图、OCR、截图录屏
- 跨屏选区（选区只能在一块屏幕内）
- 自定义任意快捷键（目前只能从 3 个预设里选）
- 开机自启需要手动在系统设置里加
