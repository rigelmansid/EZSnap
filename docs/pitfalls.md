# 踩过的坑

踩到时当场追加，编号递增（坑 1、坑 2……），旧条目不改编号。格式见 `~/agent-system/RULE.md`
第 2 节。还没解决的，在 [project-notes.md](project-notes.md) 的待办里写一行“解决 坑 n”；
解决后只改标题里的状态。

<!-- 模板（复制后去掉行首 4 个空格）：
    ### 坑 1 标题（YYYY-MM-DD，未解决 | 已解决 YYYY-MM-DD）

    - 现象：
    - 原因：
    - 修法：
    - 启示：
-->

### 坑 1 主菜单里有的快捷键，没有对象响应也会被吞掉（2026-10-20，已解决 2026-10-20）

- 现象：离屏探针里，主菜单有 ⌘C（copy:）而响应链上没有对象实现 copy: 时，按 ⌘C 后宿主视图的 keyDown 收不到这次按键。
- 原因：NSMenu 匹配到快捷键后，即使菜单项不可用，performKeyEquivalent 也返回 true，事件被消耗，不再交给第一响应者。
- 修法：主菜单里有的快捷键都在响应者上实现对应方法（OverlayView、PinView 的 copy:/saveDocument:/undo:），可用状态在 validateMenuItem 里给（D-1）。
- 启示：加主菜单前，先把 keyDown 里的 ⌘ 判断一起迁成响应方法；这类 AppKit 行为先用离屏小程序验证再动手。

### 坑 2 离屏测试里按键没送到、截图会话被提前释放（2026-10-20，已解决 2026-10-20）

- 现象：测试程序里回车、Esc 没有效果；拖出选区后另一块屏没被锁定，⌘S 也没有回调。
- 原因：测试进程没激活、没有 key 窗口，经 NSApp.sendEvent 送的普通按键没有到达第一响应者（带 ⌘ 的经主菜单可以）；视图对 CaptureSession 是弱引用，测试没持有会话，创建后马上被释放。
- 修法：测试里覆写 NSApplication.keyWindow；带 ⌘ 的键走 NSApp.sendEvent，普通按键直接交给窗口的 sendEvent；用数组持有会话。
- 启示：测试要照真实的持有关系来（应用里由 AppDelegate 持有会话）；离屏测试只能验证到分发目标，“点击让窗口成为 key”这类系统行为还得在真机上试。

### 坑 3 快捷键录制框收不到带 ⌘ 的组合键（2026-10-20，已解决 2026-10-20）

- 现象：离屏测试里，录制框重写了 performKeyEquivalent，发给录制窗口的 ⌘C 还是没到录制框，被主菜单的“拷贝”吞掉（坑 1）。
- 原因：没完全查清：可能 AppKit 先查主菜单再查窗口里的视图，也可能是测试里伪造的 key 窗口没被问到。另外，测试里用 postEvent 放进队列的事件，nextEvent 取不出来。
- 修法：录制期间装本地事件监视器（NSEvent.addLocalMonitorForEvents），它在 NSApp.sendEvent 里先于主菜单拿到按键，不依赖分发顺序；测试直接调用 NSApp.sendEvent。
- 启示：要在主菜单之前截下带 ⌘ 的组合键，用本地事件监视器，不要依赖视图的 performKeyEquivalent。

### 坑 4 pre-commit 钩子把 app.manifest 里的 publicKeyToken 当成密钥（2026-10-21，已解决 2026-10-21）

- 现象：提交 Windows 版时被钩子拦下：app.manifest 第 27 行的 publicKeyToken（一串 16 位十六进制）报 [secret]。记这个坑时原样抄了那串值，又被拦了一次。
- 原因：钩子按“名字像 TOKEN、值是 16 位以上带数字的串”判断密钥；这是微软通用控件库公开的标识，属于误报。
- 修法：不绕过钩子，删掉 manifest 里这段依赖声明（Program.cs 的 Application.EnableVisualStyles() 已经启用新样式控件）。
- 启示：钩子误报时先找能去掉的内容；去不掉再告诉用户，不用 --no-verify。另外提交失败时命令要停下，那次没停，把没有新提交的分支也推了上去。

### 坑 5 用 Write 写进文件的中文也会冒出 U+FFFD（2026-10-21，已解决 2026-10-21）

- 现象：新建的 windows/EZSnap/SelfTest.cs 注释里“交互”的“互”变成了 U+FFFD；之前用 perl 改测试程序时也出现过。
- 原因：模型输出个别汉字时会产生坏字节，和用哪个工具写无关。
- 修法：每次写完中文都用 grep 查 U+FFFD，查到的按码点替换（这次用 python 写入 U+4E92）。
- 启示：写中文的文件、提交说明都要查，不只是改文档时查（RULE 第 7 节）。

### 坑 6 Windows 版一截图就报 CopyPixelOperation 参数无效（2026-10-21，已解决 2026-10-21）

- 现象：用户在 Windows 10 上截图，弹出“Capture Failed：The value of argument 'value' (1087111200) is invalid for Enum type 'CopyPixelOperation'”。
- 原因：.NET Framework 的 Graphics.CopyFromScreen 会校验参数，只认单个取值，SourceCopy | CaptureBlt（0x40CC0020）被拒；CI 的自检只渲染合成图片，从没调用过真实截屏，所以没发现。
- 修法：改为直接调用 BitBlt（SRCCOPY | CAPTUREBLT），截图格式用不带透明通道的 32 位；自检里加上真实截屏和取窗口列表，CI 上也会跑到。
- 启示：自检不能只测“画”，真正和系统打交道的入口至少要在 CI 上调用一次；Windows 版的 API 行为要以 .NET Framework 4.8 为准，不能按新版 .NET 的文档想当然。
