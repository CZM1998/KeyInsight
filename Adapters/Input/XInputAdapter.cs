using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using KeyInsight.Core.Devices;

namespace KeyInsight.Adapters.Input
{
    /// <summary>
    /// 手柄 Adapter（方案 §9.4，PR-11 最小版）：XInput 槽位探测（1-4）。
    /// - 只读：连接状态、电池类型/电量；不采集按键/摇杆（测试功能后续单独 PR）；
    /// - XInput 不提供唯一硬件 ID：身份键用槽位（文档记录该局限，换槽位会成新档案）；
    /// - 电池不可读时记录为未知。
    /// </summary>
    public static class XInputAdapter
    {
        public sealed class PadInfo
        {
            public int Slot;
            public bool Connected;
            public int BatteryType;     // 0=断开 1=有线 2=碱性 3=镍氢 4=锂
            public int BatteryLevel;    // 0=空 1=低 2=中 3=满
            public ushort Buttons;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XInputState
        {
            public uint packetNumber;
            public ushort wButtons;
            public byte leftTrigger;
            public byte rightTrigger;
            public short sLx;
            public short sLy;
            public short sRx;
            public short sRy;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XInputBattery
        {
            public byte type;
            public byte level;
        }

        [DllImport("xinput1_4.dll")]
        private static extern int XInputGetState(int userIndex, out XInputState state);

        [DllImport("xinput1_4.dll")]
        private static extern int XInputGetBatteryInformation(int userIndex, int deviceType,
            out XInputBattery battery);

        private static bool _unavailable;

        public static List<DeviceSnapshot> Enumerate()
        {
            List<DeviceSnapshot> result = new List<DeviceSnapshot>();
            if (_unavailable) return result;
            try
            {
                for (int slot = 0; slot < 4; slot++)
                {
                    XInputState state;
                    int r = XInputGetState(slot, out state);
                    bool connected = r == 0;
                    if (!connected && r != 1167 /*ERROR_DEVICE_NOT_CONNECTED*/)
                    {
                        // DLL 缺失/不完整：标记不可用，避免每轮重复报错
                        if (r != 0) { /* 保持逐槽探测，xinput 缺失时首调即失败 */ }
                    }
                    if (!connected && r != 1167)
                    {
                        _unavailable = true;
                        return result;
                    }

                    DeviceSnapshot s = new DeviceSnapshot();
                    s.Category = DeviceCategory.Input;
                    s.Source = "XInput";
                    s.IsPresent = connected;
                    s.InstanceKey = "input:xinput:slot" + slot;
                    s.InstanceId = "XINPUT\\SLOT" + slot;
                    s.DeviceClass = "GAMEPAD";
                    s.Name = connected ? "游戏手柄（槽位 " + (slot + 1) + "）" : "游戏手柄（槽位 " + (slot + 1) + "）";
                    s.Model = "XInput 手柄";
                    s.ProblemCode = 0;

                    int batteryType = 0, batteryLevel = 0;
                    if (connected)
                    {
                        XInputBattery b;
                        if (XInputGetBatteryInformation(slot, 0, out b) == 0)
                        {
                            batteryType = b.type;
                            batteryLevel = b.level;
                        }
                    }

                    StringBuilder sb = new StringBuilder(160);
                    sb.Append("{");
                    sb.Append("\"xinputSlot\":").Append(slot).Append(",");
                    sb.Append("\"batteryType\":").Append(batteryType).Append(",");
                    sb.Append("\"batteryLevel\":").Append(batteryLevel).Append(",");
                    sb.Append("\"connected\":").Append(connected ? 1 : 0).Append(",");
                    sb.Append("\"identityNote\":\"XInput 不提供唯一硬件 ID，按槽位建档\"");
                    sb.Append("}");
                    s.AttributesJson = sb.ToString();
                    result.Add(s);
                }
            }
            catch
            {
                _unavailable = true;
            }
            return result;
        }

        public static string BatteryText(int type, int level)
        {
            switch (type)
            {
                case 1: return "有线供电";
                case 2: return "碱性电池 · " + LevelText(level);
                case 3: return "镍氢电池 · " + LevelText(level);
                case 4: return "锂电池 · " + LevelText(level);
                default: return "未知";
            }
        }

        private static string LevelText(int level)
        {
            switch (level)
            {
                case 3: return "满";
                case 2: return "中";
                case 1: return "低";
                default: return "空";
            }
        }
    }
}
