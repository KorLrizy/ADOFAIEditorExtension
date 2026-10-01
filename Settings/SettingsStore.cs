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
    ///   "pagerListEnabled": true, "pagerAutoWindowSize": true, "pagerWindowScale": 1.0,
    ///   "pagerWindowSnap": false, "eventGroupEditing": "Decoration", "writeGroupConfig": false,
    ///   "decorationGroupCount": 1, "decorationGroups": [ { "name": "...", "tag": "...", "color": "rrggbbaa" } ],
    ///   "eventGroupCount": 0, "eventGroups": [],
    ///   "pagerWindow": { "width": 0, "height": 0, "x": 0.5, "y": 0.5, "snapEdges": 0 } }
    /// </code>
    /// <c>pagerWindow</c> 是分页器弹窗的布局偏好（手调宽高 / 归一化位置 / 吸附边），见
    /// <see cref="PagerWindowPreferences"/>：它**不是**设置页上的开关，只在拖拽结束时被控制器写一次，
    /// 也是本文件里唯一"值不在设置事件上"的一组字段。缺失或字段非法时全部回落到"没有偏好"。
    /// 其中 <c>x</c>/<c>y</c>（位置）与 <c>snapEdges</c>（贴边）来自**拖动标题移动窗口**，
    /// 自动尺寸开着或关着都能拖，所以两种模式下都会更新；吸附只改位置、不改窗口大小。
    /// 读取按字段逐个宽松解析：缺字段 / 类型不对的字段保持默认值，整个文件缺失或损坏 ⇒ 全用默认值 + 一行日志，绝不抛出。
    /// <c>color</c> 是组头颜色（原版约定的小写 hex：8 位 rrggbbaa，默认 <c>ffffff00</c> = 全透明纯白 = 不着色）；
    /// 缺这个字段或值不合法都按默认色处理（<see cref="CurrentVersion"/> 仍是 1：新增字段向后兼容，老文件照样能读）。
    /// 窗口那三连（自动调节 / 倍率 / 吸附）与倍率行同样是"新增字段向后兼容"，没有动版本号。
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
        private const string KeyGroupColor = "color";
        // 分页器弹窗的布局偏好（5 项，见 <see cref="PagerWindowPreferences"/>）：
        // 单独一个 root.pagerWindow 对象，与上面这些平级；缺字段/非法值一律宽松回落到"没有偏好"。
        private const string KeyPagerWindow = "pagerWindow";
        private const string KeyPagerWidth = "width";
        private const string KeyPagerHeight = "height";
        private const string KeyPagerPosX = "x";
        private const string KeyPagerPosY = "y";
        private const string KeyPagerSnap = "snapEdges";

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
                    root = ParseLoose(text);
                }
                catch (Exception parseError)
                {
                    Main.Logger?.Log("设置文件 " + FileName + " 损坏，使用默认设置（原文件另存为 " + FileName + ".bad）: " + parseError.Message);
                    try { File.Copy(path, path + ".bad", true); }
                    catch { }
                    return;
                }

                Apply(root, settings);
                // 窗口布局偏好存在静态字段里，不在设置事件上：必须先 Load 一次再谈"内容没变就不写盘"，
                // 否则第一次 Save 会把刚读到的偏好当成默认值写回去（见 ApplyPagerWindow 的说明）。
                ApplyPagerWindow(root);
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

        /// <summary>
        /// 宽松解析：先用默认设置解析；失败时退回"整数一律当 double 读"的 reader 再试一次。
        ///
        /// 原因：Newtonsoft 在 .NET Framework 上读到 JSON 里的 <c>NaN</c> / <c>Infinity</c>
        /// （消歧写法，例如手改过的 <c>"pagerWindowScale": NaN</c>）会直接抛 JsonReaderException。
        /// 那一步失败原本会把**整份设置**当损坏文件另存 .bad 并全部回落默认值 —— 对新增的倍率字段来说太狠了。
        /// 第二次解析把数字统一当 double（<c>ReadAsDouble</c> 自己就能处理 NaN/Infinity），于是文件其余部分照常读出来，
        /// 坏掉的只是那一个字段（<see cref="ReadNullableFloat"/> 会把它判成"不可用"）。两次都失败才算真损坏。
        /// </summary>
        private static JObject ParseLoose(string text)
        {
            try
            {
                return JObject.Parse(text);
            }
            catch (JsonException firstError)
            {
                try
                {
                    using (var reader = new JsonTextReader(new StringReader(text)))
                    {
                        reader.FloatParseHandling = FloatParseHandling.Double;
                        JObject retried = JObject.Load(reader);
                        if (retried != null)
                        {
                            Main.Logger?.Log("设置文件里有非法数字（NaN/Infinity 之类），已按「跳过该字段」处理: " + firstError.Message);
                            return retried;
                        }
                    }
                }
                catch { }
                throw;      // 第二次也失败 ⇒ 原样抛出，交给调用方按"文件损坏"处理
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
            // 窗口三连里的两个 bool 也照原有 settings 字段持久化（与上面几行同款）
            ApplyBool(root, settings, Main.KeyPagerAutoWindowSize);
            ApplyBool(root, settings, Main.KeyPagerWindowSnap);

            // 倍率是 float：缺失/非法/NaN/Infinity 一律回落到默认 1.0，有限但越界的夹到 0.5..2.5
            float scale = ReadFloat(root[Main.KeyPagerWindowScale]);
            if (!float.IsNaN(scale) && !float.IsInfinity(scale))
                settings[Main.KeyPagerWindowScale] = Main.ClampPagerWindowScale(scale);

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

        /// <summary>
        /// 读一个 float：缺失 / 不是数字 / 是 JSON 里的 NaN、Infinity（Newtonsoft 的消歧写法 "NaN" 等）
        /// 一律返回 <see cref="float.NaN"/> 表示"这个字段不可用"。绝不抛。
        /// </summary>
        private static float ReadFloat(JToken token)
        {
            float? value = ReadNullableFloat(token);
            return value.HasValue ? value.Value : float.NaN;
        }

        private static float? ReadNullableFloat(JToken token)
        {
            if (token == null)
                return null;
            try
            {
                if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
                    return (float)token;
                if (token.Type == JTokenType.String
                    && float.TryParse((string)token, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float parsed))
                    return parsed;
            }
            catch
            {
                // 显式 NaN / Infinity：Newtonsoft 在 net4.8 上会直接抛，这里当"字段不可用"处理
            }
            return null;
        }

        /// <summary>
        /// 读 <c>root.pagerWindow</c>（分页器弹窗的手调宽高 / 归一化位置 / 吸附边）到
        /// <see cref="PagerWindowPreferences"/>。
        ///
        /// 宽松策略（与文件的其它字段一致）：
        ///  · 整个对象缺失 / 不是对象 ⇒ 保持默认（宽高 0 = 没手调、位置 0.5、不吸附）；
        ///  · 宽高：缺失 / 非法 / NaN / Infinity ⇒ 保持默认 0；&lt; 1 也当 0；有效值夹到 &lt;= 10000；
        ///  · 位置：缺失 / 非法 ⇒ 保持默认 0.5；有效值夹到 0..1（拖动标题移动窗口时记录，两种模式都有）；
        ///  · 吸附边：缺失 / 非法 ⇒ 0；否则按位规范化（丢未知位、左右/上下只留一条）。
        ///    吸附只影响位置，不会改变这里记的宽高。
        /// 两个宽高只有**都**合法才会一起写进去，免得留下"有宽没高"的半套数据。
        /// </summary>
        private static void ApplyPagerWindow(JObject root)
        {
            JObject window = root[KeyPagerWindow] as JObject;
            if (window == null)
            {
                PagerWindowPreferences.Reset();
                return;
            }

            float? x = ReadNullableFloat(window[KeyPagerPosX]);
            float? y = ReadNullableFloat(window[KeyPagerPosY]);
            if (x.HasValue && y.HasValue && !float.IsNaN(x.Value) && !float.IsNaN(y.Value)
                && !float.IsInfinity(x.Value) && !float.IsInfinity(y.Value))
                PagerWindowPreferences.TrySetPosition(PagerWindowPreferences.Clamp01(x.Value), PagerWindowPreferences.Clamp01(y.Value));

            float? width = ReadNullableFloat(window[KeyPagerWidth]);
            float? height = ReadNullableFloat(window[KeyPagerHeight]);
            if (width.HasValue && height.HasValue
                && PagerWindowPreferences.TrySanitizeManualSize(width.Value, out float safeWidth)
                && PagerWindowPreferences.TrySanitizeManualSize(height.Value, out float safeHeight)
                && safeWidth > 0f && safeHeight > 0f)
                PagerWindowPreferences.TrySetManualSize(safeWidth, safeHeight);
            else
                PagerWindowPreferences.TrySetManualSize(0f, 0f);    // 半套 / 全坏 ⇒ 明确回到"没手调过"

            int? edges = ReadNullableInt(window[KeyPagerSnap]);
            PagerWindowPreferences.TrySetSnapEdges(edges.HasValue ? edges.Value : 0);
        }

        private static int? ReadNullableInt(JToken token)
        {
            if (token == null)
                return null;
            try
            {
                if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
                    return (int)Math.Round((double)token);
                if (token.Type == JTokenType.String && int.TryParse((string)token, out int parsed))
                    return parsed;
            }
            catch { }
            return null;
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
                // 颜色：缺字段 / 非法 hex 一律回落默认色（ffffff00 = 不着色），并规范成 8 位小写 hex
                settings[DecoGroupState.ColorKey(set, i)] = group != null ? ReadGroupColor(group[KeyGroupColor]) : DecoGroupState.DefaultGroupColorHex;
            }
            DecoGroupState.SetCount(set, count);
        }

        /// <summary>
        /// 分组颜色字段：无论磁盘上是什么（缺失 / 空串 / 带 '#' / 大小写混杂 / 长度不对 / 数字 / 对象），
        /// 要么给出规范化的 8 位小写 hex，要么给默认值；绝不抛（<see cref="DecoGroupState.TryParseGroupColor"/> 是纯逻辑解析）。
        /// </summary>
        private static string ReadGroupColor(JToken token)
        {
            if (token == null)
                return DecoGroupState.DefaultGroupColorHex;
            return DecoGroupState.TryParseGroupColor(ReadText(token), out UnityEngine.Color color)
                ? DecoGroupState.ToHex(color)
                : DecoGroupState.DefaultGroupColorHex;
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
            root[KeyPagerWindow] = BuildPagerWindow();
            return root.ToString(Formatting.Indented);
        }

        /// <summary>
        /// 弹窗布局偏好那 5 项。**不**走 <see cref="Main.IsPagerListEnabled"/> 那套"从设置事件重新读一遍"的写法：
        /// 这几个值只在拖拽/缩放结束时被 <c>PagerListController</c> 写进 <see cref="PagerWindowPreferences"/>，
        /// 设置事件里压根没有对应字段，所以这里直接序列化静态字段当前值（写出前再规范化一次，保证落盘的永远合法）。
        /// 写盘时机由调用方决定（拖拽结束时调一次 Save），不会每帧写。
        /// </summary>
        private static JObject BuildPagerWindow()
        {
            float width = 0f, height = 0f;
            if (PagerWindowPreferences.HasManualSize
                && PagerWindowPreferences.TrySanitizeManualSize(PagerWindowPreferences.ManualWidth, out float w)
                && PagerWindowPreferences.TrySanitizeManualSize(PagerWindowPreferences.ManualHeight, out float h))
            {
                width = w;
                height = h;
            }
            return new JObject
            {
                [KeyPagerWidth] = width,
                [KeyPagerHeight] = height,
                [KeyPagerPosX] = PagerWindowPreferences.Clamp01(PagerWindowPreferences.PositionX),
                [KeyPagerPosY] = PagerWindowPreferences.Clamp01(PagerWindowPreferences.PositionY),
                [KeyPagerSnap] = PagerWindowPreferences.SanitizeSnapEdges(PagerWindowPreferences.SnapEdges)
            };
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
                    [KeyGroupTag] = DecoGroupState.ReadString(settings, DecoGroupState.TagKey(set, i)),
                    // data 里存的就是 hex 字符串；这里统一规范成 8 位小写（读不回来 ⇒ 默认色）
                    [KeyGroupColor] = DecoGroupState.ReadCustomGroupColorHex(settings, set, i)
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
