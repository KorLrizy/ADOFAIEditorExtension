using ADOFAIEditorExtension.Features.DecoGrouping;
using ADOFAIEditorExtension.PropertyCollection;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace ADOFAIEditorExtension
{
    /// <summary>
    /// 标签页面板的字段声明。取值全部走 Main 里的 key 常量，字段值存于标签页自己的 LevelEvent。
    /// 自定义分组的行数与 MultiTrackHelper 的"倍频器行"同款：字段一次性声明到上限，
    /// 运行时只改哪些行可见（activeChilden + UpdatePanel），不做真正的列表增删。
    /// </summary>
    public static class PrefabProperties
    {
        /// <summary>面板上始终可见的字段（顺序即面板顺序）。</summary>
        public static List<string> BaseToActive { get; } = new List<string>
        {
            Main.KeyDecoGroupingEnabled,
            Main.KeyAutoGroupMode,
            Main.KeyShowGroupCounts,
            Main.KeyPagerListEnabled,
            Main.KeyPagerAutoWindowSize,
            Main.KeyPagerWindowScale,
            Main.KeyPagerWindowSnap,
            Main.KeyEventGroupEditing,
            Main.KeyWriteGroupConfig,
            "addGroup"
        };

        /// <summary>
        /// 「窗口放大倍率」那一行的无条件置灰/恢复：把 <c>PropertyControl.UpdateEnabled()</c> 对所有行重跑一遍。
        ///
        /// 正常路径**不需要**调用它 —— 原版就有两处会自己重算（IL 已核，见
        /// <c>工作记录</c> 一节）：
        ///  · <c>PropertyControl_Bool.SetValue</c> → <c>ToggleOthersEnabled</c>（用户点开关时）；
        ///  · <c>PropertyControl_Text.&lt;Setup&gt;b__17_1</c> → <c>ToggleOthersEnabled</c>（文本框结束编辑时）；
        ///  · <c>PropertiesPanel.SetProperties</c> 循环结束后也会调一次 <c>ToggleOthersEnabled</c>；
        /// 而 <c>ToggleOthersEnabled</c> 内部对每一行调 <c>UpdateEnabled</c> ⇒ <c>PropertyInfo.CheckIfEnabled</c>
        /// ⇒ <c>PropertyControl.SetEnabled</c>（Graphic 变灰 + 所有 Selectable.interactable = false）。
        ///
        /// 这里是兜底：万一某条原版刷新路径没跑到（例如游戏改版删掉了上面某次调用），
        /// 也能保证"Auto 关 ⇒ 倍率行真的灰掉且点不动"，不需要我们自己写置灰逻辑。
        /// 取不到控件时静默返回（面板没打开 / 已经重建过）。
        /// </summary>
        internal static void ReapplyWindowRowEnabled()
        {
            try
            {
                if (Main.Aee == null || scnEditor.instance == null || scnEditor.instance.settingsPanel == null)
                    return;
                // 类型名写全：本文件里有 ADOFAIEditorExtension.PropertyCollection.Property，
                // 一旦 using ADOFAI 就会和原版的 ADOFAI.Property 撞名（CS0104）
                List<ADOFAI.PropertiesPanel> panels = scnEditor.instance.settingsPanel.panelsList;
                if (panels == null)
                    return;
                ADOFAI.PropertiesPanel panel = panels.Find(p => p != null && p.name == Main.Aee.name);
                if (panel == null || panel.properties == null)
                    return;
                foreach (KeyValuePair<string, ADOFAI.Property> pair in panel.properties)
                {
                    if (pair.Value == null || pair.Value.control == null)
                        continue;
                    try { pair.Value.control.UpdateEnabled(); }
                    catch { }
                }
            }
            catch (Exception e)
            {
                Main.Logger?.Log("重算设置行启用状态失败: " + e.Message);
            }
        }

        /// <summary>第 index 行的三个字段名（0 基）—— 装饰那一套。</summary>
        public static string NameKey(int index) => DecoGroupState.NameKey(DecoGroupState.GroupSet.Decoration, index);
        public static string TagKey(int index) => DecoGroupState.TagKey(DecoGroupState.GroupSet.Decoration, index);
        public static string DeleteKey(int index) => DecoGroupState.DeleteKey(DecoGroupState.GroupSet.Decoration, index);
        public static string ColorKey(int index) => DecoGroupState.ColorKey(DecoGroupState.GroupSet.Decoration, index);

        /// <summary>第 index 行的三个字段名（0 基）—— 事件那一套（§17.2）。</summary>
        internal static string EventNameKey(int index) => DecoGroupState.NameKey(DecoGroupState.GroupSet.Event, index);
        internal static string EventTagKey(int index) => DecoGroupState.TagKey(DecoGroupState.GroupSet.Event, index);
        internal static string EventDeleteKey(int index) => DecoGroupState.DeleteKey(DecoGroupState.GroupSet.Event, index);
        internal static string EventColorKey(int index) => DecoGroupState.ColorKey(DecoGroupState.GroupSet.Event, index);

        /// <summary>第 index 行（0 基）的本地化键，带行号，由 <see cref="RegisterRowLocalizations"/> 生成。</summary>
        public static string NameLabelKey(int index) => "aee.group.name." + (index + 1);
        public static string TagLabelKey(int index) => "aee.group.tag." + (index + 1);
        public static string ColorLabelKey(int index) => "aee.group.color." + (index + 1);
        public static string DeleteLabelKey(int index) => "aee.group.delete." + (index + 1);

        /// <summary>把带行号的本地化键预先写进本地化表（三种语言），供标签与按钮文本共用。</summary>
        internal static void RegisterRowLocalizations(Localization localizations)
        {
            if (localizations == null)
                return;
            for (int i = 0; i < DecoGroupState.MaxCustomGroups; i++)
            {
                localizations.AddDerived(NameLabelKey(i), "aee.group.name", i + 1);
                localizations.AddDerived(TagLabelKey(i), "aee.group.tag", i + 1);
                localizations.AddDerived(ColorLabelKey(i), "aee.group.color", i + 1);
                localizations.AddDerived(DeleteLabelKey(i), "aee.group.delete", i + 1);
            }
        }

        public static List<Property> Properties { get; } = Build();

        private static List<Property> Build()
        {
            var properties = new List<Property>
            {
                new Property_Bool(
                    name: Main.KeyDecoGroupingEnabled,
                    value_default: true,
                    key: "aee.decoGroupingEnabled"
                ),
                new Property_Enum<AutoGroupMode>(
                    name: Main.KeyAutoGroupMode,
                    value_default: AutoGroupMode.ByType,
                    key: "aee.autoGroupMode"
                ),
                new Property_Bool(
                    name: Main.KeyShowGroupCounts,
                    value_default: true,
                    key: "aee.showGroupCounts"
                ),
                new Property_Bool(
                    name: Main.KeyPagerListEnabled,
                    value_default: true,
                    key: "aee.pagerListEnabled"
                ),
                // —— 直选弹窗的窗口行为（三条）。位置：紧跟「分页器直选列表」，在「编辑目标」之前 ——
                new Property_Bool(
                    name: Main.KeyPagerAutoWindowSize,
                    value_default: true,                    // 默认开：保持弹窗一直以来的"按行数自动定高"
                    key: "aee.pagerAutoWindowSize"
                ),
                // 倍率：**数值输入框**（Property_InputField + InputType.Float ⇒ 原版 PropertyControl_Text，
                // 也就是和原版其它 Float 字段一样的输入框），min/max 交给原版 PropertyInfo.Validate(float)
                // 与 PropertyControl.ValidateInput 在输入时夹取；data 里另外存一份 default 供面板回填。
                //
                // 置灰走**原版 enableIf 机制**（IL 已核，v2/v3 都支持）：
                //   PropertyInfo.ctor 把 data["enableIf"] 这个扁平列表 ["pagerAutoWindowSize", "true"]
                //   经 RDEditorUtils.DecodeStringArray + Tuple 建成 enableIfVals；
                //   PropertyControl.ToggleOthersEnabled → UpdateEnabled → CheckIfEnabled → ValueMatch
                //   会在「Auto 开关被点」与「面板 SetProperties」时重算，然后 SetEnabled(false, true)
                //   把这一行所有 Graphic 变灰（Color.gray）、所有 Selectable.interactable = false
                //   ⇒ 既不能点也不能输入。我们不需要自己写置灰代码。
                new Property_InputField(
                    name: Main.KeyPagerWindowScale,
                    type: Property_InputField.InputType.Float,
                    value_default: 1f,                      // 默认 1.0
                    min: Main.PagerWindowScaleMin,          // 0.5
                    max: Main.PagerWindowScaleMax,          // 2.5
                    key: "aee.pagerWindowScale",
                    enableIf: new Dictionary<string, string> { { Main.KeyPagerAutoWindowSize, "true" } }
                ),
                // 吸附：作用于**拖动标题移动窗口**，吸到屏幕四边/四角，不改变窗口大小。
                // 它和上面的「自动调节窗口」互不干扰 —— 自动尺寸只锁"拖边缘缩放"，两种模式下都能拖标题，
                // 所以这个开关在自动尺寸开或关时都有效（不要写成"只在关闭自动尺寸后才生效"）。
                new Property_Bool(
                    name: Main.KeyPagerWindowSnap,
                    value_default: false,                   // 默认关：不改变现有拖动手感
                    key: "aee.pagerWindowSnap"
                ),
                // 「编辑目标」（装饰分组 / 事件分组）：**两成员枚举**，原版就会渲染成并排两个按钮
                // （与 MultiTrackHelper 的 affectAt 同款），而不是下拉框（§17.4）。
                new Property_Enum<GroupEditTarget>(
                    name: Main.KeyEventGroupEditing,
                    value_default: GroupEditTarget.Decoration,
                    key: "aee.eventGroupEditing"
                ),
                new Property_Bool(
                    name: Main.KeyWriteGroupConfig,
                    value_default: false,
                    key: "aee.writeGroupConfig"
                ),
                new Property_Button(
                    name: "addGroup",
                    action: () => RunGroupAction(() => DecoGroupState.AddCustomGroup(DecoGroupState.EditingSet)),
                    key: "aee.group.add"
                )
            };

            for (int i = 0; i < DecoGroupState.MaxCustomGroups; i++)
            {
                int index = i;
                // 每行用带行号的本地化键（RegisterRowLocalizations 生成），标签与按钮文本都靠它保证不会露出 {0}
                properties.Add(new Property_InputField(
                    name: DecoGroupState.NameKey(DecoGroupState.GroupSet.Decoration, index),
                    type: Property_InputField.InputType.String,
                    value_default: "",
                    key: NameLabelKey(index)
                ));
                properties.Add(new Property_InputField(
                    name: DecoGroupState.TagKey(DecoGroupState.GroupSet.Decoration, index),
                    type: Property_InputField.InputType.String,
                    value_default: "",
                    key: TagLabelKey(index)
                ));
                // 组头颜色：原版颜色控件（点开就是原版取色器，含 RGBA）；默认纯白 + alpha 0 = 不着色。
                // 只有**用户自定义分组**（custom:i）的组头会用到它，自动分组与「未分组」在渲染层被过滤掉（需求 3）。
                properties.Add(new Property_Color(
                    name: DecoGroupState.ColorKey(DecoGroupState.GroupSet.Decoration, index),
                    value_default: DecoGroupState.DefaultGroupColor,
                    usesAlpha: true,
                    key: ColorLabelKey(index)
                ));
                properties.Add(new Property_Button(
                    name: DecoGroupState.DeleteKey(DecoGroupState.GroupSet.Decoration, index),
                    action: () => RunGroupAction(() => DecoGroupState.DeleteCustomGroup(DecoGroupState.GroupSet.Decoration, index)),
                    key: DeleteLabelKey(index)
                ));
            }

            // 事件那一套用同样的行结构，只是字段名换成 eventGroup*（面板一次全建好，可见性按 §17.2 切换）
            for (int i = 0; i < DecoGroupState.MaxCustomGroups; i++)
            {
                int index = i;
                properties.Add(new Property_InputField(
                    name: EventNameKey(index),
                    type: Property_InputField.InputType.String,
                    value_default: "",
                    key: NameLabelKey(index)
                ));
                properties.Add(new Property_InputField(
                    name: EventTagKey(index),
                    type: Property_InputField.InputType.String,
                    value_default: "",
                    key: TagLabelKey(index)
                ));
                properties.Add(new Property_Color(
                    name: EventColorKey(index),
                    value_default: DecoGroupState.DefaultGroupColor,
                    usesAlpha: true,
                    key: ColorLabelKey(index)
                ));
                properties.Add(new Property_Button(
                    name: EventDeleteKey(index),
                    action: () => RunGroupAction(() => DecoGroupState.DeleteCustomGroup(DecoGroupState.GroupSet.Event, index)),
                    key: DeleteLabelKey(index)
                ));
            }

            return properties;
        }

        /// <summary>当前应显示的字段名列表（含分组行；分组行取"正在编辑的那一套"，见 §17.2）。</summary>
        internal static List<string> BuildActiveList(DecoGroupState.GroupSet set)
        {
            var list = new List<string>(BaseToActive);
            int count = Math.Min(Math.Max(DecoGroupState.CountOf(set), 0), DecoGroupState.MaxCustomGroups);
            for (int i = 0; i < count; i++)
            {
                list.Add(DecoGroupState.NameKey(set, i));
                list.Add(DecoGroupState.TagKey(set, i));
                list.Add(DecoGroupState.ColorKey(set, i));
                list.Add(DecoGroupState.DeleteKey(set, i));
            }
            return list;
        }

        /// <summary>兼容旧签名：等价于装饰那一套。</summary>
        public static List<string> BuildActiveList(int customGroupCount)
        {
            var list = new List<string>(BaseToActive);
            int count = Math.Min(Math.Max(customGroupCount, 0), DecoGroupState.MaxCustomGroups);
            for (int i = 0; i < count; i++)
            {
                list.Add(DecoGroupState.NameKey(DecoGroupState.GroupSet.Decoration, i));
                list.Add(DecoGroupState.TagKey(DecoGroupState.GroupSet.Decoration, i));
                list.Add(DecoGroupState.ColorKey(DecoGroupState.GroupSet.Decoration, i));
                list.Add(DecoGroupState.DeleteKey(DecoGroupState.GroupSet.Decoration, i));
            }
            return list;
        }

        /// <summary>
        /// 面板按钮（添加 / 删除分组）的统一入口：模组已禁用（编辑器还没重启、按钮还残留在面板上）时什么都不做；
        /// 否则执行后把行数变化落盘（Save 幂等，内容没变不写）。按钮回调由原版 UI 触发，异常不许漏出去。
        /// </summary>
        private static void RunGroupAction(Action action)
        {
            if (!Main.IsEnabled)
            {
                Main.Logger?.Log("模组处于禁用状态，忽略");
                return;
            }
            try
            {
                action();
            }
            catch (Exception e)
            {
                Main.Logger?.Log("分组按钮操作失败: " + e);
            }
            Settings.SettingsStore.Save();
        }

        /// <summary>
        /// 分组行的标签带行号，无法用固定本地化键表达，构建 PropertyInfo 时用 customLabel 填字面文本。
        /// 非分组行返回 null。
        /// </summary>
        public static string GetRowLabel(string propertyName)
        {
            if (propertyName == null)
                return null;

            string templateKey;
            string suffix;
            if (propertyName.StartsWith("eventGroupName", StringComparison.Ordinal))
            {
                templateKey = "aee.group.name";
                suffix = propertyName.Substring("eventGroupName".Length);
            }
            else if (propertyName.StartsWith("eventGroupTag", StringComparison.Ordinal))
            {
                templateKey = "aee.group.tag";
                suffix = propertyName.Substring("eventGroupTag".Length);
            }
            else if (propertyName.StartsWith("eventGroupColor", StringComparison.Ordinal))
            {
                templateKey = "aee.group.color";
                suffix = propertyName.Substring("eventGroupColor".Length);
            }
            else if (propertyName.StartsWith("eventDeleteGroup", StringComparison.Ordinal))
            {
                templateKey = "aee.group.delete";
                suffix = propertyName.Substring("eventDeleteGroup".Length);
            }
            else if (propertyName.StartsWith("groupName", StringComparison.Ordinal))
            {
                templateKey = "aee.group.name";
                suffix = propertyName.Substring("groupName".Length);
            }
            else if (propertyName.StartsWith("groupTag", StringComparison.Ordinal))
            {
                templateKey = "aee.group.tag";
                suffix = propertyName.Substring("groupTag".Length);
            }
            else if (propertyName.StartsWith("groupColor", StringComparison.Ordinal))
            {
                templateKey = "aee.group.color";
                suffix = propertyName.Substring("groupColor".Length);
            }
            else if (propertyName.StartsWith("deleteGroup", StringComparison.Ordinal))
            {
                templateKey = "aee.group.delete";
                suffix = propertyName.Substring("deleteGroup".Length);
            }
            else
            {
                return null;
            }

            if (!int.TryParse(suffix, out int index))
                return null;

            string template = L(templateKey);
            if (string.IsNullOrEmpty(template))
                return null;
            // 用 Replace 而不是 string.Format：模板里如果出现非法花括号也不会抛错、更不会原样露出 {0}
            return template.Replace("{0}", (index + 1).ToString());
        }

        private static string L(string key) => Main.Localizations?.GetValue(key) ?? key;
    }
}
