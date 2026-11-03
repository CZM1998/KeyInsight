using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using KeyInsight.Core.Devices;

namespace KeyInsight.Storage.DeviceStore
{
    /// <summary>
    /// 诊断会话与发现存储（方案 §10.2 DiagnosticSessions/DiagnosticFindings）。
    /// diagnostics.v1.json 单文件（低频写入），原子替换；用户可导出/删除。
    /// </summary>
    public sealed class DiagnosticStore
    {
        private const int SchemaVersion = 1;
        private const int MaxSessions = 500;
        private readonly string _path;
        private readonly object _gate = new object();
        private readonly List<DiagnosticSession> _sessions = new List<DiagnosticSession>();
        private readonly List<DiagnosticFinding> _findings = new List<DiagnosticFinding>();

        public DiagnosticStore(string path)
        {
            _path = path;
            Load();
        }

        public List<DiagnosticSession> Sessions
        {
            get { lock (_gate) return new List<DiagnosticSession>(_sessions); }
        }

        public List<DiagnosticFinding> Findings
        {
            get { lock (_gate) return new List<DiagnosticFinding>(_findings); }
        }

        public List<DiagnosticFinding> FindingsForDevice(string deviceId)
        {
            List<DiagnosticFinding> result = new List<DiagnosticFinding>();
            lock (_gate)
            {
                for (int i = 0; i < _findings.Count; i++)
                {
                    if (string.Equals(_findings[i].DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(_findings[i]);
                    }
                }
            }
            return result;
        }

        /// <summary>记录一次诊断：会话 + 其发现一起落盘。</summary>
        public void AddSession(DiagnosticSession session, List<DiagnosticFinding> findings)
        {
            if (session == null || string.IsNullOrEmpty(session.SessionId)) return;
            lock (_gate)
            {
                _sessions.Add(session);
                if (_sessions.Count > MaxSessions) _sessions.RemoveAt(0);
                if (findings != null)
                {
                    for (int i = 0; i < findings.Count; i++)
                    {
                        if (string.IsNullOrEmpty(findings[i].SessionId))
                        {
                            findings[i].SessionId = session.SessionId;
                        }
                        if (string.IsNullOrEmpty(findings[i].DeviceId))
                        {
                            findings[i].DeviceId = session.DeviceId;
                        }
                        if (findings[i].CreatedAtUtc == DateTime.MinValue)
                        {
                            findings[i].CreatedAtUtc = DateTime.UtcNow;
                        }
                        _findings.Add(findings[i]);
                    }
                }
                Save();
            }
        }

        public bool UpdateFindingStatus(string findingId, FindingStatus status)
        {
            lock (_gate)
            {
                for (int i = 0; i < _findings.Count; i++)
                {
                    if (string.Equals(_findings[i].FindingId, findingId, StringComparison.Ordinal))
                    {
                        _findings[i].Status = status;
                        _findings[i].ResolvedAtUtc =
                            status == FindingStatus.Resolved ? (DateTime?)DateTime.UtcNow : null;
                        Save();
                        return true;
                    }
                }
            }
            return false;
        }

        // —— 序列化（平铺 Dictionary + JavaScriptSerializer，同 KeyPolicyStore 模式） ——

        private void Load()
        {
            try
            {
                if (!File.Exists(_path)) return;
                JavaScriptSerializer ser = new JavaScriptSerializer();
                System.Collections.IDictionary root =
                    ser.DeserializeObject(File.ReadAllText(_path)) as System.Collections.IDictionary;
                if (root == null) return;
                int schema = root.Contains("schema") ? Convert.ToInt32(root["schema"]) : 0;
                if (schema > SchemaVersion) return;

                if (root.Contains("sessions"))
                {
                    foreach (object item in root["sessions"] as System.Collections.IEnumerable)
                    {
                        System.Collections.IDictionary o = item as System.Collections.IDictionary;
                        if (o == null) continue;
                        DiagnosticSession s = new DiagnosticSession();
                        s.SessionId = Str(o, "sessionId");
                        s.DeviceId = Str(o, "deviceId");
                        s.DeviceName = Str(o, "deviceName");
                        s.TestType = Str(o, "testType");
                        s.Note = Str(o, "note");
                        s.StartedAtUtc = new DateTime(Lng(o, "startedUtcTicks", 0), DateTimeKind.Utc);
                        s.EndedAtUtc = new DateTime(Lng(o, "endedUtcTicks", 0), DateTimeKind.Utc);
                        s.Status = (DiagnosticRunState)Int(o, "status", 3);
                        _sessions.Add(s);
                    }
                }
                if (root.Contains("findings"))
                {
                    foreach (object item in root["findings"] as System.Collections.IEnumerable)
                    {
                        System.Collections.IDictionary o = item as System.Collections.IDictionary;
                        if (o == null) continue;
                        DiagnosticFinding f = new DiagnosticFinding();
                        f.FindingId = Str(o, "findingId");
                        f.SessionId = Str(o, "sessionId");
                        f.DeviceId = Str(o, "deviceId");
                        f.DiagnosticCode = Str(o, "diagnosticCode");
                        f.Severity = (FindingSeverity)Int(o, "severity", 0);
                        f.Status = (FindingStatus)Int(o, "status", 0);
                        if (o.Contains("confidence") && o["confidence"] != null)
                        {
                            f.Confidence = Convert.ToDouble(o["confidence"]);
                        }
                        f.SampleCount = Int(o, "sampleCount", 0);
                        f.EvidenceSource = (EvidenceSource)Int(o, "evidenceSource", 0);
                        f.EvidenceJson = Str(o, "evidence");
                        f.Recommendation = Str(o, "recommendation");
                        f.CreatedAtUtc = new DateTime(Lng(o, "createdUtcTicks", 0), DateTimeKind.Utc);
                        long resolved = Lng(o, "resolvedUtcTicks", 0);
                        f.ResolvedAtUtc = resolved > 0
                            ? (DateTime?)new DateTime(resolved, DateTimeKind.Utc) : null;
                        _findings.Add(f);
                    }
                }
            }
            catch
            {
            }
        }

        private void Save()
        {
            try
            {
                string dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                Dictionary<string, object> root = new Dictionary<string, object>();
                root["schema"] = SchemaVersion;
                root["savedUtcTicks"] = DateTime.UtcNow.Ticks;
                List<object> sessions = new List<object>();
                for (int i = 0; i < _sessions.Count; i++)
                {
                    DiagnosticSession s = _sessions[i];
                    Dictionary<string, object> o = new Dictionary<string, object>();
                    o["sessionId"] = s.SessionId;
                    o["deviceId"] = s.DeviceId ?? "";
                    o["deviceName"] = s.DeviceName ?? "";
                    o["testType"] = s.TestType ?? "";
                    o["startedUtcTicks"] = s.StartedAtUtc.Ticks;
                    o["endedUtcTicks"] = s.EndedAtUtc.Ticks;
                    o["status"] = (int)s.Status;
                    if (!string.IsNullOrEmpty(s.Note)) o["note"] = s.Note;
                    sessions.Add(o);
                }
                root["sessions"] = sessions;

                List<object> findings = new List<object>();
                for (int i = 0; i < _findings.Count; i++)
                {
                    DiagnosticFinding f = _findings[i];
                    Dictionary<string, object> o = new Dictionary<string, object>();
                    o["findingId"] = f.FindingId;
                    o["sessionId"] = f.SessionId ?? "";
                    o["deviceId"] = f.DeviceId ?? "";
                    o["diagnosticCode"] = f.DiagnosticCode ?? "";
                    o["severity"] = (int)f.Severity;
                    o["status"] = (int)f.Status;
                    o["confidence"] = f.Confidence.HasValue ? (object)f.Confidence.Value : null;
                    o["sampleCount"] = f.SampleCount;
                    o["evidenceSource"] = (int)f.EvidenceSource;
                    o["evidence"] = f.EvidenceJson ?? "";
                    o["recommendation"] = f.Recommendation ?? "";
                    o["createdUtcTicks"] = f.CreatedAtUtc.Ticks;
                    o["resolvedUtcTicks"] = f.ResolvedAtUtc.HasValue
                        ? (object)f.ResolvedAtUtc.Value.Ticks : null;
                    findings.Add(o);
                }
                root["findings"] = findings;

                string json = new JavaScriptSerializer().Serialize(root);
                string tmp = _path + ".tmp";
                File.WriteAllText(tmp, json, new UTF8Encoding(false));
                if (File.Exists(_path))
                {
                    try
                    {
                        string backup = _path + ".bak";
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
            }
        }

        private static string Str(System.Collections.IDictionary o, string key)
        {
            return o.Contains(key) && o[key] != null ? Convert.ToString(o[key]) : null;
        }

        private static int Int(System.Collections.IDictionary o, string key, int def)
        {
            return o.Contains(key) && o[key] != null ? Convert.ToInt32(o[key]) : def;
        }

        private static long Lng(System.Collections.IDictionary o, string key, long def)
        {
            return o.Contains(key) && o[key] != null ? Convert.ToInt64(o[key]) : def;
        }
    }
}
