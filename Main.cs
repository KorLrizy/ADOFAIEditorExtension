using ADOFAI;
using ADOFAI.LevelEditor.Controls;
using ADOFAIEditorExtension.Features.DecoGrouping;
using ADOFAIEditorExtension.Features.PagerList;
using ADOFAIEditorExtension.Utils;
using DG.Tweening;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityModManagerNet;

namespace ADOFAIEditorExtension
{
    /// <summary>
    /// The main class for the mod. Call other parts of your code from this
    /// class.
    /// </summary>
    public static class Main
    {
        /// <summary>装饰栏分组在界面上的四种状态（"不分组"对应设置里的 decoGroupingEnabled=false）。</summary>
        internal enum GroupingMode
        {
            Off,
            ByType,
            ByTag,
            Custom
        }

        /// <summary>本模组伪装成的关卡事件类型 ID（避开游戏 0..65、MappingHelper 801/802、MultiTrackHelper 810）。</summary>
        internal const int ModEventType = 812;

        /// <summary>事件类型名，同时也是 GCS.settingsInfo 的键。</summary>
        internal const string ModEventName = "ADOFAIEditorExtension";

        internal const string KeyDecoGroupingEnabled = "decoGroupingEnabled";
        internal const string KeyAutoGroupMode = "autoGroupMode";
        internal const string KeyShowGroupCounts = "showGroupCounts";
        internal const string KeyPagerListEnabled = "pagerListEnabled";
        internal const string KeyEventGroupEditing = "eventGroupEditing";   // 面板正在编辑哪一套自定义分组（GroupEditTarget：Decoration / Event）
        internal const string KeyWriteGroupConfig = "writeGroupConfig";     // 是否把分组归属写进关卡文件（默认关）

        /// <summary>标签页的 LevelEvent（字段值都存这里）。</summary>
        internal static LevelEvent AeeLevelEvent { get; set; }

        internal static CustomTab Aee { get; set; }

        internal static Localization Localizations { get; set; }

        /// <summary>当前面板应显示的字段名列表。</summary>
        internal static List<string> propertiesToActive { get; set; }

        /// <summary>标签页/面板是否已注入（每次编辑器 Awake 前重置，Start 兜底用）。</summary>
        internal static bool SettingsUiInjected;

        /// <summary>
        /// Whether the mod is enabled.
        /// </summary>
        public static bool IsEnabled { get; private set; }

        /// <summary>
        /// UMM's logger instance.
        /// </summary>
        public static UnityModManager.ModEntry.ModLogger Logger { get; private set; }

        private static Harmony harmony;

        public static UnityModManager.ModEntry ModEntry { get; private set; }

        /// <summary>
        /// Perform any initial setup with the mod here.
        /// </summary>
        /// <param name="modEntry">UMM's mod entry for the mod.</param>
        internal static void Setup(UnityModManager.ModEntry modEntry)
        {
            Logger = modEntry.Logger;
            modEntry.OnToggle = OnToggle;
            ModEntry = modEntry;
            Localizations = new Localization(Path.Combine(modEntry.Path, "Localizations.json"));
            PrefabProperties.RegisterRowLocalizations(Localizations);
            propertiesToActive = PrefabProperties.BuildActiveList(0);

            Aee = new CustomTab
            {
                // 图标在注入时从 GCS.levelEventIcons 里取原版图标（AddDecoration），不额外带图片资源
                icon = null,
                type = ModEventType,
                name = ModEventName,
                title = "ADOFAI Editor Extension",
                index = -1,
                properties = PrefabProperties.Properties.Select(property => property.ToData()).ToList(),
                onFocused = () => { },
                onUnFocused = () => { },
                onChange = OnSettingChanged,
                saveSetting = true
            };
        }

        /// <summary>
        /// Handler for toggling the mod on/off.
        /// </summary>
        private static bool OnToggle(UnityModManager.ModEntry modEntry, bool value)
        {
            IsEnabled = value;
            if (value)
            {
                StartMod(modEntry);
                Logger?.Log(Localizations?.GetValue("aee.enabled") ?? "ADOFAI Editor Extension enabled");
            }
            else
            {
                StopMod(modEntry);
                Logger?.Log(Localizations?.GetValue("aee.disabled") ?? "ADOFAI Editor Extension disabled");
            }

            RequestEditorReload();
            return true;
        }

