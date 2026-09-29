using ADOFAI;
using ADOFAIEditorExtension.PropertyCollection;
using ADOFAIEditorExtension.Utils;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

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
                    if (info.propertiesInfo.TryGetValue(KeyNote, out ADOFAI.PropertyInfo existing) && existing != null)
                    {
                        existing.invisible = false;   // 禁用时被 SetHidden(true) 藏起来过 ⇒ 重新启用要复位
                        continue;
                    }

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

        /// <summary>
        /// 禁用模组时隐藏"备注"行但**保留注册**（见 EditorIntegration.RemoveInjection 的说明）：
        /// 注册一摘，禁用状态下的保存/读档就会丢掉关卡里的备注。
        /// </summary>
        internal static void SetHidden(bool hidden)
        {
            if (GCS.levelEventsInfo == null)
                return;
            try
            {
                foreach (KeyValuePair<string, LevelEventInfo> pair in GCS.levelEventsInfo)
                {
                    if (pair.Value?.propertiesInfo != null
                        && pair.Value.propertiesInfo.TryGetValue(KeyNote, out ADOFAI.PropertyInfo info) && info != null)
                        info.invisible = hidden;
                }
            }
            catch (Exception e)
            {
                Main.Logger?.Log("切换事件备注可见性失败: " + e.Message);
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
    /// ① **临时摘掉不该落盘的本模组键**：
    ///    - 空的 `aeeNote`（null / 空串 / 纯空白）：备注是随手填的，空的没必要落盘；
    ///    - 值为空的分组归属键（`DecoGroupState.IsMembershipKey`：`aeeGroupDeco` / `aeeGroupEvent` / 旧版 `aeeGroup`）
    ///      —— 归属键现在对装饰 / 砖上事件类型**始终注册**，新建对象都带着 `""`，不摘的话每个对象都会多写一个空键；
    ///    - "把分组归属写进关卡文件"关着时（`!Main.WriteGroupConfig`）摘掉**全部**归属键：这时归属只存在
    ///      `DecoGroupState` 的会话表里，data 里的值不参与当前会话。
    ///
    ///    **只是"保存期间"摘**：Prefix 摘键并记下原值，Finalizer（保存正常结束或抛异常都会跑）原样放回。
    ///    不能永久删：`PropertiesPanel.SetProperties` 是**按 data 键**遍历来刷新控件的（IL 已核：
    ///    `data.Keys.ToList()` → `properties.ContainsKey(key)` → 写控件），键不在 data 里时该行控件
    ///    **不会被刷新**，会一直显示上一个选中事件的备注文字（不抛异常，但显示错、且之后编辑会把旧文字写进新事件）。
    ///    放回原值也保证"保存"这个动作对内存模型零副作用（归属键的会话语义由 `DecoGroupState` 自己管）。
    ///    `SaveLevelAs` 最终总是调到 `SaveLevel()`（IL 已核：`SaveLevelAsCo` → 本地函数 `Save` → `SaveLevel`），
    ///    异步走文件对话框时真正的编码发生在后面那次 `SaveLevel` 里，由它自己的 Prefix/Finalizer 再摘/放一次；
    ///    同步嵌套时外层已经摘过，内层什么也摘不到，放回由外层负责。
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
    /// 按 `PropertyInfo.type` 转换：String/LongString/File/Color → `castclass System.String`，
    /// Int/Rating → `unbox.any Int32`，Bool → `unbox.any Boolean`，类型不符直接 `InvalidCastException`；
    /// **Float 例外**，走的是宽松的 `Convert.ToSingle(object)`（double / int / 数字字符串都能过，
    /// 只有转不了的值 —— 例如非数字字符串 —— 才抛）。r148 的 Encode 不做任何 cast，所以旧版同样的写法没事。
    /// 这个异常还被 `scnEditor.SaveLevel` 自己的 catch 吞成"保存失败！！！"弹框，玩家看不到原因
    /// （异常可见性由 `Patches.LevelEncodeFinalizerPatch` 补，见 §42）。
    /// 所以在编码之前把不一致的值就地纠正（这一步是**永久**的：纠正后的值才是该属性应有的类型）：
    /// 数值 → Int32 按 `MidpointRounding.AwayFromZero` 四舍五入（超出范围算失败）；数值 0/1、"0"/"1"、
    /// "true"/"false" → Boolean；Float 只在 `Convert.ToSingle` 会抛时才介入。纠正不了就换成该属性的默认值
    /// （落盘效果等于"丢掉这个值、读档回到默认"，总比整份关卡存不下去好）；默认值本身类型也不对才摘键。
    /// 只处理 `propertiesInfo` 里**注册过**的键：未注册的 Encode 本来就会跳过。
    ///
    /// 覆盖范围：IL 核过 `scnEditor` 上四个会写盘的出口 —— `SaveLevel` / `SaveBackup` /
    /// `SaveLevelAs(bool, string)` / `ExportLevel(bool)`，即保存、备份、另存、导出四条路。
    /// 有内容的备注**原样保留**，只摘 null / 空串 / 纯空白。
    ///
    /// Prefix / Finalizer 都整段包在 try/catch 里：清理只是保险，绝不能让异常打断玩家的保存流程。
    ///
    /// **目标必须用 <see cref="TargetMethods"/> 列出**：Harmony 2 会把同一个类上的多个
    /// <c>[HarmonyPatch(typeof, name, args)]</c> 合并成**一个**目标（后写的覆盖先写的），
    /// 旧写法实测只挂上了 <c>ExportLevel(bool)</c>，普通保存完全没被拦截。
    /// </summary>
    [HarmonyPatch]
    internal static class SaveEmptyNoteStripPatch
    {
        internal static IEnumerable<MethodBase> TargetMethods()
        {
            MethodBase[] targets =
            {
                AccessTools.Method(typeof(scnEditor), "SaveLevel", new Type[0]),
                AccessTools.Method(typeof(scnEditor), "SaveBackup", new Type[0]),
                AccessTools.Method(typeof(scnEditor), "SaveLevelAs", new[] { typeof(bool), typeof(string) }),
                AccessTools.Method(typeof(scnEditor), "ExportLevel", new[] { typeof(bool) })
            };
            foreach (MethodBase target in targets)
            {
                if (target != null)
                    yield return target;
            }
        }

        /// <summary>一次保存调用期间临时摘掉的键（Finalizer 按它原样放回）。</summary>
        internal sealed class StripState
        {
            internal readonly List<(LevelEvent Event, string Key, object Value)> Removed =
                new List<(LevelEvent, string, object)>();
        }

        /// <summary>保存入口的嵌套深度（SaveLevel → SaveLevelAs → SaveLevel 可能同步嵌套），只在最外层清日志去重表。</summary>
        private static int depth;

        internal static void Prefix(scnEditor __instance, out StripState __state)
        {
            __state = new StripState();
            if (depth++ == 0)
                loggedCorrections.Clear();      // "每次保存"只报一次：新的一次保存从空表开始
            try
            {
                scnEditor editor = __instance != null ? __instance : scnEditor.instance;
                if (editor == null)
                    return;
                bool stripAllMembership = !Main.WriteGroupConfig;
                StripTransientKeys(editor.decorations, stripAllMembership, __state);
                StripTransientKeys(editor.events, stripAllMembership, __state);
                SanitizeDataTypes(editor.decorations);
                SanitizeDataTypes(editor.events);
            }
            catch (Exception e)
            {
                Main.Logger?.Log("保存前置清理失败（摘空备注 / 归属键 / data 类型体检）: " + e.Message);
            }
        }

        /// <summary>保存结束（含异常）后把 Prefix 摘掉的键原样放回；null 放回成 ""（面板刷新会对值 ToString）。</summary>
        internal static void Finalizer(StripState __state)
        {
            if (__state == null)
                return;
            depth = Math.Max(0, depth - 1);
            try
            {
                List<(LevelEvent Event, string Key, object Value)> removed = __state.Removed;
                for (int i = 0; i < removed.Count; i++)
                {
                    Dictionary<string, object> data = removed[i].Event?.data;
                    if (data == null || data.ContainsKey(removed[i].Key))
                        continue;                 // 保存期间别处又写了这个键：以新值为准
                    data[removed[i].Key] = removed[i].Value ?? "";
                }
                removed.Clear();
            }
            catch (Exception e)
            {
                Main.Logger?.Log("保存后放回临时摘掉的键失败: " + e.Message);
            }
        }

        /// <summary>摘掉空备注、空归属键（写入开关关时摘全部归属键），并记进 state 供保存后放回。</summary>
        private static void StripTransientKeys(List<LevelEvent> events, bool stripAllMembership, StripState state)
        {
            if (events == null)
                return;
            List<string> toRemove = new List<string>(3);
            for (int i = 0; i < events.Count; i++)
            {
                LevelEvent e = events[i];
                if (e == null || e.data == null)
                    continue;
                Dictionary<string, object> data = e.data;
                toRemove.Clear();
                foreach (KeyValuePair<string, object> pair in data)
                {
                    bool isNote = pair.Key == EventNote.KeyNote;
                    bool isMembership = !isNote && DecoGrouping.DecoGroupState.IsMembershipKey(pair.Key);
                    if (!isNote && !isMembership)
                        continue;
                    bool empty = pair.Value == null || EventNote.IsEmpty(pair.Value.ToString());
                    if (empty || (isMembership && stripAllMembership))
                        toRemove.Add(pair.Key);
                }
                for (int k = 0; k < toRemove.Count; k++)
                {
                    state.Removed.Add((e, toRemove[k], data[toRemove[k]]));
                    data.Remove(toRemove[k]);
                }
            }
        }

        // ------------------------------------------------------------ data 类型体检（§42 bug 1）

        /// <summary>同一次保存里同一个键只报一条日志：一份关卡动辄几千个事件，逐个报会刷屏（每次保存开始时清空）。</summary>
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
                    CorrectOne(info, data, key, value, propertyInfo);
                }
            }
        }

        private static void CorrectOne(LevelEventInfo info, Dictionary<string, object> data, string key, object value, ADOFAI.PropertyInfo propertyInfo)
        {
            PropertyType type = propertyInfo.type;
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
                    if (TryConvertInt(value, out integer))
                    {
                        data[key] = integer;
                        LogCorrection(info, key, value, type, "转成 Int32");
                    }
                    else
                    {
                        Fallback(info, data, key, value, propertyInfo);
                    }
                    return;

                // ---- 原版走宽松的 `Convert.ToSingle(object)`：它能转的一律不动，只救它会抛的
                case PropertyType.Float:
                    if (value is float || ConvertToSingleSucceeds(value))
                        return;
                    float real;
                    if (TryParseFloat(value, out real))
                    {
                        data[key] = real;
                        LogCorrection(info, key, value, type, "解析成 Single");
                    }
                    else
                    {
                        Fallback(info, data, key, value, propertyInfo);
                    }
                    return;

                // ---- 原版按 `unbox.any Boolean` 转
                case PropertyType.Bool:
                    if (value is bool)
                        return;
                    bool flag;
                    if (TryConvertBool(value, out flag))
                    {
                        data[key] = flag;
                        LogCorrection(info, key, value, type, "解析成 Boolean");
                    }
                    else
                    {
                        Fallback(info, data, key, value, propertyInfo);
                    }
                    return;

                default:
                    return;      // Enum / Vector2 / Export 等：原版不按标量硬转，这里不动
            }
        }

        /// <summary>
        /// 纠正不了的值：换成该属性自己的默认值（类型本来就对），默认值类型也不对才摘键。
        /// 落盘结果与"摘键后读档由 Decode 补默认值"一样，但 data 里键还在 ——
        /// `PropertiesPanel.SetProperties` 按 data 键刷新控件，键没了该行会停在上一个事件的值上。
        /// </summary>
        private static void Fallback(LevelEventInfo info, Dictionary<string, object> data, string key, object value, ADOFAI.PropertyInfo propertyInfo)
        {
            object fallback = propertyInfo.value_default;
            if (fallback != null && MatchesEncodeType(fallback, propertyInfo.type))
            {
                data[key] = fallback;
                LogCorrection(info, key, value, propertyInfo.type, "解析失败，已换成默认值 " + Text(fallback));
                return;
            }
            data.Remove(key);
            LogCorrection(info, key, value, propertyInfo.type, "解析失败，已摘键");
        }

        private static bool MatchesEncodeType(object candidate, PropertyType type)
        {
            switch (type)
            {
                case PropertyType.Int:
                case PropertyType.Rating:
                    return candidate is int;
                case PropertyType.Bool:
                    return candidate is bool;
                case PropertyType.Float:
                    return candidate is float || ConvertToSingleSucceeds(candidate);
                default:
                    return candidate is string;
            }
        }

        private static string Text(object value)
        {
            try { return Convert.ToString(value, CultureInfo.InvariantCulture); }
            catch { return "<不可转换>"; }
        }

        /// <summary>是不是 .NET 的数值类型（装箱后）。</summary>
        private static bool IsNumeric(object value)
        {
            switch (value)
            {
                case byte _:
                case sbyte _:
                case short _:
                case ushort _:
                case int _:
                case uint _:
                case long _:
                case ulong _:
                case float _:
                case double _:
                case decimal _:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>数值（含 "1.5" 这类数字字符串）→ Int32：四舍五入（远离零），NaN/∞/超出 Int32 范围算失败。</summary>
        private static bool TryConvertInt(object value, out int result)
        {
            result = 0;
            if (value is bool b)
            {
                result = b ? 1 : 0;
                return true;
            }
            double number;
            if (IsNumeric(value))
            {
                if (value is long || value is ulong || value is decimal)
                {
                    // 大整数先走 decimal，避免 double 精度把边界值挪过界
                    decimal exact;
                    try { exact = Convert.ToDecimal(value, CultureInfo.InvariantCulture); }
                    catch { return false; }
                    exact = Math.Round(exact, MidpointRounding.AwayFromZero);
                    if (exact < int.MinValue || exact > int.MaxValue)
                        return false;
                    result = (int)exact;
                    return true;
                }
                number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            }
            else
            {
                string text = Text(value);
                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
                    return true;
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                    return false;
            }
            if (double.IsNaN(number) || double.IsInfinity(number))
                return false;
            number = Math.Round(number, MidpointRounding.AwayFromZero);
            if (number < int.MinValue || number > int.MaxValue)
                return false;
            result = (int)number;
            return true;
        }

        /// <summary>与原版 Encode 同一个调用（`Convert.ToSingle(object)`，当前区域性）：能过就说明原版不会抛。</summary>
        private static bool ConvertToSingleSucceeds(object value)
        {
            try
            {
                Convert.ToSingle(value);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryParseFloat(object value, out float result)
        {
            return float.TryParse(Text(value), NumberStyles.Float, CultureInfo.InvariantCulture, out result);
        }

        /// <summary>→ Boolean：数值只认 0 / 1；字符串认 "true"/"false"（不分大小写）与 "0"/"1"。</summary>
        private static bool TryConvertBool(object value, out bool result)
        {
            result = false;
            if (IsNumeric(value))
            {
                double number;
                try { number = Convert.ToDouble(value, CultureInfo.InvariantCulture); }
                catch { return false; }
                if (number == 0d || number == 1d)
                {
                    result = number == 1d;
                    return true;
                }
                return false;
            }
            string text = Text(value)?.Trim();
            if (bool.TryParse(text, out result))
                return true;
            if (text == "0" || text == "1")
            {
                result = text == "1";
                return true;
            }
            return false;
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
