using ADOFAI;
using ADOFAIEditorExtension.Features.DecoGrouping;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Text;

namespace ADOFAIEditorExtension.Settings
{
    /// <summary>
    /// 模组设置的持久化（跨游戏重启）：标签页 LevelEvent 上的全部设置值 + 两套自定义分组的行数。
    /// 对外契约（其它模块只调用这两个方法）：
    ///  - <see cref="Load"/>：设置事件第一次创建后调用，把磁盘上的值灌回设置事件并恢复分组行数；
    ///  - <see cref="Save"/>：任何设置值 / 分组行数变化后调用（幂等，可频繁调用）。
    ///
    /// 文件：<c>&lt;mod 目录&gt;/Settings.json</c>（用户数据，不随 mod 发布、不进版本库）。格式：
    /// <code>
    /// { "version": 1, "decoGroupingEnabled": true, "autoGroupMode": "ByType", "showGroupCounts": true,
    ///   "pagerListEnabled": true, "eventGroupEditing": "Decoration", "writeGroupConfig": false,
    ///   "decorationGroupCount": 1, "decorationGroups": [ { "name": "...", "tag": "..." } ],
    ///   "eventGroupCount": 0, "eventGroups": [] }
    /// </code>
    /// 读取按字段逐个宽松解析：缺字段 / 类型不对的字段保持默认值，整个文件缺失或损坏 ⇒ 全用默认值 + 一行日志，绝不抛出。
    /// 写入走"临时文件 + 替换"保证原子性；内容与上次一致时跳过写盘；IO 异常只记日志。
    /// <c>CustomTab.saveSetting == false</c> 时整个持久化关闭（读写都跳过）。
    /// </summary>
    internal static class SettingsStore
    {
        private const int CurrentVersion = 1;
        private const string FileName = "Settings.json";

        private const string KeyVersion = "version";
        private const string KeyDecoCount = "decorationGroupCount";
        private const string KeyDecoGroups = "decorationGroups";
        private const string KeyEventCount = "eventGroupCount";
        private const string KeyEventGroups = "eventGroups";
        private const string KeyGroupName = "name";
        private const string KeyGroupTag = "tag";

        /// <summary>是否已经跑过 <see cref="Load"/>：没读过就写会拿默认值覆盖掉磁盘上的设置，所以 Save 以它为前提。</summary>
        private static bool loaded;

        /// <summary>最近一次与磁盘一致的内容（读到的 / 写成功的），用于"内容没变就不写盘"。</summary>
        private static string lastContent;

        /// <summary>上一条写盘失败的消息：同样的失败只记一次，免得每次改设置都刷一行。</summary>
        private static string lastSaveError;

        private static bool PersistenceEnabled => Main.Aee == null || Main.Aee.saveSetting;

        private static string FilePath
        {
            get
            {
                string dir = Main.ModEntry?.Path;
                return string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, FileName);
            }
        }

        // ------------------------------------------------------------------ 读

        internal static void Load()
        {
            if (loaded)
                return;
            loaded = true;     // 先置位：下面任何一步取设置事件都不会再次进来
            try
            {
                if (!PersistenceEnabled)
                    return;
                LevelEvent settings = Main.AeeLevelEvent;
                string path = FilePath;
                if (settings == null || path == null)
                    return;

                if (!File.Exists(path))
                {
                    Main.Logger?.Log("未找到设置文件 " + FileName + "，使用默认设置");
                    return;
                }

                string text = File.ReadAllText(path, Encoding.UTF8);
                JObject root;
                try
                {
                    root = JObject.Parse(text);
                }
                catch (Exception parseError)
                {
                    Main.Logger?.Log("设置文件 " + FileName + " 损坏，使用默认设置（原文件另存为 " + FileName + ".bad）: " + parseError.Message);
                    try { File.Copy(path, path + ".bad", true); }
                    catch { }
                    return;
                }

                Apply(root, settings);
                // 以"按当前值重新序列化"作为基准：读回来的值没变时，下一次 Save 不会只因格式差异就重写文件
                lastContent = Serialize(settings);
                Main.Logger?.Log(string.Format("已读取设置文件 {0}：自定义分组 装饰 {1} 行 / 事件 {2} 行",
                    FileName, DecoGroupState.CountOf(DecoGroupState.GroupSet.Decoration),
                    DecoGroupState.CountOf(DecoGroupState.GroupSet.Event)));
            }
            catch (Exception e)
            {
                Main.Logger?.Log("读取设置文件失败，使用默认设置: " + e.Message);
            }
        }

