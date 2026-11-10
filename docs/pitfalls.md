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

### 坑 7 macOS 26 菜单里“屏幕录制权限设置…”前面空了一块（2026-10-21，已解决 2026-10-21）

- 现象：菜单栏菜单最后一组里，“屏幕录制权限设置…”比别的项多缩进一截。
- 原因：macOS 26 自动给系统标准操作（如 terminate: 的“退出”）配图标；同一组（两条分隔线之间）里没有图标的项会空出图标位来对齐。
- 修法：给权限设置项也配系统图标 lock.shield（AppDelegate.rebuildMenu）。
- 启示：新加菜单项时看同组里有没有系统会自动配图标的项；用小程序打印 NSMenuItem.image 就能确认。

### 坑 8 macOS 自带 bash 3.2 在 set -u 下展开空数组会报错（2026-10-21，已解决 2026-10-21）

- 现象：build.sh 加了 ARCH_FLAGS 数组后，不带 UNIVERSAL=1 运行时报 “ARCH_FLAGS[@]: unbound variable”，install.sh 也跟着用不了。
- 原因：bash 3.2 把空数组的 "${a[@]}" 当成未定义变量，set -u 下直接退出；新版 bash 没有这个问题。
- 修法：写成 ${a[@]+"${a[@]}"}；改完把带参数和不带参数两种调用都跑一遍。
- 启示：脚本要在系统自带的 /bin/bash 上测；改动只测了新加的那条路径，差点把原来的路径弄坏。

### 坑 9 PlistBuddy 改一个值会把 Info.plist 的键重新排序（2026-10-21，已解决 2026-10-21）

- 现象：用 PlistBuddy 改版本号后，git diff 显示 Info.plist 改了 12 行，几个键换了位置。
- 原因：PlistBuddy 读进来再整份写回，按键名排序。
- 修法：恢复原文件，只用文本替换改版本号那两行。
- 启示：改入库的 plist 用文本替换，或改完看 git diff 只剩想改的行；plutil -lint 检查格式。

### 坑 10 Windows 版第二次建文字输入框就报 Parameter is not valid（2026-11-03，已解决 2026-11-03）

- 现象：CI 自检里第二次双击文字重新编辑时，new TextBox() 在 Font.GetHeight 抛出 ArgumentException: Parameter is not valid。真机上同一次运行里第二次用文字工具应该也会这样。
- 原因：ApplyStyleToTextBox 先 Dispose 输入框原来的字体再换新字体；输入框还没加进窗口时，它的 Font 是全进程共用的 Control.DefaultFont，被释放后再建任何控件都会出错。以前的自检每个进程只建一次输入框，没测出来。
- 修法：输入框的字体由会话自己建、自己记（textFont），只释放它；输入框移除时一并释放。自检加了“共用的默认字体仍然可用”。
- 启示：控件的 Font 可能是继承来的共用对象，不是自己 new 的不要 Dispose；自检要覆盖同一功能连续用两次。

### 坑 11 换版本后屏幕录制权限显示为开，但新版本拿不到权限（2026-11-03，未解决）

- 现象：装过并授权的 EZSnap 卸载后，“屏幕与系统音频录制”列表里还留着 EZSnap，开关是开的；装上新版本后它一直提示没有权限，开关却显示为开，怎么点都没用。只有在列表里选中 EZSnap 点“−”删掉，再打开新版本重新授权才正常。用户从 0.1.0 换到 0.2.0 时遇到。
- 原因（推断，未逐项验证）：系统按 bundle id 加签名要求记录授权；EZSnap 是 ad-hoc 签名，签名要求就是可执行文件的哈希，每次编译都会变，旧记录对不上新程序，列表却按名字显示成同一项。问题出在换版本，不在卸载，覆盖安装也一样；所有 Mac 用户从 0.1.1 升到 0.2.0 都会遇到。
- 修法：D-24：发布版和本机构建改用自签名证书“EZSnap Code Signing”签名，签名要求变成“bundle id + 证书”，换版本不变；权限提示框说明旧记录并提供“重置权限”（tccutil reset）；README 常见问题和 0.2.0 发布说明写了处理方法。换版本后授权仍有效这一点待真机验证。
- 启示：没有固定签名证书时，屏幕录制、辅助功能这类权限每次换版本都要重新授权；发布说明和 README 要写清楚。

### 坑 12 bash 把紧跟在变量后面的中文引号当成变量名的一部分（2026-11-03，已解决 2026-11-03）

- 现象：make-signing-cert.sh 里 `echo "已生成“$NAME”"` 报 unbound variable，变量名显示为 NAME 后面跟着半个乱码字符，证书其实已经导入成功。
- 原因：在 UTF-8 区域设置下，bash 把 `”` 的多字节字符当成标识符字符，变量名变成了 `NAME` 加半个汉字；set -u 下直接报错。
- 修法：写成 `${NAME}`。
- 启示：脚本里变量后面紧跟中文标点时一律用 `${VAR}`；可以用 `grep -n '\$[A-Z_]*[“”]' scripts/*.sh` 查。
