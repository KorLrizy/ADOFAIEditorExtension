using ADOFAI;
using ADOFAI.LevelEditor.Controls;
using HarmonyLib;
using System;

namespace ADOFAIEditorExtension.Features.PagerList
{
    /// <summary>
    /// 分页器直选列表的补丁集（见 docs/分组功能设计.md §3.4）。
    /// 原版箭头（CycleButtons.CycleEvent）逻辑完全不动。
    /// </summary>
    internal static class PagerListPatches
    {
        /// <summary>
        /// 多选批量编辑：把"用户刚改的那个键"逐事件写回（带键，§29.2/§31.1/§32.2）。
        ///
        /// **挂 `PropertyControl.OnValueChange`**：IL 扫过所有 `PropertyControl_*` 的写值回调，结论是
        /// —— 除了 `File::OnRightClick` / `Toggle::OggEncodeCallback` 这两个异步/弹窗路径，
        /// **其它全部都会调 `OnValueChange`**（`Bool::SetValue`、`Color::OnChange`、`Text::&lt;Setup&gt;b__17_1`、
        /// `Toggle::SelectVar`、`LongText`、以及**不调 `ApplyTileChanges`** 的那几个：
        /// `FloatPair::SaveWithoutRecording`、`MinMaxGradient::Save`、`Rating::SetInt`、
        /// `Vector2`/`Vector2Range::SetVectorVals`、`File::ProcessFile`、`Toggle::ProcessFile`）。
        /// 原先挂在 `ApplyTileChanges` 上，正好漏掉后一半（字符串类/tag 就死在这里），
        /// 所以换到"所有写值路径的公共出口"上。
        /// </summary>
        [HarmonyPatch(typeof(PropertyControl), "OnValueChange")]
        internal static class MultiSelectValueChangedPatch
        {
            internal static void Postfix(PropertyControl __instance)
            {
                PagerListController.ApplyFakeToRealEvents(PropertyNameOf(__instance));
            }
        }

        /// <summary>
        /// 兜底：**写进我们 fake 的任何一次 `LevelEvent.set_Item`** 都触发逐键写回（§32.2）。
        ///
        /// 这一层专门覆盖绕过 `OnValueChange` 的路径（OGG 编码回调、文件处理回调之类，将来游戏加新控件也接得住），
        /// 以及"某个控件一次改多个键"的情况。`ApplyFakeToRealEvents` 自带幂等检查
        /// （值已经一致就什么都不做）⇒ 两个钩子对同一次编辑各跑一次也不会重复写、不会多出撤销点。
        /// 只在写的是我们的 fake 时动手：真实事件的写值（含原版/PACL2 的整体写回）一律不管。
        /// </summary>
        [HarmonyPatch(typeof(LevelEvent), "set_Item")]
        internal static class FakeValueWrittenPatch
        {
            internal static void Postfix(LevelEvent __instance, string key)
            {
                PagerListController.OnFakeValueWritten(__instance, key);
            }
        }