        /// <summary>把 JSON 里的值按运行时类型写回设置事件（bool 存 bool、枚举存枚举实例、文本存 string）。</summary>
        private static void Apply(JObject root, LevelEvent settings)
        {
            int version = ReadInt(root[KeyVersion], 0);
            if (version > CurrentVersion)
                Main.Logger?.Log("设置文件版本 " + version + " 比本模组支持的 " + CurrentVersion + " 新，只读取认识的字段");

            ApplyBool(root, settings, Main.KeyDecoGroupingEnabled);
            ApplyBool(root, settings, Main.KeyShowGroupCounts);
            ApplyBool(root, settings, Main.KeyPagerListEnabled);
            ApplyBool(root, settings, Main.KeyWriteGroupConfig);

            if (TryReadEnum(root[Main.KeyAutoGroupMode], out AutoGroupMode autoMode))
                settings[Main.KeyAutoGroupMode] = autoMode;

            JToken editing = root[Main.KeyEventGroupEditing];
            if (editing != null && editing.Type == JTokenType.Boolean)
                settings[Main.KeyEventGroupEditing] = (bool)editing ? GroupEditTarget.Event : GroupEditTarget.Decoration;   // 兼容旧的 bool 存法
            else if (TryReadEnum(editing, out GroupEditTarget target))
                settings[Main.KeyEventGroupEditing] = target;

            ApplyGroups(root, settings, DecoGroupState.GroupSet.Decoration, KeyDecoCount, KeyDecoGroups);
            ApplyGroups(root, settings, DecoGroupState.GroupSet.Event, KeyEventCount, KeyEventGroups);
        }

        private static void ApplyBool(JObject root, LevelEvent settings, string key)
        {
            JToken token = root[key];
            if (token == null)
                return;
            if (token.Type == JTokenType.Boolean)
                settings[key] = (bool)token;
            else if (token.Type == JTokenType.String && bool.TryParse((string)token, out bool parsed))
                settings[key] = parsed;
        }

        private static bool TryReadEnum<T>(JToken token, out T value) where T : struct
        {
            value = default;
            if (token == null || token.Type != JTokenType.String)
                return false;
            string text = (string)token;
            return !string.IsNullOrEmpty(text) && Enum.TryParse(text, true, out value) && Enum.IsDefined(typeof(T), value);
        }

        private static int ReadInt(JToken token, int fallback)
        {
            if (token == null)
                return fallback;
            try
            {
                if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
                    return (int)Math.Round((double)token);
                if (token.Type == JTokenType.String && int.TryParse((string)token, out int parsed))
                    return parsed;
            }
            catch { }
            return fallback;
        }

