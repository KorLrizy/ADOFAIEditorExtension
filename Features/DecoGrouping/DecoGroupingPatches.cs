using ADOFAI;
using ADOFAI.LevelEditor.Controls;
using HarmonyLib;
using System;

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
            internal static void Postfix(PropertyControl_DecorationsList __instance)
            {
                DecoGroupRenderer.SyncReorderable(__instance);
                if (!Main.IsDecoGroupingEnabled)
                {
                    DecoGroupState.ResetRenderState();
                    return;
                }
                DecoGroupRenderer.Build(__instance);
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
                DecoGroupRenderer.ApplyUpdateList(__instance, forceRefreshAll);
                return false;
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

        /// <summary>滚动到指定装饰前：目标在折叠组里就先展开（否则它在 filteredEvents 里找不到，定位会落空）。</summary>
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
                // "写入关卡文件"关闭时：把 data 里我们的归属键剥离干净（保存出来的 .adofai 不含它们）
                if (!Main.WriteGroupConfig)
                    DecoGroupState.PurgeWrittenKeys();
                // 面板底部工具栏里挂一个“分组方式”按钮（原版按钮 prefab + 版本风格文字）
                DecoGroupModeButton.Attach(__instance);
            }
        }
    }
}
