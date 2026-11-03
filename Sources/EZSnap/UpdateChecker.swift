import AppKit

/// 检查 GitHub 上有没有新版本（D-22）：只读最新正式版的版本号和发布页地址，不下载、不安装。
/// 自动检查默认打开，可以在菜单里关掉；关掉后只有点“检查更新…”时才联网。
enum UpdateChecker {
    /// 只返回最新的正式版，预发布版和草稿不算。
    static let latestURL = URL(string: "https://api.github.com/repos/rigelmansid/EZSnap/releases/latest")!
    static let interval: TimeInterval = 24 * 60 * 60

    struct Release: Equatable {
        /// 去掉了前面的 v，如 0.2.0。
        let version: String
        let page: URL
    }

    static var currentVersion: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0"
    }

    static var autoCheck: Bool {
        get { UserDefaults.standard.object(forKey: "autoCheckUpdates") as? Bool ?? true }
        set { UserDefaults.standard.set(newValue, forKey: "autoCheckUpdates") }
    }

    /// 上次成功检查的时间。失败不记，下一轮会再试。
    static var lastCheck: Date? {
        get { UserDefaults.standard.object(forKey: "lastUpdateCheck") as? Date }
        set { UserDefaults.standard.set(newValue, forKey: "lastUpdateCheck") }
    }

    /// 已经弹过提示框的版本。同一个版本只弹一次，之后只留菜单项。
    static var notifiedVersion: String? {
        get { UserDefaults.standard.string(forKey: "notifiedUpdateVersion") }
        set { UserDefaults.standard.set(newValue, forKey: "notifiedUpdateVersion") }
    }

    /// 自动检查打开，并且距上次成功检查已满 24 小时。
    static var isDue: Bool {
        autoCheck && (lastCheck.map { Date().timeIntervalSince($0) >= interval } ?? true)
    }

    /// a 比 b 新吗？按点分段逐段比数字；前面的 v 去掉，缺的段当 0（0.1 和 0.1.0 一样）。
    static func isNewer(_ a: String, than b: String) -> Bool {
        let x = parts(a), y = parts(b)
        for i in 0..<max(x.count, y.count) {
            let l = i < x.count ? x[i] : 0, r = i < y.count ? y[i] : 0
            if l != r { return l > r }
        }
        return false
    }

    private static func parts(_ version: String) -> [Int] {
        stripV(version).split(separator: ".").map { Int($0.prefix { $0.isNumber }) ?? 0 }
    }

    private static func stripV(_ s: String) -> String {
        s.hasPrefix("v") || s.hasPrefix("V") ? String(s.dropFirst()) : s
    }

    /// 解析 releases/latest 的返回内容。发布页地址必须在 github.com 上，否则当作无法识别。
    static func parse(_ data: Data) throws -> Release {
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let tag = object["tag_name"] as? String, !tag.isEmpty,
              let page = (object["html_url"] as? String).flatMap(URL.init(string:)),
              page.scheme == "https", page.host == "github.com"
        else { throw UpdateError.badResponse }
        return Release(version: stripV(tag), page: page)
    }

    static func fetchLatest() async throws -> Release {
        var request = URLRequest(url: latestURL, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: 15)
        request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        request.setValue("EZSnap/\(currentVersion)", forHTTPHeaderField: "User-Agent")
        let (data, response) = try await URLSession.shared.data(for: request)
        let status = (response as? HTTPURLResponse)?.statusCode ?? 0
        guard status == 200 else { throw UpdateError.status(status) }
        return try parse(data)
    }
}

enum UpdateError: LocalizedError, Equatable {
    case badResponse
    case status(Int)

    var errorDescription: String? {
        switch self {
        case .badResponse:
            return L("GitHub returned a response EZSnap couldn't read.", "GitHub 返回的内容无法识别。", "GitHub 傳回的內容無法辨識。")
        case .status(let code):
            return L("GitHub returned HTTP \(code).", "GitHub 返回了 HTTP \(code)。", "GitHub 傳回了 HTTP \(code)。")
        }
    }
}