        private static void StartMod(UnityModManager.ModEntry modEntry)
        {
            harmony = new Harmony(modEntry.Info.Id);
            // 逐类打补丁而不是 PatchAll：某个补丁类失败（例如运行时不支持给泛型实例打补丁）时
            // 不会连带其他补丁一起失败。
            foreach (Type type in Assembly.GetExecutingAssembly().GetTypes())
            {
                if (type.GetCustomAttributes(typeof(HarmonyPatch), true).Length == 0)
                    continue;
                try
                {
                    harmony.CreateClassProcessor(type).Patch();
                }
                catch (Exception e)
                {
                    Logger?.Log("补丁 " + type.FullName + " 应用失败: " + e.Message);
                }
            }
        }

        private static void StopMod(UnityModManager.ModEntry modEntry)
        {
            harmony.UnpatchAll(modEntry.Info.Id);
            // 清掉注入到 GCS 的静态数据（跨场景重载仍存在），否则禁用后事件类型/图标仍残留
            Patches.EditorIntegration.RemoveInjection();
            DecoGroupRenderer.ResetStaticState();
            PagerListController.Reset();
        }

        /// <summary>
        /// 在 UMM 里开关 mod 后重启编辑器并重载当前关卡：本模组在 scnEditor.Awake/Start 时注入标签页/事件类型，
        /// 只有重载编辑器才能干净地加上（开启）或去掉（关闭）这些注入。
        /// 仅在关卡编辑器里生效；有未保存改动时走游戏自带的“未保存”提示，避免静默丢失改动。
        /// </summary>
        private static void RequestEditorReload()
        {
            try
            {
                scnEditor editor = ADOBase.editor;
                if (editor == null)
                    return;

                Logger?.Log("mod 开关切换：请求重启编辑器并重载当前关卡");
                editor.CheckUnsavedChanges(() => RestartEditorWithWipe(), false);
            }
            catch (Exception e)
            {
                Logger?.Log("重启编辑器失败: " + e);
            }
        }

        /// <summary>
        /// 重启编辑器并重载当前关卡：先记 <c>scnEditor.levelToOpenOnLoad</c>，再 <c>ADOBase.RestartScene()</c>。
        /// （r148 这里走的是 <c>ADOBase.loader</c> + <c>LoadSceneWithTransition</c> 的黑屏过场；
        ///  r265 删了 <c>ADOBase.loader</c> 与 <c>scrLoader.LoadSceneWithTransition</c>，见 §41 A-4。）
        /// </summary>
        private static void RestartEditorWithWipe()
        {
            try
            {
                string levelPath = ADOBase.levelPath;
                if (!string.IsNullOrEmpty(levelPath))
                    scnEditor.levelToOpenOnLoad = levelPath;

                DOTween.KillAll(false);
                ADOBase.RestartScene();
            }
            catch (Exception e)
            {
                Logger?.Log("重启编辑器失败: " + e);
            }
        }

        // ------------------------------------------------------------------ 标签页设置值

        /// <summary>取（必要时创建）本模组标签页的 LevelEvent。</summary>
        internal static LevelEvent GetSettingsEvent()
        {
            if (AeeLevelEvent != null)
                return AeeLevelEvent;
            LevelEventInfo info = null;
            if (GCS.settingsInfo == null || !GCS.settingsInfo.TryGetValue(ModEventName, out info) || info == null)
                return null;
            AeeLevelEvent = new LevelEvent(0, (LevelEventType)ModEventType, info);
            return AeeLevelEvent;
        }

        /// <summary>装饰栏分组总开关（缺省 true）。</summary>
        internal static bool IsDecoGroupingEnabled => GetBoolSetting(KeyDecoGroupingEnabled, true);

        /// <summary>分页器直选列表开关（缺省 true）。</summary>
        internal static bool IsPagerListEnabled => GetBoolSetting(KeyPagerListEnabled, true);

        /// <summary>设置面板编辑的是"事件自定义分组"那一套（缺省 false = 装饰分组）。见 §17.2。</summary>
        internal static bool EventGroupEditing => EditTarget == GroupEditTarget.Event;

