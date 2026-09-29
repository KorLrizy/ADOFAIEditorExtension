using ADOFAI;
using ADOFAI.LevelEditor.Controls;
using ADOFAIEditorExtension.Utils;
using HarmonyLib;
using System;
using System.Collections.Generic;

namespace ADOFAIEditorExtension.Features.DecoGrouping
{
    /// <summary>
    /// 装饰栏分组的补丁集（补丁表见 docs/分组功能设计.md §3.3）。
    /// 关闭分组开关时所有补丁直通原版逻辑。
    /// </summary>
    internal static class DecoGroupingPatches
    {
        /// <summary>搜索过滤完成后：重排 filteredEvents 为分组顺序、剔除折叠组的行、生成显示槽位。</summary>
        [HarmonyPatch(typeof(PropertyControl_DecorationsList), "FilterSearchResults")]
        internal static class FilterSearchResultsPatch
        {
            internal static void Postfix(PropertyControl_DecorationsList __instance, bool adjustRect,
                ref bool ___applyRefreshScrollRect, ref LevelEvent ___cacheEventForRectAdjust)
            {
                DecoGroupRenderer.SyncReorderable(__instance);
                if (!Main.IsDecoGroupingEnabled)
                {
                    DecoGroupState.ResetRenderState();
                    return;
                }

                // 原版方法体末尾：adjustRect 时对"过滤结果的第一个"请求滚动（RefreshScrollRectPosition，r148 IL 已核），
                // 下一帧 LateUpdate → AdjustItemListScrollRect(LevelEvent)。那个事件是**分组重排之前**的第一个，
                // 可能落在折叠组里 ⇒ 我们的 AdjustScrollByEventPatch 会把组展开（"打字搜索自动展开折叠组"）。
                // 这里记下它，重排后把请求改成"分组后的第一行"；一行都没有（全折叠）就撤销这次请求。
                List<LevelEvent> filtered = adjustRect ? __instance.Get<List<LevelEvent>>("filteredEvents") : null;
                LevelEvent searchScrollTarget = filtered != null && filtered.Count > 0 ? filtered[0] : null;

                DecoGroupRenderer.Build(__instance);

                if (searchScrollTarget != null && ___applyRefreshScrollRect
                    && ReferenceEquals(___cacheEventForRectAdjust, searchScrollTarget)
                    && DecoGroupState.Slots.Count > 0)
                {
                    // Build 原地重排 filteredEvents（同一个 List），此时它只含可见行
                    List<LevelEvent> rows = __instance.Get<List<LevelEvent>>("filteredEvents");
                    if (rows != null && rows.Count > 0)
                    {
                        ___cacheEventForRectAdjust = rows[0];
                    }
                    else
                    {
                        ___applyRefreshScrollRect = false;
                        ___cacheEventForRectAdjust = null;
                    }
                }
            }
        }

        /// <summary>分组开启时完全接管渲染（复制原版 ApplyUpdateList 方法体，数据源换成槽位）。</summary>
        [HarmonyPatch(typeof(PropertyControl_List), "ApplyUpdateList")]
        internal static class ApplyUpdateListPatch
        {
            internal static bool Prefix(PropertyControl_List __instance, bool forceRefreshAll)
            {
                if (!(__instance is PropertyControl_DecorationsList))
                    return true;
                if (!Main.IsDecoGroupingEnabled)
                    return true;
                if (DecoGroupState.Slots.Count == 0)
                    return true;
                // 渲染不了（itemHeight 读不到等）就落回原版：宁可没有组头，也别把列表画空
                return !DecoGroupRenderer.ApplyUpdateList(__instance, forceRefreshAll);
            }
        }

        /// <summary>ClearShownItems 会把 contentRT 的所有子对象塞回游戏的 ListItemPool——头行必须先摘出去。</summary>
        [HarmonyPatch(typeof(PropertyControl_List), "ClearShownItems")]
        internal static class ClearShownItemsPatch
        {
            internal static void Prefix(PropertyControl_List __instance)
            {
                if (!(__instance is PropertyControl_DecorationsList))
                    return;
                DecoGroupRenderer.DetachHeaders();
            }
        }

