using System;

namespace KeyInsight.UI
{
    public static class MouseNames
    {
        public static string Full(int button)
        {
            switch (button)
            {
                case 1: return "左键";
                case 2: return "右键";
                case 3: return "中键";
                case 4: return "侧键 4（后退）";
                case 5: return "侧键 5（前进）";
                case 6: return "滚轮上";
                case 7: return "滚轮下";
                case 8: return "滚轮左";
                case 9: return "滚轮右";
                default: return "按键 " + button;
            }
        }

        public static string Short(int button)
        {
            switch (button)
            {
                case 1: return "左键";
                case 2: return "右键";
                case 3: return "中键";
                case 4: return "侧键 4";
                case 5: return "侧键 5";
                case 6: return "滚上";
                case 7: return "滚下";
                case 8: return "滚左";
                case 9: return "滚右";
                default: return "按键 " + button;
            }
        }

        public static bool IsSide(int button)
        {
            return button == 4 || button == 5;
        }
    }
}
