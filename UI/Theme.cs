using System;
using System.Drawing;
using Microsoft.Win32;

namespace KeyInsight.UI
{
    public enum ThemeMode
    {
        FollowSystem = 0,
        Dark = 1,
        Light = 2
    }

    public sealed class Palette
    {
        public bool Dark;
        public Color Bg;
        public Color Sidebar;
        public Color TitleBar;
        public Color Card;
        public Color CardAlt;
        public Color Border;
        public Color BorderStrong;
        public Color Text;
        public Color Muted;
        public Color Dim;
        public Color Accent1;
        public Color Accent2;
        public Color Accent3;
        public Color Good;
        public Color Warn;
        public Color Bad;
        public Color Info;
        public Color Hover;
        public Color Selected;
        public Color[] Heat;
        public Color HeatEmpty;
        public Color Grid;

        public Color HeatColor(double t)
        {
            if (t <= 0) return HeatEmpty;
            if (t > 1) t = 1;
            double scaled = t * (Heat.Length - 1);
            int i = (int)Math.Floor(scaled);
            if (i >= Heat.Length - 1) return Heat[Heat.Length - 1];
            double f = scaled - i;
            return Ui.Blend(Heat[i], Heat[i + 1], (float)f);
        }

        public Color TextOn(Color bg)
        {
            double lum = (0.2126 * bg.R + 0.7152 * bg.G + 0.0722 * bg.B) / 255.0;
            return lum > 0.62 ? Color.FromArgb(30, 36, 34) : Color.White;
        }

        // Design Tokens（UI/性能改造 PR-4）：石墨灰底 + 低饱和青绿主色。
        public static Palette CreateDark()
        {
            Palette p = new Palette();
            p.Dark = true;
            p.Bg = Color.FromArgb(0x17, 0x19, 0x1B);
            p.Sidebar = Color.FromArgb(0x1F, 0x22, 0x25);
            p.TitleBar = Color.FromArgb(0x17, 0x19, 0x1B);
            p.Card = Color.FromArgb(0x25, 0x29, 0x2D);
            p.CardAlt = Color.FromArgb(0x20, 0x24, 0x27);
            p.Border = Color.FromArgb(0x35, 0x3A, 0x3F);
            p.BorderStrong = Color.FromArgb(0x45, 0x4B, 0x50);
            p.Text = Color.FromArgb(0xE8, 0xEA, 0xEC);
            p.Muted = Color.FromArgb(0x9D, 0xA5, 0xAA);
            p.Dim = Color.FromArgb(0x6E, 0x76, 0x7B);
            // 主色保持蓝色系（设计指南的“低饱和”方向），而非文档示例的青绿。
            p.Accent1 = Color.FromArgb(0x7F, 0xA3, 0xC8);
            p.Accent2 = Color.FromArgb(0x5E, 0x82, 0xA8);
            p.Accent3 = Color.FromArgb(0xA7, 0xC1, 0xDA);
            p.Good = Color.FromArgb(0x6F, 0xA9, 0x8C);
            p.Warn = Color.FromArgb(0xD2, 0xA6, 0x6D);
            p.Bad = Color.FromArgb(0xC5, 0x6A, 0x65);
            p.Info = Color.FromArgb(0x8A, 0xA6, 0xBC);
            p.Hover = Color.FromArgb(0x2B, 0x30, 0x34);
            p.Selected = Color.FromArgb(0x2E, 0x34, 0x38);
            p.HeatEmpty = Color.FromArgb(0x22, 0x26, 0x2A);
            p.Heat = new Color[]
            {
                Color.FromArgb(0x26, 0x2B, 0x31),
                Color.FromArgb(0x2E, 0x3A, 0x46),
                Color.FromArgb(0x3B, 0x4E, 0x62),
                Color.FromArgb(0x4D, 0x66, 0x85),
                Color.FromArgb(0x62, 0x80, 0xA4),
                Color.FromArgb(0x7F, 0xA3, 0xC8)
            };
            p.Grid = Color.FromArgb(0x2C, 0x31, 0x36);
            return p;
        }

