using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using UnityEngine;

namespace InvokyrConfigMod
{
    // "配置修改" —— 游戏中按 F8 弹出/关闭一个配置编辑面板。
    // 通用版：扫描所有已加载 BepInEx 插件的配置文件（排除 BepInEx 核心与本 mod），
    // 每个 mod 一个 Tab，可编辑任意配置项；保存时写回 ConfigEntry.BoxedValue，立即生效并写回 .cfg。
    // 界面可切换中/英文，配置项名通过内置词典汉化（词典可扩展）。
    [BepInPlugin("top.juluo.invokyr.configmod", "Invokyr Config Mod", "2.0.0")]
    public class Plugin : BasePlugin
    {
        internal static ManualLogSource ModLog;

        public override void Load()
        {
            ModLog = base.Log;
            base.AddComponent<ConfigUI>();
            ModLog.LogInfo("[ConfigMod] loaded. Press F9 to open/close the in-game config editor.");
        }
    }

    // One editable setting, backed by a live ConfigEntryBase from some plugin's ConfigFile.
    public class GenericEntry
    {
        public ConfigEntryBase Entry;   // live reference; BoxedValue get/set does the work
        public string Section;
        public string Key;

        public bool IsBool;
        public bool IsNumber;
        public bool IsEditable;         // bool / number / string are editable; other types are read-only

        public bool BoolBuffer;
        public string Buffer;
    }

    // One Tab = one plugin.
    public class ModTab
    {
        public string Guid;
        public string Name;             // plugin display name
        public List<GenericEntry> Entries = new List<GenericEntry>();
    }

    // Overlay UI driven by Unity's immediate-mode GUI (OnGUI). F8 toggles the panel.
    public class ConfigUI : MonoBehaviour
    {
        enum Lang { Cn, En }

        // ---- Localisation dictionary: "GUID|Section|Key" -> Chinese display name.
        // Add more entries here (or in code) to localise other mods' settings.
        static readonly Dictionary<string, string> Dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["top.juluo.invokyr.blesscontrol|General|Enable"] = "总开关",
            ["top.juluo.invokyr.blesscontrol|BoardDice|BlessHealAmount"] = "祝福加血量",
            ["top.juluo.invokyr.blesscontrol|BoardDice|CurseEnabled"] = "保留诅咒面",
            ["top.juluo.invokyr.nochaosdice|General|Enable"] = "总开关",
            ["top.juluo.invokyr.nochaosdice|General|BlockMultiEffectDice"] = "拦截多效果组合骰",
            ["top.juluo.invokyr.nochaosdice|General|ExtraBlock"] = "额外拦截子串",
            ["top.juluo.invokyr.nochaosdice|General|Allow"] = "放行子串",
            ["top.juluo.invokyr.nochaosdice|General|DebugLog"] = "调试日志",
        };

        // Plugins we always skip: our own mod and BepInEx core/infrastructure.
        static readonly string[] SkipGuids =
        {
            "top.juluo.invokyr.configmod",
        };

