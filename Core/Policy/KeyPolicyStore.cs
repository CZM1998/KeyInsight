using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;
using KeyInsight.Core.Input;

namespace KeyInsight.Core.Policy
{
    /// <summary>单键策略条目（P1-01）。序列化身份字符串形如 "vk=45;scan=18;ext=0"。</summary>
    public sealed class KeyPolicyEntry
    {
        public string Identity;
        public int VirtualKey;
        public int ScanCode;
        public bool Extended;
        public int ThresholdMs = 20;
        public bool Enabled;
        public long EnabledAtUtcTicks;
        public string Note;

        public PhysicalKeyId Key
        {
            get { return new PhysicalKeyId(VirtualKey, ScanCode, Extended); }
        }
    }

    /// <summary>
    /// 单键策略持久化（P1-01）：profiles/key-policies.v1.json，原子写入（tmp+replace），
    /// schema 版本校验；损坏时回退空表并保留 .corrupt 副本，绝不静默清空用户配置。
    /// 只在 UI/控制线程访问；钩子线程通过 PolicySnapshot 读到的是不可变拷贝。
    /// </summary>
    public sealed class KeyPolicyStore
    {
        private const int SchemaVersion = 1;
        private readonly string _path;
        private List<KeyPolicyEntry> _entries = new List<KeyPolicyEntry>();

        public KeyPolicyStore(string path)
        {
            _path = path;
            Load();
        }

        public string FilePath { get { return _path; } }

        private string UndoPath { get { return _path + ".undo"; } }

        /// <summary>记录一次应用前快照（DIA-008 回退）。每次编辑前调用，只保留最近一次。</summary>
        public void BeginEdit()
        {
            try
            {
                string dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                WriteEntriesTo(UndoPath);
            }
            catch
            {
            }
        }

        public bool HasUndo
        {
            get { return File.Exists(UndoPath); }
        }

        /// <summary>回退到上一次 BeginEdit 前的状态并保存；无快照返回 false。</summary>
        public bool Revert()
        {
            try
            {
                if (!File.Exists(UndoPath)) return false;
                List<KeyPolicyEntry> restored = ParseEntries(File.ReadAllText(UndoPath));
                if (restored == null) return false;
                _entries = restored;
                Save();
                try { File.Delete(UndoPath); } catch { }
                return true;
            }
            catch
            {
                return false;
            }
        }

        public List<KeyPolicyEntry> Entries
        {
            get { return new List<KeyPolicyEntry>(_entries); }
        }

