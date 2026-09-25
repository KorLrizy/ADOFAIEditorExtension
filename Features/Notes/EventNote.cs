using ADOFAI;
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
    /// 空备注由下面的 `Encode` 后置补丁摘掉（见该类的注释），所以不会给每个事件都留一个 `"aeeNote": ""`。
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
    /// 空备注不落盘：`LevelEvent.Encode` 的返回值里把空的 `aeeNote` 摘掉。
    ///
    /// 为什么不走"`canBeDisabled: true` + 手动维护 `disabled["aeeNote"]`"那条路（探索结论里的备选）：
    /// 那样面板会先把这一行画成"关"（`disabled[name]`=true ⇒ offText 亮、控件隐藏），
    /// 用户必须先点一下启用才能输入；而且 `LevelEvent.set_Item` 不动 disabled，还得再找地方补维护。
    /// 改成在 `Encode` 出口摘空值：属性恒可用（见 EventNote 的说明），**一个补丁、一个点**，
    /// 而且与"值怎么变成这样的"无关 —— 手输、多选批量写回、粘贴、撤销回退，保存时都会被这里纠正。
    ///
    /// 覆盖范围：全程序集里 `LevelEvent.Encode` 的调用者只有 `LevelData.EncodeToDictionary`、
    /// `scnEditor.SaveLevel/SaveBackup/ExportLevel`（IL 扫描过），所以保存/备份/导出三条路都覆盖。
    /// 有内容的备注**原样保留**，只摘空串/纯空白。
    /// </summary>
    [HarmonyPatch(typeof(LevelEvent), "Encode")]
    internal static class EncodeNotePatch
    {
        internal static void Postfix(Dictionary<string, object> __result)
        {
            if (__result == null)
                return;
            if (!__result.TryGetValue(EventNote.KeyNote, out object value))
                return;
            if (value == null || EventNote.IsEmpty(value.ToString()))
                __result.Remove(EventNote.KeyNote);
        }
    }
}