        /// <summary>
        /// 设置页「编辑目标」当前值（缺省装饰分组）。字段是两成员枚举 ⇒ 面板上渲染成"装饰分组 / 事件分组"
        /// 两个并排按钮（§19.2）。旧设置里存的是 bool，这里一并兼容。
        /// </summary>
        internal static GroupEditTarget EditTarget
        {
            get
            {
                LevelEvent settings = GetSettingsEvent();
                if (settings != null)
                {
                    try
                    {
                        if (settings.TryGet<object>(KeyEventGroupEditing, out object raw) && raw != null)
                        {
                            if (raw is GroupEditTarget target)
                                return target;
                            if (raw is bool flag)
                                return flag ? GroupEditTarget.Event : GroupEditTarget.Decoration;
                            if (Enum.TryParse(raw.ToString(), out GroupEditTarget parsed))
                                return parsed;
                        }
                    }
                    catch { }
                }
                return GroupEditTarget.Decoration;
            }
        }

        /// <summary>是否把分组归属（aeeGroupDeco / aeeGroupEvent）写进关卡文件；缺省 **false**（见 §17.3）。</summary>
        internal static bool WriteGroupConfig => GetBoolSetting(KeyWriteGroupConfig, false);

        /// <summary>当前分组状态（列表内按钮与设置页共用同一批字段）。</summary>
        internal static GroupingMode CurrentGroupingMode
        {
            get
            {
                if (!IsDecoGroupingEnabled)
                    return GroupingMode.Off;
                switch (AutoGroupMode)
                {
                    case AutoGroupMode.ByTag:
                        return GroupingMode.ByTag;
                    case AutoGroupMode.Custom:
                        return GroupingMode.Custom;
                    default:
                        return GroupingMode.ByType;
                }
            }
        }

        /// <summary>
        /// 切换分组状态：写回设置字段（与设置页同源）→ 同步拖拽开关 → 重建装饰列表 → 刷新设置页与面板按钮。
        /// </summary>
        internal static void SetGroupingMode(GroupingMode mode)
        {
            LevelEvent settings = GetSettingsEvent();
            if (settings != null)
            {
                try
                {
                    settings[KeyDecoGroupingEnabled] = mode != GroupingMode.Off;
                    if (mode != GroupingMode.Off)
                    {
                        AutoGroupMode auto = mode == GroupingMode.ByTag ? AutoGroupMode.ByTag
                            : mode == GroupingMode.Custom ? AutoGroupMode.Custom
                            : AutoGroupMode.ByType;
                        // 与 PropertyControl_Toggle 的写法一致：存枚举实例（设置页控件也按这个读）
                        settings[KeyAutoGroupMode] = auto;
                    }
                    settings.UpdatePanel();
                }
                catch (Exception e)
                {
                    Logger?.Log("切换分组方式失败: " + e.Message);
                    return;
                }
            }

            DecoGroupRenderer.SyncReorderable(DecoGroupState.Panel);
            if (mode == GroupingMode.Off)
                DecoGroupState.ResetRenderState();
            activeChilden();
            DecoGroupRenderer.RefreshList();
            DecoGroupModeButton.Refresh();
        }

        /// <summary>头行是否显示组内数量（缺省 true）。</summary>
        internal static bool ShowGroupCounts => GetBoolSetting(KeyShowGroupCounts, true);

        /// <summary>自动分组方式（缺省 ByType）。</summary>
        internal static AutoGroupMode AutoGroupMode
        {
            get
            {
                LevelEvent settings = GetSettingsEvent();
                if (settings != null)
                {
                    try
                    {
                        if (settings.TryGet<object>(KeyAutoGroupMode, out object raw) && raw != null)
                        {
                            if (raw is AutoGroupMode mode)
                                return mode;
                            if (Enum.TryParse(raw.ToString(), out AutoGroupMode parsed))
                                return parsed;
                        }
                    }
                    catch { }
                }
                return AutoGroupMode.ByType;
            }
        }

        internal static bool GetBoolSetting(string key, bool fallback)
        {
            LevelEvent settings = GetSettingsEvent();
            if (settings != null)
            {
                try
                {
                    if (settings.TryGet<object>(key, out object raw) && raw != null)
                    {
                        if (raw is bool flag)
                            return flag;
                        if (bool.TryParse(raw.ToString(), out bool parsed))
                            return parsed;
                    }
                }
                catch { }
            }
            return fallback;
        }

