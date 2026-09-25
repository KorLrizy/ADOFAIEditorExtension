using ADOFAI;
using ADOFAIEditorExtension.PropertyCollection;
using ADOFAIEditorExtension.Utils;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace ADOFAIEditorExtension.Features.Notes
{
    /// <summary>
    /// 事件备注（§21）：给**每个事件类型**注册一个可见的 String 属性 `aeeNote`。
    ///
    /// 为什么这样就够了：原版 `PropertiesPanel.Init` 遍历 `info.propertiesInfo` 逐个建行，
    /// 所以注册完面板自动多出一行输入框；输入走 `PropertyControl_Text` 的 onEndEdit →
    /// `selectedEvent[name] = text`（进 data），序列化交给原版 —— 除了下面摘掉空备注的那一个补丁，
    /// **一个新补丁都不需要**（行标签由 `Property.set_info` 按 dict 的 `key` 取，配上本模组
    /// 现成的 `RDString.GetWithCheck` 补丁就能显示中文）。
    ///
    /// 三个声明细节都是照原版 ctor 定的：
    ///  - `default: ""` —— String 分支是 `value_default = RDString.Get((string)dict["default"])`，
    ///    默认值会被当**本地化键**处理，写 `""` 最安全；
    ///  - `canBeDisabled: false` —— `PropertiesPanel.SetupCheckmark` 里
    ///    `disabled = (canBeDisabled | isFake) & disabled[name]`，canBeDisabled=false 时该行**恒为可用**，
    ///    用户点开就能输入，不用先点"启用"（这正是备注这种随手填的字段该有的样子）；
    ///  - 长度限制的键名是 `minLength`/`maxLength`（不是 `min`/`max`），而且这两个字段在游戏里
    ///    没有任何引用（死字段），所以不做长度限制；`localizable` 会改变 `GetStringLocalized` 的语义，不开。
    ///
    /// 落盘：本属性**不挂**"把分组归属写进关卡文件"那个开关 —— 备注是用户内容，始终注册、始终可保存。
    /// 空备注由下面的保存前置补丁摘掉（见该类的注释），所以不会给每个事件都留一个 `"aeeNote": ""`。
    /// </summary>
    internal static class EventNote
    {
        /// <summary>事件 data 里的备注键（也是面板上那一行的属性名）。</summary>
        internal const string KeyNote = "aeeNote";

        /// <summary>面板行标签的本地化键（dict 的 `key`）。</summary>
        internal const string LabelKey = "aee.note";

        /// <summary>
        /// 把 `aeeNote` 注册进每个**事件类型**（幂等；已注册就跳过）。
        /// 只动 `GCS.levelEventsInfo`（事件），不动 `GCS.settingsInfo`（设置面板）——
        /// 否则每个设置页都会多出一行"备注"，那不是需求。
        /// </summary>
        internal static void EnsureRegistered()
        {
            if (GCS.levelEventsInfo == null)
                return;
            try
            {
                foreach (KeyValuePair<string, LevelEventInfo> pair in GCS.levelEventsInfo)
                {
                    LevelEventInfo info = pair.Value;
                    if (info == null || info.propertiesInfo == null)
                        continue;
                    if (info.propertiesInfo.ContainsKey(KeyNote))
                        continue;

                    var property = new Property_InputField(
                        name: KeyNote,
                        type: Property_InputField.InputType.String,
                        value_default: "",
                        key: LabelKey,
                        canBeDisabled: false,
                        startEnabled: true);
                    ADOFAI.PropertyInfo propertyInfo = new ADOFAI.PropertyInfo(property.ToData(), info);
                    propertyInfo.order = 0;
                    info.propertiesInfo[KeyNote] = propertyInfo;
                }
            }
            catch (Exception e)
            {
                Main.Logger?.Log("注册事件备注属性失败: " + e.Message);
            }
        }

        /// <summary>读一条事件备注（没这个键、类型不对、异常都返回空串）。</summary>
        internal static string GetNote(LevelEvent e)
        {
            if (e == null)
                return "";
            try
            {
                if (e.TryGet<string>(KeyNote, out string note))
                    return note ?? "";
            }
            catch { }
            return "";
        }

        /// <summary>备注是否为空（用于行上要不要画、悬停要不要弹）。</summary>
        internal static bool IsEmpty(string note) => string.IsNullOrWhiteSpace(note);
    }

    /// <summary>
    /// 保存前置的两件清理，挂在**保存动作的入口**（不是 `LevelEvent.Encode`）：
    ///
    /// ① **摘掉空的 `aeeNote`**：备注是随手填的，空的没必要落盘。
    ///
    /// 为什么不挂 `LevelEvent.Encode`（v1 的做法）：r265 把关卡序列化从"每个事件 Encode 出一个
    /// Dictionary、再整体转 JSON"改成了**直接拼文本**，`Encode` 的返回值随之从
    /// `Dictionary&lt;string, object&gt;` 变成了 `string` —— 原先那个按 `Dictionary __result` 摘键的
    /// 后置补丁签名对不上，已经打不上了（审计 A-6 / §41）。挂到保存入口去改 `data` 反而更稳：
    /// 与"值怎么变成这样的"无关（手输、多选批量写回、粘贴、撤销回退都会纠正），也不依赖序列化怎么改。
    ///
    /// 也不走"`canBeDisabled: true` + 手动维护 `disabled["aeeNote"]`"那条路：那样面板会先把这一行画成
    /// "关"（`disabled[name]`=true ⇒ offText 亮、控件隐藏），用户必须先点一下启用才能输入。
    ///
    /// ② **按声明类型纠正 data 里的值**（§42 bug 1）：r265 的 `LevelEvent.Encode(bool)` 按 data 键遍历、
    /// 按 `PropertyInfo.type` **硬转**（String/LongString/File/Color → `castclass System.String`，
    /// Int/Rating → `unbox.any Int32`，Float/Bool 同理），类型不符直接 `InvalidCastException`；
    /// 而 r148 的 Encode 不做任何 cast，所以旧版同样的写法没事。这个异常还被
    /// `scnEditor.SaveLevel` 自己的 catch 吞成"保存失败！！！"弹框，玩家看不到原因
    /// （异常可见性由 `Patches.LevelEncodeFinalizerPatch` 补，见 §42）。
    /// 所以在编码之前把不一致的值就地纠正（纠正不了就摘键 —— 摘掉的键 Encode 会跳过，等于丢掉该属性默认值，
    /// 总比整份关卡存不下去好）。只处理 `propertiesInfo` 里**注册过**的键：未注册的 Encode 本来就会跳过。
    ///
    /// 覆盖范围：IL 核过 `scnEditor` 上四个会写盘的出口 —— `SaveLevel` / `SaveBackup` /
    /// `SaveLevelAs(bool, string)` / `ExportLevel(bool)`，即保存、备份、另存、导出四条路。
    /// 有内容的备注**原样保留**，只摘 null / 空串 / 纯空白。
    ///
    /// 整个 Prefix 包在 try/catch 里：清理只是保险，绝不能让异常打断玩家的保存流程。
    /// </summary>
    [HarmonyPatch(typeof(scnEditor), "SaveLevel", new Type[0])]
    [HarmonyPatch(typeof(scnEditor), "SaveBackup", new Type[0])]
    [HarmonyPatch(typeof(scnEditor), "SaveLevelAs", new[] { typeof(bool), typeof(string) })]
    [HarmonyPatch(typeof(scnEditor), "ExportLevel", new[] { typeof(bool) })]
    internal static class SaveEmptyNoteStripPatch
    {
        internal static void Prefix(scnEditor __instance)
        {
            try
            {
                scnEditor editor = __instance != null ? __instance : scnEditor.instance;
                if (editor == null)
                    return;
                StripEmptyNotes(editor.decorations);
                StripEmptyNotes(editor.events);
                SanitizeDataTypes(editor.decorations);
                SanitizeDataTypes(editor.events);
            }
            catch (Exception e)
            {
                Main.Logger?.Log("保存前置清理失败（摘空备注 / data 类型体检）: " + e.Message);
            }
        }

        private static void StripEmptyNotes(List<LevelEvent> events)
        {
            if (events == null)
                return;
            for (int i = 0; i < events.Count; i++)
            {
                LevelEvent e = events[i];
                if (e == null || e.data == null)
                    continue;
                if (!e.data.TryGetValue(EventNote.KeyNote, out object value))
                    continue;
                if (value == null || EventNote.IsEmpty(value.ToString()))
                    e.data.Remove(EventNote.KeyNote);
            }
        }

        // ------------------------------------------------------------ data 类型体检（§42 bug 1）

        /// <summary>同一次保存里同一个键只报一条日志：一份关卡动辄几千个事件，逐个报会刷屏。</summary>
        private static readonly HashSet<string> loggedCorrections = new HashSet<string>(StringComparer.Ordinal);

        private static void SanitizeDataTypes(List<LevelEvent> events)
        {
            if (events == null)
                return;
            for (int i = 0; i < events.Count; i++)
            {
                LevelEvent e = events[i];
                if (e == null)
                    continue;
                Dictionary<string, object> data = e.data;
                if (data == null || data.Count == 0)
                    continue;
                LevelEventInfo info = e.info;
                if (info == null || info.propertiesInfo == null)
                    continue;

                // 快照键再遍历：纠正过程本身要往 data 里写/摘，边改边枚举会抛
                List<string> keys = new List<string>(data.Keys);
                for (int k = 0; k < keys.Count; k++)
                {
                    string key = keys[k];
                    ADOFAI.PropertyInfo propertyInfo;
                    if (!info.propertiesInfo.TryGetValue(key, out propertyInfo) || propertyInfo == null)
                        continue;                     // 未注册的键 Encode 会跳过，不用管
                    object value;
                    if (!data.TryGetValue(key, out value) || value == null)
                        continue;
                    CorrectOne(info, data, key, value, propertyInfo.type);
                }
            }
        }

        private static void CorrectOne(LevelEventInfo info, Dictionary<string, object> data, string key, object value, PropertyType type)
        {
            switch (type)
            {
                // ---- 原版按 `castclass System.String` 转的四类
                case PropertyType.String:
                case PropertyType.LongString:
                case PropertyType.File:
                case PropertyType.Color:
                    if (value is string)
                        return;
                    data[key] = Convert.ToString(value, CultureInfo.InvariantCulture);
                    LogCorrection(info, key, value, type, "转成字符串");
                    return;

                // ---- 原版按 `unbox.any Int32` 转的两类
                case PropertyType.Int:
                case PropertyType.Rating:
                    if (value is int)
                        return;
                    int integer;
                    if (TryParseInt(value, out integer))
                    {
                        data[key] = integer;
                        LogCorrection(info, key, value, type, "解析成 Int32");
                    }
                    else
                    {
                        data.Remove(key);
                        LogCorrection(info, key, value, type, "解析失败，已摘键");
                    }
                    return;

                case PropertyType.Float:
                    if (value is float)
                        return;
                    float real;
                    if (TryParseFloat(value, out real))
                    {
                        data[key] = real;
                        LogCorrection(info, key, value, type, "解析成 Single");
                    }
                    else
                    {
                        data.Remove(key);
                        LogCorrection(info, key, value, type, "解析失败，已摘键");
                    }
                    return;

                case PropertyType.Bool:
                    if (value is bool)
                        return;
                    bool flag;
                    if (TryParseBool(value, out flag))
                    {
                        data[key] = flag;
                        LogCorrection(info, key, value, type, "解析成 Boolean");
                    }
                    else
                    {
                        data.Remove(key);
                        LogCorrection(info, key, value, type, "解析失败，已摘键");
                    }
                    return;

                default:
                    return;      // Enum / Vector2 / Export 等：原版不按标量硬转，这里不动
            }
        }

        private static string Text(object value)
        {
            try { return Convert.ToString(value, CultureInfo.InvariantCulture); }
            catch { return "<不可转换>"; }
        }

        private static bool TryParseInt(object value, out int result)
        {
            return int.TryParse(Text(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
        }

        private static bool TryParseFloat(object value, out float result)
        {
            return float.TryParse(Text(value), NumberStyles.Float, CultureInfo.InvariantCulture, out result);
        }

        private static bool TryParseBool(object value, out bool result)
        {
            return bool.TryParse(Text(value), out result);
        }

        private static void LogCorrection(LevelEventInfo info, string key, object value, PropertyType type, string action)
        {
            if (Main.Logger == null)
                return;
            string signature = (info != null ? info.name : "?") + "/" + key + "/" + action;
            if (!loggedCorrections.Add(signature))
                return;
            Main.Logger.Log(string.Format(
                "保存前类型体检：{0}.{1} 声明为 {2}，实际是 {3}（值 {4}），已{5}",
                info != null ? info.name : "?", key, type,
                value != null ? value.GetType().FullName : "null", Text(value), action));
        }
    }
}