        /// <summary>
        /// 滚动到指定装饰前：目标在折叠组里就先展开（否则它在 filteredEvents 里找不到，定位会落空）。
        /// 只剩"明确指向某个装饰"的请求会走到这里（选中 / 粘贴等）：搜索框打字触发的那一次
        /// 已在 FilterSearchResultsPatch 里改成分组后的第一行，不会再把折叠组撑开。
        /// </summary>
        [HarmonyPatch(typeof(PropertyControl_List), "AdjustItemListScrollRect", new Type[] { typeof(LevelEvent) })]
        internal static class AdjustScrollByEventPatch
        {
            internal static void Prefix(PropertyControl_List __instance, LevelEvent levelEvent)
            {
                if (levelEvent == null || !Main.IsDecoGroupingEnabled)
                    return;
                if (!DecoGroupState.GroupKeyOfEvent.TryGetValue(levelEvent, out string key) || key == null)
                    return;
                DecoGroupState.CollapsedGroups.Remove(key);
                // 原版方法体自己会调 FilterSearchResults 重建列表，这里只负责把折叠组打开
            }
        }

        /// <summary>滚动到第 N 行时，把 filteredEvents 下标翻译成显示槽位下标（头行占位）。</summary>
        [HarmonyPatch(typeof(PropertyControl_List), "AdjustItemListScrollRect", new Type[] { typeof(int) })]
        internal static class AdjustScrollByIndexPatch
        {
            internal static bool Prefix(PropertyControl_List __instance, ref int decorationIndex)
            {
                if (!(__instance is PropertyControl_DecorationsList))
                    return true;
                if (!Main.IsDecoGroupingEnabled)
                    return true;
                int[] map = DecoGroupState.SlotIndexByRowIndex;
                if (map.Length == 0)
                {
                    // 有分组但一行都没显示（比如全部折叠）：没有可滚动的目标，别走原版
                    if (DecoGroupState.Slots.Count == 0)
                        return true;
                    DecoGroupRenderer.ClearScrollAdjust(__instance);
                    return false;
                }
                if (decorationIndex < 0 || decorationIndex >= map.Length)
                {
                    DecoGroupRenderer.ClearScrollAdjust(__instance);
                    return false;
                }
                decorationIndex = map[decorationIndex];
                return true;
            }

            /// <summary>
            /// 原版方法体第一句把内容高度设成 `itemHeight * filteredEvents.Count`（只算行、不算组头，r148 IL 已核），
            /// 紧接着 ScrollTo 槽位下标 ⇒ 高度不够时 ScrollRect 会把位置夹回去，滚动停在目标前面。
            /// 这里在同一帧里马上把高度改回"槽位数 × 行高"（与我们的 ApplyUpdateList 一致）。
            /// </summary>
            internal static void Postfix(PropertyControl_List __instance)
            {
                if (!(__instance is PropertyControl_DecorationsList))
                    return;
                if (!Main.IsDecoGroupingEnabled)
                    return;
                DecoGroupRenderer.RestoreContentHeight(__instance);
            }
        }

        /// <summary>
        /// shift 范围选择：分组显示下改成按**显示顺序**取范围（原版按全关卡装饰数组下标 ⇒ 与分组后的
        /// 屏幕顺序不符，会把别组的装饰一起选进来，见 §30.1）。
        /// </summary>
        [HarmonyPatch(typeof(PropertyControl_List), "SelectItemsInRange")]
        internal static class SelectItemsInRangePatch
        {
            internal static bool Prefix(PropertyControl_List __instance, LevelEvent endRangeItem)
            {
                if (!(__instance is PropertyControl_DecorationsList))
                    return true;
                if (!Main.IsDecoGroupingEnabled)
                    return true;
                if (DecoGroupState.Slots.Count == 0)
                    return true;
                // 处理不了就返回 true，落回原版逻辑
                return !DecoGroupActions.SelectDisplayRange(__instance, endRangeItem);
            }
        }

        /// <summary>面板重新 Start（含场景重载）：清掉指向旧对象的头行池，并同步拖拽重排开关。</summary>
        [HarmonyPatch(typeof(PropertyControl_DecorationsList), "Start")]
        internal static class DecorationsListStartPatch
        {
            internal static void Postfix(PropertyControl_DecorationsList __instance)
            {
                DecoGroupRenderer.ResetStaticState();
                DecoGroupRenderer.SyncReorderable(__instance);
                // 归属键一直留在关卡 data 里（撤销/重做跟着走）；"写入关卡文件"关闭时只是保存输出里不写它们
                // （由 LevelEvent.Encode 后置补丁剥离），这里不再动 data。
                // 面板底部工具栏里挂一个“分组方式”按钮（原版按钮 prefab + 版本风格文字）
                DecoGroupModeButton.Attach(__instance);
            }
        }
    }
}
