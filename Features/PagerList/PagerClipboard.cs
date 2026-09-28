using ADOFAI;
using ADOFAIEditorExtension.Utils;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace ADOFAIEditorExtension.Features.PagerList
{
    /// <summary>快捷键想干的事（类型名 → 意图的映射单独抽出来，便于离线验证）。</summary>
    internal enum PagerClipboardIntent
    {
        None,
        CopyEvents,
        CutEvents,
        CopyAllSameType,
        CutAllSameType,
        PasteEvents
    }

    /// <summary>
    /// 弹窗打开期间的"复制 / 剪切 / 粘贴事件"（§24）。
    ///
    /// **为什么必须自己做**：原版 `scnEditor.HandleKeyboardActions` 第一句就是
    /// `if (showingPopup) { 只处理 Esc; return; }` —— 而本弹窗是用 `scnEditor.ShowPopup` 打开的
    /// （`showingPopup` 为真）⇒ **弹窗期间所有编辑器快捷键全部停摆**，复制粘贴自然按不动。
    ///
    /// **做法**：在 `HandleKeyboardActions` 之前接一棒，按**原版自己的键位表**
    /// （`EditorKeybindManager.dictionary`，用户改过键位也能跟上）判断按下了哪条 action：
    ///  · 单选（列表里只选中一个事件）⇒ 直接调用原版 action 实例
    ///    （`CopyEventsEditorAction` / `CutEvents…` / `…AllSameType…` / `PasteEventsEditorAction`），
    ///    行为与撤销点与原版逐字一致（原版的"选中事件"就是 `levelEventsPanel.selectedEvent`，
    ///    而弹窗的当前行一直与它同步）；
    ///  · 多选（弹窗自己的多选集，原版没有这种状态）⇒ 原版**没有**"一次复制多个事件"的入口，
    ///    这里按原版剪贴板格式自己拼：`clipboard` 里放一条 `scnEditor+FloorData`、
    ///    `clipboardContent = Floors(1)`，每个事件用 8 参 ctor 浅拷贝（r265 删了 `LevelEvent.CopyShallow()`，
    ///    见 <see cref="CopyEventsForClipboard"/>）并把 `floor` 改成源砖
    ///    （与原版 `scnEditor.CopyEvent(ev, floor)` 做的事一样）；粘贴依旧交回原版 action。
    /// </summary>
    internal static class PagerClipboard
    {
        /// <summary>原版剪贴板里 "Floors" 这一档（= 1）；事件也走这一档（见 §24.1）。</summary>
        private const int ClipboardContentFloors = 1;

        /// <summary>类型名 → 意图（纯映射，离线可测）。</summary>
        internal static PagerClipboardIntent IntentOf(string actionTypeName)
        {
            switch (actionTypeName)
            {
                case "CopyEventsEditorAction": return PagerClipboardIntent.CopyEvents;
                case "CutEventsEditorAction": return PagerClipboardIntent.CutEvents;
                case "CopyAllSameTypeEventsEditorAction": return PagerClipboardIntent.CopyAllSameType;
                case "CutAllSameTypeEventsEditorAction": return PagerClipboardIntent.CutAllSameType;
                case "PasteEventsEditorAction": return PagerClipboardIntent.PasteEvents;
                default: return PagerClipboardIntent.None;
            }
        }

        /// <summary>我们关心的那几条键位（键位对象 + 它挂着的、属于我们的 action）。</summary>
        private sealed class Bind
        {
            internal object Keybind;
            internal List<object> Actions;
        }

        // 键位表是每帧都要看的，所以只挑出我们关心的几条缓存起来（每次打开弹窗刷新一次，
        // 这样用户中途改键位也能跟上）；否则每帧都要把整张键位表反射跑一遍。
        private static readonly List<Bind> cachedBinds = new List<Bind>();
        private static bool cacheValid;

        // "PACL2 的复制提示会不会响"的判定缓存（每次打开弹窗重判，见 InvalidateCache）
        private static bool pacl2Checked;
        private static bool pacl2ToastsCopy;

        /// <summary>
        /// 弹窗每次打开时刷新缓存：键位表（用户可能改过键位）与"PACL2 提示是否生效"
        /// （用户可能在两次打开之间开关过 PACL2）。
        /// </summary>
        internal static void InvalidateCache()
        {
            cacheValid = false;
            cachedBinds.Clear();
            pacl2Checked = false;
        }

        /// <summary>由 `scnEditor.HandleKeyboardActions` 的前缀补丁每帧调用一次。</summary>
        internal static void HandleKeybinds()
        {
            if (!PagerListController.IsPopupOpen)
                return;
            scnEditor editor = scnEditor.instance;
            if (editor == null)
                return;
            // 弹窗确实把原版快捷键挡住了（showingPopup）才由我们接手；否则原版自己会处理，
            // 我们再来一遍就会重复执行（粘贴尤其不能重复）。
            // 注意用"非泛型 Get + is 判断"而不是 Get<bool>()：Reflections.Get<T> 在成员缺失时
            // 返回 null，往值类型上转会直接 NRE（§25.1 的教训）。
            if (!(editor.Get("showingPopup") is bool showing) || !showing)
                return;

            EnsureCache(editor);
            for (int i = 0; i < cachedBinds.Count; i++)
            {
                if (!IsPressed(cachedBinds[i].Keybind))
                    continue;
                // 与原版 ExecutePressedActions 一致：一帧只执行第一组按下的键位
                HandleActions(editor, cachedBinds[i].Actions);
                return;
            }
        }

        private static void EnsureCache(scnEditor editor)
        {
            if (cacheValid)
                return;

            IDictionary binds = ResolveKeybindDict(editor);
            if (binds == null)
            {
                cacheValid = true;                 // 读不到键位表就别每帧重试一次反射
                cachedBinds.Clear();
                return;
            }

            // **先快照再遍历**：这张表是原版自己的键位字典，改键位/重注册 action 只要撞上遍历中途，
            // 直接 foreach 字典就抛 InvalidOperationException —— 而这条调用链在 `HandleKeyboardActions`
            // 的**前置**里，抛出去会让 Harmony 跳过整个原版键盘处理（所有快捷键一起停摆，§39 M4）。
            var entries = new List<DictionaryEntry>();
            foreach (DictionaryEntry entry in binds)
                entries.Add(entry);

            cachedBinds.Clear();
            for (int i = 0; i < entries.Count; i++)
            {
                List<object> ours = null;
                if (entries[i].Value is IEnumerable actions)
                {
                    foreach (object action in actions)
                    {
                        if (action == null || IntentOf(action.GetType().Name) == PagerClipboardIntent.None)
                            continue;
                        if (ours == null)
                            ours = new List<object>();
                        ours.Add(action);
                    }
                }
                if (ours != null)
                    cachedBinds.Add(new Bind { Keybind = entries[i].Key, Actions = ours });
            }
            cacheValid = true;
        }

        private static void HandleActions(scnEditor editor, List<object> actions)
        {
            if (actions == null)
                return;
            for (int i = 0; i < actions.Count; i++)
            {
                object action = actions[i];
                if (action == null)
                    continue;
                PagerClipboardIntent intent = IntentOf(action.GetType().Name);
                if (intent == PagerClipboardIntent.None)
                    continue;
                try
                {
                    HandleAction(editor, action, intent);
                }
                catch (Exception e)
                {
                    // 打完整 ToString（含堆栈）：只打 Message 的话像 §25.1 那种 NRE 根本定位不到
                    Main.Logger?.Log("弹窗快捷键 " + action.GetType().Name + " 失败: " + e);
                }
            }
        }

        private static void HandleAction(scnEditor editor, object action, PagerClipboardIntent intent)
        {
            if (intent == PagerClipboardIntent.PasteEvents)
            {
                // 粘贴一律交回原版（它自己会按 clipboard / clipboardContent 的语义处理，
                // 包括"不覆盖"、多处 FloorData 按砖顺延等）
                int clipboardCount = DescribeClipboard(editor, out int clipboardEvents);
                int targetFloor = ResolveFloorID(editor, PagerListController.SelectedPopupEvents());
                int before = CountFloorEvents(editor, targetFloor);
                // 原版粘贴收尾会 SelectFloor + ShowTabsForFloor + ShowPanel，那会命中"面板切换就关窗"
                // 的补丁 ⇒ 先声明这是我们自己触发的，弹窗才不会被顺手关掉（之后 ReloadRows 重建列表）
                PagerListController.SuppressAutoClose();
                Run(editor, action);
                int after = CountFloorEvents(editor, targetFloor);
                // 诊断日志：剪贴板里几条 FloorData / 几个事件，以及目标砖事件数的变化 ——
                // 把"只贴了一个"这类问题显性化（剪贴板里本来就只有一个事件？还是被原版跳过了？）
                Main.Logger?.Log(string.Format("弹窗粘贴事件：剪贴板 {0} 条 FloorData / {1} 个事件，砖 {2} 事件数 {3} → {4}",
                    clipboardCount, clipboardEvents, targetFloor, before, after));
                // 提示**不在这里弹**：粘贴绝大多数情况发生在弹窗已经关掉之后（换砖就会关窗），
                // 所以由挂在"粘贴事件 action"上的 PasteEventsNotifyPatch 统一处理（§27），
                // 否则弹窗内粘贴会弹两次。
                // 声明"这帧已经弹过操作提示"：紧接着的多选态收尾会退出多选，同帧再弹一条"已退出多选"
                // 就会把刚弹的"已粘贴 N"顶掉（左上角只有一条位置，§39 M1）。
                PagerListController.SuppressExitToastThisFrame();
                PagerListController.AfterPopupClipboardChange(true, false);
                return;
            }

            List<LevelEvent> selected = PagerListController.SelectedPopupEvents();
            if (selected.Count == 0)
                return;

            bool allSameType = intent == PagerClipboardIntent.CopyAllSameType
                || intent == PagerClipboardIntent.CutAllSameType;
            bool cut = IsCut(intent);

            if (selected.Count == 1)
            {
                // 单选：原版的"选中的事件"就是面板里那个事件，与我们列表当前行一致 ⇒ 直接复用原版 action
                // （剪切的收尾会 ShowTabsForFloor/ShowPanel ⇒ 先压住"面板切换就关窗"）
                int affected = allSameType ? CollectSameTypeEvents(editor, selected).Count : 1;
                PagerListController.SuppressAutoClose();
                Run(editor, action);
                if (cut)
                    PagerListController.AfterPopupClipboardChange(false, false);
                // 复制走的就是原版 scnEditor.CopyFloor ⇒ 装了（且启用）PACL2 时它已经弹过
                // "已复制选中方块！"；剪切原版不弹（PACL2 也没挂 CutFloor）⇒ 由我们补上。见 §26.3 策略表。
                if (ShouldNotify(cut, false, Pacl2ToastsCopy()))
                    Notify(cut ? "aee.notify.cut" : "aee.notify.copied", Mathf.Max(affected, 1));
                return;
            }

            // 多选：剪贴板是我们自己拼的 / 事件是我们自己删的，PACL2 完全感知不到 ⇒ 提示始终由我们弹
            List<LevelEvent> toCopy = allSameType ? CollectSameTypeEvents(editor, selected) : selected;
            if (toCopy.Count == 0)
                return;

            int floorID = ResolveFloorID(editor, toCopy);
            if (!BuildClipboard(editor, toCopy, floorID))
                return;
            if (cut)
            {
                PagerListController.SuppressAutoClose();   // 删除的收尾同样会刷标签页
                RemoveEvents(editor, toCopy, floorID);
            }

            // 复制不改变任何事件 ⇒ 保留多选集与右侧批量面板（`keepBatch: !cut`，见 §31.2）；
            // 剪切把事件删了，必须清掉重建。
            PagerListController.AfterPopupClipboardChange(false, !cut);
            Main.Logger?.Log(string.Format("弹窗{0}事件：{1} 个（多选）", cut ? "剪切" : "复制", toCopy.Count));
            Notify(cut ? "aee.notify.cut" : "aee.notify.copied", toCopy.Count);
        }

        private static bool IsCut(PagerClipboardIntent intent)
        {
            return intent == PagerClipboardIntent.CutEvents || intent == PagerClipboardIntent.CutAllSameType;
        }

        /// <summary>
        /// 这次操作要不要**由我们**弹提示（纯策略，离线 harness 直接断言；文档 §26.3 的策略表就是它）：
        ///  · 剪切（单选走原版 `CutFloor`、多选走我们自己的 RemoveEvents）：原版与 PACL2 都没提示 ⇒ 我们弹；
        ///  · 多选复制：剪贴板是我们自己拼的，PACL2 感知不到 ⇒ 我们弹；
        ///  · 单选复制（走原版 `CopyFloor`）：装了**且启用**的 PACL2 已经在 `CopyFloor` 前缀里弹过 ⇒ 不重复。
        /// </summary>
        internal static bool ShouldNotify(bool cut, bool multiSelect, bool pacl2Toasts)
        {
            if (cut || multiSelect)
                return true;
            return !pacl2Toasts;
        }

        // ---------------------------------------------------------------- 浮出提示（§26.3）

        /// <summary>
        /// 浮出提示：**复用游戏自己的 `scnEditor.ShowNotification(text)`**
        /// （签名 `ShowNotification(string, Color? = null, float = 1.25f)`，原版公开 API）。
        /// PACL2 的"已复制选中方块！"用的就是它（IL 核过：`ldstr "FixChartLoad.CopyFloorSelected"` →
        /// `scnEditor::ShowNotification`）⇒ 样式、位置、时长天然一致，而且**没有 PACL2 时照样能用**。
        /// </summary>
        private static void Notify(string localizationKey, int count)
        {
            scnEditor editor = scnEditor.instance;
            if (editor == null)
                return;
            string text = Format(localizationKey, count);
            if (string.IsNullOrEmpty(text))
            {
                // §35：文案缺失时以前是静默返回，看起来就是"提示不弹" ⇒ 记一条
                Main.Logger?.Log("浮出提示跳过：文案为空（key=" + localizationKey + "）");
                return;
            }
            try
            {
                editor.ShowNotification(text);
            }
            catch (Exception e)
            {
                Main.Logger?.Log("浮出提示失败: " + e.Message);
            }
        }

        /// <summary>取本地化文本并按数量填充（模板缺失或格式错误时原样返回）。</summary>
        private static string Format(string key, params object[] args)
        {
            string template = Main.Localizations?.GetValue(key);
            if (string.IsNullOrEmpty(template))
                return null;
            try
            {
                return string.Format(template, args);
            }
            catch (FormatException)
            {
                return template;
            }
        }

        /// <summary>
        /// "PACL2 的复制提示当前会不会响"：看 `scnEditor.CopyFloor` 上有没有**来自 PACL2 程序集**的
        /// Harmony 补丁 —— 而不是"PACL2 装没装"。这样"装了但被禁用 / 补丁没打上"的情况也能判对
        /// （只看 UMM 的 mod 列表做不到）。每次打开弹窗重判（`InvalidateCache`）。
        /// </summary>
        private static bool Pacl2ToastsCopy()
        {
            if (pacl2Checked)
                return pacl2ToastsCopy;
            pacl2Checked = true;
            pacl2ToastsCopy = false;
            try
            {
                MethodBase target = AccessTools.Method(typeof(scnEditor), "CopyFloor");
                HarmonyLib.Patches info = target != null ? Harmony.GetPatchInfo(target) : null;
                if (info == null)
                    return false;
                pacl2ToastsCopy = HasPacl2Patch(info.Prefixes)
                    || HasPacl2Patch(info.Postfixes)
                    || HasPacl2Patch(info.Transpilers);
            }
            catch (Exception e)
            {
                Main.Logger?.Log("检测 PACL2 复制提示失败（按未安装处理）: " + e.Message);
            }
            return pacl2ToastsCopy;
        }

        private static bool HasPacl2Patch(IEnumerable<Patch> patches)
        {
            if (patches == null)
                return false;
            foreach (Patch patch in patches)
            {
                Type declaring = patch.PatchMethod != null ? patch.PatchMethod.DeclaringType : null;
                if (declaring != null && declaring.Assembly.GetName().Name == "PACL2")
                    return true;
            }
            return false;
        }

        // ---------------------------------------------------------------- 与原版一致的剪贴板

        /// <summary>
        /// 按原版 `scnEditor.CopyEvent(eventToCopy, floor)` 的做法造副本：浅拷贝 + 把 floor 改成源砖。
        /// （纯逻辑，离线 harness 直接断言。）
        ///
        /// 不用新 dll 的 `LevelEvent.Copy()`：它照抄源事件的 visible/locked，副本会带着源事件的隐藏/锁定态；
        /// 原版的浅拷贝语义是"激活、可见、未锁"，这里用 8 参 ctor 逐参写死。
        /// </summary>
        internal static List<LevelEvent> CopyEventsForClipboard(List<LevelEvent> events, int floorID)
        {
            var copies = new List<LevelEvent>(events != null ? events.Count : 0);
            if (events == null)
                return copies;
            foreach (LevelEvent ev in events)
            {
                if (ev == null)
                    continue;
                LevelEvent copy = new LevelEvent(ev.floor, ev.eventType, ev.info, ev.data, ev.disabled, true, true, false);
                copy.floor = floorID;
                copies.Add(copy);
            }
            return copies;
        }

        private static bool BuildClipboard(scnEditor editor, List<LevelEvent> events, int floorID)
        {
            List<LevelEvent> copies = CopyEventsForClipboard(events, floorID);
            if (copies.Count == 0)
                return false;

            object floorData = CreateFloorData(editor, floorID, copies);
            if (floorData == null)
                return false;

            IList clipboard = editor.clipboard;
            if (clipboard == null)
                return false;
            clipboard.Clear();
            clipboard.Add(floorData);
            // 与原版 CopyFloor 一样标成 Floors(1) —— 事件剪贴板也走这一档，原版粘贴据此判断
            editor.clipboardContent = (scnEditor.ClipboardContent)ClipboardContentFloors;
            return true;
        }

        /// <summary>
        /// 造一条 `scnEditor+FloorData`：ctor = (char stringDirection, float floatDirection,
        /// List&lt;LevelEvent&gt; levelEventData, List&lt;LevelEvent&gt; attachedDecorations)
        /// —— 字段顺序逐字核过。事件粘贴只读 `levelEventData`，方向字段照抄源砖
        /// （万一之后用原版"粘贴砖块"贴它，也不至于角度错乱）。
        /// </summary>
        private static object CreateFloorData(scnEditor editor, int floorID, List<LevelEvent> events)
        {
            Type floorDataType = typeof(scnEditor).GetNestedType("FloorData", BindingFlags.Public | BindingFlags.NonPublic);
            if (floorDataType == null)
                return null;
            ConstructorInfo ctor = floorDataType.GetConstructor(new[]
            {
                typeof(char), typeof(float), typeof(List<LevelEvent>), typeof(List<LevelEvent>)
            });
            if (ctor == null)
                return null;

            char stringDir = '\0';
            float floatDir = 0f;
            scrFloor floor = FloorOf(editor, floorID);
            if (floor != null)
            {
                // 字段名是 stringDirection / floatDirection（不是 stringDir）。
                // **必须**用"非泛型 Get + is 判断"：Reflections.Get<T>() 在成员缺失时返回 null，
                // 往 char/float 这种值类型上转会直接 NRE —— 这正是"多选复制必定失败"的根因（§25.1）。
                object sd = floor.Get("stringDirection");
                if (sd is char c)
                    stringDir = c;
                object fd = floor.Get("floatDirection");
                if (fd is float f)
                    floatDir = f;
            }
            return ctor.Invoke(new object[] { stringDir, floatDir, events, new List<LevelEvent>() });
        }

        /// <summary>剪贴板里有几条 FloorData、合计多少个事件（诊断用；读不到就返回 0）。</summary>
        private static int DescribeClipboard(scnEditor editor, out int eventCount)
        {
            eventCount = 0;
            int floorDataCount = 0;
            try
            {
                IList clipboard = editor.clipboard;
                if (clipboard == null)
                    return 0;
                for (int i = 0; i < clipboard.Count; i++)
                {
                    object entry = clipboard[i];
                    if (entry == null)
                        continue;
                    floorDataCount++;
                    if (entry.Get("levelEventData") is IList events)
                        eventCount += events.Count;
                }
            }
            catch { }
            return floorDataCount;
        }

        /// <summary>把"已粘贴 N 个事件"改成公开入口，供 <see cref="PasteEventsNotifyPatch"/> 调用。</summary>
        internal static void NotifyPasted(int count)
        {
            Notify("aee.notify.pasted", count);
        }

        /// <summary>通用的左上角提示入口（多选数量 / 退出多选这类提示复用同一套样式，§34.4）。</summary>
        internal static void Toast(string localizationKey, int count)
        {
            Notify(localizationKey, count);
        }

        /// <summary>某块砖上有多少个事件（诊断用）。</summary>
        internal static int CountFloorEvents(scnEditor editor, int floorID)
        {
            if (floorID < 0)
                return -1;
            int count = 0;
            foreach (LevelEvent ev in AllEvents(editor))
                if (ev != null && ev.floor == floorID)
                    count++;
            return count;
        }

        /// <summary>
        /// 整关有多少个事件（"已粘贴 N"的口径）。粘贴事件固定不覆盖（`overwrite: false`），
        /// 所以整关增量就是这次真正贴上去的个数；只数第一块选中砖的话，
        /// 一次贴多块砖（剪贴板里有多条 FloorData 时原版会按砖顺延）就只报出第一块的量（§39 M6）。
        /// </summary>
        internal static int CountAllEvents(scnEditor editor)
        {
            int count = 0;
            foreach (LevelEvent ev in AllEvents(editor))
                if (ev != null)
                    count++;
            return count;
        }

        private static scrFloor FloorOf(scnEditor editor, int floorID)
        {
            try
            {
                object floors = editor.Get<object>("floors");
                if (floors is IList list && floorID >= 0 && floorID < list.Count)
                    return list[floorID] as scrFloor;
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 剪切：把选中的事件从关卡里删掉。整套动作与原版 `CutFloor` 的收尾一致
        /// （`RemoveEvents` → `ApplyEventsToFloors` → `ShowTabsForFloor` → `ShowEventIndicators`，
        /// 全在一个 `SaveStateScope(editor, false, true, false)` 里 ⇒ 一步撤销）。
        /// </summary>
        private static void RemoveEvents(scnEditor editor, List<LevelEvent> events, int floorID)
        {
            if (events == null || events.Count == 0)
                return;
            using (new SaveStateScope(editor, false, true, false))
            {
                editor.RemoveEvents(new List<LevelEvent>(events));
                editor.ApplyEventsToFloors();

                InspectorPanel panel = editor.levelEventsPanel;
                if (panel != null && floorID >= 0)
                    panel.ShowTabsForFloor(floorID);
                scrFloor floor = FloorOf(editor, floorID);
                if (floor != null)
                    editor.ShowEventIndicators(floor);
            }
        }

        /// <summary>复制目标砖：优先用原版选中砖（原版 action 也是这么取），退回事件自己的 floor。</summary>
        private static int ResolveFloorID(scnEditor editor, List<LevelEvent> events)
        {
            try
            {
                List<scrFloor> selected = editor.selectedFloors;
                if (selected != null && selected.Count == 1 && selected[0] != null)
                    return selected[0].seqID;
            }
            catch { }
            if (events != null && events.Count > 0 && events[0] != null)
                return events[0].floor;
            return -1;
        }

        /// <summary>"复制/剪切全部同类事件"：该类型在整关里的所有事件（原版 allSameTypeEvents 分支同义）。</summary>
        private static List<LevelEvent> CollectSameTypeEvents(scnEditor editor, List<LevelEvent> selected)
        {
            var result = new List<LevelEvent>();
            if (selected == null || selected.Count == 0 || selected[0] == null)
                return result;
            LevelEventType type = selected[0].eventType;
            foreach (LevelEvent ev in AllEvents(editor))
                if (ev != null && ev.eventType == type)
                    result.Add(ev);
            return result;
        }

        private static IEnumerable AllEvents(scnEditor editor)
        {
            try
            {
                object events = editor.Get<object>("events");
                if (events is IEnumerable enumerable)
                    return enumerable;
            }
            catch { }
            return new List<LevelEvent>();
        }

        // ---------------------------------------------------------------- 原版键位表 / action 调用

        /// <summary>原版的键位表：EditorKeybind → List&lt;EditorAction&gt;（用户改过键位也能跟上）。</summary>
        private static IDictionary ResolveKeybindDict(scnEditor editor)
        {
            try
            {
                object manager = editor.Get<object>("keybindManager");
                if (manager == null)
                    return null;
                return manager.Get<IDictionary>("dictionary");
            }
            catch
            {
                return null;
            }
        }

        private static bool IsPressed(object keybind)
        {
            if (keybind == null)
                return false;
            try
            {
                return keybind.GetType().GetMethod("IsPressed", Type.EmptyTypes) is MethodInfo m
                    && (bool)m.Invoke(keybind, null);
            }
            catch
            {
                return false;
            }
        }

        private static void Run(scnEditor editor, object action)
        {
            MethodInfo execute = action.GetType().GetMethod("Execute", new[] { typeof(scnEditor) });
            execute?.Invoke(action, new object[] { editor });
        }
    }

    /// <summary>
    /// 粘贴事件后的浮出提示（§27）："已粘贴 N 个事件"。
    ///
    /// **为什么不能只接在弹窗的键位处理器上**（上一版的做法）：粘贴绝大多数发生在**弹窗已经关掉之后** ——
    /// 弹窗一换砖就关（`SelectedFloorChangePatch`），而正常流程恰恰是"弹窗里复制 → 关窗 → 换砖 → 粘贴"，
    /// 于是被 `IsPopupOpen` 门控的 `HandleKeybinds` 永远看不到这次粘贴 ⇒ 两种环境都不弹。
    ///
    /// **为什么挂"粘贴事件"这个 action，而不是原版 `scnEditor.PasteEvents`**（IL 逐条核过）：
    /// `scnEditor.PasteEvents` 不是粘贴事件专用的 —— `PasteFloorEditorAction` /
    /// `PasteFloorWithoutDecorationsEditorAction`（普通 ctrl-V 粘砖）也调它，而且传 `overwrite: true`，
    /// 原版会先 `RemoveAll` 掉目标砖上的旧事件再贴 ⇒ "事件数增量"= 新事件数 − 旧事件数，会报出错误的个数。
    /// 而 `ADOFAI.Editor.Actions.PasteEventsEditorAction.Execute` 是**粘贴事件自己的 action**：
    /// 弹窗内粘贴走 `Run(editor, action)`，关窗后按快捷键也由键位管理器调它（同一个 action），
    /// 且它两条分支都固定 `overwrite: false`（IL：只有 `ldarg overwrite; brfalse` 跳过 RemoveAll）
    /// ⇒ 增量就是这次真正贴上去的事件数。
    ///
    /// 手法（与 PACL2 给 `CopyFloor` 挂提示同款）：Prefix 记下**整关**事件总数，Postfix 再数一次，
    /// 差值 `> 0` 才弹（剪贴板为空 / 选中为空 / 被原版的 solo 规则跳过 ⇒ 不弹）。
    /// 用整关而不是"目标砖"的增量：一次粘贴可能铺到多块砖上（见 <see cref="PagerClipboard.CountAllEvents"/>，§39 M6）。
    /// PACL2 没有给粘贴挂提示（IL 核过：它的 ShowNotification 只在 CopyFloor / MultiCopyFloors /
    /// DeleteMultiSelection 等处），因此两种环境下都不会重复。
    /// </summary>
    [HarmonyPatch(typeof(ADOFAI.Editor.Actions.PasteEventsEditorAction), "Execute")]
    internal static class PasteEventsNotifyPatch
    {
        /// <summary>`ref __state` 传的状态：粘贴前**整关**事件总数（-1 = 没读到，Postfix 就不报）。</summary>
        internal sealed class PasteState
        {
            internal int Before = -1;
        }

        internal static void Prefix(scnEditor editor, ref PasteState __state)
        {
            __state = new PasteState();
            if (editor == null)
                return;
            __state.Before = PagerClipboard.CountAllEvents(editor);
        }

        internal static void Postfix(scnEditor editor, PasteState __state)
        {
            if (editor == null || __state == null || __state.Before < 0)
                return;
            int after = PagerClipboard.CountAllEvents(editor);
            if (after > __state.Before)
                PagerClipboard.NotifyPasted(after - __state.Before);
        }
    }
}
