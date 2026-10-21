using System.Globalization;

namespace EZSnap
{
    /// <summary>界面语言（D-12）：跟随系统，或者在托盘菜单里指定。</summary>
    internal enum Language { System, En, ZhHans, ZhHant }

    internal static class L
    {
        /// <summary>用户的选择，启动时从设置读出。</summary>
        public static Language Choice = Language.System;

        public static Language Current => Choice != Language.System ? Choice : FromCulture(CultureInfo.CurrentUICulture.Name);

        /// <summary>系统界面语言对应的语言：繁体中文（含港澳台）用繁体，其他中文用简体，其余用英文。</summary>
        public static Language FromCulture(string name)
        {
            if (!name.StartsWith("zh")) return Language.En;
            var hant = name.Contains("Hant") || name.EndsWith("-TW") || name.EndsWith("-HK") || name.EndsWith("-MO");
            return hant ? Language.ZhHant : Language.ZhHans;
        }

        /// <summary>按当前界面语言取文案：英文、简体中文、繁体中文。</summary>
        public static string T(string en, string hans, string hant)
        {
            switch (Current)
            {
                case Language.ZhHans: return hans;
                case Language.ZhHant: return hant;
                default: return en;
            }
        }

        /// <summary>菜单里显示的名字：三种语言都用各自的写法，看不懂当前语言的人也认得出来。</summary>
        public static string Title(Language l)
        {
            switch (l)
            {
                case Language.En: return "English";
                case Language.ZhHans: return "简体中文";
                case Language.ZhHant: return "繁體中文";
                default: return T("Follow System", "跟随系统", "跟隨系統");
            }
        }

        public static string Key(Language l) => l == Language.En ? "en" : l == Language.ZhHans ? "zh-Hans" : l == Language.ZhHant ? "zh-Hant" : "system";

        public static Language Parse(string s) => s == "en" ? Language.En : s == "zh-Hans" ? Language.ZhHans : s == "zh-Hant" ? Language.ZhHant : Language.System;
    }
}
