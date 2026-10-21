import AppKit
import Carbon

/// 全局快捷键。用 Carbon 的 RegisterEventHotKey，不需要辅助功能权限。
final class HotKey {
    static var onPress: (() -> Void)?
    /// 早于这个时刻（GetCurrentEventTime 的时间）按下的快捷键不处理。
    static var ignoreBefore: EventTime = 0
    private static var handlerInstalled = false
    private var ref: EventHotKeyRef?

    @discardableResult
    func register(keyCode: UInt32, modifiers: UInt32) -> Bool {
        unregister()
        if !HotKey.handlerInstalled {
            var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
            InstallEventHandler(GetApplicationEventTarget(), { _, event, _ in
                let time = GetEventTime(event)
                DispatchQueue.main.async {
                    if time >= HotKey.ignoreBefore { HotKey.onPress?() }
                }
                return noErr
            }, 1, &spec, nil, nil)
            HotKey.handlerInstalled = true
        }
        let id = EventHotKeyID(signature: OSType(0x534E_4150), id: 1) // "SNAP"
        return RegisterEventHotKey(keyCode, modifiers, id, GetApplicationEventTarget(), 0, &ref) == noErr
    }

    func unregister() {
        if let ref { UnregisterEventHotKey(ref) }
        ref = nil
    }
}

/// 一个快捷键：物理键位加修饰键（只取 ⌃⌥⇧⌘）。
struct Shortcut: Equatable {
    let keyCode: UInt32
    let modifiers: NSEvent.ModifierFlags

    init(keyCode: UInt32, modifiers: NSEvent.ModifierFlags) {
        self.keyCode = keyCode
        self.modifiers = modifiers.intersection([.control, .option, .shift, .command])
    }

    /// 菜单里的三个预设。按字符定义，用时按当前键盘布局找键位：AZERTY 上 ⌃⌘A 也在标着 A 的键上（D-6）。
    static var presets: [Shortcut] {
        let a = KeyLayout.keyCode(for: "a") ?? UInt32(kVK_ANSI_A)
        return [Shortcut(keyCode: a, modifiers: [.control, .command]),
                Shortcut(keyCode: a, modifiers: [.option]),
                Shortcut(keyCode: a, modifiers: [.shift, .command])]
    }

    var carbonModifiers: UInt32 {
        var m = 0
        if modifiers.contains(.control) { m |= controlKey }
        if modifiers.contains(.option) { m |= optionKey }
        if modifiers.contains(.shift) { m |= shiftKey }
        if modifiers.contains(.command) { m |= cmdKey }
        return UInt32(m)
    }

    /// 显示用，例如“⌃⌘A”：修饰键按系统惯用的 ⌃⌥⇧⌘ 顺序，按键按当前键盘布局显示。
    var title: String {
        var s = ""
        if modifiers.contains(.control) { s += "⌃" }
        if modifiers.contains(.option) { s += "⌥" }
        if modifiers.contains(.shift) { s += "⇧" }
        if modifiers.contains(.command) { s += "⌘" }
        return s + KeyLayout.name(for: keyCode)
    }

    /// 不能用作全局快捷键的原因；能用时为 nil（D-6）。
    var problem: String? {
        if KeyLayout.functionKeys.contains(keyCode) { return nil }
        if modifiers.intersection([.control, .option, .command]).isEmpty {
            return L("Include at least one of ⌘, ⌃, ⌥ (F1–F20 can be used alone)", "要带 ⌘、⌃、⌥ 中的至少一个（F1–F20 可以单独用）",
                     "要包含 ⌘、⌃、⌥ 其中至少一個（F1–F20 可單獨使用）")
        }
        if modifiers == .command { return L("⌘ alone would take over other apps' shortcuts (like ⌘C); add another modifier",
                                             "只带 ⌘ 会抢走各个应用自己的快捷键（如 ⌘C），请再加一个修饰键", "只用 ⌘ 會搶走各個 App 自己的快速鍵（如 ⌘C），請再加一個修飾鍵") }
        return nil
    }

    /// 状态栏菜单项的 keyEquivalent；空格、方向键这类表示不了的为 nil。
    var menuKeyEquivalent: String? {
        if let n = KeyLayout.functionKeys.firstIndex(of: keyCode), let f = UnicodeScalar(UInt32(NSF1FunctionKey + n)) {
            return String(Character(f))
        }
        guard KeyLayout.specialNames[keyCode] == nil, let c = KeyLayout.character(for: keyCode) else { return nil }
        return c.lowercased()
    }
}

