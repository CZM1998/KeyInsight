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
            return lum > 0.62 ? Color.FromArgb(24, 33, 53) : Color.White;
        }

        public static Palette CreateDark()
        {
            Palette p = new Palette();
            p.Dark = true;
            p.Bg = Color.FromArgb(10, 14, 24);
            p.Sidebar = Color.FromArgb(13, 19, 34);
            p.TitleBar = Color.FromArgb(10, 14, 24);
            p.Card = Color.FromArgb(18, 26, 43);
            p.CardAlt = Color.FromArgb(15, 22, 38);
            p.Border = Color.FromArgb(30, 42, 64);
            p.BorderStrong = Color.FromArgb(44, 61, 88);
            p.Text = Color.FromArgb(232, 238, 249);
            p.Muted = Color.FromArgb(138, 155, 184);
            p.Dim = Color.FromArgb(91, 107, 133);
            p.Accent1 = Color.FromArgb(56, 189, 248);
            p.Accent2 = Color.FromArgb(99, 102, 241);
            p.Accent3 = Color.FromArgb(168, 85, 247);
            p.Good = Color.FromArgb(52, 211, 153);
            p.Warn = Color.FromArgb(251, 191, 36);
            p.Bad = Color.FromArgb(251, 113, 133);
            p.Info = Color.FromArgb(96, 165, 250);
            p.Hover = Color.FromArgb(26, 36, 57);
            p.Selected = Color.FromArgb(28, 39, 64);
            p.HeatEmpty = Color.FromArgb(20, 29, 47);
            p.Heat = new Color[]
            {
                Color.FromArgb(24, 34, 55),
                Color.FromArgb(30, 58, 138),
                Color.FromArgb(37, 99, 235),
                Color.FromArgb(124, 58, 237),
                Color.FromArgb(192, 38, 211)
            };
            p.Grid = Color.FromArgb(30, 42, 64);
            return p;
        }

        public static Palette CreateLight()
        {
            Palette p = new Palette();
            p.Dark = false;
            p.Bg = Color.FromArgb(243, 246, 252);
            p.Sidebar = Color.FromArgb(255, 255, 255);
            p.TitleBar = Color.FromArgb(255, 255, 255);
            p.Card = Color.FromArgb(255, 255, 255);
            p.CardAlt = Color.FromArgb(247, 249, 253);
            p.Border = Color.FromArgb(225, 231, 240);
            p.BorderStrong = Color.FromArgb(203, 213, 229);
            p.Text = Color.FromArgb(20, 33, 61);
            p.Muted = Color.FromArgb(91, 107, 133);
            p.Dim = Color.FromArgb(135, 148, 168);
            p.Accent1 = Color.FromArgb(37, 99, 235);
            p.Accent2 = Color.FromArgb(99, 102, 241);
            p.Accent3 = Color.FromArgb(147, 51, 234);
            p.Good = Color.FromArgb(5, 150, 105);
            p.Warn = Color.FromArgb(217, 119, 6);
            p.Bad = Color.FromArgb(220, 38, 38);
            p.Info = Color.FromArgb(37, 99, 235);
            p.Hover = Color.FromArgb(239, 244, 252);
            p.Selected = Color.FromArgb(232, 240, 254);
            p.HeatEmpty = Color.FromArgb(238, 242, 249);
            p.Heat = new Color[]
            {
                Color.FromArgb(238, 242, 249),
                Color.FromArgb(191, 219, 254),
                Color.FromArgb(96, 165, 250),
                Color.FromArgb(59, 130, 246),
                Color.FromArgb(109, 40, 217)
            };
            p.Grid = Color.FromArgb(225, 231, 240);
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
