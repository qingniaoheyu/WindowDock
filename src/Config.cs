using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace WindowDock
{
    public class SlotConfig
    {
        public string ProcessName = "";
        public string TitleHint = "";
    }

    public class TaskItem
    {
        public bool Checked;
        public string Text = "";
        public DateTime? Deadline;

        public override string ToString()
        {
            return Text;
        }
    }

    public class Settings
    {
        public const int MaxSlots = 8;
        public bool StartWithWindows;
        public string LayoutMode = "Row";
        public int GridRows = 2;
        public int GridColumns = 2;
        public string Edge = "Right";
        public int MonitorIndex = 0;
        public int SlotWidth = 520;
        public int SlotHeight = 620;
        public bool AutoHide = true;
        public int AutoHideDelayMs = 900;
        public bool AlwaysOnTop = true;
        public int HotkeyMods = Native.MOD_CONTROL | Native.MOD_ALT;
        public int HotkeyKey = 32;
        public bool AllowSlotExpand = true;
        public bool TasksVisible = true;
        public int OpacityPercent = 100;
        public int BackgroundColorArgb = Color.FromArgb(30, 31, 35).ToArgb();
        public int DeadlineReminderHours = 24;
        public List<SlotConfig> Slots = new List<SlotConfig>();
        public List<TaskItem> Tasks = new List<TaskItem>();

        public Settings()
        {
            Slots.Add(new SlotConfig());
            Slots.Add(new SlotConfig());
        }

        public Settings Clone()
        {
            Settings c = new Settings();
            c.Slots.Clear();
            c.StartWithWindows = StartWithWindows;
            c.LayoutMode = LayoutMode;
            c.GridRows = GridRows;
            c.GridColumns = GridColumns;
            c.Edge = Edge;
            c.MonitorIndex = MonitorIndex;
            c.SlotWidth = SlotWidth;
            c.SlotHeight = SlotHeight;
            c.AutoHide = AutoHide;
            c.AutoHideDelayMs = AutoHideDelayMs;
            c.AlwaysOnTop = AlwaysOnTop;
            c.HotkeyMods = HotkeyMods;
            c.HotkeyKey = HotkeyKey;
            c.AllowSlotExpand = AllowSlotExpand;
            c.TasksVisible = TasksVisible;
            c.OpacityPercent = OpacityPercent;
            c.BackgroundColorArgb = BackgroundColorArgb;
            c.DeadlineReminderHours = DeadlineReminderHours;
            foreach (SlotConfig s in Slots)
            {
                SlotConfig sc = new SlotConfig();
                sc.ProcessName = s.ProcessName;
                sc.TitleHint = s.TitleHint;
                c.Slots.Add(sc);
            }
            foreach (TaskItem t in Tasks)
            {
                TaskItem tc = new TaskItem();
                tc.Checked = t.Checked;
                tc.Text = t.Text;
                tc.Deadline = t.Deadline;
                c.Tasks.Add(tc);
            }
            return c;
        }

        public static string ConfigPath
        {
            get
            {
                string dir = Path.GetDirectoryName(Application.ExecutablePath);
                string path = Path.Combine(dir, "WindowDock.ini");
                if (File.Exists(path)) return path;

                try
                {
                    using (FileStream fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write))
                    {
                    }
                    return path;
                }
                catch (Exception)
                {
                    string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowDock");
                    Directory.CreateDirectory(local);
                    return Path.Combine(local, "WindowDock.ini");
                }
            }
        }

        public static Settings Load()
        {
            Settings cfg = new Settings();
            cfg.StartWithWindows = StartupRegistration.IsEnabled;
            string path = ConfigPath;
            if (!File.Exists(path)) return cfg;

            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                foreach (string raw in lines)
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1).Trim();
                    map[key] = val;
                }
            }
            catch (Exception)
            {
                return cfg;
            }

            cfg.Edge = Get(map, "Edge", cfg.Edge);
            if (cfg.Edge != "Left" && cfg.Edge != "Right" && cfg.Edge != "Top" && cfg.Edge != "Bottom")
                cfg.Edge = "Right";
            cfg.MonitorIndex = Clamp(GetInt(map, "Monitor", cfg.MonitorIndex), 0, 15);
            cfg.SlotWidth = Clamp(GetInt(map, "SlotWidth", cfg.SlotWidth), 200, 2000);
            cfg.SlotHeight = Clamp(GetInt(map, "SlotHeight", cfg.SlotHeight), 240, 2000);
            cfg.AutoHide = GetBool(map, "AutoHide", cfg.AutoHide);
            cfg.AutoHideDelayMs = Clamp(GetInt(map, "AutoHideDelayMs", cfg.AutoHideDelayMs), 200, 10000);
            cfg.AlwaysOnTop = GetBool(map, "AlwaysOnTop", cfg.AlwaysOnTop);
            cfg.HotkeyMods = Clamp(GetInt(map, "HotkeyMods", cfg.HotkeyMods), 0, 15);
            cfg.HotkeyKey = Clamp(GetInt(map, "HotkeyKey", cfg.HotkeyKey), 0, 255);
            cfg.AllowSlotExpand = GetBool(map, "AllowSlotExpand", cfg.AllowSlotExpand);
            cfg.TasksVisible = GetBool(map, "TasksVisible", cfg.TasksVisible);
            cfg.OpacityPercent = Clamp(GetInt(map, "OpacityPercent", cfg.OpacityPercent), 20, 100);
            cfg.BackgroundColorArgb = Color.FromArgb(255,
                Color.FromArgb(GetInt(map, "BackgroundColorArgb", cfg.BackgroundColorArgb))).ToArgb();
            cfg.DeadlineReminderHours = Clamp(GetInt(map, "DeadlineReminderHours", cfg.DeadlineReminderHours), 1, 720);
            cfg.LayoutMode = Get(map, "Layout", cfg.LayoutMode) == "Grid" ? "Grid" : "Row";
            cfg.GridRows = Clamp(GetInt(map, "GridRows", cfg.GridRows), 1, MaxSlots);
            cfg.GridColumns = Clamp(GetInt(map, "GridColumns", cfg.GridColumns), 1, MaxSlots);

            int taskCount = Clamp(GetInt(map, "TaskCount", 0), 0, 50);
            cfg.Tasks.Clear();
            for (int i = 0; i < taskCount; i++)
            {
                TaskItem t = new TaskItem();
                t.Checked = GetBool(map, "Task" + i + "Checked", false);
                string text = Get(map, "Task" + i + "Text", "");
                if (text.Length > 500) text = text.Substring(0, 500);
                t.Text = text;
                DateTime deadline;
                if (DateTime.TryParseExact(Get(map, "Task" + i + "Deadline", ""), "o",
                    CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out deadline))
                    t.Deadline = deadline.ToLocalTime();
                if (t.Text.Length > 0) cfg.Tasks.Add(t);
            }

            int slotCount = GetInt(map, "SlotCount", -1);
            if (slotCount >= 0)
            {
                slotCount = Clamp(slotCount, 0, MaxSlots);
                cfg.Slots.Clear();
                for (int i = 0; i < slotCount; i++)
                {
                    SlotConfig sc = new SlotConfig();
                    sc.ProcessName = Get(map, "Slot" + i + "Process", "");
                    sc.TitleHint = Get(map, "Slot" + i + "Title", "");
                    cfg.Slots.Add(sc);
                }
            }

            if (cfg.GridRows * cfg.GridColumns > MaxSlots ||
                (cfg.LayoutMode == "Grid" && cfg.Slots.Count > cfg.GridRows * cfg.GridColumns))
                cfg.LayoutMode = "Row";

            return cfg;
        }

        public void Save()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# WindowDock configuration");
            sb.AppendLine("Layout=" + LayoutMode);
            sb.AppendLine("GridRows=" + GridRows);
            sb.AppendLine("GridColumns=" + GridColumns);
            sb.AppendLine("Edge=" + Edge);
            sb.AppendLine("Monitor=" + MonitorIndex);
            sb.AppendLine("SlotWidth=" + SlotWidth);
            sb.AppendLine("SlotHeight=" + SlotHeight);
            sb.AppendLine("AutoHide=" + (AutoHide ? "1" : "0"));
            sb.AppendLine("AutoHideDelayMs=" + AutoHideDelayMs);
            sb.AppendLine("AlwaysOnTop=" + (AlwaysOnTop ? "1" : "0"));
            sb.AppendLine("HotkeyMods=" + HotkeyMods);
            sb.AppendLine("HotkeyKey=" + HotkeyKey);
            sb.AppendLine("AllowSlotExpand=" + (AllowSlotExpand ? "1" : "0"));
            sb.AppendLine("TasksVisible=" + (TasksVisible ? "1" : "0"));
            sb.AppendLine("OpacityPercent=" + OpacityPercent);
            sb.AppendLine("BackgroundColorArgb=" + BackgroundColorArgb.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("DeadlineReminderHours=" + DeadlineReminderHours);
            sb.AppendLine("SlotCount=" + Slots.Count);
            for (int i = 0; i < Slots.Count; i++)
            {
                sb.AppendLine("Slot" + i + "Process=" + Clean(Slots[i].ProcessName));
                sb.AppendLine("Slot" + i + "Title=" + Clean(Slots[i].TitleHint));
            }
            sb.AppendLine("TaskCount=" + Tasks.Count);
            for (int i = 0; i < Tasks.Count; i++)
            {
                sb.AppendLine("Task" + i + "Checked=" + (Tasks[i].Checked ? "1" : "0"));
                sb.AppendLine("Task" + i + "Text=" + Clean(Tasks[i].Text));
                sb.AppendLine("Task" + i + "Deadline=" + (Tasks[i].Deadline.HasValue
                    ? Tasks[i].Deadline.Value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) : ""));
            }

            UTF8Encoding enc = new UTF8Encoding(false);
            string path = ConfigPath;
            try
            {
                File.WriteAllText(path, sb.ToString(), enc);
            }
            catch (Exception)
            {
                try
                {
                    string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowDock");
                    Directory.CreateDirectory(local);
                    File.WriteAllText(Path.Combine(local, "WindowDock.ini"), sb.ToString(), enc);
                }
                catch (Exception)
                {
                }
            }
        }

        private static string Clean(string s)
        {
            if (s == null) return "";
            return s.Replace('\r', ' ').Replace('\n', ' ');
        }

        private static string Get(Dictionary<string, string> map, string key, string def)
        {
            string v;
            if (map.TryGetValue(key, out v)) return v;
            return def;
        }

        private static int GetInt(Dictionary<string, string> map, string key, int def)
        {
            string v;
            if (!map.TryGetValue(key, out v)) return def;
            int n;
            if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return n;
            return def;
        }

        private static bool GetBool(Dictionary<string, string> map, string key, bool def)
        {
            string v;
            if (!map.TryGetValue(key, out v)) return def;
            if (v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase)) return true;
            if (v == "0" || string.Equals(v, "false", StringComparison.OrdinalIgnoreCase)) return false;
            return def;
        }

        private static int Clamp(int v, int min, int max)
        {
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }
    }
}
