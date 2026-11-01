using System;
using System.Collections.Generic;

namespace KeyInsight.UI
{
    public sealed class KeyDef
    {
        public string Label;
        public float X;
        public float Y;
        public float W;
        public float H;
        public int[] Vks;
        public bool SmallLabel;

        public KeyDef(string label, float x, float y, float w, float h, bool small, int[] vks)
        {
            Label = label;
            X = x;
            Y = y;
            W = w;
            H = h;
            SmallLabel = small;
            Vks = vks;
        }
    }

    public sealed class KeyLayoutDef
    {
        public string Name;
        public string ShortName;
        public float WidthUnits;
        public List<KeyDef> Keys;

        public KeyLayoutDef(string name, string shortName, float widthUnits, List<KeyDef> keys)
        {
            Name = name;
            ShortName = shortName;
            WidthUnits = widthUnits;
            Keys = keys;
        }

        public int KeyCount
        {
            get { return Keys.Count; }
        }
    }

    public static class KeyLayout
    {
        public const float HeightUnits = 6f;

        private static readonly Dictionary<int, string> _labels = new Dictionary<int, string>();

        public static KeyLayoutDef Layout87 { get; private set; }
        public static KeyLayoutDef Layout108 { get; private set; }

        static KeyLayout()
        {
            List<KeyDef> list87 = BuildBase();
            Layout87 = new KeyLayoutDef("87 键 TKL", "87 键", 18.75f, list87);

            List<KeyDef> list108 = new List<KeyDef>(list87);
            AddNumpad(list108);
            Layout108 = new KeyLayoutDef("108 键全尺寸", "108 键", 23.25f, list108);

            SetLabel(0x60, "Num 0"); SetLabel(0x61, "Num 1"); SetLabel(0x62, "Num 2");
            SetLabel(0x63, "Num 3"); SetLabel(0x64, "Num 4"); SetLabel(0x65, "Num 5");
            SetLabel(0x66, "Num 6"); SetLabel(0x67, "Num 7"); SetLabel(0x68, "Num 8");
            SetLabel(0x69, "Num 9"); SetLabel(0x6F, "Num /"); SetLabel(0x6A, "Num *");
            SetLabel(0x6D, "Num -"); SetLabel(0x6B, "Num +"); SetLabel(0x6E, "Num .");
            SetLabel(0x90, "NumLock");
            SetLabel(0x1000D, "Num Enter");
        }

