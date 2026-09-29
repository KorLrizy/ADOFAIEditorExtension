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

                // 原版方法体末尾：adjustRect 时对"过滤结果的第一个"请求滚动（RefreshScrollRectPosition，IL 已核），
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
                    if (filtered.Count > 0)
                    {
                        ___cacheEventForRectAdjust = filtered[0];
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
            /// 原版方法体第一句把内容高度设成 `itemHeight * filteredEvents.Count`（只算行、不算组头），
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
                // "写入关卡文件"关闭时：先把 data 里带来的归属读进会话表，再剥离 data 里的归属键
                // （保存出来的 .adofai 不含它们，当前会话的分组也不会丢）
                if (!Main.WriteGroupConfig)
                    DecoGroupState.MoveWrittenKeysToSession();
                // 面板底部工具栏里挂一个“分组方式”按钮（原版按钮 prefab + 版本风格文字）
                DecoGroupModeButton.Attach(__instance);
            }
        }

        /// <summary>编辑器（重新）Awake：上一个关卡的 LevelEvent 全部作废，会话内的手动归属表跟着清空。</summary>
        [HarmonyPatch(typeof(scnEditor), "Awake")]
        internal static class EditorAwakeSessionPatch
        {
            internal static void Prefix()
            {
                DecoGroupState.ClearSession();
            }
        }

        /// <summary>
        /// 读档完成（编辑器打开关卡走 scnEditor.OpenLevelCo → scnGame.LoadLevel，IL 已核）：
        /// 换了一批 LevelEvent ⇒ 重置会话表；写入开关关着时把文件里的归属搬进会话表并剥离 data。
        /// 不挂在 LevelData.Decode 上：导出（GetExportLevelFiles）、读关卡名（GetCustomLevelName）也会调它，
        /// 解的是别的 LevelData，不能拿来清当前关卡的状态。
        /// </summary>
        [HarmonyPatch(typeof(scnGame), "LoadLevel")]
        internal static class LevelLoadPatch
        {
            internal static void Postfix(scnGame __instance, bool __result)
            {
                if (!__result || __instance == null)
                    return;
                try
                {
                    DecoGroupState.OnLevelLoaded(__instance.levelData);
                }
                catch (Exception e)
                {
                    Main.Logger?.Log("读档后重置分组会话状态失败: " + e.Message);
                }
            }
        }
    }
}