/// 键位和字符的换算，默认按当前键盘布局。
enum KeyLayout {
    /// F1–F20 的键位，按顺序。
    static let functionKeys: [UInt32] = [kVK_F1, kVK_F2, kVK_F3, kVK_F4, kVK_F5, kVK_F6, kVK_F7, kVK_F8, kVK_F9, kVK_F10,
                                         kVK_F11, kVK_F12, kVK_F13, kVK_F14, kVK_F15, kVK_F16, kVK_F17, kVK_F18, kVK_F19,
                                         kVK_F20].map { UInt32($0) }

    /// 不打出可见字符的键，显示成符号。
    static let specialNames: [UInt32: String] = Dictionary(uniqueKeysWithValues: [
        (kVK_Return, "↩"), (kVK_ANSI_KeypadEnter, "⌤"), (kVK_Tab, "⇥"), (kVK_Space, "空格"), (kVK_Delete, "⌫"),
        (kVK_ForwardDelete, "⌦"), (kVK_Escape, "⎋"), (kVK_LeftArrow, "←"), (kVK_RightArrow, "→"), (kVK_UpArrow, "↑"),
        (kVK_DownArrow, "↓"), (kVK_Home, "↖"), (kVK_End, "↘"), (kVK_PageUp, "⇞"), (kVK_PageDown, "⇟"),
    ].map { (UInt32($0.0), $0.1) })

    /// 按键的显示名：F 键写成 F1，特殊键用符号，其余按布局取字符并大写。
    static func name(for keyCode: UInt32, layout: TISInputSource? = nil) -> String {
        if let n = functionKeys.firstIndex(of: keyCode) { return "F\(n + 1)" }
        if keyCode == UInt32(kVK_Space) { return L("Space", "空格", "空白鍵") }
        if let s = specialNames[keyCode] { return s }
        return character(for: keyCode, layout: layout)?.uppercased() ?? L("Key \(keyCode)", "键\(keyCode)", "鍵\(keyCode)")
    }

    /// 按这个键打出的字符。打不出可见的拉丁字符时（俄文等布局）退回 ⌘ 层，和菜单快捷键的认法一致。
    static func character(for keyCode: UInt32, layout: TISInputSource? = nil) -> String? {
        guard let data = layoutData(layout) else { return nil }
        if let c = translate(keyCode, modifierState: 0, data),
           c.unicodeScalars.allSatisfy({ $0.isASCII && $0.value > 0x20 && $0.value != 0x7F }) {
            return c
        }
        return translate(keyCode, modifierState: commandState, data)
    }

    /// 打出这个字符的键位：先找不按修饰键的那层，再找 ⌘ 层；找不到为 nil。
    static func keyCode(for character: String, layout: TISInputSource? = nil) -> UInt32? {
        guard let data = layoutData(layout) else { return nil }
        for state in [0, commandState] {
            if let code = (0..<128).first(where: { translate(UInt32($0), modifierState: state, data) == character }) {
                return UInt32(code)
            }
        }
        return nil
    }

    private static let commandState = UInt32(cmdKey >> 8) & 0xFF

    private static func layoutData(_ source: TISInputSource?) -> Data? {
        guard let source = source ?? TISCopyCurrentKeyboardLayoutInputSource()?.takeRetainedValue(),
              let ptr = TISGetInputSourceProperty(source, kTISPropertyUnicodeKeyLayoutData) else { return nil }
        return Unmanaged<CFData>.fromOpaque(ptr).takeUnretainedValue() as Data
    }

    private static func translate(_ keyCode: UInt32, modifierState: UInt32, _ data: Data) -> String? {
        data.withUnsafeBytes { raw -> String? in
            guard let layout = raw.bindMemory(to: UCKeyboardLayout.self).baseAddress else { return nil }
            var deadKeys: UInt32 = 0
            var chars = [UniChar](repeating: 0, count: 4)
            var length = 0
            let status = UCKeyTranslate(layout, UInt16(keyCode), UInt16(kUCKeyActionDisplay), modifierState,
                                        UInt32(LMGetKbdType()), OptionBits(kUCKeyTranslateNoDeadKeysMask),
                                        &deadKeys, chars.count, &length, &chars)
            guard status == noErr, length > 0 else { return nil }
            return String(utf16CodeUnits: chars, count: length)
        }
    }
}
