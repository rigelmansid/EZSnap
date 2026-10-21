import Foundation

/// 界面语言（D-12）：跟随系统，或者在菜单里指定。
enum Language: String, CaseIterable {
    case system, en, zhHans = "zh-Hans", zhHant = "zh-Hant"

    /// 菜单里显示的名字：三种语言都用各自的写法，看不懂当前语言的人也认得出来。
    var title: String {
        switch self {
        case .system: return L("Follow System", "跟随系统", "跟隨系統")
        case .en: return "English"
        case .zhHans: return "简体中文"
        case .zhHant: return "繁體中文"
        }
    }

    /// 系统首选语言对应的界面语言：繁体中文（含港澳台）用繁体，其他中文用简体，其余用英文。
    static func from(preferred: String) -> Language {
        guard preferred.hasPrefix("zh") else { return .en }
        let hant = preferred.contains("Hant") || ["-TW", "-HK", "-MO"].contains { preferred.hasSuffix($0) }
        return hant ? .zhHant : .zhHans
    }
}

enum L10n {
    /// 自检时固定语言，让输出的图片不随系统语言变。
    static var forced: Language?

    static var choice: Language {
        get { Language(rawValue: UserDefaults.standard.string(forKey: "language") ?? "") ?? .system }
        set { UserDefaults.standard.set(newValue.rawValue, forKey: "language") }
    }

    static var current: Language {
        if let forced { return forced }
        let chosen = choice
        return chosen == .system ? Language.from(preferred: Locale.preferredLanguages.first ?? "en") : chosen
    }
}

/// 按当前界面语言取文案：英文、简体中文、繁体中文。
func L(_ en: String, _ hans: String, _ hant: String) -> String {
    switch L10n.current {
    case .zhHans: return hans
    case .zhHant: return hant
    default: return en
    }
}