        public static Palette CreateLight()
        {
            Palette p = new Palette();
            p.Dark = false;
            p.Bg = Color.FromArgb(0xF4, 0xF3, 0xEF);
            p.Sidebar = Color.FromArgb(0xFF, 0xFF, 0xFF);
            p.TitleBar = Color.FromArgb(0xFF, 0xFF, 0xFF);
            p.Card = Color.FromArgb(0xFF, 0xFF, 0xFF);
            p.CardAlt = Color.FromArgb(0xF8, 0xF7, 0xF3);
            p.Border = Color.FromArgb(0xE7, 0xE5, 0xDF);
            p.BorderStrong = Color.FromArgb(0xD5, 0xD2, 0xCA);
            p.Text = Color.FromArgb(0x28, 0x2D, 0x2C);
            p.Muted = Color.FromArgb(0x74, 0x7C, 0x78);
            p.Dim = Color.FromArgb(0xA3, 0xAA, 0xA6);
            p.Accent1 = Color.FromArgb(0x4A, 0x6F, 0x94);
            p.Accent2 = Color.FromArgb(0x3E, 0x5F, 0x80);
            p.Accent3 = Color.FromArgb(0x7E, 0x9C, 0xB8);
            p.Good = Color.FromArgb(0x4F, 0x8A, 0x6E);
            p.Warn = Color.FromArgb(0xC4, 0x8E, 0x4D);
            p.Bad = Color.FromArgb(0xB8, 0x5F, 0x5B);
            p.Info = Color.FromArgb(0x5A, 0x7C, 0x98);
            p.Hover = Color.FromArgb(0xF0, 0xEF, 0xEA);
            p.Selected = Color.FromArgb(0xE9, 0xED, 0xE8);
            p.HeatEmpty = Color.FromArgb(0xEC, 0xE9, 0xE2);
            p.Heat = new Color[]
            {
                Color.FromArgb(0xED, 0xEB, 0xE5),
                Color.FromArgb(0xDC, 0xE4, 0xEC),
                Color.FromArgb(0xC2, 0xD1, 0xDF),
                Color.FromArgb(0xA2, 0xB9, 0xCE),
                Color.FromArgb(0x7B, 0x99, 0xB5),
                Color.FromArgb(0x4A, 0x6F, 0x94)
            };
            p.Grid = Color.FromArgb(0xE7, 0xE5, 0xDF);
            return p;
        }
    }

    public static class Theme
    {
        public static ThemeMode Mode = ThemeMode.FollowSystem;
        public static bool? ForceDark;
        public static Palette P { get; private set; }
        public static bool IsDark { get; private set; }
        public static event Action Changed;

        static Theme()
        {
            IsDark = ComputeDark();
            P = IsDark ? Palette.CreateDark() : Palette.CreateLight();
        }

        public static void Refresh()
        {
            Refresh(true);
        }

        public static void Refresh(bool raiseEvent)
        {
            bool dark = ComputeDark();
            if (dark == IsDark && P != null) return;
            IsDark = dark;
            P = dark ? Palette.CreateDark() : Palette.CreateLight();
            if (raiseEvent && Changed != null) Changed();
        }

        public static void SetMode(ThemeMode mode)
        {
            Mode = mode;
            Refresh(true);
        }

        public static bool ComputeDark()
        {
            if (ForceDark.HasValue) return ForceDark.Value;
            if (Mode == ThemeMode.Dark) return true;
            if (Mode == ThemeMode.Light) return false;
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (key != null)
                    {
                        object v = key.GetValue("AppsUseLightTheme");
                        if (v is int) return (int)v == 0;
                    }
                }
            }
            catch
            {
            }
            return false;
        }
    }
}
