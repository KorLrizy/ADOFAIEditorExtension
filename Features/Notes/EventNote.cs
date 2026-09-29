using ADOFAI;
using ADOFAIEditorExtension.Features.DecoGrouping;
using ADOFAIEditorExtension.PropertyCollection;
using HarmonyLib;
using System;
using System.Collections.Generic;

namespace ADOFAIEditorExtension.Features.Notes
{
    /// <summary>
    /// 事件备注（§21）：给**每个事件类型**注册一个可见的 String 属性 `aeeNote`。
    ///
    /// 为什么这样就够了：原版 `PropertiesPanel.Init` 遍历 `info.propertiesInfo` 逐个建行，
    /// 所以注册完面板自动多出一行输入框；输入走 `PropertyControl_Text` 的 onEndEdit →
    /// `selectedEvent[name] = text`（进 data），保存/读档走原版 `LevelEvent.Encode/Decode`
    /// —— **一个新补丁都不需要**（行标签由 `Property.set_info` 按 dict 的 `key` 取，配上本模组
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
    /// 空备注由下面的 `Encode` 后置补丁摘掉（见该类的注释），所以不会给每个事件都留一个 `"aeeNote": ""`
    /// （调用链：`SaveLevel` → `LevelData.Encode` → `LevelData.EncodeToDictionary` → `LevelEvent.Encode`）。
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
        /// 禁用模组时隐藏"备注"行但**保留注册**：r148 的 Decode 只读、Encode 只写注册过的键，
        /// 注册一摘，禁用状态下的保存/读档就会丢掉关卡里的备注。原版 PropertyInfo.CheckIfShown
        /// 第一句就是 <c>if (invisible) return false</c>，所以标 invisible 即可让面板不再显示这一行。
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
    /// 空备注 / 分组归属不该落盘的部分：`LevelEvent.Encode` 的返回值里摘掉。
    ///  - 空的 `aeeNote`（空串/纯空白/null）；
    ///  - 分组归属键（<see cref="DecoGroupState.IsMembershipKey"/>）值为空的（= 没有手动归属）；
    ///  - "把分组归属写进关卡文件"关着时（<see cref="Main.WriteGroupConfig"/> == false）**全部**归属键。
    /// 归属始终留在 data 里（键始终注册、默认 ""），开关只决定"保存时写不写"，所以只在这里摘。
    ///
    /// 为什么直接改 `__result` 是安全的：r148 的 `Encode(bool settings)` 每次都**新建**一个 Dictionary
    /// 往里拷值，不是返回 data 本身；撤销用 `Copy()`、剪贴板用 `CopyShallow`，都不经过 Encode ⇒
    /// 摘掉的只是这次保存的输出，活数据原封不动，不需要事后放回。
    ///
    /// 为什么不走"`canBeDisabled: true` + 手动维护 `disabled["aeeNote"]`"那条路（探索结论里的备选）：
    /// 那样面板会先把这一行画成"关"（`disabled[name]`=true ⇒ offText 亮、控件隐藏），
    /// 用户必须先点一下启用才能输入；而且 `LevelEvent.set_Item` 不动 disabled，还得再找地方补维护。
    /// 改成在 `Encode` 出口摘空值：属性恒可用（见 EventNote 的说明），**一个补丁、一个点**，
    /// 而且与"值怎么变成这样的"无关 —— 手输、多选批量写回、粘贴、撤销回退，保存时都会被这里纠正。
    ///
    /// 覆盖范围：全程序集里 `LevelEvent.Encode` 的调用者只有 `LevelData.EncodeToDictionary`（IL 扫描过），
    /// 调用链是 `scnEditor.SaveLevel / SaveBackup / GetExportLevelFiles / ExportLevel` → `LevelData.Encode`
    /// → `LevelData.EncodeToDictionary` → `LevelEvent.Encode`，所以保存/备份/导出几条路都覆盖。
    /// 有内容的备注**原样保留**，只摘空串/纯空白。
    /// 整段包 try/catch：清理只是锦上添花，绝不能让异常打断玩家的保存。
    /// </summary>
    [HarmonyPatch(typeof(LevelEvent), "Encode")]
    internal static class EncodeNotePatch
    {
        internal static void Postfix(Dictionary<string, object> __result)
        {
            if (__result == null)
                return;
            try
            {
                if (__result.TryGetValue(EventNote.KeyNote, out object value)
                    && (value == null || EventNote.IsEmpty(value.ToString())))
                    __result.Remove(EventNote.KeyNote);

                // 归属键：先看有没有（绝大多数事件没有），有才去读开关，免得每个事件都查一次设置
                List<string> toRemove = null;
                bool? writeGroups = null;
                foreach (KeyValuePair<string, object> pair in __result)
                {
                    if (!DecoGroupState.IsMembershipKey(pair.Key))
                        continue;
                    if (writeGroups == null)
                        writeGroups = Main.WriteGroupConfig;
                    if (writeGroups == true && pair.Value != null && !string.IsNullOrWhiteSpace(pair.Value.ToString()))
                        continue;
                    (toRemove ?? (toRemove = new List<string>(2))).Add(pair.Key);
                }
                if (toRemove != null)
                {
                    for (int i = 0; i < toRemove.Count; i++)
                        __result.Remove(toRemove[i]);
                }
            }
            catch (Exception e)
            {
                Main.Logger?.Log("保存时清理事件备注/分组归属失败: " + e.Message);
            }
        }
    }

    /// <summary>
    /// `LevelEvent.Encode` 抛异常时补一行 UMM 日志：r148 的 `SaveLevel` 会把编码异常吞掉
    /// （只 `Debug.LogError` + 弹窗），UMM 日志里什么都没有，很难定位是哪个事件坏了。
    /// 这里只记录事件类型与砖号，然后**原样把异常还回去**（返回 __exception），不改变原版的处理流程。
    /// 单独一个补丁类：与上面的后置补丁互不影响（任一个打补丁失败不会连带另一个）。
    /// </summary>
    [HarmonyPatch(typeof(LevelEvent), "Encode")]
    internal static class EncodeFinalizerPatch
    {
        internal static Exception Finalizer(Exception __exception, LevelEvent __instance)
        {
            if (__exception == null)
                return null;
            try
            {
                string type = "?";
                string floor = "?";
                if (__instance != null)
                {
                    type = __instance.eventType.ToString();
                    floor = __instance.floor.ToString();
                }
                Main.Logger?.Log(string.Format("LevelEvent.Encode 失败（事件 {0}，砖 {1}）: {2}", type, floor, __exception));
            }
            catch { }
            return __exception;
        }
    }
}