        public KeyPolicyEntry Find(int virtualKey, int scanCode, bool extended)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                KeyPolicyEntry e = _entries[i];
                if (e.VirtualKey == virtualKey && e.ScanCode == scanCode && e.Extended == extended) return e;
            }
            return null;
        }

        public KeyPolicyEntry FindByVirtualKey(int virtualKey)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].VirtualKey == virtualKey) return _entries[i];
            }
            return null;
        }

        public KeyPolicyEntry GetOrCreate(int virtualKey, int scanCode, bool extended)
        {
            KeyPolicyEntry e = Find(virtualKey, scanCode, extended);
            if (e != null) return e;
            e = new KeyPolicyEntry();
            e.VirtualKey = virtualKey;
            e.ScanCode = scanCode;
            e.Extended = extended;
            e.Identity = new Core.Input.PhysicalKeyId(virtualKey, scanCode, extended).ToString();
            e.ThresholdMs = 20;
            _entries.Add(e);
            return e;
        }

        public bool Remove(int virtualKey, int scanCode, bool extended)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                KeyPolicyEntry e = _entries[i];
                if (e.VirtualKey == virtualKey && e.ScanCode == scanCode && e.Extended == extended)
                {
                    _entries.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        public void Save()
        {
            try
            {
                WriteEntriesTo(_path);
            }
            catch
            {
                throw;
            }
        }

        private void WriteEntriesTo(string path)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            Dictionary<string, object> root = new Dictionary<string, object>();
            root["schema"] = SchemaVersion;
            root["savedUtcTicks"] = DateTime.UtcNow.Ticks;
            List<object> list = new List<object>();
            for (int i = 0; i < _entries.Count; i++)
            {
                KeyPolicyEntry e = _entries[i];
                Dictionary<string, object> o = new Dictionary<string, object>();
                o["identity"] = e.Identity;
                o["vk"] = e.VirtualKey;
                o["scan"] = e.ScanCode;
                o["ext"] = e.Extended ? 1 : 0;
                o["thresholdMs"] = e.ThresholdMs;
                o["enabled"] = e.Enabled;
                o["enabledAtUtcTicks"] = e.EnabledAtUtcTicks;
                if (!string.IsNullOrEmpty(e.Note)) o["note"] = e.Note;
                list.Add(o);
            }
            root["keys"] = list;

            string json = new JavaScriptSerializer().Serialize(root);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, json, new System.Text.UTF8Encoding(false));

            if (File.Exists(path))
            {
                try
                {
                    string backup = path + ".bak";
                    File.Replace(tmp, path, backup);
                    try { if (File.Exists(backup)) File.Delete(backup); } catch { }
                }
                catch
                {
                    try { File.Copy(tmp, path, true); File.Delete(tmp); }
                    catch { throw; }
                }
            }
            else
            {
                File.Move(tmp, path);
            }
        }

        /// <summary>解析策略 JSON（IDictionary/IEnumerable：JavaScriptSerializer 嵌套数组是 ArrayList）。
        /// 返回 null 表示结构不可识别；schema 高于当前版本时返回空表并保留原文件。</summary>
        private static List<KeyPolicyEntry> ParseEntries(string json)
        {
            JavaScriptSerializer ser = new JavaScriptSerializer();
            System.Collections.IDictionary root =
                ser.DeserializeObject(json) as System.Collections.IDictionary;
            if (root == null) return null;

            int schema = root.Contains("schema") ? Convert.ToInt32(root["schema"]) : 0;
            if (schema > SchemaVersion) return new List<KeyPolicyEntry>();

            List<KeyPolicyEntry> parsed = new List<KeyPolicyEntry>();
            if (root.Contains("keys"))
            {
                System.Collections.IEnumerable items = root["keys"] as System.Collections.IEnumerable;
                if (items != null)
                {
                    foreach (object item in items)
                    {
                        System.Collections.IDictionary o = item as System.Collections.IDictionary;
                        if (o == null) continue;
                        KeyPolicyEntry e = new KeyPolicyEntry();
                        if (o.Contains("identity")) e.Identity = Convert.ToString(o["identity"]);
                        if (o.Contains("vk")) e.VirtualKey = Convert.ToInt32(o["vk"]);
                        if (o.Contains("scan")) e.ScanCode = Convert.ToInt32(o["scan"]);
                        if (o.Contains("ext")) e.Extended = Convert.ToInt32(o["ext"]) != 0;
                        if (o.Contains("thresholdMs")) e.ThresholdMs = Convert.ToInt32(o["thresholdMs"]);
                        if (o.Contains("enabled")) e.Enabled = Convert.ToBoolean(o["enabled"]);
                        if (o.Contains("enabledAtUtcTicks")) e.EnabledAtUtcTicks = Convert.ToInt64(o["enabledAtUtcTicks"]);
                        if (o.Contains("note")) e.Note = Convert.ToString(o["note"]);
                        if (e.ThresholdMs < 1) e.ThresholdMs = 1;
                        if (e.ThresholdMs > 100) e.ThresholdMs = 100;
                        if (string.IsNullOrEmpty(e.Identity))
                        {
                            e.Identity = new Core.Input.PhysicalKeyId(e.VirtualKey, e.ScanCode, e.Extended).ToString();
                        }
                        parsed.Add(e);
                    }
                }
            }
            return parsed;
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(_path)) return;
                List<KeyPolicyEntry> parsed = ParseEntries(File.ReadAllText(_path));
                if (parsed == null)
                {
                    _entries = new List<KeyPolicyEntry>();
                    return;
                }
                _entries = parsed;
            }
            catch
            {
                // 损坏：保留损坏副本，回退空表（不清空原文件）
                try
                {
                    if (File.Exists(_path))
                    {
                        string corrupt = _path + ".corrupt";
                        File.Copy(_path, corrupt, true);
                    }
                }
                catch { }
                _entries = new List<KeyPolicyEntry>();
            }
        }
    }
}
