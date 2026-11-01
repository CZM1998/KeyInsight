using System;
using System.Collections.Generic;
using System.Drawing;

namespace KeyInsight.UI
{
    public static class AppNames
    {
        private static readonly Dictionary<string, string> Known =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "chrome", "Chrome" },
            { "msedge", "Edge" },
            { "firefox", "Firefox" },
            { "code", "VS Code" },
            { "devenv", "Visual Studio" },
            { "explorer", "资源管理器" },
            { "windowsterminal", "Windows Terminal" },
            { "cmd", "命令提示符" },
            { "powershell", "PowerShell" },
            { "pwsh", "PowerShell" },
            { "idea64", "IntelliJ IDEA" },
            { "pycharm64", "PyCharm" },
            { "webstorm64", "WebStorm" },
            { "goland64", "GoLand" },
            { "clion64", "CLion" },
            { "rider64", "Rider" },
            { "studio64", "Android Studio" },
            { "wechat", "微信" },
            { "weixin", "微信" },
            { "qq", "QQ" },
            { "dingtalk", "钉钉" },
            { "feishu", "飞书" },
            { "lark", "飞书" },
            { "discord", "Discord" },
            { "steam", "Steam" },
            { "steamwebhelper", "Steam" },
            { "telegram", "Telegram" },
            { "notepad", "记事本" },
            { "notepad++", "Notepad++" },
            { "sublime_text", "Sublime Text" },
            { "winword", "Word" },
            { "excel", "Excel" },
            { "powerpnt", "PowerPoint" },
            { "outlook", "Outlook" },
            { "acrobat", "Acrobat" },
            { "acrord32", "Acrobat" },
            { "applicationframehost", "UWP 应用" },
            { "keyinsight", "KeyInsight" },
            { "obs64", "OBS" },
            { "vlc", "VLC" },
            { "spotify", "Spotify" },
            { "javaw", "Java 应用" },
            { "unknown", "未知" },
            { "desktop", "桌面" }
        };

        private static readonly Color[] Colors = new Color[]
        {
            Color.FromArgb(56, 189, 248),
            Color.FromArgb(99, 102, 241),
            Color.FromArgb(168, 85, 247),
            Color.FromArgb(52, 211, 153),
            Color.FromArgb(251, 191, 36),
            Color.FromArgb(251, 113, 133),
            Color.FromArgb(96, 165, 250),
            Color.FromArgb(45, 212, 191)
        };

        public static string Display(string process)
        {
            if (string.IsNullOrEmpty(process)) return "未知";
            string name;
            if (Known.TryGetValue(process, out name)) return name;
            string s = process.Replace('_', ' ').Trim();
            if (s.Length == 0) return "未知";
            return char.ToUpperInvariant(s[0]) + (s.Length > 1 ? s.Substring(1) : "");
        }

        public static Color ColorFor(string process)
        {
            if (string.IsNullOrEmpty(process)) return Colors[0];
            int hash = 0;
            for (int i = 0; i < process.Length; i++) hash = ((hash * 31) + process[i]) & 0x7fffffff;
            return Colors[hash % Colors.Length];
        }
    }
}
