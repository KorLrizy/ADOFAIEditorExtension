using ADOFAI;
using ADOFAIEditorExtension.Features.DecoGrouping;
using ADOFAIEditorExtension.Features.PagerList;
using ADOFAIEditorExtension.Utils;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Events;

namespace ADOFAIEditorExtension.Patches
{
    /// <summary>
    /// 把本模组标签页的本地化文本接入游戏文本系统。
    /// 游戏给枚举等生成的 key 会带上程序集限定名（Enum:..., ADOFAIEditorExtension, Version=..., Culture=..., PublicKeyToken=...），
    /// 这里先剥掉限定名再查我们自己的 Localizations.json。
    /// </summary>
    [HarmonyPatch(typeof(RDString), "GetWithCheck")]
    internal static class GetWithCheckPatch
    {
        internal static void Postfix(ref string __result, ref string key, ref bool exists, ref Dictionary<string, object> parameters)
        {
            if (string.IsNullOrEmpty(key))
                return;

            if (key.StartsWith("enum.", StringComparison.Ordinal)
                && key.IndexOf("enum.ADOFAIEditorExtension", StringComparison.Ordinal) >= 0
                && key.IndexOf("Version", StringComparison.Ordinal) >= 0
                && key.IndexOf("Culture", StringComparison.Ordinal) >= 0
                && key.IndexOf("PublicKeyToken", StringComparison.Ordinal) >= 0)
            {
                int commaIndex = key.IndexOf(',');
                int lastDotIndex = key.LastIndexOf('.');
                if (commaIndex > 0 && lastDotIndex > commaIndex)
                    key = key.Substring(0, commaIndex) + "." + key.Substring(lastDotIndex + 1);
            }

            // 分组行的 tag 输入框：原版会按 "editor.<事件名>.<字段名>.placeholder" 取占位符，
            // 这里把带行号的键统一映射到 aee.group.tagPlaceholder，就地提示"留空 = 无 tag 装饰"。
            // 装饰那套字段名是 groupTagN、事件那套是 eventGroupTagN（大写 G），两种都要认。
            if (key.EndsWith(".placeholder", StringComparison.Ordinal)
                && (key.IndexOf(".groupTag", StringComparison.Ordinal) >= 0 || key.IndexOf(".eventGroupTag", StringComparison.Ordinal) >= 0)
                && Main.Localizations != null)
            {
                string hint = Main.Localizations.GetValue("aee.group.tagPlaceholder");
                if (!string.IsNullOrEmpty(hint))
                {
                    exists = true;
                    __result = hint;
                    return;
                }
            }

            if (Main.Localizations != null && Main.Localizations.Get(key, out string value, parameters))
            {
                exists = true;
                __result = value;
            }
        }
    }