        private static string ReadText(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined)
                return "";
            if (token.Type == JTokenType.String)
                return (string)token ?? "";
            if (token.Type == JTokenType.Object || token.Type == JTokenType.Array)
                return "";
            return token.ToString();
        }

        private static void ApplyGroups(JObject root, LevelEvent settings, DecoGroupState.GroupSet set, string countKey, string listKey)
        {
            JArray groups = root[listKey] as JArray;
            // 行数优先用显式字段；没有就按数组长度推
            int count = ReadInt(root[countKey], groups != null ? groups.Count : 0);
            count = Math.Max(0, Math.Min(count, DecoGroupState.MaxCustomGroups));

            for (int i = 0; i < count; i++)
            {
                JObject group = groups != null && i < groups.Count ? groups[i] as JObject : null;
                settings[DecoGroupState.NameKey(set, i)] = group != null ? ReadText(group[KeyGroupName]) : "";
                settings[DecoGroupState.TagKey(set, i)] = group != null ? ReadText(group[KeyGroupTag]) : "";
            }
            DecoGroupState.SetCount(set, count);
        }

        // ------------------------------------------------------------------ 写

        internal static void Save()
        {
            try
            {
                if (!PersistenceEnabled)
                    return;
                LevelEvent settings = Main.GetSettingsEvent();   // 第一次取会顺带触发 Load
                if (settings == null || !loaded)
                    return;
                string path = FilePath;
                if (path == null)
                    return;

                string content = Serialize(settings);
                if (string.Equals(content, lastContent, StringComparison.Ordinal))
                    return;

                WriteAtomically(path, content);
                lastContent = content;
                lastSaveError = null;
            }
            catch (Exception e)
            {
                string message = e.GetType().Name + ": " + e.Message;
                if (!string.Equals(message, lastSaveError, StringComparison.Ordinal))
                    Main.Logger?.Log("保存设置文件失败: " + message);
                lastSaveError = message;
            }
        }

        private static string Serialize(LevelEvent settings)
        {
            var root = new JObject
            {
                [KeyVersion] = CurrentVersion,
                [Main.KeyDecoGroupingEnabled] = Main.IsDecoGroupingEnabled,
                [Main.KeyAutoGroupMode] = Main.AutoGroupMode.ToString(),
                [Main.KeyShowGroupCounts] = Main.ShowGroupCounts,
                [Main.KeyPagerListEnabled] = Main.IsPagerListEnabled,
                [Main.KeyEventGroupEditing] = Main.EditTarget.ToString(),
                [Main.KeyWriteGroupConfig] = Main.WriteGroupConfig
            };
            WriteGroups(root, settings, DecoGroupState.GroupSet.Decoration, KeyDecoCount, KeyDecoGroups);
            WriteGroups(root, settings, DecoGroupState.GroupSet.Event, KeyEventCount, KeyEventGroups);
            return root.ToString(Formatting.Indented);
        }

        private static void WriteGroups(JObject root, LevelEvent settings, DecoGroupState.GroupSet set, string countKey, string listKey)
        {
            int count = Math.Max(0, Math.Min(DecoGroupState.CountOf(set), DecoGroupState.MaxCustomGroups));
            var groups = new JArray();
            for (int i = 0; i < count; i++)
            {
                groups.Add(new JObject
                {
                    [KeyGroupName] = DecoGroupState.ReadString(settings, DecoGroupState.NameKey(set, i)),
                    [KeyGroupTag] = DecoGroupState.ReadString(settings, DecoGroupState.TagKey(set, i))
                });
            }
            root[countKey] = count;
            root[listKey] = groups;
        }

        /// <summary>先写同目录临时文件，再整体替换目标文件：写到一半崩溃也不会留下半截 JSON。</summary>
        private static void WriteAtomically(string path, string content)
        {
            string temp = path + ".tmp";
            File.WriteAllText(temp, content, new UTF8Encoding(false));
            if (!File.Exists(path))
            {
                File.Move(temp, path);
                return;
            }
            try
            {
                File.Replace(temp, path, null);
            }
            catch (Exception replaceError) when (replaceError is IOException || replaceError is PlatformNotSupportedException
                || replaceError is UnauthorizedAccessException)
            {
                // 某些文件系统（网络盘 / FAT）不支持 Replace：退回"覆盖复制 + 删临时文件"
                File.Copy(temp, path, true);
                try { File.Delete(temp); }
                catch { }
            }
        }
    }
}
