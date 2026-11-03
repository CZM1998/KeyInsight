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
            p.Bg = Color.FromArgb(0x0D, 0x16, 0x26);
            p.Sidebar = Color.FromArgb(0x11, 0x1F, 0x33);
            p.TitleBar = Color.FromArgb(0x0D, 0x16, 0x26);
            p.Card = Color.FromArgb(0x14, 0x24, 0x3A);
            p.CardAlt = Color.FromArgb(0x11, 0x1E, 0x31);
            p.Border = Color.FromArgb(0x30, 0x49, 0x67);
            p.BorderStrong = Color.FromArgb(0x3F, 0x5C, 0x80);
            p.Text = Color.FromArgb(0xEA, 0xF2, 0xFC);
            p.Muted = Color.FromArgb(0xA7, 0xB9, 0xD0);
            p.Dim = Color.FromArgb(0x74, 0x8A, 0xA5);
            p.Accent1 = Color.FromArgb(0x4C, 0x8D, 0xFF);
            p.Accent2 = Color.FromArgb(0x55, 0xC5, 0xD8);
            p.Accent3 = Color.FromArgb(0x77, 0xAC, 0xFF);
            p.Good = Color.FromArgb(0x54, 0xB9, 0x9B);
            p.Warn = Color.FromArgb(0xE4, 0xB0, 0x5D);
            p.Bad = Color.FromArgb(0xE4, 0x77, 0x7B);
            p.Info = Color.FromArgb(0x55, 0xC5, 0xD8);
            p.Hover = Color.FromArgb(0x1A, 0x2D, 0x48);
            p.Selected = Color.FromArgb(0x1E, 0x33, 0x52);
            p.HeatEmpty = Color.FromArgb(0x11, 0x1E, 0x31);
            p.Heat = new Color[]
            {
                Color.FromArgb(0x14, 0x24, 0x3A),
                Color.FromArgb(0x1B, 0x33, 0x55),
                Color.FromArgb(0x27, 0x47, 0x74),
                Color.FromArgb(0x35, 0x60, 0x96),
                Color.FromArgb(0x45, 0x7C, 0xC2),
                Color.FromArgb(0x4C, 0x8D, 0xFF)
            };
            p.Grid = Color.FromArgb(0x22, 0x36, 0x54);
            return p;
        }

        public static Palette CreateLight()
        {
            Palette p = new Palette();
            p.Dark = false;
            p.Bg = Color.FromArgb(0xF3, 0xF6, 0xFB);
            p.Sidebar = Color.FromArgb(0xFF, 0xFF, 0xFF);
            p.TitleBar = Color.FromArgb(0xFF, 0xFF, 0xFF);
            p.Card = Color.FromArgb(0xFF, 0xFF, 0xFF);
            p.CardAlt = Color.FromArgb(0xF8, 0xFA, 0xFD);
            p.Border = Color.FromArgb(0xD9, 0xE3, 0xEF);
            p.BorderStrong = Color.FromArgb(0xC2, 0xD2, 0xE6);
            p.Text = Color.FromArgb(0x1B, 0x2B, 0x40);
            p.Muted = Color.FromArgb(0x66, 0x7A, 0x93);
            p.Dim = Color.FromArgb(0x8C, 0x9D, 0xB5);
            p.Accent1 = Color.FromArgb(0x34, 0x78, 0xE5);
            p.Accent2 = Color.FromArgb(0x26, 0x8D, 0xAA);
            p.Accent3 = Color.FromArgb(0x5A, 0x95, 0xF0);
            p.Good = Color.FromArgb(0x26, 0x8A, 0x70);
            p.Warn = Color.FromArgb(0xB6, 0x7A, 0x20);
            p.Bad = Color.FromArgb(0xC6, 0x4F, 0x58);
            p.Info = Color.FromArgb(0x26, 0x8D, 0xAA);
            p.Hover = Color.FromArgb(0xEA, 0xF1, 0xFA);
            p.Selected = Color.FromArgb(0xDD, 0xEA, 0xFB);
            p.HeatEmpty = Color.FromArgb(0xEC, 0xF1, 0xF8);
            p.Heat = new Color[]
            {
                Color.FromArgb(0xEC, 0xF1, 0xF8),
                Color.FromArgb(0xC8, 0xDA, 0xF3),
                Color.FromArgb(0x9C, 0xBB, 0xE9),
                Color.FromArgb(0x6F, 0x9F, 0xDE),
                Color.FromArgb(0x4C, 0x8D, 0xFF),
                Color.FromArgb(0x2E, 0x66, 0xC4)
            };
            p.Grid = Color.FromArgb(0xD9, 0xE3, 0xEF);
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