    /// <summary>让关卡文件里的 "ADOFAIEditorExtension" / "812" 能解析回 (LevelEventType)812。</summary>
    [HarmonyPatch]
    internal static class RDUtilsParseEnumPatch
    {
        internal static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(RDUtils), "ParseEnum", null, null).MakeGenericMethod(new Type[]
            {
                typeof(LevelEventType)
            });
        }

        internal static bool Prefix(string str, ref LevelEventType defaultValue, ref LevelEventType __result)
        {
            if (str == Main.ModEventName || str == Main.ModEventType.ToString())
            {
                __result = (LevelEventType)Main.ModEventType;
                return false;
            }

            return true;
        }
    }

    /// <summary>让编辑器把 812 当作一个“设置型”事件类型对待。</summary>
    [HarmonyPatch(typeof(EditorConstants), "IsSetting")]
    internal static class EditorConstantsIsSettingPatch
    {
        internal static void Postfix(LevelEventType type, ref bool __result)
        {
            if ((int)type == Main.ModEventType)
                __result = true;
        }
    }

    /// <summary>
    /// 本模组类型始终拿到一个空表、不走原版。
    /// 原版（IL）：GetSelectedFloorEvents(t) = GetFloorEvents(selectedFloors[0].seqID, t)，
    /// 而 GetFloorEvents 对 IsSetting(t) 直接返回 null（812 被上面的补丁算作设置型）；
    /// 没有选砖时 selectedFloors[0] 还会越界抛异常。设置型类型本来就没有"砖上的事件"，空表即可。
    /// </summary>
    [HarmonyPatch(typeof(scnEditor), "GetSelectedFloorEvents")]
    internal static class scnEditorGetSelectedFloorEventsPatch
    {
        internal static bool Prefix(LevelEventType eventType, ref List<LevelEvent> __result)
        {
            if ((int)eventType != Main.ModEventType)
                return true;
            __result = new List<LevelEvent>();
            return false;
        }
    }

    /// <summary>标签页选中/取消时的回调：进入时刷新可见行，离开时按最新设置重建装饰列表。</summary>
    [HarmonyPatch(typeof(InspectorTab), "SetSelected")]
    internal static class InspectorTabSetSelectedPatch
    {
        internal static void Postfix(InspectorTab __instance, bool selected)
        {
            if ((int)__instance.levelEventType != Main.ModEventType)
                return;

            try
            {
                Main.activeChilden();
                if (!selected)
                    DecoGroupRenderer.RefreshList();
            }
            catch (Exception e)
            {
                Main.Logger?.Log("标签页选中回调异常: " + e);
            }
        }
    }

    /// <summary>
    /// 注入 / 移除本模组在游戏静态表里的“设置型关卡事件”，以及标签页与面板的兜底创建。
    /// </summary>
    internal static class EditorIntegration
    {
        /// <summary>
        /// 注入标签页/事件类型/图标。正常情况下由 scnEditor.Awake Prefix 调用（赶在游戏布局前）；
        /// 若那次因为面板未就绪而跳过，scnEditor.Start Postfix 会再补一次。有“已存在则跳过”的去重保护。
        /// </summary>
        internal static void Inject()
        {
            if (GCS.levelEventsInfo == null || Main.Aee == null)
                return;
            if (GCS.levelEventIcons == null)
            {
                GCS.levelEventIcons = new Dictionary<LevelEventType, Sprite>();
                foreach (object obj in Enum.GetValues(typeof(LevelEventType)))
                {
                    Sprite sprite = Resources.Load<Sprite>("LevelEditor/LevelEvents/" + obj.ToString());
                    if (sprite != null)
                    {
                        GCS.levelEventIcons.Add((LevelEventType)obj, sprite);
                    }
                }
            }

            LevelEventInfo levelEventInfo = new LevelEventInfo
            {
                categories = new List<LevelEventCategory>(),
                executionTime = LevelEventExecutionTime.Special,
                name = Main.Aee.name,
                propertiesInfo = new Dictionary<string, ADOFAI.PropertyInfo>(),
                type = (LevelEventType)Main.Aee.type
            };
            if (Main.Aee.properties != null)
            {
                foreach (Dictionary<string, object> dictionary in Main.Aee.properties)
                {
                    ADOFAI.PropertyInfo propertyInfo = new ADOFAI.PropertyInfo(dictionary, levelEventInfo);
                    if (dictionary.TryGetValue("type", out object typeObj) && typeObj is string typeStr && typeStr == "Export"
                        && dictionary.TryGetValue("default", out object actionObj) && actionObj is UnityAction action)
                        propertyInfo.value_default = action;
                    // 分组行的标签带行号，无法用固定本地化键表达，构建时按当前语言求值
                    string customLabel = PrefabProperties.GetRowLabel(propertyInfo.name);
                    if (customLabel != null)
                        propertyInfo.customLabel = customLabel;
                    propertyInfo.order = 0;
                    levelEventInfo.propertiesInfo.Add(propertyInfo.name, propertyInfo);
                }
            }

            Main.Aee.icon = Main.ResolveTabIcon();
            GCS.levelEventTypeString[(LevelEventType)Main.Aee.type] = Main.Aee.name;
            GCS.levelEventIcons[(LevelEventType)Main.Aee.type] = Main.Aee.icon;
            GCS.settingsInfo[Main.Aee.name] = levelEventInfo;

            // 分组归属键**始终**注册成 invisible 属性（装饰类型 aeeGroupDeco / 砖上事件类型 aeeGroupEvent，§18.2）：
            // 原版 Encode/Decode 只认注册过的键，不注册的话写在 data 里的键既存不下去也读不回来。
            // "写入关卡文件"开关只决定保存时写不写（保存前置补丁按开关剥离），不再决定注册与否。
            Features.DecoGrouping.DecoGroupState.EnsureInvisibleProperties();

            // 事件备注（§21）：这个键**始终**注册且**要显示**（原版面板自动多出一行输入框），
            // 与上面的归属键相反，不挂"写入关卡文件"开关。
            Features.Notes.EventNote.EnsureRegistered();

            // 只需注册到 GCS：scnEditor.Awake → LoadEditorProperties → InspectorPanel.Init(GCS.settingsInfo)
            // 会遍历该字典，自动为我们实例化面板与标签页，并做好 SetParent / 定位 / 加入 panelsList。
            // 我们在 Awake Prefix 注册，正好赶在它之前。
        }

        /// <summary>
        /// 兜底：若游戏没有替我们建出标签页（例如注册晚了），手动创建一份。
        /// </summary>
        internal static void EnsureUiExists()
        {
            InspectorPanel settingsPanel = scnEditor.instance != null ? scnEditor.instance.settingsPanel : null;
            if (settingsPanel == null || Main.Aee == null)
                return;
            if (!GCS.settingsInfo.ContainsKey(Main.Aee.name))
                return;

            for (int i = 0; i < settingsPanel.tabs.childCount; i++)
            {
                InspectorTab existing = settingsPanel.tabs.GetChild(i).GetComponent<InspectorTab>();
                if (existing != null && (int)existing.levelEventType == Main.Aee.type)
                {
                    Main.SettingsUiInjected = true;
                    return;
                }
            }

            GameObject gameObject = UnityEngine.Object.Instantiate(RDConstants.data.prefab_propertiesPanel);
            // 必须挂到面板容器下（与 InspectorPanel.Init 一致），否则根对象会被拉伸成整屏
            gameObject.transform.SetParent(settingsPanel.panels, false);
            gameObject.name = Main.Aee.name;
            PropertiesPanel component = gameObject.GetComponent<PropertiesPanel>();
            component.levelEventType = (LevelEventType)Main.Aee.type;
            component.gameObject.SetActive(false);
            GameObject gameObject2 = UnityEngine.Object.Instantiate(RDConstants.data.prefab_tab);
            InspectorTab component2 = gameObject2.GetComponent<InspectorTab>();
            component.Init(settingsPanel, GCS.settingsInfo[Main.Aee.name]);
            component2.Init((LevelEventType)Main.Aee.type, settingsPanel);
            component2.SetSelected(false);

            component2.GetComponent<RectTransform>().AnchorPosY(8f - 68f * settingsPanel.tabs.childCount);
            gameObject2.transform.SetParent(settingsPanel.tabs, false);

            Main.SettingsUiInjected = true;
        }

        /// <summary>
        /// 关闭模组时移除注入到 GCS 的静态数据。这些字典跨场景重载仍然存在，
        /// 不清掉的话即使编辑器重启，我们的“关卡事件类型/标题/图标”仍然残留。
        /// levelEventIcons 只摘我们自己的类型；若该表是 Inject() 新建的，留着也无妨（里面都是原版图标）。
        ///
        /// 注册进各事件类型的属性（事件备注 aeeNote、分组归属键）**不摘**，只把 aeeNote 标成 invisible
        /// （原版 PropertyInfo.CheckIfShown / PropertyControl.SetShown 认这个标记 ⇒ 面板不再显示“备注”行）：
        ///  - 原版 Encode 只写注册过的键、Decode 只读注册过的键 ⇒ 摘掉注册后，禁用状态下保存/读档会把
        ///    关卡里的备注与归属**永久抹掉**；
        ///  - 原版多选 InspectorPanel.ShowPanel 对 data 的每个键做 propertiesInfo[key] ⇒ 事件 data 里还带着
        ///    这些键（例如用户取消了"未保存"提示、编辑器没重启）时，摘掉注册会直接抛 KeyNotFound。
        /// 重新启用时 EventNote.EnsureRegistered 会把 invisible 复位。
        /// </summary>
        internal static void RemoveInjection()
        {
            if (Main.Aee == null)
                return;
            GCS.settingsInfo?.Remove(Main.Aee.name);
            GCS.levelEventTypeString?.Remove((LevelEventType)Main.Aee.type);
            GCS.levelEventIcons?.Remove((LevelEventType)Main.Aee.type);
            Features.Notes.EventNote.SetHidden(true);
        }
    }

    /// <summary>
    /// 编辑器 Awake：关卡（重新）加载，清掉分组功能的静态状态后注入事件类型。
    /// 注意用 Postfix 之外的方式不行——scnEditor.instance 是在 Awake 函数体里才赋值的，
    /// 而注入必须在游戏自己的布局流程之前完成，所以用 Prefix。
    /// </summary>
    [HarmonyPatch(typeof(scnEditor), "Awake")]
    internal static class scnEditorAwakePatch
    {
        internal static void Prefix()
        {
            Main.SettingsUiInjected = false;
            DecoGroupRenderer.ResetStaticState();
            DecoGroupModeButton.Reset();
            PagerListController.Reset();
            Patches.ValueChangePatch2.Reset();
            EditorIntegration.Inject();
        }
    }

    /// <summary>兜底：正常由游戏 InspectorPanel.Init 依 GCS.settingsInfo 建出面板/标签页。</summary>
    [HarmonyPatch(typeof(scnEditor), "Start")]
    internal static class scnEditorStartPatch
    {
        internal static void Postfix()
        {
            if (!Main.SettingsUiInjected)
            {
                // 兜底：Awake Prefix 注入时 GCS 表可能还没建好，这里再补一次注入与手动建 UI
                if (GCS.settingsInfo == null || Main.Aee == null || !GCS.settingsInfo.ContainsKey(Main.Aee.name))
                    EditorIntegration.Inject();
                EditorIntegration.EnsureUiExists();
            }
            // 事件备注属性：幂等，这里再兜一次（万一 Awake 那次 GCS.levelEventsInfo 还没建好）
            Features.Notes.EventNote.EnsureRegistered();
            Popup.EnsureCaptured();
        }
    }

    /// <summary>面板切换到 812 时接管 InspectorPanel.ShowPanel，维护我们自己的 LevelEvent 数据。</summary>
    [HarmonyPatch(typeof(InspectorPanel), "ShowPanel")]
    internal static class InspectorPanelShowPanelPatch
    {
        /// <summary>InspectorPanel.showingPanel 是 private bool，缓存一次 FieldInfo。</summary>
        private static readonly FieldInfo ShowingPanelField = AccessTools.Field(typeof(InspectorPanel), "showingPanel");

        internal static bool Prefix(InspectorPanel __instance, LevelEventType eventType, int eventIndex = 0)
        {
            if ((int)eventType != Main.ModEventType)
                return true;

            LevelEvent levelEvent = Main.GetSettingsEvent();
            if (levelEvent == null)
                return true;

            FieldInfo showingPanelField = ShowingPanelField;
            showingPanelField?.SetValue(__instance, true);
            scnEditor editor = scnEditor.instance;
            // 与原版 ShowPanel 一致：SaveStateScope(editor, false, false, false) —— 只压 changingState、不调 SaveState。
            // 之前照抄 MultiTrackHelper 的 editor.SaveState(true, false) 会把 unsavedChanges 置真（IL：SaveState 里
            // `if (save) unsavedChanges = true`），于是“只是打开模组设置标签页”就让关卡标题挂上了未保存星号。
            SaveStateScope scope = editor != null ? new SaveStateScope(editor, false, false, false) : null;
            try
            {
                PropertiesPanel propertiesPanel = null;
                foreach (PropertiesPanel panel in __instance.panelsList)
                {
                    if (panel.levelEventType == eventType)
                    {
                        propertiesPanel = panel;
                        panel.gameObject.SetActive(true);
                    }
                    else
                    {
                        panel.gameObject.SetActive(false);
                    }
                }

                __instance.title.text = Main.Localizations?.GetValue("aee.title") ?? Main.ModEventName;
                Main.AeeLevelEvent = levelEvent;
                __instance.selectedEvent = levelEvent;
                __instance.selectedEventType = levelEvent.eventType;
                if (propertiesPanel != null)
                {
                    propertiesPanel.SetProperties(levelEvent, true);
                    levelEvent.UpdatePanel();
                }

                IEnumerator enumerator = __instance.tabs.GetEnumerator();
                while (enumerator.MoveNext())
                {
                    RectTransform rect = (RectTransform)enumerator.Current;
                    InspectorTab component = rect.gameObject.GetComponent<InspectorTab>();
                    component?.SetSelected(eventType == component.levelEventType);
                }
            }
            finally
            {
                scope?.Dispose();
                showingPanelField?.SetValue(__instance, false);
            }

            return false;
        }

        internal static void Postfix(InspectorPanel __instance, LevelEventType eventType, int eventIndex = 0)
        {
            if ((int)eventType != Main.ModEventType)
                return;
            try
            {
                Main.activeChilden();
            }
            catch (Exception e)
            {
                Main.Logger?.Log("ShowPanel 后置补丁异常: " + e);
            }
        }
    }
}
