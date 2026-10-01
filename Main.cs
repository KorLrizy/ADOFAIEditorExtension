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
        internal const string KeyPagerAutoWindowSize = "pagerAutoWindowSize";   // 直选弹窗是否随内容自动调节尺寸（默认开）
        internal const string KeyPagerWindowScale = "pagerWindowScale";         // 自动尺寸下的窗口放大倍率（0.5..2.5，默认 1.0）
        internal const string KeyPagerWindowSnap = "pagerWindowSnap";           // 拖动标题移动窗口时是否吸附到屏幕四边/四角（默认关，两种模式都有效）

        /// <summary>窗口放大倍率的合法区间（与 Features.PagerList.PagerWindowGeometry 的 MinScale/MaxScale 同值）。</summary>
        internal const float PagerWindowScaleMin = 0.5f;

        internal const float PagerWindowScaleMax = 2.5f;

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

            LogCoexistingMods();
        }

        /// <summary>
        /// 启动共存日志（§43 加固项 3）：把其它启用中的 mod 逐条打出来，并对已知会互相影响的几个给一句提示。
        /// 纯日志，不做任何行为上的适配 —— 与 Iridium 的共存结论是“无硬性冲突”，不需要检测/让步逻辑。
        /// 整段包 try/catch：这是排查用的保险，绝不能因为 UMM 的 API 变化打断启动。
        /// </summary>
        private static void LogCoexistingMods()
        {
            try
            {
                List<UnityModManager.ModEntry> entries = UnityModManager.modEntries;
                if (entries == null)
                    return;
                string ownId = ModEntry?.Info?.Id;
                foreach (UnityModManager.ModEntry entry in entries)
                {
                    if (entry == null || !entry.Enabled || entry.Info == null)
                        continue;
                    if (string.Equals(entry.Info.Id, ownId, StringComparison.Ordinal))
                        continue;

                    string line = "共存 mod: " + entry.Info.Id + " " + entry.Info.Version;
                    string hint = CoexistenceHint(entry.Info.Id);
                    if (!string.IsNullOrEmpty(hint))
                        line += " —— " + hint;
                    Logger?.Log(line);
                }
            }
            catch (Exception e)
            {
                Logger?.Log("扫描已启用 mod 失败: " + e.Message);
            }
        }

        /// <summary>已知 mod 的共存提示；不认识的返回 null。</summary>
        private static string CoexistenceHint(string id)
        {
            switch (id)
            {
                case "Iridium":
                    return "已评估可共存，注意其编辑器「砖块优化」相关选项保持默认。";
                case "EnhancedEffectRemover":
                    return "其去特效功能会删装饰/事件，请检查该功能的开关是否符合预期。";
                case "PACL2":
                case "QuickChart":
                    return "其 v3 线构建在 v2.9.8 上，与本模组有兼容风险。";
                default:
                    return null;
            }
        }

        private static void StopMod(UnityModManager.ModEntry modEntry)
        {
            // 先收掉直选弹窗：它是 ShowPopup(true, 233) 开的，原版 showingPopup 此时为 true；
            // 下面的 PagerListController.Reset() 只清引用不收窗，补丁一撤就再也没人把标志放回去
            // （原版快捷键全部停摆，只剩 Esc 能救）。CloseIfOpen → Close → ShowPopup(false, 233) 会一并复位标志。
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

        /// <summary>最近一次重启请求的序号：旧请求的"未重启"提示协程发现序号变了就自行作废。</summary>
        private static int reloadRequestId;

        /// <summary>当前这次重启请求是否已经走到了确认回调（= 编辑器真的会重启）。</summary>
        private static bool reloadConfirmed;

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
                // 刚收掉的弹窗（例如 StopMod 里关的直选列表）还在滑出动画中时，原版 ShowPopup(true, …) 会直接 return，
                // “未保存”提示就弹不出来 ⇒ 先等动画结束再发请求。
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
            // 确认路径是同步调 RestartScene，场景切换在之后的帧生效：多等两帧，免得在切换前误报
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
            Popup.ShowMessage(ReopenEditorMessage());
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

        /// <summary>把设置事件改绑到新的 LevelEventInfo；新 info 里多出来的属性按默认值补进 data（与 8 参构造同语义）。</summary>
        private static void RebindSettingsInfo(LevelEvent settings, LevelEventInfo info)
        {
            try
            {
                settings.info = info;
                if (settings.data == null || info.propertiesInfo == null)
                    return;
                foreach (KeyValuePair<string, ADOFAI.PropertyInfo> pair in info.propertiesInfo)
                {
                    if (pair.Value != null && !settings.data.ContainsKey(pair.Key))
                        settings.data[pair.Key] = pair.Value.value_default;
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

        /// <summary>
        /// 直选弹窗是否"自动调节窗口"（缺省 **true**）：开 = 弹窗按事件行数自己算宽高；
        /// 关 = 用用户手调过的尺寸（见 <see cref="Settings.PagerWindowPreferences"/>）。
        /// 同时也是面板上「窗口放大倍率」那一行的启用条件（原版 enableIf 机制，见 PrefabProperties）。
        /// </summary>
        internal static bool PagerAutoWindowSize => GetBoolSetting(KeyPagerAutoWindowSize, true);

        /// <summary>
        /// 用鼠标拖动标题区域移动窗口时，是否吸附到屏幕的四边/四角（缺省 **false**）。
        /// **两种模式都有效**：拖动标题只改位置、不改尺寸，自动尺寸开着时也照样能拖，所以吸附同样生效
        /// （自动尺寸只锁"拖边缘缩放"，见 <see cref="PagerAutoWindowSize"/> 那一行）。
        /// 吸附本身不改变窗口大小，见 Features.PagerList.PagerWindowGeometry.Move。
        /// </summary>
        internal static bool PagerWindowSnap => GetBoolSetting(KeyPagerWindowSnap, false);

        /// <summary>
        /// 自动尺寸下的窗口放大倍率（缺省 1.0）。读取时做完整兜底：非有限数（NaN/±Infinity）、
        /// 类型不对、字段缺失一律回落到 1.0，有限但越界的值夹到 [0.5, 2.5]。
        /// 倍率改变的是**弹窗的宽高**（给列表和文本更多空间），不是字号 —— 面板上的说明也这么写。
        /// </summary>
        internal static float PagerWindowScale
        {
            get
            {
                LevelEvent settings = GetSettingsEvent();
                if (settings != null)
                {
                    try
                    {
                        if (settings.TryGet<object>(KeyPagerWindowScale, out object raw) && raw != null)
                        {
                            float value;
                            if (raw is float single)
                                value = single;
                            else if (raw is double number)
                                value = (float)number;
                            else if (raw is int integer)
                                value = integer;
                            else if (raw is long big)
                                value = big;
                            else if (!float.TryParse(raw.ToString(), System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out value))
                                value = float.NaN;
                            if (!float.IsNaN(value) && !float.IsInfinity(value))
                                return ClampPagerWindowScale(value);
                        }
                    }
                    catch { }
                }
                return 1f;
            }
        }

        /// <summary>把倍率夹到合法区间（NaN 当 1.0 处理；调用方一般已经判过有限性）。</summary>
        internal static float ClampPagerWindowScale(float value)
        {
            if (float.IsNaN(value))
                return 1f;
            if (value < PagerWindowScaleMin)
                return PagerWindowScaleMin;
            if (value > PagerWindowScaleMax)
                return PagerWindowScaleMax;
            return value;
        }

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

        /// <summary>
        /// 读一个 float 设置项：字段缺失 / 类型不对 / 非有限数（NaN、±Infinity）都回落到 <paramref name="fallback"/>。
        /// 只做类型与有限性判断，不夹取范围 —— 范围由各自的属性（例如 <see cref="PagerWindowScale"/>）决定。
        /// </summary>
        internal static bool TryGetFloatSetting(string key, out float value)
        {
            value = 0f;
            LevelEvent settings = GetSettingsEvent();
            if (settings == null)
                return false;
            try
            {
                if (!settings.TryGet<object>(key, out object raw) || raw == null)
                    return false;
                float parsed;
                if (raw is float single)
                    parsed = single;
                else if (raw is double number)
                    parsed = (float)number;
                else if (raw is int integer)
                    parsed = integer;
                else if (raw is long big)
                    parsed = big;
                else if (!float.TryParse(raw.ToString(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out parsed))
                    return false;
                if (float.IsNaN(parsed) || float.IsInfinity(parsed))
                    return false;
                value = parsed;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>把一个 float 设置项写进设置事件（SettingsStore 读盘时用；设置事件还不存在时静默失败）。</summary>
        internal static void SetFloatSetting(string key, float value)
        {
            LevelEvent settings = GetSettingsEvent();
            if (settings == null)
                return;
            try
            {
                settings[key] = value;
            }
            catch (Exception e)
            {
                Logger?.Log("写入 float 设置 " + key + " 失败: " + e.Message);
            }
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

                // 窗口三连（自动调节 / 倍率 / 吸附）：原版 PropertyControl_Bool.SetValue 已经调过
                // ToggleOthersEnabled（⇒ 所有行的 CheckIfEnabled + SetEnabled），这里再通知控制器按新值
                // 重算一次几何并落盘。倍率行的置灰**不靠**这里，靠 PrefabProperties 里的 disableIf 元数据。
                if (key == KeyPagerAutoWindowSize || key == KeyPagerWindowScale || key == KeyPagerWindowSnap)
                {
                    PagerListController.OnWindowSettingsChanged();
                    // 兜底重算一次所有行的启用状态（正常路径原版已经算过，见 PrefabProperties 的说明）
                    PrefabProperties.ReapplyWindowRowEnabled();
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
            // 我们的监听挂在它之后（ValueChangePatch2 是 Setup 后置），所以这里读到的已是新值。
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
