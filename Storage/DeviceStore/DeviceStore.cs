using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;
using KeyInsight.Core.Devices;

namespace KeyInsight.Storage.DeviceStore
{
    /// <summary>
    /// 设备档案仓库（方案 §10.1/§10.2，PR-3）。
    /// storage/devicecenter/devices.v1.json，原子写入 + SchemaVersion + 损坏保留 .corrupt 副本。
    /// 独立数据域：不迁移、不重写现有 KDS 文件。线程约定：仅在服务层锁内读写。
    /// </summary>
    public sealed class DeviceStore
    {
        private const int SchemaVersion = 1;
        private readonly string _path;
        private readonly Dictionary<string, DeviceRecord> _records =
            new Dictionary<string, DeviceRecord>(StringComparer.OrdinalIgnoreCase);

        public DeviceStore(string path)
        {
            _path = path;
            Load();
        }

        public string FilePath { get { return _path; } }

        public int Count
        {
            get
            {
                lock (_records) return _records.Count;
            }
        }

        public List<DeviceRecord> All()
        {
            lock (_records) return new List<DeviceRecord>(_records.Values);
        }

        public DeviceRecord Get(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId)) return null;
            lock (_records)
            {
                DeviceRecord r;
                return _records.TryGetValue(deviceId, out r) ? r : null;
            }
        }

        public DeviceRecord FindByIdentityKey(string identityKey)
        {
            if (string.IsNullOrEmpty(identityKey)) return null;
            lock (_records)
            {
                foreach (DeviceRecord r in _records.Values)
                {
                    if (string.Equals(r.IdentityKey, identityKey, StringComparison.OrdinalIgnoreCase))
                    {
                        return r;
                    }
                }
            }
            return null;
        }

        public DeviceRecord FindByInstanceKey(string instanceKey)
        {
            if (string.IsNullOrEmpty(instanceKey)) return null;
            lock (_records)
            {
                foreach (DeviceRecord r in _records.Values)
                {
                    if (r.InstanceKeys.Contains(instanceKey)) return r;
                }
            }
            return null;
        }

        /// <summary>插入或更新档案并落盘。</summary>
        public void Upsert(DeviceRecord record)
        {
            if (record == null || string.IsNullOrEmpty(record.DeviceId)) return;
            lock (_records)
            {
                _records[record.DeviceId] = record;
                Save();
            }
        }

        /// <summary>用户清理：归档（不物理删除，方案 §6.2 Archived）。</summary>
        public bool Archive(string deviceId)
        {
            DeviceRecord r = Get(deviceId);
            if (r == null) return false;
            r.Connection = ConnectionState.Disconnected;
            Save();
            return true;
        }

        // —— 序列化（KeyPolicyStore 同款模式：Dictionary 平铺 + JavaScriptSerializer） ——

        private void Load()
        {
            try
            {
                if (!File.Exists(_path)) return;
                string json = File.ReadAllText(_path);
                JavaScriptSerializer ser = new JavaScriptSerializer();
                System.Collections.IDictionary root = ser.DeserializeObject(json)
                    as System.Collections.IDictionary;
                if (root == null) throw new InvalidDataException("not a device store");
                int schema = root.Contains("schema") ? Convert.ToInt32(root["schema"]) : 0;
                if (schema > SchemaVersion)
                {
                    // 未来版本：保留原文件不动，按空库运行（方案 §10.1 升级可回滚）
                    return;
                }
                System.Collections.IEnumerable items = root.Contains("devices")
                    ? root["devices"] as System.Collections.IEnumerable : null;
                if (items == null) return;
                foreach (object item in items)
                {
                    DeviceRecord r = ParseRecord(item as System.Collections.IDictionary);
                    if (r != null && !string.IsNullOrEmpty(r.DeviceId))
                    {
                        _records[r.DeviceId] = r;
                    }
                }
            }
            catch (Exception)
            {
                TryKeepCorruptCopy();
            }
        }

        private static DeviceRecord ParseRecord(System.Collections.IDictionary o)
        {
            if (o == null) return null;
            DeviceRecord r = new DeviceRecord();
            r.DeviceId = Str(o, "deviceId");
            r.IdentityKey = Str(o, "identityKey");
            r.Category = (DeviceCategory)Int(o, "category", 0);
            r.Name = Str(o, "name");
            r.UserAlias = Str(o, "userAlias");
            r.UserNote = Str(o, "userNote");
            r.Manufacturer = Str(o, "manufacturer");
            r.Model = Str(o, "model");
            r.SerialNumber = Str(o, "serialNumber");
            r.FirstSeenUtc = new DateTime(Lng(o, "firstSeenUtcTicks", DateTime.UtcNow.Ticks), DateTimeKind.Utc);
            r.LastSeenUtc = new DateTime(Lng(o, "lastSeenUtcTicks", DateTime.UtcNow.Ticks), DateTimeKind.Utc);
            r.Connection = (ConnectionState)Int(o, "connection", 0);
            r.Operational = (OperationalState)Int(o, "operational", 0);
            r.Health = (HealthState)Int(o, "health", 0);
            r.Diagnostic = (DiagnosticRunState)Int(o, "diagnostic", 0);
            r.LastIdentityConfidence = Dbl(o, "identityConfidence", 0);
            r.IdentityConfirmedByUser = Bool(o, "identityConfirmed", false);
            r.AttributesJson = Str(o, "attributesJson");
            r.AttributesSource = Str(o, "attributesSource");
            r.AttributesUpdatedUtc = new DateTime(
                Lng(o, "attributesUpdatedUtcTicks", 0), DateTimeKind.Utc);
            if (o.Contains("instanceKeys"))
            {
                System.Collections.IEnumerable keys = o["instanceKeys"] as System.Collections.IEnumerable;
                if (keys != null)
                {
                    foreach (object k in keys)
                    {
                        string s = Convert.ToString(k);
                        if (!string.IsNullOrEmpty(s)) r.InstanceKeys.Add(s);
                    }
                }
            }
            return r;
        }

        public void Save()
        {
            lock (_records)
            {
                try
                {
                    string dir = Path.GetDirectoryName(_path);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                    Dictionary<string, object> root = new Dictionary<string, object>();
                    root["schema"] = SchemaVersion;
                    root["savedUtcTicks"] = DateTime.UtcNow.Ticks;
                    List<object> list = new List<object>();
                    foreach (DeviceRecord r in _records.Values)
                    {
                        Dictionary<string, object> o = new Dictionary<string, object>();
                        o["deviceId"] = r.DeviceId;
                        o["identityKey"] = r.IdentityKey ?? "";
                        o["category"] = (int)r.Category;
                        o["name"] = r.Name ?? "";
                        if (!string.IsNullOrEmpty(r.UserAlias)) o["userAlias"] = r.UserAlias;
                        if (!string.IsNullOrEmpty(r.UserNote)) o["userNote"] = r.UserNote;
                        o["manufacturer"] = r.Manufacturer ?? "";
                        o["model"] = r.Model ?? "";
                        o["serialNumber"] = r.SerialNumber ?? "";
                        o["firstSeenUtcTicks"] = r.FirstSeenUtc.Ticks;
                        o["lastSeenUtcTicks"] = r.LastSeenUtc.Ticks;
                        o["connection"] = (int)r.Connection;
                        o["operational"] = (int)r.Operational;
                        o["health"] = (int)r.Health;
                        o["diagnostic"] = (int)r.Diagnostic;
                        o["identityConfidence"] = r.LastIdentityConfidence;
                        o["identityConfirmed"] = r.IdentityConfirmedByUser;
                        o["attributesJson"] = r.AttributesJson ?? "";
                        o["attributesSource"] = r.AttributesSource ?? "";
                        o["attributesUpdatedUtcTicks"] = r.AttributesUpdatedUtc.Ticks;
                        List<object> keys = new List<object>();
                        for (int i = 0; i < r.InstanceKeys.Count; i++) keys.Add(r.InstanceKeys[i]);
                        o["instanceKeys"] = keys;
                        list.Add(o);
                    }
                    root["devices"] = list;

                    string json = new JavaScriptSerializer().Serialize(root);
                    string tmp = _path + ".tmp";
                    File.WriteAllText(tmp, json, new System.Text.UTF8Encoding(false));
                    if (File.Exists(_path))
                    {
                        string backup = _path + ".bak";
                        try
                        {
                            File.Replace(tmp, _path, backup);
                            try { if (File.Exists(backup)) File.Delete(backup); } catch { }
                        }
                        catch
                        {
                            File.Copy(tmp, _path, true);
                            File.Delete(tmp);
                        }
                    }
                    else
                    {
                        File.Move(tmp, _path);
                    }
                }
                catch
                {
                    // 存储失败不崩溃 UI；下次变更重试（方案 §12.1 批量写入）
                }
            }
        }

        private void TryKeepCorruptCopy()
        {
            try
            {
                if (File.Exists(_path))
                {
                    string corrupt = _path + ".corrupt";
                    if (File.Exists(corrupt)) File.Delete(corrupt);
                    File.Copy(_path, corrupt);
                }
            }
            catch
            {
            }
        }

        private static string Str(System.Collections.IDictionary o, string key)
        {
            return o.Contains(key) ? Convert.ToString(o[key]) : null;
        }

        private static int Int(System.Collections.IDictionary o, string key, int def)
        {
            return o.Contains(key) ? Convert.ToInt32(o[key]) : def;
        }

        private static long Lng(System.Collections.IDictionary o, string key, long def)
        {
            return o.Contains(key) ? Convert.ToInt64(o[key]) : def;
        }

        private static double Dbl(System.Collections.IDictionary o, string key, double def)
        {
            return o.Contains(key) ? Convert.ToDouble(o[key]) : def;
        }

        private static bool Bool(System.Collections.IDictionary o, string key, bool def)
        {
            return o.Contains(key) ? Convert.ToBoolean(o[key]) : def;
        }
    }
}
