# EZSnap 项目记录

决策见 [decisions.md](decisions.md)（D-n），踩过的坑见 [pitfalls.md](pitfalls.md)（坑 n）。

## Handoff

Updated: 2026-10-21 08:43
- Task: 无，上一个工作单元：改名 EZSnap 并做成可双击安装的 app、Windows 10 版、三种界面语言，都已合并推送（main 在 89f9839，只剩 main 一个分支；提交号是 2026-10-21 改写历史后的）
- Stopped at: 工作区干净；Mac 新版在 build/EZSnap.app（未装进 ~/Applications）；Windows 安装包在 GitHub Actions → Windows 最近一次运行的 EZSnap-windows 产物里
- Decisions: D-9～D-12（D-12 的“代码内三语文案”和 D-11 的“.NET Framework 4.8”是 agent 选择，用户未逐条确认）
- Waiting on user: Mac 和 Windows 的真机验证；签名方式；离屏测试是否入库；敏感词文件要拦哪些词
- Next: 1. 用户退出旧版后运行 ./scripts/install.sh，按待办的“在真实应用里验证”和“切换三种语言”逐项试 2. Windows 10 上装 CI 产物，按 D-11 那条清单试，问题记进待办
- Don't repeat: 不要一次派多个子代理（API 并发上限 3）；重新打包会换 ad-hoc 签名，要重新授权；写中文后一定查 U+FFFD（坑 5）；钩子误报不要 --no-verify（坑 4）；Windows 版只能靠 CI 和真机验证，这台 Mac 跑不了

## 项目概况

- 是什么：截图并标注的小工具。Mac 版是菜单栏应用（Swift + AppKit），见 [README](../README.md)；Windows 10/11 版是托盘程序（C# + WinForms，D-11），见 [windows/README.md](../windows/README.md)。界面有 English、简体中文、繁體中文（D-12）。
- 给谁用：发给别人用（D-3）。
- 目标：方便快速截图，并标注。
- 不做什么：长截图、OCR、截图录屏、开机自启。
- 仓库：GitHub 公开仓库，地址见 private-notes.md（D-7、D-16）；安装包在 Releases（D-15）。

## 待办

- [ ] 在真实应用里验证 2026-10-20 的两轮修复（D-2、D-4）：授权屏幕录制之前，先在权限提示框开着时多按几次快捷键，关掉后不应再弹；授权后试文字框里 ⌘V 粘贴；多屏时一块屏上正在输入文字、点另一块屏后文字应被提交（D-8 后的行为）；鼠标从主屏移到另一块屏后，主屏不应留着整屏蓝框，Shift 能切换颜色格式；⌘Z 撤销最后一个标注后光标马上变成手形
- [ ] 在真实应用里验证跨屏选区（D-8）：从一块屏拖到另一块屏；单击横跨两屏的窗口整个选中；跨屏选区上画标注、打字；导出的图拼接正确；钉图位置正确
- [ ] 在真实应用里验证自定义快捷键（D-6）：录一个组合键（如 ⌃⌥S）后能用它截图；录制时按现在的快捷键不应开始截图；只带 ⌘ 的组合会被拒绝
- [ ] 分发前定签名方式（D-3）：现在是 ad-hoc 签名，别人下载后会被 Gatekeeper 拦下，每个新版本还要重新授权屏幕录制；正式分发要用 Developer ID 签名并公证，需要 Apple 开发者账号
- [ ] 敏感词文件 .git/privacy-patterns 还没建（/adopt 第四节）：问用户要拦哪些真实用户名、主机名
- [ ] 从菜单开始截图靠固定等 0.25 秒让菜单淡出，机器忙时菜单可能被截进去（AppDelegate.captureFromMenu）；暂不改，真遇到再修（D-4）
- [ ] 小问题：选着文字工具时双击选区不会完成截图；“另存为”对话框开着时按快捷键会在它上面再开一次截图；暂不改（D-4）
- [ ] 在真实的 Windows 10 上验证 Windows 版（D-11）：安装、托盘图标和提示、Ctrl+Alt+A 截图、悬停识别窗口、跨屏选区、六种标注和文字输入（含中文输入法）、Ctrl+C/Ctrl+S/Ctrl+Z、钉图、自定义快捷键、卸载；多块屏 DPI 不同时界面大小是否正常
- [ ] 在两个版本里实际切换三种语言（D-12）：菜单、工具条提示、录制窗口、提示框、默认文件名；英文提示在录制窗口里是否放得下
- [ ] Windows 安装程序界面是英文的：要中文得自带 ChineseSimplified.isl（Inno Setup 官方没有）
- [ ] Windows 版代码签名：没签名会被 SmartScreen 拦，和 Mac 的签名方式一起定
