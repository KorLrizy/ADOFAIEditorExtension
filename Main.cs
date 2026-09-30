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

        /// <summary>模组启用期间的 Harmony 实例（运行时按需给别的模组挂补丁用，停用时随 UnpatchAll 一起撤掉）；未启用为 null。</summary>
        internal static Harmony HarmonyInstance => harmony;

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
                    // 完整异常（含内层异常与堆栈）：只记 Message 时 Harmony 的包装异常几乎看不出是哪一步失败
                    Logger?.Log("补丁 " + type.FullName + " 应用失败: " + e);
                }
            }
        }

        private static void StopMod(UnityModManager.ModEntry modEntry)
        {
            // 先收掉直选弹窗（必须在撤补丁之前）：它是 ShowPopup(true, …) 开的，原版 showingPopup 此时为 true；
            // 下面的 PagerListController.Reset() 只清引用不收窗，补丁一撤就再也没人把标志放回去
            // （原版快捷键全部停摆，只剩 Esc 能救）。CloseIfOpen → ShowPopup(false, …) 会一并复位标志。
            try { PagerListController.CloseIfOpen(); }
            catch (Exception e) { Logger?.Log("禁用时关闭直选列表失败: " + e.Message); }

            // 设置改动时已经随时落盘，这里再兜一次（内容没变不会写）
            Settings.SettingsStore.Save();

            // harmony 为 null：StartMod 从没跑过（例如 UMM 启动时就是禁用态再点一次禁用）
            harmony?.UnpatchAll(modEntry.Info.Id);
            harmony = null;
            // 清掉注入到 GCS 的静态数据（跨场景重载仍存在），否则禁用后事件类型/图标仍残留
            Patches.EditorIntegration.RemoveInjection();
            DecoGroupRenderer.ResetStaticState();
            PagerListController.Reset();
        }

        /// <summary>
        /// 在 UMM 里开关 mod 后重启编辑器并重载当前关卡：本模组在 scnEditor.Awake/Start 时注入标签页/事件类型，
        /// 只有重载编辑器才能干净地加上（开启）或去掉（关闭）这些注入。
        /// 仅在关卡编辑器里生效；有未保存改动时走游戏自带的“未保存”提示，避免静默丢失改动。
        ///
        /// 用户在那个提示里点了取消 ⇒ 编辑器不会重启，而补丁已经撤掉 / 刚打上（半新半旧的状态）。
        /// 原版没有取消回调，所以挂一个协程在编辑器上盯着：提示框关掉后如果确认回调没跑，就记日志并弹一句
        /// "请重新打开编辑器"。协程挂在 scnEditor 这个 MonoBehaviour 上、弹窗走 <see cref="Popup.ShowMessage"/>
        /// （原版 ShowPopup + 克隆的确认框，不依赖任何 Harmony 补丁），所以禁用后也能跑；
        /// 编辑器真的重启时旧 scnEditor 被销毁，协程随之终止，不会误报。
        /// </summary>
        private static void RequestEditorReload()
        {
            try
            {
                scnEditor editor = ADOBase.editor;
                if (editor == null)
                    return;

                int requestId = ++reloadRequestId;
                // 刚收掉的弹窗（例如 StopMod 里关的直选列表）还在滑出动画中时（0.5 秒），原版 ShowPopup(true, …)
                // 会直接 return，“未保存”提示就弹不出来、确认回调永远不跑 ⇒ 先等动画结束再发请求。
                if (IsPopupAnimating(editor))
                    editor.StartCoroutine(RequestWhenPopupSettles(editor, requestId));
                else
                    SendReloadRequest(editor, requestId);
            }
            catch (Exception e)
            {
                Logger?.Log("重启编辑器失败: " + e);
            }
        }

        /// <summary>最近一次重启请求的序号：旧请求的协程发现序号变了就自行作废（连续快速开关时只认最后一次）。</summary>
        private static int reloadRequestId;

        /// <summary>当前这次重启请求是否已经走到了确认回调（= 编辑器真的会重启）。</summary>
        private static bool reloadConfirmed;

        private static System.Collections.IEnumerator RequestWhenPopupSettles(scnEditor editor, int requestId)
        {
            float deadline = Time.realtimeSinceStartup + 2f;
            while (editor != null && IsPopupAnimating(editor) && Time.realtimeSinceStartup < deadline)
                yield return null;
            if (editor == null || requestId != reloadRequestId)
                yield break;
            SendReloadRequest(editor, requestId);
        }

        private static void SendReloadRequest(scnEditor editor, int requestId)
        {
            try
            {
                Logger?.Log("mod 开关切换：请求重启编辑器并重载当前关卡");
                reloadConfirmed = false;
                editor.CheckUnsavedChanges(() =>
                {
                    reloadConfirmed = true;
                    RestartEditorWithWipe();
                }, false);

                // 没有未保存改动时回调是同步调用的；到这里还没确认 ⇒ 弹出了“未保存”提示，等用户的选择
                if (!reloadConfirmed)
                {
                    Logger?.Log("有未保存的改动：已弹出游戏的“未保存”提示。若选择取消，编辑器不会重启，" +
                        "本模组的开关要重新打开编辑器后才完全生效");
                    editor.StartCoroutine(NotifyIfReloadCancelled(editor, requestId));
                }
            }
            catch (Exception e)
            {
                Logger?.Log("重启编辑器失败: " + e);
            }
        }

        /// <summary>等“未保存”提示关掉；关掉后确认回调仍没跑（= 用户取消）就提示用户手动重开编辑器。</summary>
        private static System.Collections.IEnumerator NotifyIfReloadCancelled(scnEditor editor, int requestId)
        {
            yield return null;
            while (editor != null && IsShowingPopup(editor))
                yield return null;
            // 确认路径的过场切场景在之后的帧才生效：多等两帧，免得在切换前误报
            yield return null;
            yield return null;
            if (editor == null || reloadConfirmed || requestId != reloadRequestId)
                yield break;

            Logger?.Log("编辑器未重启（取消了“未保存”提示）：请保存后手动重新打开编辑器，mod 开关才会完全生效");
            float deadline = Time.realtimeSinceStartup + 2f;
            while (editor != null && IsPopupAnimating(editor) && Time.realtimeSinceStartup < deadline)
                yield return null;
            if (editor == null || IsShowingPopup(editor))
                yield break;      // 用户已经又打开了别的弹窗：不去抢，日志里已经有说明
            try { Popup.ShowMessage(ReopenEditorMessage()); }
            catch (Exception e) { Logger?.Log("提示重新打开编辑器失败: " + e.Message); }
        }

        private static string ReopenEditorMessage()
        {
            string text = Localizations?.GetValue("aee.reopenEditor");
            if (!string.IsNullOrEmpty(text))
                return text;
            return RDString.language == SystemLanguage.ChineseSimplified || RDString.language == SystemLanguage.Chinese
                    || RDString.language == SystemLanguage.ChineseTraditional
                ? "编辑器没有重启：请保存关卡后重新打开编辑器，ADOFAI Editor Extension 的开关才会完全生效。"
                : "The editor was not restarted. Save the level and reopen the editor to fully apply the ADOFAI Editor Extension toggle.";
        }

        /// <summary>原版 <c>scnEditor.showingPopup</c>（private 字段，反射读；读不到按"没有弹窗"处理）。</summary>
        private static bool IsShowingPopup(scnEditor editor)
        {
            try { return editor.Get("showingPopup") is bool showing && showing; }
            catch { return false; }
        }

        /// <summary>原版 <c>scnEditor.popupIsAnimating</c>：为 true 时 ShowPopup(true, …) 直接 return。</summary>
        private static bool IsPopupAnimating(scnEditor editor)
        {
            try { return editor.Get("popupIsAnimating") is bool animating && animating; }
            catch { return false; }
        }

        /// <summary>
        /// 用游戏自带的黑屏过场重启编辑器：设置 levelToOpenOnLoad 后用 scrLoader.LoadSceneWithTransition
        /// 切到 scnEditor（与游戏 QuitToMenu 的过场同一套 API）。
        ///
        /// 回退：r148 的 <c>ADOBase.RestartScene()</c> 自己也是走 <c>ADOBase.loader</c>，loader 为 null 时
        /// 它同样会 NRE，所以不能拿它当"loader 缺失"的兜底 —— 这时直接用 Unity 的 SceneManager 同步加载。
        /// 本方法是 CheckUnsavedChanges 的回调（可能由原版弹窗按钮触发），任何异常都不许漏出去。
        /// </summary>
        private static void RestartEditorWithWipe()
        {
            try
            {
                string levelPath = ADOBase.levelPath;
                if (!string.IsNullOrEmpty(levelPath))
                    scnEditor.levelToOpenOnLoad = levelPath;

                DOTween.KillAll(false);

                scrLoader loader = ADOBase.loader;
                if (loader == null)
                {
                    Logger?.Log("找不到 scrLoader，直接重新加载 scnEditor 场景（无过场）");
                    LoadEditorSceneDirectly();
                    return;
                }

                loader.LoadSceneWithTransition(WipeDirection.StartsFromRight, "scnEditor");
            }
            catch (Exception e)
            {
                Logger?.Log("过场重启失败，回退 RestartScene: " + e);
                try
                {
                    if (ADOBase.loader != null)
                        ADOBase.RestartScene();
                    else
                        LoadEditorSceneDirectly();
                }
                catch (Exception fallbackError)
                {
                    Logger?.Log("回退重启也失败了，请手动重新打开编辑器: " + fallbackError);
                }
            }
        }

        /// <summary>不经过 scrLoader 的兜底：同步重载 scnEditor 场景（levelToOpenOnLoad 已提前设好）。</summary>
        private static void LoadEditorSceneDirectly()
        {
            UnityEngine.SceneManagement.SceneManager.LoadScene("scnEditor");
        }

        // ------------------------------------------------------------------ 标签页设置值

        /// <summary>
        /// 取（必要时创建）本模组标签页的 LevelEvent。第一次创建后立刻从 Settings.json 灌回持久化的设置
        /// （<see cref="Settings.SettingsStore.Load"/>）。
        ///
        /// 设置事件整个进程只建一次（值要跨编辑器重载保留），但每次 <c>scnEditor.Awake</c> 的
        /// <c>EditorIntegration.Inject()</c> 都会往 <c>GCS.settingsInfo</c> 放一份**新的** LevelEventInfo。
        /// 面板按属性名绑定、<c>SetProperties</c> 按 data 键遍历，所以旧 info 不会直接出错；但事件的
        /// <c>info</c> 指向已被替换掉的旧对象（禁用再启用后更是指向已从 GCS 移除的那份），
        /// 原版任何经 <c>selectedEvent.info</c> 取 PropertyInfo 的路径拿到的都是过期对象。
        /// 这里发现 info 换了就把事件重新绑到当前 info 上（同一个事件对象，data 原样保留）。
        /// </summary>
        internal static LevelEvent GetSettingsEvent()
        {
            LevelEventInfo info = null;
            bool hasInfo = GCS.settingsInfo != null && GCS.settingsInfo.TryGetValue(ModEventName, out info) && info != null;
            if (AeeLevelEvent != null)
            {
                if (hasInfo && !ReferenceEquals(AeeLevelEvent.info, info))
                    RebindSettingsInfo(AeeLevelEvent, info);
                return AeeLevelEvent;
            }
            if (!hasInfo)
                return null;
            AeeLevelEvent = new LevelEvent(0, (LevelEventType)ModEventType, info);
            Settings.SettingsStore.Load();
            return AeeLevelEvent;
        }

        /// <summary>
        /// 把设置事件改绑到新的 LevelEventInfo；新 info 里多出来的属性按默认值补进 data（与构造函数同语义）。
        /// r148 的 <c>LevelEvent.data</c> 不是 public，走 <c>GetData()</c>（返回的就是 data 本身）。
        /// </summary>
        private static void RebindSettingsInfo(LevelEvent settings, LevelEventInfo info)
        {
            try
            {
                settings.info = info;
                Dictionary<string, object> data = settings.GetData();
                if (data == null || info.propertiesInfo == null)
                    return;
                foreach (KeyValuePair<string, ADOFAI.PropertyInfo> pair in info.propertiesInfo)
                {
                    if (pair.Value != null && !data.ContainsKey(pair.Key))
                        data[pair.Key] = pair.Value.value_default;
                }
            }
            catch (Exception e)
            {
                Logger?.Log("设置事件改绑 LevelEventInfo 失败: " + e.Message);
            }
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
            // 禁用后残留的按钮/回调（编辑器还没重启）不该再改设置、重建列表
            if (!IsEnabled)
                return;
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
                Settings.SettingsStore.Save();
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
            // 禁用后（编辑器还没重启）面板上残留的控件改了值：接受这个值但不做任何刷新/落盘
            if (!IsEnabled)
                return true;
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
                    // 归属始终留在 data 里，开关只决定保存时写不写（由 EncodeNotePatch 在保存出口摘键）；
                    // 这里只通知分组模块刷新（§17.3）
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
            // 持久化（内部自带 try/catch 与"内容没变不写盘"）。文本框的值由原版 onEndEdit 监听先写进 data，
            // 我们的回调挂在它之后，所以这里读到的已是新值。
            Settings.SettingsStore.Save();
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