        // Localised Tab (plugin) names: GUID -> Chinese display name.
        static readonly Dictionary<string, string> TabDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["top.juluo.invokyr.alldiscount"] = "总是打折",
            ["top.juluo.invokyr.nochaosdice"] = "不再混沌",
            ["top.juluo.invokyr.blesscontrol"] = "可控祝福",
        };

        static ConfigUI()
        {
            LoadDictFromFile();
        }

        // Optional external localisation dictionary: a UTF-8 text file under BepInEx\config\
        // that the user can edit. Each line is "key=value". A key containing '|' localises a
        // setting (GUID|Section|Key); a key without '|' localises a plugin/Tab (GUID). External
        // entries override the built-in defaults. If the file is missing it is created with the
        // built-in defaults so the user has a template to edit.
        const string DictFileName = "配置修改汉化词典.txt";

        // Append one "key=value" line to the dictionary file (created with UTF-8 BOM if missing).
        // Used to auto-register scanned settings so the user only edits the Chinese part.
        static void AppendDictLine(string line)
        {
            try
            {
                string dir = Paths.ConfigPath;
                if (string.IsNullOrEmpty(dir)) return;
                var path = Path.Combine(dir, DictFileName);
                using (var sw = new StreamWriter(path, true, new UTF8Encoding(true)))
                    sw.WriteLine(line);
            }
            catch { }
        }

        static void LoadDictFromFile()
        {
            try
            {
                string dir = Paths.ConfigPath;
                if (string.IsNullOrEmpty(dir)) return;
                string path = Path.Combine(dir, DictFileName);
                if (!File.Exists(path))
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("# Invokyr 配置修改 - 汉化词典");
                    sb.AppendLine("# 每行一条：键=中文名");
                    sb.AppendLine("# 配置项键格式：插件GUID|配置节|配置键  （含竖线 |）");
                    sb.AppendLine("# Tab(插件)键格式：插件GUID              （不含竖线）");
                    sb.AppendLine("# 本文件内容会覆盖内置默认汉化；删除本文件可恢复默认。");
                    sb.AppendLine();
                    sb.AppendLine("# --- Tab(插件)名 ---");
                    foreach (var kv in TabDict.OrderBy(x => x.Key))
                        sb.AppendLine(kv.Key + "=" + kv.Value);
                    sb.AppendLine();
                    sb.AppendLine("# --- 配置项 ---");
                    foreach (var kv in Dict.OrderBy(x => x.Key))
                        sb.AppendLine(kv.Key + "=" + kv.Value);
                    File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
                    Plugin.ModLog?.LogInfo("[ConfigMod] created localisation file: " + path);
                    return;
                }
                foreach (var raw in File.ReadAllLines(path))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    var k = line.Substring(0, eq).Trim();
                    var v = line.Substring(eq + 1).Trim();
                    if (k.Length == 0) continue;
                    if (k.Contains('|')) Dict[k] = v;
                    else TabDict[k] = v;
                }
                Plugin.ModLog?.LogInfo($"[ConfigMod] loaded localisation file ({Dict.Count} settings, {TabDict.Count} tabs).");
            }
            catch (Exception ex)
            {
                Plugin.ModLog?.LogWarning("[ConfigMod] failed to load localisation file: " + ex.Message);
            }
        }

        private readonly List<ModTab> tabs = new List<ModTab>();
        private Lang lang = Lang.Cn;
        private int curTab;
        private bool show;
        private Vector2 scroll;
        private Vector2 tabScroll;
        private bool scanned;

        void OnGUI()
        {
            // F9 via the IMGUI event stream (works regardless of which input system the game uses).
            // F9 was chosen so it doesn't collide with Safe Room Guide / Calypso which both use F8.
            // Toggling then returning immediately: the panel is not drawn this frame, so the layout
            // structure cannot change mid-frame and no "Mismatched LayoutGroup" is raised.
            try
            {
                var ev = Event.current;
                if (ev != null && ev.type == EventType.KeyDown && ev.keyCode == KeyCode.F9)
                {
                    show = !show;
                    return;
                }
            }
            catch { }

            if (!show) return;

            if (!scanned)
            {
                ScanPlugins();
                scanned = true;
            }

            float w = 660f;
            float h = 344f;

            GUILayout.BeginArea(new Rect(16, 16, w, h), GUI.skin.box);
            try
            {
                // Title + language switch
                GUILayout.BeginHorizontal();
                GUILayout.Label(T("Invokyr 配置编辑器", "Invokyr Config Editor"),
                    GUILayout.Height(26), GUILayout.ExpandWidth(true));
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(lang == Lang.Cn ? "语言：中文 ▼" : "Language: EN ▼", GUILayout.Width(118)))
                    lang = lang == Lang.Cn ? Lang.En : Lang.Cn;
                GUILayout.EndHorizontal();

                // Tabs (one per mod) — inside a horizontal scroll view so many mods don't overflow
                tabScroll = GUILayout.BeginScrollView(tabScroll, GUILayout.Height(42));
                GUILayout.BeginHorizontal();
                if (tabs.Count == 0)
                {
                    GUILayout.Label(T("未发现其它 mod 的配置。", "No other mod configs found."), GUILayout.Height(26));
                }
                for (int i = 0; i < tabs.Count; i++)
                {
                    string nm = LocalTabName(tabs[i].Guid, tabs[i].Name, lang);
                    if (curTab == i)
                        GUILayout.Toggle(true, nm, GUI.skin.button, GUILayout.Height(26));
                    else if (GUILayout.Button(nm, GUILayout.Height(26)))
                        curTab = i;
                }
                GUILayout.EndHorizontal();
                GUILayout.EndScrollView();

                // Scrollable content for the selected mod
                GUILayout.Space(4);
                if (tabs.Count > 0)
                {
                    if (curTab < 0 || curTab >= tabs.Count) curTab = 0;
                    scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(196));
                    var tab = tabs[curTab];
                    if (tab.Entries.Count == 0)
                    {
                        GUILayout.Space(8);
                        GUILayout.Label(T("该 mod 没有可编辑的配置项。", "This mod has no editable settings."));
                    }
                    else
                    {
                        foreach (var e in tab.Entries)
                        {
                            string disp = LocalName(tab.Guid, e, lang);
                            GUILayout.BeginHorizontal();
                            GUILayout.Label(disp, GUILayout.Width(300));
                            if (!e.IsEditable)
                            {
                                GUILayout.Label(SafeStr(e.Entry.BoxedValue), GUILayout.Width(220));
                            }
                            else if (e.IsBool)
                            {
                                bool on = GUILayout.Toggle(e.BoolBuffer,
                                    e.BoolBuffer ? T("开启", "ON") : T("关闭", "OFF"), GUILayout.Width(120));
                                e.BoolBuffer = on;
                            }
                            else
                            {
                                e.Buffer = GUILayout.TextField(e.Buffer ?? "", GUILayout.Width(220));
                            }
                            GUILayout.EndHorizontal();
                        }
                    }
                    GUILayout.EndScrollView();
                }

                // Bottom buttons
                GUILayout.FlexibleSpace();
                GUILayout.BeginHorizontal();
                if (GUILayout.Button(T("保存并生效", "Save & Apply"), GUILayout.Width(140), GUILayout.Height(30)))
                    SaveAll();
                if (GUILayout.Button(T("刷新列表", "Refresh"), GUILayout.Width(110), GUILayout.Height(30)))
                { scanned = false; tabs.Clear(); curTab = 0; }
                if (GUILayout.Button(T("关闭", "Close"), GUILayout.Width(100), GUILayout.Height(30)))
                    show = false;
                GUILayout.EndHorizontal();
                GUILayout.Space(4);
            }
            catch (Exception ex)
            {
                Plugin.ModLog?.LogWarning("[ConfigMod] UI error: " + ex);
            }
            GUILayout.EndArea();
        }

        string T(string cn, string en) => lang == Lang.Cn ? cn : en;

        // Localised Tab name: Chinese name from TabDict in Cn mode, otherwise the plugin's own name.
        string LocalTabName(string guid, string enName, Lang l)
        {
            if (l == Lang.Cn && TabDict.TryGetValue(guid, out var cnName))
                return cnName;
            return enName;
        }

        // Localised display name for a setting.
        string LocalName(string guid, GenericEntry e, Lang l)
        {
            if (l == Lang.Cn && Dict.TryGetValue(guid + "|" + e.Section + "|" + e.Key, out var cnName))
                return cnName;
            return e.Section + " / " + e.Key;
        }

        // Scan every loaded plugin, build one tab per plugin (skipping BepInEx core + this mod).
        void ScanPlugins()
        {
            try
            {
                foreach (var plugin in EnumeratePlugins())
                {
                    if (plugin == null || plugin.Metadata == null) continue;
                    string guid = plugin.Metadata.GUID;
                    if (string.IsNullOrEmpty(guid)) continue;
                    if (SkipGuids.Any(s => guid.Equals(s, StringComparison.OrdinalIgnoreCase))) continue;
                    if (guid.IndexOf("BepInEx", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                    object inst = plugin.Instance;
                    if (inst == null) continue;
                    object cfgObj;
                    try { cfgObj = inst.GetType().GetProperty("Config")?.GetValue(inst); }
                    catch { cfgObj = null; }
                    var cfg = cfgObj as ConfigFile;
                    if (cfg == null) continue;

                    // Auto-register this plugin's Tab name so the user only edits the Chinese part.
                    if (!TabDict.ContainsKey(guid))
                    {
                        TabDict[guid] = SafeStr(plugin.Metadata.Name);
                        AppendDictLine(guid + "=" + TabDict[guid]);
                    }

                    var tab = new ModTab { Guid = guid, Name = SafeStr(plugin.Metadata.Name) };
                    try
                    {
                        foreach (var entry in cfg.Values)
                        {
                            var ge = Classify(entry);
                            if (ge != null)
                            {
                                tab.Entries.Add(ge);
                                // Auto-register the setting key with the raw (English) name as a
                                // placeholder; the user later changes only the Chinese value.
                                string dk = guid + "|" + ge.Section + "|" + ge.Key;
                                if (!Dict.ContainsKey(dk))
                                {
                                    Dict[dk] = ge.Section + " / " + ge.Key;
                                    AppendDictLine(dk + "=" + Dict[dk]);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Plugin.ModLog?.LogWarning($"[ConfigMod] read config of '{guid}' failed: {ex.Message}");
                    }
                    if (tab.Entries.Count > 0)
                        tabs.Add(tab);
                }
                Plugin.ModLog?.LogInfo($"[ConfigMod] scanned {tabs.Count} mod(s) with editable config.");
            }
            catch (Exception ex)
            {
                Plugin.ModLog?.LogWarning("[ConfigMod] scan failed: " + ex);
            }
        }

        // Enumerate all loaded plugins by discovering the chainloader at runtime (the actual
        // type lives in BepInEx.Unity.IL2CPP with a name we don't hard-code), then reading its
        // static PluginInfos/Plugins collection. Handles both IDictionary and IReadOnlyDictionary.
        IEnumerable<BepInEx.PluginInfo> EnumeratePlugins()
        {
            var list = new List<BepInEx.PluginInfo>();
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var aname = asm.GetName().Name;
                    if (aname != "BepInEx.Unity.IL2CPP" && aname != "BepInEx.Core") continue;
                    Type[] types;
                    try { types = asm.GetTypes(); }
                    catch { continue; }
                    foreach (var t in types)
                    {
                        if (!t.IsClass) continue;
                        if (!t.Name.Contains("Chainloader")) continue;
                        // Try static plugin collections first
                        foreach (var pname in new[] { "PluginInfos", "Plugins" })
                        {
                            var pi = t.GetProperty(pname, BindingFlags.Public | BindingFlags.Static);
                            if (pi == null) continue;
                            try
                            {
                                var val = pi.GetValue(null);
                                if (val != null)
                                {
                                    var got = IteratePluginInfos(val);
                                    if (got.Count > 0) { list.AddRange(got); return list; }
                                }
                            }
                            catch { }
                        }
                        // Then a static Instance -> its instance Plugins/PluginInfos
                        var instProp = t.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                        object instObj = null;
                        if (instProp != null)
                        {
                            try { instObj = instProp.GetValue(null); } catch { }
                        }
                        if (instObj != null)
                        {
                            foreach (var pname in new[] { "PluginInfos", "Plugins" })
                            {
                                var pi = instObj.GetType().GetProperty(pname, BindingFlags.Public | BindingFlags.Instance);
                                if (pi == null) continue;
                                try
                                {
                                    var val = pi.GetValue(instObj);
                                    if (val != null)
                                    {
                                        var got = IteratePluginInfos(val);
                                        if (got.Count > 0) { list.AddRange(got); return list; }
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                }
                Plugin.ModLog?.LogWarning("[ConfigMod] chainloader/PluginInfos not found among loaded BepInEx assemblies.");
            }
            catch (Exception ex)
            {
                Plugin.ModLog?.LogWarning("[ConfigMod] enumerate plugins failed: " + ex);
            }
            return list;
        }

        // Pull PluginInfo values out of either an IDictionary or any IEnumerable<KeyValuePair<,>>
        // / IEnumerable<PluginInfo> (covers IReadOnlyDictionary too, which isn't a non-generic IDictionary).
        static List<BepInEx.PluginInfo> IteratePluginInfos(object plugins)
        {
            var outList = new List<BepInEx.PluginInfo>();
            if (plugins is IDictionary idict)
            {
                foreach (var v in idict.Values)
                    if (v is BepInEx.PluginInfo pinfo) outList.Add(pinfo);
                return outList;
            }
            try
            {
                var en = plugins.GetType().GetMethod("GetEnumerator")?.Invoke(plugins, null);
                if (en == null) return outList;
                var current = en.GetType().GetProperty("Current");
                var moveNext = en.GetType().GetMethod("MoveNext");
                while (current != null && moveNext != null && (bool)moveNext.Invoke(en, null))
                {
                    var item = current.GetValue(en);
                    if (item is BepInEx.PluginInfo pinfo)
                    {
                        outList.Add(pinfo);
                        continue;
                    }
                    var vp = item?.GetType().GetProperty("Value");
                    if (vp != null && vp.GetValue(item) is BepInEx.PluginInfo p2) outList.Add(p2);
                }
            }
            catch { }
            return outList;
        }

        // Turn a ConfigEntryBase into an editable GenericEntry, or null if unusable.
        GenericEntry Classify(ConfigEntryBase entry)
        {
            try
            {
                var def = entry.Definition;
                var val = entry.BoxedValue;
                var ge = new GenericEntry
                {
                    Entry = entry,
                    Section = def.Section,
                    Key = def.Key,
                };
                var t = val?.GetType();
                if (t == typeof(bool)) { ge.IsBool = true; ge.IsEditable = true; ge.BoolBuffer = (bool)val; }
                else if (t == typeof(int) || t == typeof(float) || t == typeof(double) || t == typeof(long))
                { ge.IsNumber = true; ge.IsEditable = true; ge.Buffer = val.ToString(); }
                else if (t == typeof(string)) { ge.IsEditable = true; ge.Buffer = (string)val ?? ""; }
                else { ge.IsEditable = false; }
                return ge;
            }
            catch { return null; }
        }

        void SaveAll()
        {
            int ok = 0;
            foreach (var tab in tabs)
            {
                foreach (var e in tab.Entries)
                {
                    if (!e.IsEditable || e.Entry == null) continue;
                    try
                    {
                        if (e.IsBool) e.Entry.BoxedValue = e.BoolBuffer;
                        else if (e.IsNumber)
                        {
                            var cur = e.Entry.BoxedValue;
                            if (cur is int) { if (int.TryParse((e.Buffer ?? "").Trim(), out var n)) e.Entry.BoxedValue = n; }
                            else if (cur is float) { if (float.TryParse((e.Buffer ?? "").Trim(), out var n)) e.Entry.BoxedValue = n; }
                            else if (cur is long) { if (long.TryParse((e.Buffer ?? "").Trim(), out var n)) e.Entry.BoxedValue = n; }
                            else if (cur is double) { if (double.TryParse((e.Buffer ?? "").Trim(), out var n)) e.Entry.BoxedValue = n; }
                        }
                        else if (e.Entry.BoxedValue is string)
                        {
                            e.Entry.BoxedValue = e.Buffer ?? "";
                        }
                        ok++;
                    }
                    catch (Exception ex)
                    {
                        Plugin.ModLog?.LogWarning($"[ConfigMod] save {tab.Name}/{e.Section}.{e.Key} failed: {ex.Message}");
                    }
                }
            }
            Plugin.ModLog?.LogInfo($"[ConfigMod] saved & applied {ok} setting(s).");
            show = false;
        }

        static string SafeStr(object o)
        {
            try { return o?.ToString() ?? ""; } catch { return ""; }
        }
    }
}
