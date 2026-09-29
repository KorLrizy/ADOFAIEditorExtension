using ADOFAIEditorExtension.Features.DecoGrouping;
using ADOFAIEditorExtension.PropertyCollection;
using System;
using System.Collections.Generic;

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
            Main.KeyEventGroupEditing,
            Main.KeyWriteGroupConfig,
            "addGroup"
        };

        /// <summary>第 index 行的三个字段名（0 基）—— 装饰那一套。</summary>
        public static string NameKey(int index) => DecoGroupState.NameKey(DecoGroupState.GroupSet.Decoration, index);
        public static string TagKey(int index) => DecoGroupState.TagKey(DecoGroupState.GroupSet.Decoration, index);
        public static string DeleteKey(int index) => DecoGroupState.DeleteKey(DecoGroupState.GroupSet.Decoration, index);

        /// <summary>第 index 行的三个字段名（0 基）—— 事件那一套（§17.2）。</summary>
        internal static string EventNameKey(int index) => DecoGroupState.NameKey(DecoGroupState.GroupSet.Event, index);
        internal static string EventTagKey(int index) => DecoGroupState.TagKey(DecoGroupState.GroupSet.Event, index);
        internal static string EventDeleteKey(int index) => DecoGroupState.DeleteKey(DecoGroupState.GroupSet.Event, index);

        /// <summary>第 index 行（0 基）的本地化键，带行号，由 <see cref="RegisterRowLocalizations"/> 生成。</summary>
        public static string NameLabelKey(int index) => "aee.group.name." + (index + 1);
        public static string TagLabelKey(int index) => "aee.group.tag." + (index + 1);
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
                return;
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
