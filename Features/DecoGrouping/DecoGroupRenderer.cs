using ADOFAI;
using ADOFAI.LevelEditor.Controls;
using ADOFAIEditorExtension.Utils;
using HarmonyLib;
using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ADOFAIEditorExtension.Features.DecoGrouping
{
    /// <summary>头行的可点区域。</summary>
    internal enum GroupHitArea
    {
        Arrow,
        Name
    }

    /// <summary>头行对象（不经过游戏的 ListItemPool，自带一个池）。</summary>
    internal sealed class HeaderRow
    {
        internal GameObject GameObject;
        internal RectTransform Rect;
        internal TMP_Text Label;        // 组名 + 数量
        internal TMP_Text Arrow;        // 展开/收起标记
        internal Button EyeButton;
        internal Image EyeImage;
        internal Button LockButton;
        internal Image LockImage;
        internal Transform Background;
        internal Color OriginalLabelColor;
        internal bool OriginalBackgroundActive;
        internal DecoGroupHeader Marker;
    }

    /// <summary>挂在分组头行上，记住自己是哪个分组；箭头/组名/眼睛/锁四个区域各对应一个动作。</summary>
    internal sealed class DecoGroupHeader : MonoBehaviour
    {
        internal string GroupKey;

        // 模组在 UMM 里被关掉、但编辑器没能重启（用户在"未保存"提示里取消）时补丁已经卸了，
        // 这些残留的按钮回调不能再动数据 ⇒ 一律先看 Main.IsEnabled。
        internal void OnArrow()
        {
            if (Main.IsEnabled)
                DecoGroupActions.ToggleCollapse(GroupKey);
        }

        internal void OnName()
        {
            if (Main.IsEnabled)
                DecoGroupActions.SelectGroupAndShowPanel(GroupKey);
        }

        internal void OnEye()
        {
            if (Main.IsEnabled)
                DecoGroupActions.ToggleVisible(GroupKey);
        }

        internal void OnLock()
        {
            if (Main.IsEnabled)
                DecoGroupActions.ToggleLock(GroupKey);
        }
    }

    /// <summary>头行上的点击目标（箭头 / 组名），点击后消费事件避免冒泡到标签页。</summary>
    internal sealed class GroupHeaderClickTarget : MonoBehaviour, IPointerClickHandler
    {
        internal DecoGroupHeader Owner;
        internal GroupHitArea Area;

        public void OnPointerClick(PointerEventData eventData)
        {
            if (!Main.IsEnabled)
                return;   // 模组已关闭（补丁已卸）：残留的头行不再响应，也不吞事件
            eventData?.Use();
            if (Owner == null)
                return;
            if (Area == GroupHitArea.Arrow)
                Owner.OnArrow();
            else
                Owner.OnName();
        }
    }

    /// <summary>
    /// 装饰栏分组的渲染层：
    ///  · Build()——在 FilterSearchResults 之后按分组重排 filteredEvents 并生成显示槽位；
    ///  · ApplyUpdateList()——完全接管 PropertyControl_List.ApplyUpdateList 的建行/摆行逻辑（照原版 IL 复制），
    ///    普通行照旧走游戏的 ListItemPool，头行走本类自建的池。
    /// </summary>
    internal static class DecoGroupRenderer
    {
        // 折叠标记（用转义写，避免源文件编码影响字形）。
        // 首选 ▾/▸（U+25BE/U+25B8），但游戏字体不一定有这两个码位（v2 实测 CJK 字体都没有），
        // TMP 遇到缺字不报错、直接画一个方框。所以 CreateArrow 里按字体自检：有就用首选，
        // 没有就回退到 ▼/▶（U+25BC/U+25B6，覆盖面广得多）。
        private const string PreferredExpandedMark = "\u25BE";   // ▾
        private const string PreferredCollapsedMark = "\u25B8";  // ▸
        private const string ExpandedMark = "\u25BC";             // ▼
        private const string CollapsedMark = "\u25B6";            // ▶

        /// <summary>当前字体自检后的实际字形（由 <see cref="ResolveArrowMarks"/> 填，默认就是回退字形）。</summary>
        private static string resolvedExpandedMark = ExpandedMark;
        private static string resolvedCollapsedMark = CollapsedMark;
        private static TMP_FontAsset resolvedMarkFont;

        private const float ArrowWidth = 22f;

        private static readonly List<HeaderRow> headerPool = new List<HeaderRow>();
        private static bool iconsCached;
        private static Sprite eyeOpenSprite;
        private static Sprite eyeClosedSprite;
        private static Sprite lockOpenSprite;
        private static Sprite lockClosedSprite;
        private static readonly Dictionary<string, HeaderRow> activeHeaders = new Dictionary<string, HeaderRow>();
        private static readonly HashSet<string> usedHeaderKeys = new HashSet<string>();
        private static Transform headerHolder;

        private sealed class GroupBucket
        {
            internal string Key;
            internal string Label;
            /// <summary>成员为 0 时也保留这个桶（自定义分组用：空组也要显示出来，才能往里拖）。</summary>
            internal bool KeepWhenEmpty;
            internal readonly List<LevelEvent> Events = new List<LevelEvent>();
        }

        // ---------------------------------------------------------------- 静态状态重置

        /// <summary>
        /// 编辑器（重新）加载：头行与面板引用随场景销毁，池必须清空。
        /// 也由 StopMod 调用（此时 Main.IsEnabled 已是 false、补丁已卸）：编辑器可能没能重启
        /// （用户取消了"未保存"提示），场景里的头行/按钮都还在 ⇒ 先把它们拆掉、恢复原版列表。
        /// </summary>
        internal static void ResetStaticState()
        {
            if (!Main.IsEnabled)
                TearDownLiveObjects();
            headerPool.Clear();
            activeHeaders.Clear();
            usedHeaderKeys.Clear();
            headerHolder = null;
            cachedCanvas = null;
            iconsCached = false;
            resolvedMarkFont = null;
            resolvedExpandedMark = ExpandedMark;
            resolvedCollapsedMark = CollapsedMark;
            eyeOpenSprite = eyeClosedSprite = lockOpenSprite = lockClosedSprite = null;
            dropLine = null;
            cursorMark = null;
            highlightedRow = null;
            dropObjectsResolved = false;
            DecoGroupState.ResetRenderState();
        }

        /// <summary>
        /// 模组被关掉而编辑器还活着：销毁我们建的头行/落点线/分组方式按钮，恢复原版拖拽重排，
        /// 并让原版把列表重建一遍（补丁此时已卸，FilterSearchResults/ApplyUpdateList 都是原版的）。
        /// 头行要先摘出 contentRT 再 Destroy：Destroy 是帧末才生效，还挂在 contentRT 下的话会被
        /// 原版 ClearShownItems 塞进 ListItemPool，池里就多了一个即将被销毁的对象。
        /// </summary>
        private static void TearDownLiveObjects()
        {
            try
            {
                PropertyControl_DecorationsList panel = FindPanel();
                var headers = new List<HeaderRow>(headerPool);
                headers.AddRange(activeHeaders.Values);
                foreach (HeaderRow header in headers)
                    DestroyDetached(header != null ? header.GameObject : null);
                DestroyDetached(headerHolder != null ? headerHolder.gameObject : null);
                DestroyDetached(dropLine != null ? dropLine.gameObject : null);
                DestroyDetached(cursorMark != null ? cursorMark.gameObject : null);
                if (highlightedRow != null)
                    highlightedRow.ShowHighlight(false);
                DecoGroupModeButton.Detach();

                if (panel != null)
                {
                    panel.itemsReorderable = true;
                    string search = panel.searchField != null ? panel.searchField.text : "";
                    panel.Method("FilterSearchResults", new object[] { search, false });
                    panel.RefreshItemsList(true);
                }
            }
            catch (Exception e)
            {
                Main.Logger?.Log("关闭模组时清理装饰栏分组对象失败: " + e.Message);
            }
        }

        private static void DestroyDetached(GameObject go)
        {
            if (go == null)
                return;
            go.SetActive(false);
            go.transform.SetParent(null, false);
            UnityEngine.Object.Destroy(go);
        }

        // ---------------------------------------------------------------- 分组构建

        /// <summary>按当前设置把 filteredEvents 重排成分组顺序，并生成显示槽位。</summary>
        internal static void Build(PropertyControl_DecorationsList panel)
        {
            DecoGroupState.ResetRenderState();
            DecoGroupState.Panel = panel;
            if (panel == null)
                return;

            List<LevelEvent> events = panel.Get<List<LevelEvent>>("filteredEvents");
            if (events == null)
                return;

            List<GroupBucket> buckets = BuildBuckets(events);
            if (buckets.Count == 0)
                return;

            var slots = DecoGroupState.Slots;
            Dictionary<string, List<LevelEvent>> eventsByGroup = DecoGroupState.EventsByGroup;
            foreach (GroupBucket bucket in buckets)
            {
                bool collapsed = DecoGroupState.CollapsedGroups.Contains(bucket.Key);
                // 组头操作（全选/可见/锁定）要能拿到折叠组里的装饰，所以这里记录完整成员表
                eventsByGroup[bucket.Key] = new List<LevelEvent>(bucket.Events);
                slots.Add(new DecoGroupSlot
                {
                    IsHeader = true,
                    Key = bucket.Key,
                    Label = bucket.Label,
                    Count = bucket.Events.Count
                });
                foreach (LevelEvent e in bucket.Events)
                {
                    DecoGroupState.GroupKeyOfEvent[e] = bucket.Key;
                    if (!collapsed)
                        slots.Add(new DecoGroupSlot { IsHeader = false, Event = e });
                }
            }

            // filteredEvents 重排为行槽位顺序（折叠组的事件被移除，与原版"行顺序 = filteredEvents 顺序"保持一致）
            int[] slotIndexByRow = new int[slots.Count];
            int rowIndex = 0;
            for (int i = 0; i < slots.Count; i++)
                if (!slots[i].IsHeader)
                    slotIndexByRow[rowIndex++] = i;

            events.Clear();
            var visible = DecoGroupState.VisibleEvents;
            visible.Clear();
            for (int i = 0; i < slots.Count; i++)
            {
                if (slots[i].IsHeader)
                    continue;
                events.Add(slots[i].Event);
                visible.Add(slots[i].Event);
            }

            if (rowIndex == slotIndexByRow.Length)
            {
                DecoGroupState.SlotIndexByRowIndex = slotIndexByRow;
            }
            else
            {
                var map = new int[rowIndex];
                Array.Copy(slotIndexByRow, map, rowIndex);
                DecoGroupState.SlotIndexByRowIndex = map;
            }
        }

        private static List<GroupBucket> BuildBuckets(List<LevelEvent> events)
        {
            var result = new List<GroupBucket>();

            // 1. 自定义分组（按定义顺序；只有填了 tag 的分组才按 tag 精确匹配）
            List<(string Name, string Tag)> customGroups = DecoGroupState.ReadCustomGroups(DecoGroupState.GroupSet.Decoration);
            var customBuckets = new List<GroupBucket>();
            for (int i = 0; i < customGroups.Count; i++)
            {
                // 名字与 tag 都空 = 用户还没填的行，跳过（不显示、不参与匹配）
                if (customGroups[i].Tag.Length == 0 && string.IsNullOrEmpty(customGroups[i].Name))
                {
                    customBuckets.Add(null);
                    continue;
                }
                var bucket = new GroupBucket
                {
                    Key = "custom:" + i,
                    Label = string.IsNullOrEmpty(customGroups[i].Name) ? customGroups[i].Tag : customGroups[i].Name,
                    // 自定义分组即使 0 个成员也要显示：空组是拖拽落点（用户会往新建的空组里拖）
                    KeepWhenEmpty = true
                };
                customBuckets.Add(bucket);
                result.Add(bucket);
            }

            // 兜底组 = 第一个 tag 留空的自定义分组（只在自定义模式下接管"剩下的装饰"，见 §14.1）
            int fallbackIndex = DecoGroupState.FirstFallbackGroupIndex(customGroups);

            // 2. 自动分组
            AutoGroupMode mode = Main.AutoGroupMode;
            bool byTag = mode == AutoGroupMode.ByTag;
            bool customOnly = mode == AutoGroupMode.Custom;
            var autoOrder = new List<GroupBucket>();
            var autoByKey = new Dictionary<string, GroupBucket>(StringComparer.Ordinal);
            GroupBucket untagged = null;

            // 每个装饰"按规则该属于"哪个自动组 —— 与它**当前**在哪一组无关。空桶要不要留就看这个集合，
            // 否则会出现"把一个单成员自动组里唯一的装饰拖进自定义分组后，该自动组消失、装饰再也拖不出来"
            // （§22.1）。按类型：类型是装饰固有的；按标签：tag 也是装饰固有的（未分组 = "tag:"）。
            var decoAutoKeys = new HashSet<string>(StringComparer.Ordinal);
            if (!customOnly)
            {
                foreach (LevelEvent e in events)
                {
                    if (e == null)
                        continue;
                    if (byTag)
                    {
                        // tag 命中某个自定义分组的装饰按规则属于那个自定义分组，不属于任何标签组
                        string t = GetTag(e);
                        if (t.Length == 0)
                            decoAutoKeys.Add("tag:");
                        else if (MatchCustomGroup(customGroups, t) < 0)
                            decoAutoKeys.Add("tag:" + t);
                    }
                    else
                    {
                        decoAutoKeys.Add(DecoGroupState.TypeKeyOf(e.eventType));
                    }
                }
            }

            if (customOnly)
            {
                // 自定义模式：「未分组」= 没有手动归属、也没被兜底组收走的那些。
                // 有兜底组时它必为空（兜底组全收），剔除；**没有兜底组时它是唯一的"退出手动归属"落点**，
                // 所以哪怕 0 个成员也要留着（同上，否则手动归属过的装饰就没法放回来了）。
                untagged = new GroupBucket
                {
                    Key = "custom:rest",
                    Label = L("aee.group.untagged"),
                    KeepWhenEmpty = fallbackIndex < 0
                };
            }
            else if (!byTag)
            {
                // 按类型：固定顺序（图片/文本/对象/粒子/其它）；空桶是否保留稍后按 decoAutoKeys 决定
                AddAutoBucket(autoOrder, autoByKey, "type:19", L("aee.group.type.image"));
                AddAutoBucket(autoOrder, autoByKey, "type:20", L("aee.group.type.text"));
                AddAutoBucket(autoOrder, autoByKey, "type:58", L("aee.group.type.object"));
                AddAutoBucket(autoOrder, autoByKey, "type:62", L("aee.group.type.particle"));
                AddAutoBucket(autoOrder, autoByKey, "type:other", L("aee.group.other"));
            }
            else
            {
                // 按标签：与按类型同理，**先**把"按规则有人属于"的桶都建出来（下面的循环只往里放）。
                // 以前是遇到第一个成员才懒建 ⇒ 成员全被手动拖进自定义分组后桶根本不存在，
                // 空桶保留规则无从生效：「未分组」整组消失、单成员的标签组拖不回去。
                foreach (string key in decoAutoKeys)
                {
                    if (key == "tag:")
                    {
                        untagged = new GroupBucket { Key = "tag:", Label = L("aee.group.untagged") };
                        continue;
                    }
                    string tag = key.Substring(4);   // 与下面懒建时一致：autoByKey 按裸 tag 索引
                    if (autoByKey.ContainsKey(tag))
                        continue;
                    var bucket = new GroupBucket { Key = key, Label = tag };
                    autoByKey[tag] = bucket;
                    autoOrder.Add(bucket);
                }
            }

            foreach (LevelEvent e in events)
            {
                if (e == null)
                    continue;

                // ① 手动归属优先（tag 留空的自定义分组只能靠这个记录；写 tag 会破坏用户的 tag）
                //    兜底组只在自定义模式下排除：类型/标签模式里"第一个 tag 留空组"就是普通手动组，
                //    拖进去记的就是它的下标，这里必须认（否则就会看到"拖了没反应"）。
                int manual = DecoGroupState.GetManualGroup(e, DecoGroupState.GroupSet.Decoration);
                int manualExcluded = customOnly ? fallbackIndex : -1;
                if (manual >= 0 && manual < customGroups.Count && manual != manualExcluded
                    && customGroups[manual].Tag.Length == 0 && customBuckets[manual] != null)
                {
                    customBuckets[manual].Events.Add(e);
                    continue;
                }

                string tag = GetTag(e);
                int custom = MatchCustomGroup(customGroups, tag);   // tag 可能为空串（无 tag）

                if (tag.Length == 0)
                    tag = null;
                if (custom >= 0)
                {
                    customBuckets[custom].Events.Add(e);
                    continue;
                }

                // ② 兜底组：不按 tag 过滤，收走所有还没被认领的装饰
                if (customOnly && fallbackIndex >= 0 && customBuckets[fallbackIndex] != null)
                {
                    customBuckets[fallbackIndex].Events.Add(e);
                    continue;
                }

                if (customOnly)
                {
                    untagged.Events.Add(e);
                }
                else if (byTag)
                {
                    if (tag == null)
                    {
                        untagged = untagged ?? new GroupBucket { Key = "tag:", Label = L("aee.group.untagged") };
                        untagged.Events.Add(e);
                    }
                    else
                    {
                        if (!autoByKey.TryGetValue(tag, out GroupBucket bucket))
                        {
                            bucket = new GroupBucket { Key = "tag:" + tag, Label = tag };
                            autoByKey[tag] = bucket;
                            autoOrder.Add(bucket);
                        }
                        bucket.Events.Add(e);
                    }
                }
                else
                {
                    // 类型键与上面 decoAutoKeys 用的是同一份规则（DecoGroupState.TypeKeyOf）
                    if (autoByKey.TryGetValue(DecoGroupState.TypeKeyOf(e.eventType), out GroupBucket bucket))
                        bucket.Events.Add(e);
                }
            }

            // 按 tag 分组时：tag 字典序，未分组排最后（自定义模式同理，未命中项排最后）
            if (byTag)
                autoOrder.Sort((a, b) => string.CompareOrdinal(a.Label, b.Label));
            if (untagged != null)
                autoOrder.Add(untagged);

            // 自动分组的空桶：只要"还有装饰按规则属于它"就保留（组头仍在、显示 (0)），
            // 这样它继续是有效落点；真正没人属于的自动组（例如全场没有图片装饰）才剔除。
            // 按类型 / 按标签的桶都已预先建好，所以这条规则对两种模式都生效。
            foreach (GroupBucket bucket in autoOrder)
            {
                if (bucket.Events.Count == 0 && decoAutoKeys.Contains(bucket.Key))
                    bucket.KeepWhenEmpty = true;
            }

            result.AddRange(autoOrder);
            // 自动分组空桶剔除；自定义分组（用户定义的行）即使 0 个成员也留着
            result.RemoveAll(bucket => !bucket.KeepWhenEmpty && bucket.Events.Count == 0);
            return result;
        }

        private static void AddAutoBucket(List<GroupBucket> order, Dictionary<string, GroupBucket> byKey, string key, string label)
        {
            var bucket = new GroupBucket { Key = key, Label = label };
            byKey[key] = bucket;
            order.Add(bucket);
        }

        /// <summary>
        /// 自定义分组的 tag 匹配（按定义顺序，先命中先归）：**只有填了 tag 的分组才参与匹配**。
        /// tag 留空的分组不再按"无 tag"匹配 —— 第一个是兜底组（收剩下的），其余是只能拖入的成员组，
        /// 见设计文档 §14.1。
        /// </summary>
        private static int MatchCustomGroup(List<(string Name, string Tag)> customGroups, string tag)
        {
            if (string.IsNullOrEmpty(tag))
                return -1;
            for (int i = 0; i < customGroups.Count; i++)
            {
                string groupTag = customGroups[i].Tag;
                if (groupTag.Length == 0)
                    continue;
                if (string.Equals(groupTag, tag, StringComparison.Ordinal))
                    return i;
            }
            return -1;
        }

        private static string GetTag(LevelEvent e)
        {
            try
            {
                if (e.TryGet<string>("tag", out string tag))
                    return tag ?? "";
            }
            catch { }
            return "";
        }

        // ---------------------------------------------------------------- 渲染（接管 ApplyUpdateList）

        /// <summary>
        /// 照 reverse/scratch/grp/il_ApplyUpdateList.txt 复制原版方法体，只把 filteredEvents[i] 换成槽位。
        /// 返回 false = 这次渲染不了（取不到 shownItems / itemHeight 无效等），调用方应落回原版方法体，
        /// 否则列表会整个空掉。
        /// </summary>
        internal static bool ApplyUpdateList(PropertyControl_List list, bool forceRefreshAll)
        {
            List<DecoGroupSlot> slots = DecoGroupState.Slots;
            if (slots.Count == 0 || list == null)
                return false;

            List<ListItem> shownItems = GetShownItems(list);
            List<ListItem> toRemove = GetToRemove(list);
            float itemHeight = ItemHeight;
            if (shownItems == null || toRemove == null || list.contentRT == null || list.viewportRect == null || itemHeight <= 0f)
            {
                // 落回原版之前把头行收回自建池：原版不认识它们，留在 contentRT 里会叠在行上
                DetachHeaders();
                return false;
            }

            if (forceRefreshAll)
            {
                // 原版：ClearShownItems()——先把头行摘出去，别让它们被塞回游戏的 ListItemPool
                DetachHeaders();
                while (list.contentRT.childCount > 0)
                    list.listItemPool.SendItemBackToPool(list.contentRT.GetChild(0).gameObject);
                shownItems.Clear();
            }
            else
            {
                toRemove.Clear();
                foreach (ListItem item in shownItems)
                {
                    if (item == null)
                        continue;
                    // 折叠组里的行不参与渲染，必须回收（它们可能仍落在可视窗口内，只看 IsItemVisible 会漏）
                    bool stillShown = item.sourceLevelEvent != null
                        && DecoGroupState.VisibleEvents.Contains(item.sourceLevelEvent)
                        && IsItemVisible(list, item.rt.anchoredPosition.y, itemHeight);
                    if (stillShown)
                        continue;
                    toRemove.Add(item);
                    list.listItemPool.SendItemBackToPool(item.gameObject);
                }
                for (int i = 0; i < toRemove.Count; i++)
                    shownItems.Remove(toRemove[i]);
            }

            scrMisc.SizeDeltaY(list.contentRT, itemHeight * slots.Count);

            int startIndex = Math.Max(Mathf.RoundToInt(list.contentRT.anchoredPosition.y / itemHeight) - 1, 0);
            int windowSize = Mathf.RoundToInt(list.viewportRect.rect.height / itemHeight) + 2;

            usedHeaderKeys.Clear();
            for (int i = startIndex; i < startIndex + windowSize && i < slots.Count; i++)
            {
                if (!IsItemVisible(list, (float)i * itemHeight * -1f, itemHeight))
                    continue;

                DecoGroupSlot slot = slots[i];
                Vector3 position = new Vector3(0f, (float)i * itemHeight * -1f, 0f);

                if (slot.IsHeader)
                {
                    RenderHeader(list as PropertyControl_DecorationsList, slot, position);
                    continue;
                }

                scnEditor editor = ADOBase.editor;
                if (editor == null)
                    continue;
                if (!editor.decorations.Contains(slot.Event) && !editor.events.Contains(slot.Event))
                    continue;

                ListItem item = list.SearchForVisibleItem(slot.Event);
                if (item == null)
                {
                    GameObject pooled = list.listItemPool.GetPooledItem(list.contentRT, position);
                    if (pooled == null)
                        continue;
                    item = pooled.GetComponent<ListItem>();
                    if (item == null)
                        continue;
                    item.SetEvent(slot.Event);
                    EnsureDragTarget(item);
                    shownItems.Add(item);
                }
                else
                {
                    bool selected = false;
                    if (slot.Event.IsDecoration)
                    {
                        selected = editor.selectedDecorations != null && editor.selectedDecorations.Contains(slot.Event);
                    }
                    else
                    {
                        List<scrFloor> floors = editor.selectedFloors;
                        if (floors != null)
                            for (int f = 0; f < floors.Count; f++)
                                if (slot.Event.floor == floors[f].seqID)
                                {
                                    selected = true;
                                    break;
                                }
                    }
                    item.SetSelectedState(selected);
                    item.rt.anchoredPosition = position;
                    EnsureDragTarget(item);
                }
            }

            ReleaseUnusedHeaders();
            list.Set("applyUpdateList", false);
            list.Set("shouldUpdateForce", false);
            return true;
        }

        /// <summary>内容高度改回"槽位数 × 行高"（原版 AdjustItemListScrollRect(int) 会先把它设成只算行的高度）。</summary>
        internal static void RestoreContentHeight(PropertyControl_List list)
        {
            int count = DecoGroupState.Slots.Count;
            float itemHeight = ItemHeight;
            if (list == null || list.contentRT == null || count == 0 || itemHeight <= 0f)
                return;
            scrMisc.SizeDeltaY(list.contentRT, itemHeight * count);
        }

        // shownItems / toRemove 是 PropertyControl_List 的 protected 字段；渲染与拖拽反馈每帧都要读，
        // 用 Harmony 的强类型字段引用（没有 DynamicInvoke 开销），解析失败才退回通用反射。
        private static AccessTools.FieldRef<PropertyControl_List, List<ListItem>> shownItemsRef;
        private static AccessTools.FieldRef<PropertyControl_List, List<ListItem>> toRemoveRef;
        private static bool listFieldRefsResolved;

        private static void ResolveListFieldRefs()
        {
            if (listFieldRefsResolved)
                return;
            listFieldRefsResolved = true;
            try
            {
                shownItemsRef = AccessTools.FieldRefAccess<PropertyControl_List, List<ListItem>>("shownItems");
                toRemoveRef = AccessTools.FieldRefAccess<PropertyControl_List, List<ListItem>>("toRemove");
            }
            catch (Exception e)
            {
                shownItemsRef = null;
                toRemoveRef = null;
                Main.Logger?.Log("装饰列表字段引用解析失败，退回反射读取: " + e.Message);
            }
        }

        private static List<ListItem> GetShownItems(PropertyControl_List list)
        {
            if (list == null)
                return null;
            ResolveListFieldRefs();
            return shownItemsRef != null ? shownItemsRef(list) : list.Get<List<ListItem>>("shownItems");
        }

        private static List<ListItem> GetToRemove(PropertyControl_List list)
        {
            if (list == null)
                return null;
            ResolveListFieldRefs();
            return toRemoveRef != null ? toRemoveRef(list) : list.Get<List<ListItem>>("toRemove");
        }

        /// <summary>原版 IsItemVisible(Int32/RectTransform)：itemY 是行的相对 Y（行自身 anchoredPosition.y 或 -i*itemHeight）。</summary>
        private static bool IsItemVisible(PropertyControl_List list, float itemY, float itemHeight)
        {
            float num = itemY + list.contentRT.anchoredPosition.y;
            float viewportHeight = list.viewportRect.rect.height;
            if (num >= itemHeight)
                return false;
            return num > -viewportHeight;
        }

        /// <summary>PropertyControl_List.itemHeight（静态字段）的反射句柄：只解析一次，值每次现读（原版可能在 Start 里改它）。</summary>
        private static System.Reflection.FieldInfo itemHeightField;
        private static bool itemHeightFieldResolved;

        internal static float ItemHeight
        {
            get
            {
                try
                {
                    if (!itemHeightFieldResolved)
                    {
                        itemHeightFieldResolved = true;
                        itemHeightField = AccessTools.Field(typeof(PropertyControl_List), "itemHeight");
                    }
                    if (itemHeightField != null)
                        return Convert.ToSingle(itemHeightField.GetValue(null));
                    return typeof(PropertyControl_List).Get<float>("itemHeight");
                }
                catch
                {
                    return 0f;
                }
            }
        }

        // ---------------------------------------------------------------- 头行

        private static void RenderHeader(PropertyControl_DecorationsList panel, DecoGroupSlot slot, Vector3 position)
        {
            if (panel == null)
                return;

            HeaderRow header = AcquireHeader(panel, slot.Key);
            if (header == null)
                return;

            usedHeaderKeys.Add(slot.Key);
            if (header.Rect != null)
                header.Rect.anchoredPosition = position;

            bool collapsed = DecoGroupState.CollapsedGroups.Contains(slot.Key);
            if (header.Arrow != null)
                header.Arrow.text = collapsed ? resolvedCollapsedMark : resolvedExpandedMark;
            if (header.Label != null)
                header.Label.text = slot.Label + (Main.ShowGroupCounts ? " (" + slot.Count + ")" : "");

            // 眼睛/锁图标反映整组状态：全可见 = 睁眼，全锁定 = 闭锁
            bool allVisible = true;
            bool allLocked = true;
            if (DecoGroupState.EventsByGroup.TryGetValue(slot.Key, out List<LevelEvent> events))
            {
                for (int i = 0; i < events.Count; i++)
                {
                    LevelEvent e = events[i];
                    if (e == null)
                        continue;
                    if (!e.visible)
                        allVisible = false;
                    if (!e.locked)
                        allLocked = false;
                }
            }
            if (header.EyeImage != null)
                header.EyeImage.sprite = allVisible ? eyeOpenSprite : eyeClosedSprite;
            if (header.LockImage != null)
                header.LockImage.sprite = allLocked ? lockClosedSprite : lockOpenSprite;

            // 高亮（拖拽落点）之外的组头恢复原外观：头行来自对象池，可能残留上一次的高亮色
            bool highlighted = highlightedGroupKey != null && highlightedGroupKey == slot.Key;
            if (header.Label != null)
                header.Label.color = highlighted ? Color.black : header.OriginalLabelColor;
            if (header.Background != null)
                header.Background.gameObject.SetActive(highlighted || header.OriginalBackgroundActive);
        }

        private static HeaderRow AcquireHeader(PropertyControl_DecorationsList panel, string key)
        {
            if (activeHeaders.TryGetValue(key, out HeaderRow existing) && existing != null && existing.GameObject != null)
                return existing;

            HeaderRow header = null;
            for (int i = headerPool.Count - 1; i >= 0; i--)
            {
                if (headerPool[i] != null && headerPool[i].GameObject != null)
                {
                    header = headerPool[i];
                    headerPool.RemoveAt(i);
                    break;
                }
                headerPool.RemoveAt(i);
            }
            if (header == null)
                header = CreateHeader(panel);
            if (header == null)
                return null;

            header.Marker.GroupKey = key;
            header.GameObject.transform.SetParent(panel.contentRT, false);
            header.GameObject.SetActive(true);
            activeHeaders[key] = header;
            return header;
        }

        private static HeaderRow CreateHeader(PropertyControl_DecorationsList panel)
        {
            if (panel.listItemPool == null || panel.listItemPool.itemPrefab == null)
                return null;

            Transform holder = HeaderHolder(panel);
            if (holder == null)
                return null;

            GameObject clone = UnityEngine.Object.Instantiate(panel.listItemPool.itemPrefab, holder, false);
            clone.name = "aee_groupHeader";
            clone.transform.localScale = Vector3.one;
            // 与 ListItemPool.GetPooledItem 的归一化保持一致，否则头行与普通行的横向对齐会不同
            RectTransform rect = clone.GetComponent<RectTransform>();
            if (rect != null)
            {
                rect.offsetMin = rect.offsetMin.WithX(0f);
                rect.offsetMax = rect.offsetMax.WithX(0f);
            }

            ListItem item = clone.GetComponent<ListItem>();
            ListItem_Decoration decoration = clone.GetComponent<ListItem_Decoration>();
            TMP_Text label = item != null ? item.Get<TMP_Text>("itemName") : null;
            Transform background = item != null ? item.selectionBackground : null;
            Button eyeButton = decoration != null ? decoration.eyeButton : null;
            Button lockButton = decoration != null ? decoration.lockButton : null;
            Image eyeImage = decoration != null ? decoration.eyeButtonImage : null;
            Image lockImage = decoration != null ? decoration.lockButtonImage : null;
            CacheGroupIcons(decoration);

            StripRow(clone, label, background, eyeButton, lockButton);

            var marker = clone.GetComponent<DecoGroupHeader>();
            if (marker == null)
                marker = clone.AddComponent<DecoGroupHeader>();

            if (item != null)
                UnityEngine.Object.Destroy(item);
            if (decoration != null)
                UnityEngine.Object.Destroy(decoration);

            // 箭头：新建一个小 TMP 文本，字体/颜色复制自组名，放在最左侧
            TMP_Text arrow = CreateArrow(clone, label, marker);

            // 组名区域：点击 = 全选该组 + 右侧属性面板
            if (label != null)
            {
                label.raycastTarget = true;
                GroupHeaderClickTarget nameTarget = label.gameObject.GetComponent<GroupHeaderClickTarget>();
                if (nameTarget == null)
                    nameTarget = label.gameObject.AddComponent<GroupHeaderClickTarget>();
                nameTarget.Owner = marker;
                nameTarget.Area = GroupHitArea.Name;
                MakeRoomForArrow(label);
            }

            // 眼睛 / 锁：原版两个按钮，改成组级操作
            if (eyeButton != null)
            {
                eyeButton.gameObject.SetActive(true);
                eyeButton.onClick.RemoveAllListeners();
                eyeButton.onClick.AddListener(marker.OnEye);
            }
            if (lockButton != null)
            {
                lockButton.gameObject.SetActive(true);
                lockButton.onClick.RemoveAllListeners();
                lockButton.onClick.AddListener(marker.OnLock);
            }

            return new HeaderRow
            {
                GameObject = clone,
                Rect = clone.GetComponent<RectTransform>(),
                Label = label,
                Arrow = arrow,
                EyeButton = eyeButton,
                EyeImage = eyeImage,
                LockButton = lockButton,
                LockImage = lockImage,
                Background = background,
                OriginalLabelColor = label != null ? label.color : Color.white,
                OriginalBackgroundActive = background != null && background.gameObject.activeSelf,
                Marker = marker
            };
        }

        /// <summary>
        /// 头行只保留四块：最左的箭头（自建）、组名文本、原版的眼睛按钮与原版的锁按钮；
        /// 其余子对象按顶层分支整枝隐藏，原版的行行为（ListItem / ListItem_Decoration / 触发器 / 其它按钮）全部销毁。
        /// </summary>
        private static void StripRow(GameObject clone, TMP_Text label, Transform background, Button eyeButton, Button lockButton)
        {
            var keep = new HashSet<Transform>();
            AddBranchToKeep(keep, clone.transform, label != null ? label.transform : null);
            AddBranchToKeep(keep, clone.transform, background);
            AddBranchToKeep(keep, clone.transform, eyeButton != null ? eyeButton.transform : null);
            AddBranchToKeep(keep, clone.transform, lockButton != null ? lockButton.transform : null);

            // 只开关顶层分支，保留分支内部结构（眼睛/锁按钮的子对象不能被拆散）
            for (int i = 0; i < clone.transform.childCount; i++)
            {
                Transform child = clone.transform.GetChild(i);
                if (child != null && !keep.Contains(child))
                    child.gameObject.SetActive(false);
            }

            foreach (AdofaiEventTrigger trigger in clone.GetComponentsInChildren<AdofaiEventTrigger>(true))
                UnityEngine.Object.Destroy(trigger);
            foreach (Button button in clone.GetComponentsInChildren<Button>(true))
            {
                if (button == eyeButton || button == lockButton)
                    continue;
                UnityEngine.Object.Destroy(button);
            }

            Image own = clone.GetComponent<Image>();
            if (own != null)
                own.raycastTarget = false;
            if (label != null)
                label.raycastTarget = true;
        }

        private static void AddBranchToKeep(HashSet<Transform> keep, Transform root, Transform target)
        {
            for (Transform t = target; t != null && t != root; t = t.parent)
                keep.Add(t);
        }

        private static TMP_Text CreateArrow(GameObject clone, TMP_Text label, DecoGroupHeader marker)
        {
            var go = new GameObject("aee_groupArrow", typeof(RectTransform), typeof(TextMeshProUGUI));
            var rect = (RectTransform)go.transform;
            rect.SetParent(clone.transform, false);
            // 垂直拉伸到整行高度：之前锚点上下都取 0.5、sizeDelta.y=0，矩形高度为 0 ⇒ TMP 仍画出字形，
            // 但射线检测没有任何面积，于是"看得见却点不到"。这里让命中区等于整行高度。
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 0.5f);
            rect.sizeDelta = new Vector2(ArrowWidth, 0f);
            rect.anchoredPosition = new Vector2(4f, 0f);

            TextMeshProUGUI text = go.GetComponent<TextMeshProUGUI>();
            if (label != null)
            {
                text.font = label.font;
                text.fontSharedMaterial = label.fontSharedMaterial;
                text.fontSize = label.fontSize;
                text.color = label.color;
            }
            text.alignment = TextAlignmentOptions.Left;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.raycastTarget = true;
            // 先按这行实际用的字体定好字形，组头刷新文本时直接取（缺字会画成方框）
            ResolveArrowMarks(text.font);

            GroupHeaderClickTarget target = go.AddComponent<GroupHeaderClickTarget>();
            target.Owner = marker;
            target.Area = GroupHitArea.Arrow;
            return text;
        }

        /// <summary>同一字体只自检一次（每次建行可能涉及几十个组头）。</summary>
        private static void ResolveArrowMarks(TMP_FontAsset font)
        {
            if (font == null || ReferenceEquals(font, resolvedMarkFont))
                return;
            resolvedMarkFont = font;
            resolvedExpandedMark = PickArrowMark(font, PreferredExpandedMark, ExpandedMark);
            resolvedCollapsedMark = PickArrowMark(font, PreferredCollapsedMark, CollapsedMark);
        }

        /// <summary>字体里有首选码位就用它，否则回退（以后字体再变，最多退化观感，不会出方框）。</summary>
        private static string PickArrowMark(TMP_FontAsset font, string preferred, string fallback)
        {
            try
            {
                // r148 的 TMP 签名（反射已核）：HasCharacter(char character, bool includeFallbacks, bool searchActiveCharacterTableOnly)
                if (font.HasCharacter(preferred[0], true, false))
                    return preferred;
            }
            catch (Exception e)
            {
                Main.Logger?.Log("检测箭头字形失败，按回退字形处理: " + e.Message);
            }
            return fallback;
        }

        /// <summary>组名是拉伸锚点时给它左边让出箭头的宽度（不是拉伸锚点就不动，避免破坏原版布局）。</summary>
        private static void MakeRoomForArrow(TMP_Text label)
        {
            RectTransform rect = label.rectTransform;
            if (rect == null)
                return;
            if (rect.anchorMin.x > 0.01f || rect.anchorMax.x < 0.99f)
                return;
            // 每个头行都是从 prefab 新克隆的，各自偏移一次即可（早先用静态标志只让第一行让位，是错的）
            Vector2 min = rect.offsetMin;
            rect.offsetMin = new Vector2(min.x + ArrowWidth, min.y);
        }

        /// <summary>组头的眼睛/锁图标：从行 prefab 上取原版精灵。</summary>
        private static void CacheGroupIcons(ListItem_Decoration decoration)
        {
            if (decoration == null || iconsCached)
                return;
            iconsCached = true;
            eyeOpenSprite = decoration.eyeOpenSprite;
            eyeClosedSprite = decoration.eyeClosedSprite;
            lockOpenSprite = decoration.lockOpenSprite;
            lockClosedSprite = decoration.lockClosedSprite;
        }

        private static Transform HeaderHolder(PropertyControl_DecorationsList panel)
        {
            if (headerHolder == null)
            {
                if (panel == null)
                    return null;
                var holder = new GameObject("aee_groupHeaderPool");
                holder.transform.SetParent(panel.transform, false);
                headerHolder = holder.transform;
            }
            return headerHolder;
        }

        /// <summary>把所有已挂出的头行收回自建池（ClearShownItems 前必须调用，否则头行会被塞进游戏的池）。</summary>
        internal static void DetachHeaders()
        {
            foreach (KeyValuePair<string, HeaderRow> pair in activeHeaders)
                ParkHeader(pair.Value);
            activeHeaders.Clear();
            usedHeaderKeys.Clear();
        }

        /// <summary>ReleaseUnusedHeaders 的复用缓冲（每次渲染都会调，避免每帧 new 一个 List）。</summary>
        private static readonly List<string> staleHeaderKeys = new List<string>();

        private static void ReleaseUnusedHeaders()
        {
            List<string> stale = staleHeaderKeys;
            stale.Clear();
            foreach (KeyValuePair<string, HeaderRow> pair in activeHeaders)
            {
                if (!usedHeaderKeys.Contains(pair.Key))
                    stale.Add(pair.Key);
            }
            if (stale.Count == 0)
                return;
            foreach (string key in stale)
            {
                if (activeHeaders.TryGetValue(key, out HeaderRow header))
                    ParkHeader(header);
                activeHeaders.Remove(key);
            }
            stale.Clear();
        }

        private static void ParkHeader(HeaderRow header)
        {
            if (header == null || header.GameObject == null || headerPool.Contains(header))
                return;
            header.GameObject.SetActive(false);
            if (headerHolder != null)
                header.GameObject.transform.SetParent(headerHolder, false);
            headerPool.Add(header);
        }

        // ---------------------------------------------------------------- 拖拽落点（组头高亮）

        private static string highlightedGroupKey;
        private static Canvas cachedCanvas;

        internal static string HighlightedGroupKey => highlightedGroupKey;

        /// <summary>
        /// 找出屏幕点下方的分组键（不含 `type:*` —— 它是否有效要看被拖的是哪个装饰，
        /// 用带事件的 `DecoGroupActions.TryResolveAssignment(key, set, event, out …)` 判定，见 §22.2）。
        /// </summary>
        internal static string FindGroupKeyAtPointer(Vector2 screenPosition)
        {
            return TryFindDropTarget(screenPosition, out DecoGroupState.DropTarget target, out _) && IsAssignableKey(target.Key)
                ? target.Key
                : null;
        }

        /// <summary>
        /// 找出屏幕点下方的落点。判定顺序：
        ///  1. 头行矩形（折叠组只有头行，必须靠这条命中）⇒ `Anchor = null`（= 插到组尾）；
        ///  2. 组内的行（行落在"组头 + 它下面那些行"这个块里都算该组）⇒ `Anchor` = 该行的装饰，
        ///     `Before` = 指针在该行上半区（上/下半区决定插到它前面还是后面）。
        /// `hoveredRect` 回传命中的矩形（组头或行），落点线定位要用。
        /// </summary>
        internal static bool TryFindDropTarget(Vector2 screenPosition, out DecoGroupState.DropTarget target, out RectTransform hoveredRect)
        {
            target = default;
            hoveredRect = null;
            Camera camera = ResolveCamera();

            foreach (KeyValuePair<string, HeaderRow> pair in activeHeaders)
            {
                HeaderRow header = pair.Value;
                if (header == null || header.Rect == null)
                    continue;
                if (RectTransformUtility.RectangleContainsScreenPoint(header.Rect, screenPosition, camera))
                {
                    target.Key = pair.Key;
                    target.Anchor = null;      // 组头 = 插到组尾（见 §15.3）
                    target.Before = false;
                    hoveredRect = header.Rect;
                    return true;
                }
            }

            PropertyControl_DecorationsList panel = FindPanel();
            if (panel == null || panel.viewportRect == null)
                return false;
            // 指针得真的落在列表区域内，否则拖到空白处也会按 Y 命中某个组
            if (!RectTransformUtility.RectangleContainsScreenPoint(panel.viewportRect, screenPosition, camera))
                return false;

            List<ListItem> shownItems = GetShownItems(panel);
            if (shownItems == null)
                return false;
            for (int i = 0; i < shownItems.Count; i++)
            {
                ListItem item = shownItems[i];
                if (item == null || item.rt == null || item.sourceLevelEvent == null)
                    continue;
                if (!RectTransformUtility.RectangleContainsScreenPoint(item.rt, screenPosition, camera))
                    continue;
                if (!DecoGroupState.GroupKeyOfEvent.TryGetValue(item.sourceLevelEvent, out string key) || key == null)
                    continue;

                target.Key = key;
                target.Anchor = item.sourceLevelEvent;
                target.Before = IsPointerInUpperHalf(item.rt, screenPosition, camera);
                hoveredRect = item.rt;
                return true;
            }
            return false;
        }

        /// <summary>指针是否在行的上半区（用行内局部坐标比，跟画布的渲染模式无关）。</summary>
        private static bool IsPointerInUpperHalf(RectTransform rect, Vector2 screenPosition, Camera camera)
        {
            if (rect == null)
                return true;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(rect, screenPosition, camera, out Vector2 local))
                return true;
            float center = (rect.rect.yMin + rect.rect.yMax) * 0.5f;
            return local.y > center;
        }

        /// <summary>这个键能不能作为拖动落点（type:* 只能改事件类型，做不到）。</summary>
        internal static bool IsAssignableKey(string key)
        {
            return !string.IsNullOrEmpty(key) && !key.StartsWith("type:", StringComparison.Ordinal);
        }

        // ---------------------------------------------------------------- 拖拽落点反馈（原版同款）

        private static RectTransform dropLine;        // 落点白横线（自建，规格抄原版 draggingIndicatorBar）
        private static RectTransform cursorMark;      // 跟随鼠标的小方块（自建，外观抄原版 multiSelectIcon）
        private static ListItem highlightedRow;       // 当前用原版 ShowHighlight 高亮的悬停行
        private static bool dropObjectsResolved;
        private static bool dropFeedbackLogged;       // 每次拖动只打一条诊断日志（见 §15.1）

        /// <summary>
        /// 拖动中每帧的反馈：
        ///  · 可归组的目标（或"同组内排序"的目标）⇒ 组头用原版选中行样式高亮 + 悬停行用原版
        ///    `ListItem.ShowHighlight` 高亮 + 落点白横线（画在**将要插入的位置**）+ 鼠标旁小方块；
        ///  · 落在不可改归属的 type:* 组上、且不是自己所在的组 ⇒ 什么都不显示；
        ///  · 没有落点 ⇒ 什么都不显示。
        /// </summary>
        internal static void UpdateDropFeedback(Vector2 screenPosition, LevelEvent dragging)
        {
            UpdateDropFeedback(screenPosition, dragging, null);
        }

        /// <summary>
        /// 同上；<paramref name="draggingSet"/> ≥ 2 个时是多选整批拖动：落点要对**整批**有效才给反馈
        /// （口径同 <see cref="DecoGroupActions.IsBatchDropAcceptable"/>），鼠标旁方块边上显示 "×N"。
        /// 落点落在被拖集合内部 ⇒ 松手只改归属不排序，所以不画插入线；连归属也不用改就什么都不显示。
        /// </summary>
        internal static void UpdateDropFeedback(Vector2 screenPosition, LevelEvent dragging, IList<LevelEvent> draggingSet)
        {
            if (!TryFindDropTarget(screenPosition, out DecoGroupState.DropTarget target, out RectTransform hoveredRect))
            {
                ClearDropFeedback();
                return;
            }

            bool batch = draggingSet != null && draggingSet.Count >= 2;
            bool showLine = true;
            ICollection<LevelEvent> exclude = null;
            if (batch)
            {
                if (!DecoGroupActions.IsBatchDropAcceptable(draggingSet, target.Key))
                {
                    ClearDropFeedback();
                    return;
                }
                exclude = draggingSet;
                if (target.Anchor != null && draggingSet.Contains(target.Anchor))
                {
                    if (!DecoGroupActions.BatchNeedsReassign(draggingSet, target.Key))
                    {
                        ClearDropFeedback();
                        return;
                    }
                    showLine = false;
                }
            }
            else
            {
                // 落点要有意义才给反馈：可改归属的组、或"这个装饰自己的类型组"（= 从自定义分组放回来，§22.2）、
                // 或**同组内排序**（拖回自己所在的组，见 §15.3）。别的类型组两者都做不到，就不给反馈。
                bool meaningful = DecoGroupActions.TryResolveAssignment(target.Key, DecoGroupState.GroupSet.Decoration, dragging, out _)
                    || IsGroupOf(dragging, target.Key);
                if (!meaningful)
                {
                    ClearDropFeedback();
                    return;
                }
            }

            SetGroupHighlight(target.Key);
            HighlightHoveredRow(showLine ? hoveredRect : null);
            ShowDropIndicator(target, hoveredRect, screenPosition, showLine, exclude, batch ? draggingSet.Count : 1);
        }

        internal static void ClearDropFeedback()
        {
            SetGroupHighlight(null);
            HighlightHoveredRow(null);
            HideDropIndicator();
            dropFeedbackLogged = false;   // 下次拖动再打一条诊断日志
        }

        internal static void HideDropIndicator()
        {
            if (dropLine != null && dropLine.gameObject.activeSelf)
                dropLine.gameObject.SetActive(false);
            if (cursorMark != null && cursorMark.gameObject.activeSelf)
                cursorMark.gameObject.SetActive(false);
            if (countLabel != null && countLabel.gameObject.activeSelf)
                countLabel.gameObject.SetActive(false);
        }

        private static bool IsGroupOf(LevelEvent e, string key)
        {
            return e != null && !string.IsNullOrEmpty(key)
                && DecoGroupState.GroupKeyOfEvent.TryGetValue(e, out string own) && own == key;
        }

        private static void HighlightHoveredRow(RectTransform rect)
        {
            ListItem item = rect != null ? FindShownItem(rect) : null;
            if (ReferenceEquals(item, highlightedRow))
                return;
            if (highlightedRow != null)
                highlightedRow.ShowHighlight(false);
            highlightedRow = item;
            if (highlightedRow != null)
                highlightedRow.ShowHighlight(true);
        }

        private static ListItem FindShownItem(RectTransform rect)
        {
            if (rect == null)
                return null;
            PropertyControl_DecorationsList panel = FindPanel();
            List<ListItem> shownItems = GetShownItems(panel);
            if (shownItems == null)
                return null;
            for (int i = 0; i < shownItems.Count; i++)
                if (shownItems[i] != null && shownItems[i].rt == rect)
                    return shownItems[i];
            return null;
        }

        /// <summary>按装饰事件找当前挂出的行（用来定位"下一组第一行"的矩形）。</summary>
        private static bool TryGetShownRect(LevelEvent e, out RectTransform rect)
        {
            rect = null;
            if (e == null)
                return false;
            PropertyControl_DecorationsList panel = FindPanel();
            List<ListItem> shownItems = GetShownItems(panel);
            if (shownItems == null)
                return false;
            for (int i = 0; i < shownItems.Count; i++)
            {
                ListItem item = shownItems[i];
                if (item != null && item.rt != null && ReferenceEquals(item.sourceLevelEvent, e))
                {
                    rect = item.rt;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 落点白横线的世界 Y：插到锚点行之前 ⇒ 该行上沿，之后 ⇒ 该行下沿；落在组头 ⇒ 组尾边界。
        /// <paramref name="exclude"/>：多选整批拖动时正被移走的装饰，算"组尾"时跳过它们（与 DropDecorations 的锚点一致）。
        /// </summary>
        private static bool TryGetIndicatorWorldY(DecoGroupState.DropTarget target, RectTransform hoveredRect,
            ICollection<LevelEvent> exclude, out float worldY)
        {
            worldY = 0f;
            if (target.Anchor != null)
            {
                if (hoveredRect == null)
                    return false;
                return target.Before ? TryGetTopEdge(hoveredRect, out worldY) : TryGetBottomEdge(hoveredRect, out worldY);
            }

            // 组头：插到组尾 ⇒ 本组最后一个成员的下沿（与 DropDecoration 的锚点一致）；
            // 它不在视野里就取"下一组第一行"的上沿（显示上是同一个位置）
            LevelEvent last = DecoGroupState.LastEventOfGroup(target.Key, exclude);
            if (last != null && TryGetShownRect(last, out RectTransform lastRect) && TryGetBottomEdge(lastRect, out worldY))
                return true;
            LevelEvent next = DecoGroupState.NextEventAfterGroup(target.Key, exclude);
            if (next != null && TryGetShownRect(next, out RectTransform nextRect) && TryGetTopEdge(nextRect, out worldY))
                return true;
            // 组内没有可见成员（空组 / 全部滚出视野）：退回组头下沿
            return TryGetBottomEdge(hoveredRect, out worldY);
        }

        private static void ShowDropIndicator(DecoGroupState.DropTarget target, RectTransform hoveredRect, Vector2 screenPosition,
            bool showLine, ICollection<LevelEvent> exclude, int count)
        {
            ResolveDropObjects();
            if (!showLine)
            {
                // 落点在被拖集合内部：只改归属、不插入 ⇒ 不画线，只保留跟随方块与数量
                if (dropLine != null && dropLine.gameObject.activeSelf)
                    dropLine.gameObject.SetActive(false);
                ShowCursorMark(screenPosition, count);
                return;
            }
            if (dropLine == null || !TryGetIndicatorWorldY(target, hoveredRect, exclude, out float worldY))
                return;

            // 夹进列表视口，保证不会被 Viewport 的 Mask 裁掉（落点在视口外时贴边显示）
            PropertyControl_DecorationsList panel = FindPanel();
            RectTransform viewport = panel != null ? panel.viewportRect : null;
            float topY = worldY;
            float bottomY = worldY;
            if (viewport != null && TryGetWorldRange(viewport, out float top, out float bottom))
            {
                worldY = Mathf.Clamp(worldY, bottom + 2f, top - 2f);
                topY = top;
                bottomY = bottom;
            }

            Vector3 position = dropLine.position;
            dropLine.position = new Vector3(position.x, worldY, position.z);
            dropLine.gameObject.SetActive(true);

            if (!dropFeedbackLogged)
            {
                dropFeedbackLogged = true;
                if (Main.Logger != null)
                {
                    Image lineImage = dropLine.GetComponent<Image>();
                    Main.Logger.Log(string.Format(
                        "拖动落点：键={0} 锚点={1} 插入点世界Y={2:F1} 视口Y=[{3:F1},{4:F1}] 线父={5} active={6} 同层序号={7}/{8} size={9} 颜色={10}",
                        target.Key, target.Anchor != null ? (target.Before ? "行上沿" : "行下沿") : "组尾",
                        worldY, bottomY, topY,
                        dropLine.parent != null ? dropLine.parent.name : "无",
                        dropLine.gameObject.activeInHierarchy,
                        dropLine.GetSiblingIndex(), dropLine.parent != null ? dropLine.parent.childCount : 0,
                        dropLine.rect.size,
                        lineImage != null ? lineImage.color.ToString() : "无 Image"));
                }
            }

            ShowCursorMark(screenPosition, count);
        }

        private static TMP_Text countLabel;          // 多选整批拖动时方块旁的 "×N"

        /// <summary>跟随鼠标的小方块；<paramref name="count"/> ≥ 2 时在它右边显示 "×N"。</summary>
        private static void ShowCursorMark(Vector2 screenPosition, int count)
        {
            if (cursorMark == null)
                return;
            var parentRect = cursorMark.parent as RectTransform;
            if (parentRect == null
                || !RectTransformUtility.ScreenPointToLocalPointInRectangle(parentRect, screenPosition, ResolveCamera(), out Vector2 local))
                return;
            cursorMark.localPosition = local;
            cursorMark.gameObject.SetActive(true);

            if (count < 2)
            {
                if (countLabel != null && countLabel.gameObject.activeSelf)
                    countLabel.gameObject.SetActive(false);
                return;
            }
            EnsureCountLabel(parentRect);
            if (countLabel == null)
                return;
            countLabel.text = "×" + count;
            countLabel.rectTransform.localPosition = local + new Vector2(cursorMark.rect.width * 0.5f + 4f, 0f);
            if (!countLabel.gameObject.activeSelf)
                countLabel.gameObject.SetActive(true);
            countLabel.transform.SetAsLastSibling();
        }

        /// <summary>懒建 "×N" 标签：字体/字号照抄装饰列表行名的 TMP（取不到就用 TMP 默认字体）。</summary>
        private static void EnsureCountLabel(RectTransform parent)
        {
            if (countLabel != null || parent == null)
                return;
            try
            {
                var go = new GameObject("aee_dropCount", typeof(RectTransform), typeof(TextMeshProUGUI));
                var rect = (RectTransform)go.transform;
                rect.SetParent(parent, false);
                rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0f, 0.5f);
                rect.sizeDelta = new Vector2(80f, 24f);

                TextMeshProUGUI text = go.GetComponent<TextMeshProUGUI>();
                TMP_Text style = FindRowLabelStyle();
                if (style != null)
                {
                    text.font = style.font;
                    text.fontSharedMaterial = style.fontSharedMaterial;
                    text.fontSize = style.fontSize;
                }
                text.color = Color.white;
                text.alignment = TextAlignmentOptions.Left;
                text.textWrappingMode = TextWrappingModes.NoWrap;
                text.raycastTarget = false;
                go.SetActive(false);
                countLabel = text;
            }
            catch (Exception e)
            {
                Main.Logger?.Log("拖动数量标签创建失败（不影响拖动）: " + e.Message);
            }
        }

        private static TMP_Text FindRowLabelStyle()
        {
            List<ListItem> shownItems = GetShownItems(FindPanel());
            if (shownItems == null)
                return null;
            for (int i = 0; i < shownItems.Count; i++)
            {
                TMP_Text label = shownItems[i] != null ? shownItems[i].Get<TMP_Text>("itemName") : null;
                if (label != null && label.font != null)
                    return label;
            }
            return null;
        }

        /// <summary>
        /// 建我们自己的落点线/跟随方块（规格与外观都抄原版的 `draggingIndicatorBar` / `multiSelectIcon`）。
        /// 直接用原版那两个对象在实测里没能显示（见 §15.1），自建一份自己管最稳；原版对象则先藏起来避免重复。
        /// </summary>
        private static void ResolveDropObjects()
        {
            if (dropObjectsResolved)
                return;
            PropertyControl_DecorationsList panel = FindPanel();
            if (panel == null)
                return;
            dropObjectsResolved = true;

            RectTransform originalBar = AccessTools.Field(typeof(PropertyControl_List), "draggingIndicatorBar")?.GetValue(panel) as RectTransform;
            RectTransform originalIcon = AccessTools.Field(typeof(PropertyControl_List), "multiSelectIcon")?.GetValue(panel) as RectTransform;

            RectTransform parent = panel.viewportRect != null ? panel.viewportRect : panel.transform as RectTransform;
            dropLine = CreateOverlay(parent, "aee_dropLine", originalBar, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 3f));
            cursorMark = CreateOverlay(panel.transform as RectTransform, "aee_dropCursor", originalIcon, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(16f, 16f));

            if (originalBar != null && originalBar != dropLine)
                originalBar.gameObject.SetActive(false);

            if (Main.Logger != null)
                Main.Logger.Log(string.Format(
                    "拖动归组落点反馈：自建线={0}、自建方块={1}（原版 draggingIndicatorBar {2} / multiSelectIcon {3}）",
                    dropLine != null ? "ok" : "失败",
                    cursorMark != null ? "ok" : "失败",
                    originalBar != null ? "在" : "找不到",
                    originalIcon != null ? "在" : "找不到"));
        }

        /// <summary>按原版同类对象的规格建一个纯色覆盖层（保留其颜色/精灵/尺寸，保证观感一致）。</summary>
        private static RectTransform CreateOverlay(RectTransform parent, string name, RectTransform style, Vector2 anchorMin, Vector2 anchorMax, Vector2 fallbackSize)
        {
            if (parent == null)
                return null;
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = fallbackSize;
            rect.anchoredPosition = Vector2.zero;

            Image image = go.GetComponent<Image>();
            image.sprite = null;                 // 原版 draggingIndicatorBar 也没有 sprite（纯色矩形）
            image.color = Color.white;
            image.raycastTarget = false;

            if (style != null)
            {
                if (style.sizeDelta.y > 0.5f)
                    rect.sizeDelta = style.sizeDelta;
                Image styleImage = style.GetComponent<Image>();
                if (styleImage != null)
                {
                    image.color = styleImage.color;
                    if (styleImage.sprite != null)
                        image.sprite = styleImage.sprite;
                }
            }

            go.SetActive(false);
            rect.SetAsLastSibling();
            return rect;
        }

        /// <summary>GetWorldCorners 的复用缓冲（拖动时每帧要算好几次，只在主线程用）。</summary>
        private static readonly Vector3[] cornersBuffer = new Vector3[4];

        /// <summary>矩形的世界上下沿（用世界四角算，不依赖 pivot 怎么设的）。</summary>
        private static bool TryGetWorldRange(RectTransform rect, out float topWorldY, out float bottomWorldY)
        {
            topWorldY = bottomWorldY = 0f;
            if (rect == null)
                return false;
            Vector3[] corners = cornersBuffer;
            rect.GetWorldCorners(corners);   // 0=左下 1=左上 2=右上 3=右下
            bottomWorldY = (corners[0].y + corners[3].y) * 0.5f;
            topWorldY = (corners[1].y + corners[2].y) * 0.5f;
            return true;
        }

        private static bool TryGetBottomEdge(RectTransform rect, out float bottomWorldY)
        {
            return TryGetWorldRange(rect, out _, out bottomWorldY);
        }

        private static bool TryGetTopEdge(RectTransform rect, out float topWorldY)
        {
            return TryGetWorldRange(rect, out topWorldY, out _);
        }

        /// <summary>高亮拖拽落点组头（原版选中风格：白底 + 黑字），传 null 还原。</summary>
        internal static void SetGroupHighlight(string key)
        {
            highlightedGroupKey = key;
            foreach (KeyValuePair<string, HeaderRow> pair in activeHeaders)
            {
                HeaderRow header = pair.Value;
                if (header == null)
                    continue;
                bool on = key != null && pair.Key == key;
                if (header.Label != null)
                    header.Label.color = on ? Color.black : header.OriginalLabelColor;
                if (header.Background != null)
                    header.Background.gameObject.SetActive(on || header.OriginalBackgroundActive);
            }
        }

        /// <summary>给装饰行挂上"可拖到分组"的拖拽源（幂等；行来自对象池会被复用）。</summary>
        internal static void EnsureDragTarget(ListItem item)
        {
            if (item == null)
                return;
            if (item.GetComponent<DecoRowDragTarget>() == null)
                item.gameObject.AddComponent<DecoRowDragTarget>();
        }

        private static Camera ResolveCamera()
        {
            if (cachedCanvas == null)
            {
                PropertyControl_DecorationsList panel = FindPanel();
                if (panel != null)
                    cachedCanvas = panel.GetComponentInParent<Canvas>();
            }
            Canvas canvas = cachedCanvas;
            if (canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay)
                return null;
            return canvas.worldCamera;
        }

        // ---------------------------------------------------------------- 对外的刷新入口

        internal static void ToggleGroup(string key)
        {
            if (string.IsNullOrEmpty(key))
                return;
            if (!DecoGroupState.CollapsedGroups.Remove(key))
                DecoGroupState.CollapsedGroups.Add(key);
            RefreshList();
        }

        /// <summary>按当前设置与折叠状态重建装饰列表（FilterSearchResults → RefreshItemsList）。</summary>
        internal static void RefreshList()
        {
            PropertyControl_DecorationsList panel = FindPanel();
            if (panel == null)
                return;
            try
            {
                string search = panel.searchField != null ? panel.searchField.text : "";
                panel.Method("FilterSearchResults", new object[] { search, false });
                panel.RefreshItemsList(true);
            }
            catch (Exception e)
            {
                Main.Logger?.Log("刷新装饰列表失败: " + e.Message);
            }
        }

        /// <summary>当前装饰栏面板：优先用最近一次构建记录的面板，没记录过就从编辑器身上找。</summary>
        internal static PropertyControl_DecorationsList FindPanel()
        {
            if (DecoGroupState.Panel != null)
                return DecoGroupState.Panel;
            if (scnEditor.instance == null)
                return null;
            try
            {
                PropertyControl_DecorationsList panel = AccessTools.Field(typeof(scnEditor), "propertyControlDecorationsList")?.GetValue(scnEditor.instance) as PropertyControl_DecorationsList;
                DecoGroupState.Panel = panel;
                return panel;
            }
            catch (Exception e)
            {
                Main.Logger?.Log("找不到装饰列表面板: " + e.Message);
                return null;
            }
        }

        /// <summary>分组开启时必须禁用拖拽重排（EndDrag 的第一道检查），关闭时（含模组被关掉）恢复。</summary>
        internal static void SyncReorderable(PropertyControl_DecorationsList panel)
        {
            if (panel == null)
                panel = FindPanel();
            if (panel == null)
                return;
            panel.itemsReorderable = !Main.IsEnabled || !Main.IsDecoGroupingEnabled;
        }

        /// <summary>原版 AdjustItemListScrollRect 结尾的收尾（跳过原版方法体时得自己清，避免每帧重试）。</summary>
        internal static void ClearScrollAdjust(PropertyControl_List list)
        {
            if (list == null)
                return;
            list.Set("applyRefreshScrollRect", false);
            list.Set("cacheEventForRectAdjust", null);
        }

        private static string L(string key) => Main.Localizations?.GetValue(key) ?? key;
    }
}