        private static List<KeyDef> BuildBase()
        {
            List<KeyDef> list = new List<KeyDef>();
            Add(list, "Esc", 0f, 0f, 1f, 0x1B);

            for (int i = 0; i < 12; i++)
            {
                Add(list, "F" + (i + 1), 1.25f + i, 0f, 1f, 0x70 + i);
            }

            Add(list, "PrtSc", 15.75f, 0f, 1f, 0x2C);
            Add(list, "ScrLk", 16.75f, 0f, 1f, 0x91);
            Add(list, "Pause", 17.75f, 0f, 1f, 0x13);

            Add(list, "`", 0f, 1f, 1f, 0xC0);
            string[] digits = new string[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0" };
            int[] digitVks = new int[] { 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x30 };
            for (int i = 0; i < digits.Length; i++) Add(list, digits[i], 1f + i, 1f, 1f, digitVks[i]);
            Add(list, "-", 11f, 1f, 1f, 0xBD);
            Add(list, "=", 12f, 1f, 1f, 0xBB);
            Add(list, "Back", 13f, 1f, 2f, 0x08);

            Add(list, "Ins", 15.75f, 1f, 1f, 0x2D);
            Add(list, "Home", 16.75f, 1f, 1f, 0x24);
            Add(list, "PgUp", 17.75f, 1f, 1f, 0x21);

            Add(list, "Tab", 0f, 2f, 1.5f, 0x09);
            string[] qrow = new string[] { "Q", "W", "E", "R", "T", "Y", "U", "I", "O", "P" };
            int[] qvks = new int[] { 0x51, 0x57, 0x45, 0x52, 0x54, 0x59, 0x55, 0x49, 0x4F, 0x50 };
            for (int i = 0; i < qrow.Length; i++) Add(list, qrow[i], 1.5f + i, 2f, 1f, qvks[i]);
            Add(list, "[", 11.5f, 2f, 1f, 0xDB);
            Add(list, "]", 12.5f, 2f, 1f, 0xDD);
            Add(list, "\\", 13.5f, 2f, 1.5f, 0xDC);

            Add(list, "Del", 15.75f, 2f, 1f, 0x2E);
            Add(list, "End", 16.75f, 2f, 1f, 0x23);
            Add(list, "PgDn", 17.75f, 2f, 1f, 0x22);

            Add(list, "Caps", 0f, 3f, 1.75f, 0x14);
            string[] arow = new string[] { "A", "S", "D", "F", "G", "H", "J", "K", "L", ";", "'" };
            int[] avks = new int[] { 0x41, 0x53, 0x44, 0x46, 0x47, 0x48, 0x4A, 0x4B, 0x4C, 0xBA, 0xDE };
            for (int i = 0; i < arow.Length; i++) Add(list, arow[i], 1.75f + i, 3f, 1f, avks[i]);
            Add(list, "Enter", 12.75f, 3f, 2.25f, 0x0D);

            Add(list, "Shift", 0f, 4f, 2.25f, 0xA0);
            string[] zrow = new string[] { "Z", "X", "C", "V", "B", "N", "M", ",", ".", "/" };
            int[] zvks = new int[] { 0x5A, 0x58, 0x43, 0x56, 0x42, 0x4E, 0x4D, 0xBC, 0xBE, 0xBF };
            for (int i = 0; i < zrow.Length; i++) Add(list, zrow[i], 2.25f + i, 4f, 1f, zvks[i]);
            Add(list, "Shift", 12.25f, 4f, 2.75f, 0xA1);

            Add(list, "↑", 16.75f, 4f, 1f, 0x26);

            Add(list, "Ctrl", 0f, 5f, 1.25f, 0xA2);
            Add(list, "Win", 1.25f, 5f, 1.25f, 0x5B);
            Add(list, "Alt", 2.5f, 5f, 1.25f, 0xA4);
            Add(list, "Space", 3.75f, 5f, 6.25f, 0x20);
            Add(list, "Alt", 10f, 5f, 1.25f, 0xA5);
            Add(list, "Win", 11.25f, 5f, 1.25f, 0x5C);
            Add(list, "Menu", 12.5f, 5f, 1.25f, 0x5D);
            Add(list, "Ctrl", 13.75f, 5f, 1.25f, 0xA3);

            Add(list, "←", 15.75f, 5f, 1f, 0x25);
            Add(list, "↓", 16.75f, 5f, 1f, 0x28);
            Add(list, "→", 17.75f, 5f, 1f, 0x27);

            return list;
        }

        private static void AddNumpad(List<KeyDef> list)
        {
            // 4 extra keys above the numpad (108-key layout)
            Add(list, "Mute", 19.25f, 0f, 1f, 0xAD);
            Add(list, "Vol-", 20.25f, 0f, 1f, 0xAE);
            Add(list, "Vol+", 21.25f, 0f, 1f, 0xAF);
            Add(list, "Calc", 22.25f, 0f, 1f, 0xB7);

            Add(list, "NumLk", 19.25f, 1f, 1f, 0x90);
            Add(list, "/", 20.25f, 1f, 1f, 0x6F);
            Add(list, "*", 21.25f, 1f, 1f, 0x6A);
            Add(list, "-", 22.25f, 1f, 1f, 0x6D);

            Add(list, "7", 19.25f, 2f, 1f, 0x67);
            Add(list, "8", 20.25f, 2f, 1f, 0x68);
            Add(list, "9", 21.25f, 2f, 1f, 0x69);
            AddTall(list, "+", 22.25f, 2f, 1f, 2f, 0x6B);

            Add(list, "4", 19.25f, 3f, 1f, 0x64);
            Add(list, "5", 20.25f, 3f, 1f, 0x65);
            Add(list, "6", 21.25f, 3f, 1f, 0x66);

            Add(list, "1", 19.25f, 4f, 1f, 0x61);
            Add(list, "2", 20.25f, 4f, 1f, 0x62);
            Add(list, "3", 21.25f, 4f, 1f, 0x63);
            AddTall(list, "Enter", 22.25f, 4f, 1f, 2f, 0x1000D);

            Add(list, "0", 19.25f, 5f, 2f, 0x60);
            Add(list, ".", 21.25f, 5f, 1f, 0x6E);
        }

        private static void Add(List<KeyDef> list, string label, float x, float y, float w, params int[] vks)
        {
            AddTall(list, label, x, y, w, 1f, vks);
        }

        private static void AddTall(List<KeyDef> list, string label, float x, float y, float w, float h, params int[] vks)
        {
            bool small = label.Length > 2;
            KeyDef d = new KeyDef(label, x, y, w, h, small, vks);
            list.Add(d);
            for (int i = 0; i < vks.Length; i++) _labels[vks[i]] = label;
        }

        private static void SetLabel(int vk, string label)
        {
            _labels[vk] = label;
        }

        public static string LabelFor(int vk)
        {
            string label;
            if (_labels.TryGetValue(vk, out label)) return label;
            return "VK 0x" + vk.ToString("X2");
        }

        public static long CountFor(KeyDef key, Dictionary<int, KeyInsight.Stats.KeyAgg> counts)
        {
            if (counts == null) return 0;
            long n = 0;
            for (int i = 0; i < key.Vks.Length; i++)
            {
                KeyInsight.Stats.KeyAgg agg;
                if (counts.TryGetValue(key.Vks[i], out agg)) n += agg.Count;
            }
            return n;
        }

        public static long SwallowedFor(KeyDef key, Dictionary<int, KeyInsight.Stats.KeyAgg> counts)
        {
            if (counts == null) return 0;
            long n = 0;
            for (int i = 0; i < key.Vks.Length; i++)
            {
                KeyInsight.Stats.KeyAgg agg;
                if (counts.TryGetValue(key.Vks[i], out agg)) n += agg.Swallowed;
            }
            return n;
        }
    }
}
