using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;

namespace EZSnap
{
    /// <summary>
    /// 检查 GitHub 上有没有新版本（同 Mac 版 D-22）：只读最新正式版的版本号和发布页地址，不下载、不安装。
    /// 自动检查默认打开，可以在托盘菜单里关掉；关掉后只有点“检查更新…”时才联网。
    /// </summary>
    internal static class UpdateChecker
    {
        /// <summary>只返回最新的正式版，预发布版和草稿不算。改仓库地址时要同时改这里和 Mac 版。</summary>
        public const string LatestUrl = "https://api.github.com/repos/rigelmansid/EZSnap/releases/latest";
        public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

        internal sealed class Release
        {
            /// <summary>去掉了前面的 v，如 0.2.0。</summary>
            public string Version;
            public Uri Page;
        }

        // 字段由 DataContractJsonSerializer 赋值，编译器看不出来。
#pragma warning disable CS0649
        [DataContract]
        private sealed class ReleaseJson
        {
            [DataMember(Name = "tag_name")] public string Tag;
            [DataMember(Name = "html_url")] public string Url;
        }
#pragma warning restore CS0649

        private static readonly Lazy<HttpClient> Client = new Lazy<HttpClient>(() =>
        {
            // .NET Framework 4.8 默认跟随系统的 TLS 设置；老系统上补上 TLS 1.2，GitHub 只接受 1.2 以上。
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("EZSnap/" + CurrentVersion);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return client;
        });

        /// <summary>程序的版本号，取 csproj 里的 Version 的前三段，如 0.1.1。</summary>
        public static string CurrentVersion
        {
            get
            {
                var v = Assembly.GetExecutingAssembly().GetName().Version;
                return v == null ? "0" : $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";
            }
        }

        public static bool AutoCheck(Settings s) => s.Get("autoCheckUpdates") != "0";

        public static void SetAutoCheck(Settings s, bool on) => s.Set("autoCheckUpdates", on ? "1" : "0");

        /// <summary>上次成功检查的时间。失败不记，下一轮会再试。</summary>
        public static DateTime? LastCheck(Settings s) =>
            DateTime.TryParse(s.Get("lastUpdateCheck"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t : (DateTime?)null;

        public static void SetLastCheck(Settings s, DateTime t) => s.Set("lastUpdateCheck", t.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));

        /// <summary>自动检查打开，并且距上次成功检查已满 24 小时。</summary>
        public static bool IsDue(Settings s)
        {
            var last = LastCheck(s);
            return AutoCheck(s) && (last == null || DateTime.UtcNow - last.Value.ToUniversalTime() >= Interval);
        }

        /// <summary>a 比 b 新吗？按点分段逐段比数字；前面的 v 去掉，缺的段当 0（0.1 和 0.1.0 一样）。</summary>
        public static bool IsNewer(string a, string b)
        {
            int[] x = Parts(a), y = Parts(b);
            for (var i = 0; i < Math.Max(x.Length, y.Length); i++)
            {
                int l = i < x.Length ? x[i] : 0, r = i < y.Length ? y[i] : 0;
                if (l != r) return l > r;
            }
            return false;
        }

        private static int[] Parts(string version) =>
            StripV(version).Split('.').Select(p => int.TryParse(new string(p.TakeWhile(char.IsDigit).ToArray()), out var n) ? n : 0).ToArray();

        private static string StripV(string s) => s.StartsWith("v") || s.StartsWith("V") ? s.Substring(1) : s;

        /// <summary>解析 releases/latest 的返回内容；无法识别时返回 null。发布页地址必须在 github.com 上。</summary>
        public static Release Parse(string json)
        {
            try
            {
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    var r = (ReleaseJson)new DataContractJsonSerializer(typeof(ReleaseJson)).ReadObject(stream);
                    if (r == null || string.IsNullOrEmpty(r.Tag) || !Uri.TryCreate(r.Url, UriKind.Absolute, out var page)
                        || page.Scheme != Uri.UriSchemeHttps || page.Host != "github.com") return null;
                    return new Release { Version = StripV(r.Tag), Page = page };
                }
            }
            catch (SerializationException) { return null; }
            catch (InvalidCastException) { return null; }
        }

        /// <summary>查一次最新正式版。网络或返回内容有问题时抛出异常，消息可以直接给用户看。</summary>
        public static async Task<Release> FetchLatest()
        {
            using (var response = await Client.Value.GetAsync(LatestUrl).ConfigureAwait(false))
            {
                if (response.StatusCode != HttpStatusCode.OK)
                    throw new InvalidOperationException(L.T($"GitHub returned HTTP {(int)response.StatusCode}.",
                        $"GitHub 返回了 HTTP {(int)response.StatusCode}。", $"GitHub 傳回了 HTTP {(int)response.StatusCode}。"));
                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return Parse(json) ?? throw new InvalidOperationException(L.T("GitHub returned a response EZSnap couldn't read.",
                    "GitHub 返回的内容无法识别。", "GitHub 傳回的內容無法辨識。"));
            }
        }
    }
}