        /// <summary>控件对应的属性键（拿不到就返回 null ⇒ 写回走"整体写回"的老路）。</summary>
        internal static string PropertyNameOf(PropertyControl control)
        {
            try
            {
                return control != null && control.propertyInfo != null ? control.propertyInfo.name : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// 分页器箭头（◀ ▶）切事件：切到批量范围之外就退出多选（§34.3）。
        /// 原版箭头按钮的回调就是 `CycleButtons.CycleEvent(bool)`。
        /// </summary>
        [HarmonyPatch(typeof(CycleButtons), "CycleEvent")]
        internal static class PagerArrowCyclePatch
        {
            internal static void Postfix(CycleButtons __instance)
            {
                PagerListController.OnPagerArrowCycled(__instance);
            }
        }

        /// <summary>
        /// 撤销 / 重做后重建批量面板（§34.5：值被回滚、事件对象可能被换掉 ⇒ 混合标记要重算，
        /// 全失效就退出多选）。
        /// </summary>
        [HarmonyPatch(typeof(scnEditor), "Undo")]
        internal static class UndoPatch
        {
            internal static void Postfix()
            {
                PagerListController.OnLevelUndoRedo("撤销");
            }
        }

        [HarmonyPatch(typeof(scnEditor), "Redo")]
        internal static class RedoPatch
        {
            internal static void Postfix()
            {
                PagerListController.OnLevelUndoRedo("重做");
            }
        }

        /// <summary>
        /// 每帧的收尾（多选数量提示的去抖，§34.4）：挂在编辑器右侧面板的 `Update` 上，
        /// 里面只做一个时间比较，平时什么都不干。
        /// </summary>
        [HarmonyPatch(typeof(InspectorPanel), "Update")]
        internal static class InspectorPanelTickPatch
        {
            internal static void Postfix()
            {
                PagerListController.Tick();
            }
        }

        /// <summary>
        /// 每个标签页创建时，给分页器文本（以及分页器根对象）挂上左键点击入口。
        /// 这是唯一的注入点：InspectorPanel.Init → InspectorTab.Init 对每个标签页都会走一次。
        /// </summary>
        [HarmonyPatch(typeof(InspectorTab), "Init")]
        internal static class InspectorTabInitPatch
        {
            internal static void Postfix(InspectorTab __instance)
            {
                PagerListController.AttachToTab(__instance);
            }
        }

        /// <summary>选中的砖变化时关掉列表（避免列表还停留在旧的堆叠上）；顺便作废挂起的多选（§32.1）。</summary>
        [HarmonyPatch(typeof(scnEditor), "OnSelectedFloorChange")]
        internal static class SelectedFloorChangePatch
        {
            internal static void Postfix()
            {
                PagerListController.OnSelectedFloorChanged();
            }
        }

        /// <summary>
        /// 面板切换到**别的类型/别的标签页**时关掉列表；同一类型的刷新（我们自己的选事件/多选刷新、
        /// 原版重渲染）不关 —— 否则弹窗内点一下就把自己关掉了（§26.2）。
        /// </summary>
        [HarmonyPatch(typeof(InspectorPanel), "ShowPanel")]
        internal static class ShowPanelClosePatch
        {
            internal static void Postfix(LevelEventType eventType)
            {
                PagerListController.CloseIfOpenOnOtherPanel(eventType);
                // 批量态活着时，原版这次 ShowPanel 可能把面板切成了单事件 ⇒ 挂回批量视图（§34.2）
                PagerListController.EnsureBatchPanelBound();
            }
        }

        /// <summary>
        /// 弹窗打开时接管"复制/剪切/粘贴事件"这几条快捷键（§24）。
        ///
        /// 原版 `HandleKeyboardActions` 的第一句是 `if (showingPopup) { 只处理 Esc; return; }`，
        /// 而本弹窗就是 `scnEditor.ShowPopup` 打开的 ⇒ 弹窗期间原版**所有**快捷键都不执行。
        /// 这里在它之前补上我们自己的处理（只在我们弹窗开着、且确实处于 showingPopup 状态时动手，
        /// 免得与原版路径重复执行）。
        /// </summary>
        [HarmonyPatch(typeof(scnEditor), "HandleKeyboardActions")]
        internal static class PagerKeybindPatch
        {
            internal static void Prefix()
            {
                // 这个前缀每帧都会跑（`scnEditor.Update` → `HandleKeyboardActions`；原版那条
                // "showingPopup 时只处理 Esc" 的分支在方法体里，我们跑在它之前），
                // 所以顺手当"每帧收尾"用：
                //  ① 冲掉待弹的数量提示 + 补做欠一次的批量存活校验（§38.1 / §39 H3）；
                //  ② 接管 Esc 关窗（§38.2：原版只收它自己的弹窗状态，我们这块自建窗口它管不到）；
                //  ③ 自检"showingPopup 卡住"（§42 bug 2：只有状态变化才记一条，不每帧刷）。
                // **整体兜异常**：前缀里抛出去，Harmony 会连原版方法体一起跳过 ⇒ 表现为
                // "复制/粘贴/Esc 全都不通"，跟我们的代码看起来毫无关系（§39 M4）。
                try
                {
                    PagerListController.Tick();
                    PagerListController.WarnIfPopupFlagStuck();
                    PagerListController.HandlePopupEscape();
                    PagerClipboard.HandleKeybinds();
                }
                catch (Exception e)
                {
                    Main.Logger?.Log("分页器快捷键前缀失败（已吞掉，原版快捷键照常）: " + e);
                }
            }
        }

        /// <summary>
        /// 批量态的存活校验（§39 H3）：事件被**绕开剪贴板与撤销**的路径删掉之后（Delete 键、删砖、
        /// 整砖粘贴、其它模组），fake 的 `realEvents` 里会留下僵尸引用。这四个出口是 IL 扫 `scnEditor`
        /// 全量方法体得出的"增删事件的公共出口"：
        ///  · `RemoveEvent` —— 单个事件删除（`RemoveEvents` / `CutDecoration` / `RemoveEventAtSelected` 都走它）；
        ///  · `DeleteFloor` —— 删砖（直接 `events.RemoveAll`，**不**经过 `ApplyEventsToFloors`，IL 核过）；
        ///  · `ApplyEventsToFloors` —— 增/剪/贴与筛选的收尾（原版一半以上的改动都收在这）；
        ///  · `NewLevel` —— 建新关卡（旧事件全没了，而编辑器对象不重建，`Reset` 那套路径不会跑）。
        /// 校验本身见 <see cref="PagerListController.VerifyBatchEventsAlive"/>：撤销作用域还开着时只记账，
        /// 下一帧再查，免得在半途的关卡上误判成"事件全死了"。
        /// </summary>
        [HarmonyPatch(typeof(scnEditor), "ApplyEventsToFloors", new Type[0])]
        [HarmonyPatch(typeof(scnEditor), "RemoveEvent", new[] { typeof(LevelEvent), typeof(bool) })]
        [HarmonyPatch(typeof(scnEditor), "DeleteFloor", new[] { typeof(int), typeof(bool) })]
        [HarmonyPatch(typeof(scnEditor), "NewLevel", new Type[0])]
        internal static class BatchEventsLivenessPatch
        {
            internal static void Postfix()
            {
                PagerListController.VerifyBatchEventsAlive();
            }
        }
    }
}