        /// <summary>字段值变化回调（由 Patches.PropertyPanelPatches 调用）；返回 false 表示回滚该值。</summary>
        internal static bool OnSettingChanged(LevelEvent levelEvent, string key, object oldValue, object newValue)
        {
            try
            {
                if (key == KeyDecoGroupingEnabled)
                {
                    DecoGroupRenderer.SyncReorderable(DecoGroupState.Panel);
                    if (!IsDecoGroupingEnabled)
                        DecoGroupState.ResetRenderState();
                }
                else if (key == KeyPagerListEnabled && !IsPagerListEnabled)
                {
                    // 关掉开关时把已经打开的直选列表一起收掉
                    PagerListController.CloseIfOpen();
                }
                else if (key == KeyWriteGroupConfig)
                {
                    // 关→开：把会话内的归属落进 data；开→关：搬到会话并剥离开关卡数据（§17.3）
                    DecoGroupState.OnWriteModeChanged();
                }
                // 值变化后重新套用可见性（与 MultiTrackHelper 的 onChange → activeChilden 一致）：
                // 游戏在某些刷新路径里会把属性行重新显示出来，只靠 SetProperties 后置补丁盖不住。
                activeChilden();
                DecoGroupRenderer.RefreshList();
            }
            catch (Exception e)
            {
                Logger?.Log("设置变更后的刷新失败: " + e);
            }
            return true;
        }

        /// <summary>按当前自定义分组行数刷新面板上方的可见行。</summary>
        public static void activeChilden()
        {
            if (scnEditor.instance == null || scnEditor.instance.settingsPanel == null || Aee == null)
                return;

            List<PropertiesPanel> panelsList = scnEditor.instance.settingsPanel.panelsList;
            if (panelsList == null)
                return;
            PropertiesPanel panel = panelsList.Find(p => p.name == Aee.name);
            if (panel == null)
                return;
            Transform content = panel.transform.Find("viewport")?.Find("content");
            if (content == null)
                return;
            // 面板控件还没开始渲染（例如编辑器刚 Awake 时的 SetProperties）：这时没有子对象可套用，
            // 静默返回即可，等控件建好后的 SetProperties/ShowPanel 再套用（早先这里会误报“缺字段”）。
            if (content.childCount == 0)
                return;

            propertiesToActive = PrefabProperties.BuildActiveList(DecoGroupState.EditingSet);

            // 一次性建立 名字→子对象 映射，避免对每个属性做一次 Transform.Find（O(n²)）
            var childMap = new Dictionary<string, Transform>(content.childCount);
            for (int i = 0; i < content.childCount; i++)
            {
                Transform child = content.GetChild(i);
                if (!childMap.ContainsKey(child.name))
                    childMap[child.name] = child;
            }

            for (int i = 0; i < content.childCount; i++)
                content.GetChild(i).gameObject.SetActive(false);

            var missingNames = new List<string>();
            for (int i = 0; i < propertiesToActive.Count; i++)
            {
                if (propertiesToActive[i] == null)
                    continue;
                if (!childMap.TryGetValue(propertiesToActive[i], out Transform child) || child == null)
                {
                    missingNames.Add(propertiesToActive[i]);
                    continue;
                }
                child.gameObject.SetActive(true);
                child.SetSiblingIndex(i);
            }

            // 面板里找不到某个字段（名字对不上）时留个日志，便于定位“行可见性不对”的问题。
            // 这里列出具体缺哪些字段名：只报个数的话还得再去猜是 Properties 少了声明还是行名对不上。
            if (missingNames.Count > 0)
                Logger?.Log(string.Format("设置面板缺字段: {0}/{1}（共 {2} 个子对象），缺: {3}",
                    missingNames.Count, propertiesToActive.Count, content.childCount, string.Join(", ", missingNames)));
        }

        /// <summary>标签页图标：直接复用游戏原版图标（AddDecoration），不引入自己的图片资源。</summary>
        internal static Sprite ResolveTabIcon()
        {
            Sprite sprite = null;
            if (GCS.levelEventIcons != null)
            {
                if (!GCS.levelEventIcons.TryGetValue(LevelEventType.AddDecoration, out sprite) || sprite == null)
                {
                    foreach (KeyValuePair<LevelEventType, Sprite> pair in GCS.levelEventIcons)
                    {
                        if (pair.Value != null)
                        {
                            sprite = pair.Value;
                            break;
                        }
                    }
                }
            }
            if (sprite == null)
                sprite = Resources.Load<Sprite>("LevelEditor/LevelEvents/" + LevelEventType.AddDecoration);
            return sprite;
        }
    }
}
