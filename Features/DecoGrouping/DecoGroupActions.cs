using ADOFAI;
using ADOFAI.LevelEditor.Controls;
using ADOFAIEditorExtension.Utils;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace ADOFAIEditorExtension.Features.DecoGrouping
{
    /// <summary>
    /// 组头行的四个动作：折叠/展开、全选并打开右侧面板、整组可见开关、整组锁定开关。
    ///
    /// 可见/锁定走原版逐装饰的同一套 API（`ListItem_Decoration.PowerButton/LockButton` 用的就是
    /// `scnEditor.ShowEvent / LockEvent`，直接写 LevelEvent.visible / locked）；它们本身不压
    /// SaveStateScope，所以既不会置关卡脏标记，也不需要额外的撤销处理（与原版逐行按钮行为一致）。
    /// 全选走原版 `scnEditor.SelectDecoration(...)`（内部自带 SaveStateScope(…, false, …)，不置脏），
    /// 选完后由原版 InspectorPanel 生成"多选 = fake event"面板，属性修改经
    /// `LevelEvent.ApplyPropertiesToRealEvents()` 统一落到每个被选中的装饰上。
    /// </summary>
    internal static class DecoGroupActions
    {
        /// <summary>组里的全部装饰事件（含折叠组；按当前列表顺序）。</summary>
        internal static List<LevelEvent> GetGroupEvents(string key)
        {
            if (string.IsNullOrEmpty(key))
                return new List<LevelEvent>();
            return DecoGroupState.EventsByGroup.TryGetValue(key, out List<LevelEvent> events) && events != null
                ? events
                : new List<LevelEvent>();
        }

        internal static void ToggleCollapse(string key)
        {
            if (string.IsNullOrEmpty(key))
                return;
            DecoGroupRenderer.ToggleGroup(key);
        }

        /// <summary>点组名：全选该组装饰，并在右侧显示原版多选面板（= 分组详情窗口）。</summary>
        internal static void SelectGroupAndShowPanel(string key)
        {
            scnEditor editor = scnEditor.instance;
            if (editor == null)
                return;

            List<LevelEvent> events = GetGroupEvents(key);
            if (events.Count == 0)
                return;

            int selected = 0;
            // 与原版 SelectItemsInRange 一样只压一个外层 SaveStateScope：里面 DeselectAll / 每次 SelectDecoration
            // 自带的 scope 因 changingState > 0 不再各自存状态（否则每选一个就整关复制一次快照、撤销栈被冲掉）。
            // 参数取 SelectDecoration 自己那套 (false, false)：纯选择，不置"未保存"（r148 IL 已核）
            using (new SaveStateScope(editor, false, false, false))
            {
                // 点组名 = "选中这一组"（替换选区），不是累加
                editor.DeselectAllDecorations();
                editor.DeselectFloors(false);

                for (int i = 0; i < events.Count; i++)
                {
                    LevelEvent e = events[i];
                    if (e == null || scrDecorationManager.GetDecoration(e) == null)
                        continue;
                    // ignoreDeselection：累加选择；ignoreAdjustRect：先不动面板，最后统一刷新
                    editor.SelectDecoration(e, false, false, true, true);
                    selected++;
                }
            }
            if (selected == 0)
                return;

            // 统一刷新右侧属性面板：原版会为多选生成 fake event（同类型才能一起编辑，
            // 混合类型时原版自己会给出"不同装饰类型"的提示，这里不干预）
            LevelEventType type = events[0].eventType;
            editor.levelEventsPanel?.ShowInspector(true, false);
            editor.levelEventsPanel?.ShowPanel(type, 0);
            editor.propertyControlDecorationsList?.RefreshItemsList(false);

            if (!IsHomogeneous(events) && Main.Logger != null)
                Main.Logger.Log("分组详情：该组含多种装饰类型，原版面板只会提示无法同时编辑（组级可见/锁定仍可用）");
        }

        /// <summary>整组显示/隐藏：全部可见则整组隐藏，否则整组显示。</summary>
        internal static void ToggleVisible(string key)
        {
            scnEditor editor = scnEditor.instance;
            if (editor == null)
                return;
            List<LevelEvent> events = GetGroupEvents(key);
            if (events.Count == 0)
                return;

            bool allVisible = true;
            for (int i = 0; i < events.Count; i++)
                if (events[i] != null && !events[i].visible)
                {
                    allVisible = false;
                    break;
                }

            bool target = !allVisible;
            for (int i = 0; i < events.Count; i++)
                if (events[i] != null)
                    editor.ShowEvent(events[i], target);

            DecoGroupRenderer.RefreshList();
        }

        /// <summary>整组锁定/解锁：全部锁定则整组解锁，否则整组锁定。</summary>
        internal static void ToggleLock(string key)
        {
            scnEditor editor = scnEditor.instance;
            if (editor == null)
                return;
            List<LevelEvent> events = GetGroupEvents(key);
            if (events.Count == 0)
                return;

            bool allLocked = true;
            for (int i = 0; i < events.Count; i++)
                if (events[i] != null && !events[i].locked)
                {
                    allLocked = false;
                    break;
                }

            bool target = !allLocked;
            for (int i = 0; i < events.Count; i++)
                if (events[i] != null)
                    editor.LockEvent(events[i], target);

            DecoGroupRenderer.RefreshList();
        }

        /// <summary>一次拖动落点的语义（见设计文档 §14.1 的命中顺序）。</summary>
        internal enum GroupAssignKind
        {
            /// <summary>写 tag：命中填了 tag 的自定义分组，或按标签分组的 `tag:xxx`。</summary>
            Tag,
            /// <summary>记手动归属：拖进"tag 留空"的自定义分组（第一个除外，那是兜底组）。</summary>
            Manual,
            /// <summary>退出所有分组：清 tag + 清手动归属（「未分组」/兜底组）。</summary>
            Clear,
            /// <summary>
            /// 只清手动归属、**保留 tag**（拖到"自己的类型分组"上，见 §22.2）：
            /// 类型分组是"每个装饰按类型天然属于"的组，拖回去的语义就是解除手动归属、让规则重新接管，
            /// 而 tag 是用户数据（条件事件要用），不能顺手清掉。
            /// </summary>
            ClearManual
        }

        internal struct GroupAssignment
        {
            internal GroupAssignKind Kind;
            internal string Tag;    // Kind == Tag
            internal int Index;     // Kind == Manual
        }

        /// <summary>
        /// 拖动落点：把装饰归入 groupKey 对应的组，并（可选）插到指定位置。
        /// 归属部分（§14.1）：
        ///  · `custom:i` 且该组填了 tag  → 写 tag（+ 清手动归属）；
        ///  · `custom:i` 且该组 tag 留空 → 手动归属（+ 保留原 tag）；
        ///    其中**第一个** tag 留空的自定义分组是兜底组，拖进去 = 退出所有分组；
        ///  · `tag:xxx`（按标签分组）→ 写 tag（+ 清手动归属）；`tag:`（未分组）→ 清空；
        ///  · `custom:rest`（未分组）→ 清 tag + 清手动归属；
        ///  · `type:*`（按类型分组）→ **只有它就是这个装饰自己的类型组时**才是有效落点，
        ///    语义是"清手动归属"（装饰回到自己的类型组，tag 不动）；其它类型组仍然不改归属
        ///    （那等于改事件类型）⇒ 无效。见 §22.2。
        /// 排序部分（§15.3）：把装饰在 `levelData.decorations` 里移到锚点行之前/之后；落在组头上 =
        /// 移到该组末尾。默认在同一个 `SaveStateScope` 里和归属一起完成，与各自的撤销点一致。
        ///
        /// PACL2 例外（§43，IL 已核）：BetterUndoRedo 生效时撤销语义完全不同，这里会分两条路——
        ///  · **跨组拖动（要改归属）**：只改归属、**不动数组顺序**，撤销点靠一个
        ///    `SaveStateScope(…, dataHasChanged: true)`（PACL2 的 `SaveStatePatch.Set` 只在该 scope 压出的
        ///    `currentState` 存在时才记录 tag/归属改动）。顺序不变 ⇒ PACL2 按"下标"还原的选区不会错位，
        ///    这正是"拖第 2 个装饰进别的组、Ctrl+Z 后第 1 个也变选中"这个 bug 的来源。
        ///  · **同组内重排（不重新归属）**：用 PACL2 原生的 `DecoDragScope`（反射构造，见
        ///    <see cref="Pacl2Compat.TryBeginDecorationDragScope"/>）记录顺序——它只记顺序，正好对应这一步；
        ///    拿不到 scope 就放弃重排。详见 <see cref="Pacl2Compat"/> 的类注释。
        /// </summary>
        internal static bool DropDecoration(LevelEvent levelEvent, string groupKey, LevelEvent anchor, bool insertBefore)
        {
            scnEditor editor = scnEditor.instance;
            if (editor == null || levelEvent == null || string.IsNullOrEmpty(groupKey))
                return false;

            // 落点解析要带事件：`type:*` 只有"自己的类型组"才算有效落点（§22.2）
            bool resolvable = TryResolveAssignment(groupKey, DecoGroupState.GroupSet.Decoration, levelEvent, out GroupAssignment assignment);
            bool reassign = resolvable && NeedsChange(levelEvent, assignment);
            bool sameGroup = IsInGroup(levelEvent, groupKey);
            if (!reassign && !resolvable && !sameGroup)
                return false;   // 无效落点（含"别的类型组"）：既不能改归属，排序也无意义

            LevelEvent insertAnchor = anchor;
            if (insertAnchor == null)
            {
                // 落在组头上 ⇒ 插到该组末尾。组成员在装饰数组里**不一定连续**（分组显示是按规则重排的），
                // 锚在"下一组第一行之前"可能落进本组中间 ⇒ 锚点取本组最后一个成员、插到它后面；
                // 组是空的才退回"下一组第一行之前"（没有下一行就追加到装饰数组末尾）
                LevelEvent last = DecoGroupState.LastEventOfGroup(groupKey);
                if (last != null)
                {
                    insertAnchor = last;
                    insertBefore = false;
                }
                else
                {
                    insertAnchor = DecoGroupState.NextEventAfterGroup(groupKey);
                    insertBefore = true;
                }
            }

            // 先判断会不会真的改东西：什么都不变就别压撤销点（SaveStateScope 的 ctor 会立刻整关存一次快照并置脏）
            List<LevelEvent> order = editor.levelData != null ? editor.levelData.decorations : null;
            bool willMove = TryComputeInsertIndex(order, levelEvent, insertAnchor, insertBefore, out _);
            if (!reassign && !willMove)
                return false;

            bool moved = false;
            try
            {
                if (Pacl2Compat.IsBetterUndoRedoActive())
                {
                    // PACL2 的 BetterUndoRedo 接管了撤销栈（PACL2 侧行为见 Pacl2Compat 类注释）：
                    //  · 跨组拖动（reassign，含"只是清手动归属"）**只改归属、不动数组顺序**。
                    //    顺序不动 ⇒ PACL2 按"下标"还原选区（`selectedDecorationIndices` 对着
                    //    `allDecorations`）永远指向同一个装饰，一步 Ctrl+Z 就能把归属和选区一起还原。
                    //    这里仍要用一个原版 SaveStateScope(…, dataHasChanged: true) 来**压撤销点**：
                    //    PACL2 的 `SaveStatePatch.Set` 只在 `currentState != null` 时才记录 tag 改动，
                    //    没有 scope 的话这次归属改动根本撤销不了。
                    //    代价是 PACL2 会打一条 "Old SaveState called!" 警告，且回滚靠 changedEventValues
                    //    （tag / 手动归属都是 set_Item 值改动，正好被记到）——这是它能正确撤销的那条路。
                    //  · 同组内重排（不重新归属）用 PACL2 原生的 DecoDragScope 记录顺序，**不能**再套
                    //    SaveStateScope：DecoDragScope 构造时自己不压 DefaultLevelState，
                    //    而一旦有 currentState，MoveInDecorations 里的 `DecorationsArray.Insert`
                    //    （IL 已核：它就是 `SaveStatePatch.Add` 的 patch 目标）会被记成"新增事件"，
                    //    撤销时又 `events.Add` 追加一次 ⇒ 记录打架。拿不到 scope 就整个放弃重排。
                    scrDecoration dragged = (reassign || !willMove) ? null : scrDecorationManager.GetDecoration(levelEvent);
                    if (dragged != null)
                    {
                        using (IDisposable dragScope = Pacl2Compat.TryBeginDecorationDragScope(editor.propertyControlDecorationsList, dragged))
                        {
                            if (dragScope == null)
                            {
                                willMove = false;   // 记录不了顺序 ⇒ 绝不改顺序
                                moved = false;
                            }
                            else
                            {
                                moved = MoveInDecorations(editor, levelEvent, insertAnchor, insertBefore);
                            }
                        }
                    }
                    else
                    {
                        // 同组重排但连场景装饰对象都取不到 ⇒ 记录不了，直接放弃（别退化成无记录的重排）
                        if (!reassign)
                            willMove = false;
                        if (reassign)
                        {
                            using (new SaveStateScope(editor, false, true, false))
                            {
                                ApplyAssignment(levelEvent, assignment);
                                editor.UpdateDecorationObject(levelEvent);
                            }
                        }
                    }
                }
                else
                {
                    using (new SaveStateScope(editor, false, true, false))
                    {
                        moved = willMove && MoveInDecorations(editor, levelEvent, insertAnchor, insertBefore);
                        if (reassign)
                            ApplyAssignment(levelEvent, assignment);
                        if (reassign || moved)
                            editor.UpdateDecorationObject(levelEvent);
                    }
                }
            }
            catch (Exception e)
            {
                Main.Logger?.Log("拖动归组/排序失败: " + e.Message);
                return false;
            }

            if (!reassign && !moved)
                return false;

            // 绘制顺序是"数组顺序"的派生结果，放在改完数据（含 UpdateDecorationObject）之后同步
            if (moved)
                SyncDecorationOrder(editor);

            DecoGroupRenderer.RefreshList();
            return true;
        }

        // ---------------------------------------------------------------- 多选整批拖动

        /// <summary>
        /// 这次拖动要搬哪些装饰（规则同原版 `PropertyControl_DecorationsList.CacheOnStartDrag`，IL 已核）：
        /// 被拖的那一行**在**当前多选集合里、且集合 ≥ 2 ⇒ 整个选中集合；否则只有被拖的那一个。
        /// 结果按装饰数组顺序（= 显示与绘制顺序）排好；已不在关卡里的僵尸项剔除。
        /// </summary>
        internal static List<LevelEvent> CollectDraggingSet(LevelEvent dragged)
        {
            var result = new List<LevelEvent>();
            if (dragged == null)
                return result;

            scnEditor editor = scnEditor.instance;
            List<LevelEvent> selected = editor != null ? editor.selectedDecorations : null;
            List<LevelEvent> order = editor != null && editor.levelData != null ? editor.levelData.decorations : null;
            if (selected != null && order != null && selected.Count >= 2 && selected.Contains(dragged))
            {
                var set = new HashSet<LevelEvent>(selected);
                for (int i = 0; i < order.Count; i++)
                    if (order[i] != null && set.Contains(order[i]))
                        result.Add(order[i]);
            }
            if (result.Count < 2 || !result.Contains(dragged))
            {
                result.Clear();
                result.Add(dragged);
            }
            return result;
        }

        /// <summary>
        /// 整批落点是否有效（拖动反馈与 <see cref="DropDecorations"/> 共用一套口径）：
        /// **每一个**装饰都要么能按该键解析出归属、要么本来就在该组里；有一个做不到就整批无效
        /// （典型：跨类型多选拖到某个 `type:*` 组，见 §22.2）。
        /// </summary>
        internal static bool IsBatchDropAcceptable(IList<LevelEvent> dragging, string groupKey)
        {
            if (dragging == null || dragging.Count == 0 || string.IsNullOrEmpty(groupKey))
                return false;
            for (int i = 0; i < dragging.Count; i++)
            {
                LevelEvent e = dragging[i];
                if (e == null)
                    return false;
                if (!TryResolveAssignment(groupKey, DecoGroupState.GroupSet.Decoration, e, out _) && !IsInGroup(e, groupKey))
                    return false;
            }
            return true;
        }

        /// <summary>整批里有没有需要改归属的（落点落在被拖集合内部时，只有这种情况才值得给反馈）。</summary>
        internal static bool BatchNeedsReassign(IList<LevelEvent> dragging, string groupKey)
        {
            if (dragging == null || string.IsNullOrEmpty(groupKey))
                return false;
            for (int i = 0; i < dragging.Count; i++)
            {
                LevelEvent e = dragging[i];
                if (e != null && TryResolveAssignment(groupKey, DecoGroupState.GroupSet.Decoration, e, out GroupAssignment a) && NeedsChange(e, a))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 多选整批拖动落点（单个时等价于 <see cref="DropDecoration"/>）。规则：
        ///  · 归属：逐个解析，只改真正需要改的；任何一个装饰对该组无效 ⇒ 整批放弃（<see cref="IsBatchDropAcceptable"/>）。
        ///  · 排序：保持被拖装饰原有的相对顺序，整块插到落点；落点就在被拖集合内部 ⇒ 不排序、只改归属
        ///    （与原版 `OnItemDropSides` 一致）；落在组头 ⇒ 插到该组（不算被拖的）最后一个成员之后。
        ///  · 撤销：普通情况一个 SaveStateScope 覆盖整批；PACL2 BetterUndoRedo 生效时照单个的规矩——
        ///    有要改归属的就只改归属不改顺序，纯同组重排走 PACL2 的 DecoDragScope，且只在被拖装饰原本
        ///    就连续时才重排（DecoDragScope 的撤销只能整块放回，见 Pacl2Compat 的多选重载注释）。
        /// </summary>
        internal static bool DropDecorations(IList<LevelEvent> dragging, string groupKey, LevelEvent anchor, bool insertBefore)
        {
            if (dragging == null || dragging.Count == 0 || string.IsNullOrEmpty(groupKey))
                return false;
            if (dragging.Count == 1)
                return DropDecoration(dragging[0], groupKey, anchor, insertBefore);

            scnEditor editor = scnEditor.instance;
            DecorationsArray<LevelEvent> order = editor != null && editor.levelData != null ? editor.levelData.decorations : null;
            if (order == null)
                return false;

            // 被拖集合：只留还在关卡里的，按数组顺序（拖动开始到松手之间关卡可能变了）
            var draggingSet = new HashSet<LevelEvent>(dragging);
            var moving = new List<LevelEvent>(dragging.Count);
            for (int i = 0; i < order.Count; i++)
                if (order[i] != null && draggingSet.Contains(order[i]))
                    moving.Add(order[i]);
            if (moving.Count == 0)
                return false;
            if (moving.Count == 1)
                return DropDecoration(moving[0], groupKey, anchor, insertBefore);
            var movingSet = new HashSet<LevelEvent>(moving);

            if (!IsBatchDropAcceptable(moving, groupKey))
            {
                Main.Logger?.Log(string.Format("装饰整批拖动：{0} 个，目标={1} 对其中部分装饰无效，整批放弃", moving.Count, groupKey));
                return false;
            }

            var toAssign = new List<(LevelEvent Event, GroupAssignment Assignment)>();
            for (int i = 0; i < moving.Count; i++)
            {
                LevelEvent e = moving[i];
                if (TryResolveAssignment(groupKey, DecoGroupState.GroupSet.Decoration, e, out GroupAssignment a) && NeedsChange(e, a))
                    toAssign.Add((e, a));
            }

            // 落点 → 插入锚点（被拖的装饰一律不能当锚点）
            bool anchorInside = anchor != null && movingSet.Contains(anchor);
            LevelEvent insertAnchor = anchor;
            if (!anchorInside && insertAnchor == null)
            {
                LevelEvent last = DecoGroupState.LastEventOfGroup(groupKey, movingSet);
                if (last != null)
                {
                    insertAnchor = last;
                    insertBefore = false;
                }
                else
                {
                    insertAnchor = DecoGroupState.NextEventAfterGroup(groupKey, movingSet);   // null ⇒ 追加到末尾
                    insertBefore = true;
                }
            }

            List<LevelEvent> planned = null;
            bool willMove = !anchorInside && TryPlanBatchMove(order, moving, insertAnchor, insertBefore, out planned);
            bool pacl2 = Pacl2Compat.IsBetterUndoRedoActive();
            string suppressed = null;
            if (willMove && pacl2)
            {
                if (toAssign.Count > 0)
                    suppressed = "PACL2 下有要改归属的装饰，只改归属不改顺序";
                else if (!IsContiguous(order, moving))
                    suppressed = "PACL2 下被拖装饰原本不连续，撤销无法还原原排布，不改顺序";
                if (suppressed != null)
                    willMove = false;
            }

            Main.Logger?.Log(string.Format(
                "装饰整批拖动：{0} 个，目标={1}，锚点={2}，改归属={3} 个，排序={4}{5}{6}",
                moving.Count, groupKey,
                anchorInside ? "被拖集合内部" : insertAnchor == null ? "末尾" : (insertBefore ? "锚点前" : "锚点后"),
                toAssign.Count, willMove ? "是" : "否",
                pacl2 ? "，PACL2 BetterUndoRedo" : "",
                suppressed != null ? "（" + suppressed + "）" : ""));

            if (toAssign.Count == 0 && !willMove)
                return false;

            bool moved = false;
            try
            {
                if (pacl2 && willMove)
                {
                    // 纯同组整块重排：DecoDragScope 记录顺序，不能再套 SaveStateScope（理由同单个版本）
                    var decos = new List<scrDecoration>(moving.Count);
                    for (int i = 0; i < moving.Count; i++)
                    {
                        scrDecoration d = scrDecorationManager.GetDecoration(moving[i]);
                        if (d == null)
                        {
                            decos = null;
                            break;
                        }
                        decos.Add(d);
                    }
                    IDisposable dragScope = decos != null
                        ? Pacl2Compat.TryBeginDecorationDragScope(editor.propertyControlDecorationsList, decos)
                        : null;
                    if (dragScope == null)
                        return false;   // 记录不了顺序 ⇒ 绝不改顺序（此分支 toAssign 为空，也就没别的可做）
                    using (dragScope)
                        moved = MoveBatchInDecorations(editor, order, moving, planned);
                }
                else
                {
                    using (new SaveStateScope(editor, false, true, false))
                    {
                        moved = willMove && MoveBatchInDecorations(editor, order, moving, planned);
                        for (int i = 0; i < toAssign.Count; i++)
                            ApplyAssignment(toAssign[i].Event, toAssign[i].Assignment);
                        if (toAssign.Count > 0 || moved)
                            for (int i = 0; i < moving.Count; i++)
                                editor.UpdateDecorationObject(moving[i]);
                    }
                }
            }
            catch (Exception e)
            {
                Main.Logger?.Log("整批拖动归组/排序失败: " + e.Message);
                return false;
            }

            if (toAssign.Count == 0 && !moved)
                return false;
            if (moved)
                SyncDecorationOrder(editor);
            DecoGroupRenderer.RefreshList();
            return true;
        }

        /// <summary>被拖的装饰在数组里是否本来就是连续的一块（<paramref name="moving"/> 已按数组顺序）。</summary>
        private static bool IsContiguous(List<LevelEvent> order, List<LevelEvent> moving)
        {
            int first = order.IndexOf(moving[0]);
            if (first < 0 || first + moving.Count > order.Count)
                return false;
            for (int i = 1; i < moving.Count; i++)
                if (!ReferenceEquals(order[first + i], moving[i]))
                    return false;
            return true;
        }

        /// <summary>
        /// 纯逻辑（便于离线验证）：把 <paramref name="moving"/>（已按数组顺序）从 <paramref name="order"/> 里拿出来，
        /// 整块插到 anchor 之前/之后（anchor == null ⇒ 追加到末尾），得到新顺序。
        /// 返回 false = 顺序不会变 / 锚点不合法（是被拖的装饰之一、或已不在数组里）。
        /// </summary>
        internal static bool TryPlanBatchMove(List<LevelEvent> order, List<LevelEvent> moving, LevelEvent anchor, bool insertBefore,
            out List<LevelEvent> planned)
        {
            planned = null;
            if (order == null || moving == null || moving.Count == 0)
                return false;
            var movingSet = new HashSet<LevelEvent>(moving);
            if (anchor != null && movingSet.Contains(anchor))
                return false;

            var rest = new List<LevelEvent>(order.Count);
            for (int i = 0; i < order.Count; i++)
                if (!movingSet.Contains(order[i]))
                    rest.Add(order[i]);
            if (rest.Count + moving.Count != order.Count)
                return false;   // moving 里有不在数组里的（或重复的）：不动

            int at;
            if (anchor == null)
                at = rest.Count;
            else
            {
                int anchorIndex = rest.IndexOf(anchor);
                if (anchorIndex < 0)
                    return false;
                at = insertBefore ? anchorIndex : anchorIndex + 1;
            }
            rest.InsertRange(at, moving);

            for (int i = 0; i < order.Count; i++)
            {
                if (!ReferenceEquals(order[i], rest[i]))
                {
                    planned = rest;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 按 <see cref="TryPlanBatchMove"/> 的结果落地：`levelData.decorations` 与 `allDecorations`
        /// 在同一下标上一起整块移除 / 插入（约定同 <see cref="MoveInDecorations"/>）。
        /// 只有最后一次插入走 `DecorationsArray.Insert`（它会回调 OnDecorationUpdate 刷新列表），
        /// 其余走 `List.Insert`，保证回调时两边数组都已经是最终状态、且只回调一次。
        /// </summary>
        private static bool MoveBatchInDecorations(scnEditor editor, DecorationsArray<LevelEvent> decorations,
            List<LevelEvent> moving, List<LevelEvent> planned)
        {
            if (decorations == null || moving == null || planned == null || moving.Count == 0)
                return false;
            int at = planned.IndexOf(moving[0]);
            if (at < 0)
                return false;

            // 场景侧装饰对象：全都找得到才一起动，否则只重排关卡数据（与单个版本的退路一致）
            List<scrDecoration> all = null;
            var decos = new List<scrDecoration>(moving.Count);
            try
            {
                all = scrDecorationManager.instance != null ? scrDecorationManager.instance.allDecorations : null;
                if (all != null)
                {
                    for (int i = 0; i < moving.Count; i++)
                    {
                        scrDecoration d = scrDecorationManager.GetDecoration(moving[i]);
                        if (d == null || !all.Contains(d))
                        {
                            all = null;
                            break;
                        }
                        decos.Add(d);
                    }
                }
            }
            catch (Exception e)
            {
                Main.Logger?.Log("读取装饰对象列表失败，只重排关卡数据: " + e.Message);
                all = null;
            }
            if (all == null)
                Main.Logger?.Log("整批拖动：部分装饰找不到场景对象，只重排关卡数据");

            List<LevelEvent> baseList = decorations;   // List.Remove / List.Insert：不触发 DecorationsArray 的回调
            for (int i = 0; i < moving.Count; i++)
            {
                baseList.Remove(moving[i]);
                if (all != null)
                    all.Remove(decos[i]);
            }

            int n = moving.Count;
            if (all != null)
                for (int i = 0; i < n; i++)
                    all.Insert(Mathf.Clamp(at + i, 0, all.Count), decos[i]);   // 先插场景列表：回调刷新时两边已一致
            for (int i = 0; i < n - 1; i++)
                baseList.Insert(Mathf.Clamp(at + i, 0, baseList.Count), moving[i]);

            LevelEvent lastEvent = moving[n - 1];
            int lastIndex = Mathf.Clamp(at + n - 1, 0, baseList.Count);
            try
            {
                decorations.Insert(lastIndex, lastEvent);
            }
            catch (Exception e)
            {
                Main.Logger?.Log("装饰数组 Insert 失败（整批最后一项）: " + e.Message);
                if (!baseList.Contains(lastEvent))
                    baseList.Insert(Mathf.Clamp(lastIndex, 0, baseList.Count), lastEvent);
            }
            return true;
        }

        /// <summary>
        /// 这个键的语义是否**可能与事件有关**（"未分组 / 自定义分组 / 标签分组"都是）。
        /// `type:*` 从这里看是否定的，但"自己的类型组"经
        /// <see cref="TryResolveAssignment(string, DecoGroupState.GroupSet, LevelEvent, out GroupAssignment)"/>
        /// 仍可成为有效落点（§22.2）——所以做落点判定时请用那个带事件的重载。
        /// </summary>
        internal static bool IsAssignableKey(string groupKey)
        {
            return !string.IsNullOrEmpty(groupKey) && !groupKey.StartsWith("type:", StringComparison.Ordinal);
        }

        private static bool IsInGroup(LevelEvent levelEvent, string groupKey)
        {
            return DecoGroupState.GroupKeyOfEvent.TryGetValue(levelEvent, out string own) && own == groupKey;
        }

        /// <summary>分组键 → 落点语义（纯逻辑，便于离线验证）。不支持的类型返回 false。</summary>
        internal static bool TryResolveAssignment(string groupKey, out GroupAssignment assignment)
        {
            return TryResolveAssignment(groupKey, DecoGroupState.GroupSet.Decoration, out assignment);
        }

        /// <summary>
        /// 带"被拖的那个事件"的落点解析（§22.2）：在下面那张纯映射表之上只补一条 ——
        /// `type:*` 当且仅当**它就是该事件自己的类型组**时有效，语义是"清除手动归属"
        /// （装饰随即按规则回到自己的类型组；tag 不动）。拖到别的类型组仍然无效（那等于改类型）。
        /// </summary>
        internal static bool TryResolveAssignment(string groupKey, DecoGroupState.GroupSet set, LevelEvent levelEvent,
            out GroupAssignment assignment)
        {
            if (TryResolveAssignment(groupKey, set, out assignment))
                return true;

            assignment = default;
            if (levelEvent == null || string.IsNullOrEmpty(groupKey))
                return false;
            if (!groupKey.StartsWith("type:", StringComparison.Ordinal))
                return false;
            if (!string.Equals(groupKey, DecoGroupState.TypeKeyOf(levelEvent.eventType), StringComparison.Ordinal))
                return false;

            assignment.Kind = GroupAssignKind.ClearManual;
            return true;
        }

        /// <summary>与上面同义，但指定读哪一套自定义分组定义（装饰 / 事件，见 §17.2）。</summary>
        internal static bool TryResolveAssignment(string groupKey, DecoGroupState.GroupSet set, out GroupAssignment assignment)
        {
            assignment = default;
            if (string.IsNullOrEmpty(groupKey))
                return false;

            if (groupKey.StartsWith("custom:", StringComparison.Ordinal))
            {
                if (groupKey == "custom:rest")
                {
                    assignment.Kind = GroupAssignKind.Clear;   // 「未分组」：退出手动/标签分组
                    return true;
                }
                if (!int.TryParse(groupKey.Substring("custom:".Length), out int index))
                    return false;

                List<(string Name, string Tag)> groups = DecoGroupState.ReadCustomGroups(set);
                if (index < 0 || index >= groups.Count)
                    return false;
                if (groups[index].Tag.Length > 0)
                {
                    assignment.Kind = GroupAssignKind.Tag;
                    assignment.Tag = groups[index].Tag;
                    return true;
                }
                // tag 留空的分组都是"手动成员组"；**只有自定义模式**下第一个才是兜底组
                // （拖进去 = 退出手动/tag 归属）。类型/标签模式下没有"兜底"可退，
                // 拖进任何自定义分组都应该老老实实记手动归属 —— 见 §14.1 / §15.6。
                if (Main.AutoGroupMode == AutoGroupMode.Custom
                    && index == DecoGroupState.FirstFallbackGroupIndex(groups))
                {
                    assignment.Kind = GroupAssignKind.Clear;
                    return true;
                }
                assignment.Kind = GroupAssignKind.Manual;
                assignment.Index = index;
                return true;
            }

            if (groupKey.StartsWith("tag:", StringComparison.Ordinal))
            {
                string tag = groupKey.Length > 4 ? groupKey.Substring(4) : "";
                if (tag.Length == 0)
                {
                    assignment.Kind = GroupAssignKind.Clear;   // 「未分组」
                    return true;
                }
                assignment.Kind = GroupAssignKind.Tag;
                assignment.Tag = tag;
                return true;
            }

            // type:*（按类型分组）改归属等于改事件类型，做不到
            return false;
        }

        /// <summary>
        /// 兼容旧签名（离线 harness 用它验证"落点 → tag"的纯映射）：
        /// Tag/Clear 都能给出 tag，Manual 没有 tag 可写（该组 tag 本来就是空的）故返回空串。
        /// </summary>
        internal static bool TryResolveTargetTag(string groupKey, out string targetTag)
        {
            targetTag = null;
            if (!TryResolveAssignment(groupKey, out GroupAssignment assignment))
                return false;
            targetTag = assignment.Kind == GroupAssignKind.Tag ? assignment.Tag : "";
            return true;
        }

        /// <summary>把落点语义写到事件上（分页器直选弹窗也复用这套，见 §16.2）。</summary>
        internal static void ApplyAssignment(LevelEvent levelEvent, GroupAssignment assignment)
        {
            ApplyAssignment(levelEvent, assignment, DecoGroupState.GroupSet.Decoration);
        }

        /// <remarks>
        /// 标签写到 <see cref="GroupTagKeyOf"/> 给出的属性上（装饰 "tag"、砖上事件 "eventTag"）。
        /// 该事件类型没有标签属性（返回 null）时：Tag 什么都不做（它进不了按 tag 匹配的组，
        /// <see cref="NeedsChange(LevelEvent, GroupAssignment, DecoGroupState.GroupSet)"/> 也返回 false）；
        /// Clear 只清手动归属、不写任何标签键（不往 data 里塞未注册键）。
        /// 调用方负责包在 SaveStateScope 里（标签与手动归属都是关卡数据）。
        /// </remarks>
        internal static void ApplyAssignment(LevelEvent levelEvent, GroupAssignment assignment, DecoGroupState.GroupSet set)
        {
            if (levelEvent == null)
                return;
            string tagKey = GroupTagKeyOf(levelEvent, set);
            switch (assignment.Kind)
            {
                case GroupAssignKind.Tag:
                    if (tagKey == null)
                        break;   // 没有标签属性：写不进去，也就不动手动归属
                    levelEvent[tagKey] = assignment.Tag;
                    DecoGroupState.ClearManualGroup(levelEvent, set);
                    break;
                case GroupAssignKind.Manual:
                    // 只记归属，不动 tag：tag 是用户的数据，留给按标签分组用
                    DecoGroupState.SetManualGroup(levelEvent, set, assignment.Index);
                    break;
                case GroupAssignKind.ClearManual:
                    // 只清手动归属，tag 原样留着（拖回自己的类型分组，见 §22.2）
                    DecoGroupState.ClearManualGroup(levelEvent, set);
                    break;
                default:
                    if (tagKey != null)
                        levelEvent[tagKey] = "";
                    DecoGroupState.ClearManualGroup(levelEvent, set);
                    break;
            }
        }

        /// <summary>
        /// 把装饰在 `levelData.decorations` 里移到锚点之前/之后（`anchor == null` ⇒ 追加到末尾），
        /// 并把装饰对象的 sibling 顺序同步成数组顺序（数组顺序就是绘制顺序，原版拖拽也是这么做的）。
        /// 返回是否真的动了。
        ///
        /// 与原版 `PropertyControl_DecorationsList.OnItemDropSides` 一致（r148 IL 已核）：`levelData.decorations`
        /// 与 `scrDecorationManager.allDecorations` **在同一下标上一起** Remove/Insert。只动前者的话两个数组错位：
        /// `SaveState` 按 allDecorations 下标记选区、`UndoOrRedo` 用 levelData.decorations[下标] 还原
        /// ⇒ 撤销后选中的是别的装饰（shift 范围选择、GetDecorationIndex 也都按 allDecorations 算）。
        /// </summary>
        private static bool MoveInDecorations(scnEditor editor, LevelEvent levelEvent, LevelEvent anchor, bool insertBefore)
        {
            DecorationsArray<LevelEvent> decorations = editor.levelData != null ? editor.levelData.decorations : null;
            if (decorations == null)
                return false;
            if (!TryComputeInsertIndex(decorations, levelEvent, anchor, insertBefore, out int insertIndex))
                return false;

            int from = decorations.IndexOf(levelEvent);
            if (from < 0)
                return false;

            // 场景侧的装饰对象列表：原版用 GetDecorationIndex（= allDecorations 下标）当插入位，两个数组同步增删
            List<scrDecoration> all = null;
            scrDecoration decoration = null;
            int allFrom = -1;
            try
            {
                all = scrDecorationManager.instance != null ? scrDecorationManager.instance.allDecorations : null;
                decoration = scrDecorationManager.GetDecoration(levelEvent);
                allFrom = all != null && decoration != null ? all.IndexOf(decoration) : -1;
            }
            catch (Exception e)
            {
                Main.Logger?.Log("读取装饰对象列表失败，只重排关卡数据: " + e.Message);
                all = null;
            }

            // DecorationsArray 没有重写 RemoveAt（IL 已核：只有 Add / Insert 两个 new 方法 + CallDecorationUpdate），
            // 这里就是 List.RemoveAt、不回调；下面的 Insert 会回调一次 OnDecorationUpdate，正好覆盖整次移动。
            decorations.RemoveAt(from);
            if (allFrom >= 0)
                all.RemoveAt(allFrom);
            int index = Mathf.Clamp(insertIndex, 0, decorations.Count);
            if (allFrom >= 0)
                all.Insert(Mathf.Clamp(index, 0, all.Count), decoration);   // 先插场景列表：Insert 回调里刷新列表时两边已一致
            if (InsertDecoration(decorations, index, from, levelEvent))
                return true;
            // 关卡数据被放回了原位置：场景列表也放回去，保持两边对齐
            if (allFrom >= 0 && all.Remove(decoration))
                all.Insert(Mathf.Clamp(allFrom, 0, all.Count), decoration);
            return false;
        }

        /// <summary>
        /// 纯逻辑（便于离线验证）：算"把 item 移到 anchor 之前/之后"在**从数组里移除 item 之后**的目标下标。
        /// `anchor == null` ⇒ 追加到末尾。返回 false = 不用动（已经在该位置 / 锚点就是自己 / 找不到）。
        /// </summary>
        internal static bool TryComputeInsertIndex(List<LevelEvent> order, LevelEvent item, LevelEvent anchor, bool insertBefore, out int insertIndex)
        {
            insertIndex = -1;
            if (order == null || item == null)
                return false;
            if (anchor != null && ReferenceEquals(anchor, item))
                return false;   // 拖到自己身上

            int from = order.IndexOf(item);
            if (from < 0)
                return false;

            if (anchor == null)
            {
                insertIndex = order.Count - 1;              // 末尾（移除后长度 -1）
                return from != order.Count - 1;
            }

            int anchorIndex = order.IndexOf(anchor);
            if (anchorIndex < 0)
                return false;
            int target = insertBefore ? anchorIndex : anchorIndex + 1;
            if (target > from)
                target--;                                    // 移除 item 之后，目标下标前移一位
            insertIndex = target;
            return target != from;
        }

        /// <summary>
        /// 调 `DecorationsArray&lt;T&gt;.Insert`（`new` 隐藏而不是 override，所以必须用 DecorationsArray 类型的引用
        /// 在编译期绑定；它 = `List.Insert` + `CallDecorationUpdate()` 刷新列表）。
        /// 失败时不能盲目重插：`List.Insert` 可能已经成功、只是后面的回调抛了 ⇒ 先看元素在不在数组里；
        /// 不在就退回 `List.Insert`，再失败就放回原位置 <paramref name="originalIndex"/>，保证元素既不重复也不丢。
        /// 返回元素是否已经在新位置上。
        /// </summary>
        private static bool InsertDecoration(DecorationsArray<LevelEvent> decorations, int index, int originalIndex, LevelEvent levelEvent)
        {
            try
            {
                decorations.Insert(index, levelEvent);
                return true;
            }
            catch (Exception e)
            {
                Main.Logger?.Log("装饰数组 Insert 失败: " + e.Message);
            }

            List<LevelEvent> list = decorations;
            if (list.Contains(levelEvent))
                return true;   // 数据已经插进去了（抛异常的是刷新回调），别再插第二次
            try
            {
                list.Insert(Mathf.Clamp(index, 0, list.Count), levelEvent);
                return true;
            }
            catch (Exception e)
            {
                Main.Logger?.Log("退回 List.Insert 也失败，装饰放回原位置: " + e.Message);
            }
            if (!list.Contains(levelEvent))
                list.Insert(Mathf.Clamp(originalIndex, 0, list.Count), levelEvent);
            return false;
        }

        /// <summary>装饰的绘制顺序 = `levelData.decorations` 顺序：把场景里装饰对象的分层顺序对齐。</summary>
        private static void SyncDecorationOrder(scnEditor editor)
        {
            List<LevelEvent> list = editor.levelData != null ? editor.levelData.decorations : null;
            if (list == null)
                return;

            Transform parent = null;
            int sibling = 0;
            for (int i = 0; i < list.Count; i++)
            {
                var decoration = scrDecorationManager.GetDecoration(list[i]);
                if (decoration == null)
                    continue;
                Transform transform = decoration.transform;
                if (parent == null)
                    parent = transform.parent;
                if (parent == null || transform.parent != parent)
                    continue;
                transform.SetSiblingIndex(sibling++);
            }
        }

        /// <summary>已经是目标状态就不用动（避免无谓的撤销点与列表重建）。</summary>
        internal static bool NeedsChange(LevelEvent levelEvent, GroupAssignment assignment)
        {
            return NeedsChange(levelEvent, assignment, DecoGroupState.GroupSet.Decoration);
        }

        internal static bool NeedsChange(LevelEvent levelEvent, GroupAssignment assignment, DecoGroupState.GroupSet set)
        {
            if (levelEvent == null)
                return false;
            int manual = DecoGroupState.GetManualGroup(levelEvent, set);
            string tagKey = GroupTagKeyOf(levelEvent, set);   // null = 该类型没有标签属性（与 ApplyAssignment 同一判定）
            switch (assignment.Kind)
            {
                case GroupAssignKind.Tag:
                    if (tagKey == null)
                        return false;                    // 写不了标签 ⇒ ApplyAssignment 什么都不做
                    return manual >= 0 || !string.Equals(GetTag(levelEvent, tagKey), assignment.Tag, StringComparison.Ordinal);
                case GroupAssignKind.Manual:
                    return manual != assignment.Index;
                case GroupAssignKind.ClearManual:
                    return manual >= 0;                  // 只看手动归属；tag 不动，也就不比 tag
                default:
                    return manual >= 0 || (tagKey != null && GetTag(levelEvent, tagKey).Length > 0);
            }
        }

        /// <summary>
        /// 分组用的"标签"属性名（装饰 / 事件两套共用的约定，拖放写入与分组读取都必须走这里）：
        ///  - 装饰：<c>"tag"</c>（装饰自己的标签）；
        ///  - 砖上事件：<c>"eventTag"</c>（事件自己的标签）。事件上的 <c>"tag"</c> 是 MoveDecorations /
        ///    SetText / RepeatEvents 等的**目标选择器**，绝不能当分组键读写。
        /// 该事件类型没有注册这个属性时返回 null（⇒ 不能按标签分组/改写，只能手动归属）。
        /// r148 只有 23/57 个事件类型注册了 eventTag，所以 null 分支很常见。
        /// </summary>
        internal static string GroupTagKeyOf(LevelEvent levelEvent, DecoGroupState.GroupSet set)
        {
            string key = set == DecoGroupState.GroupSet.Event ? "eventTag" : "tag";
            if (levelEvent == null)
                return null;
            if (set == DecoGroupState.GroupSet.Decoration)
                return key;
            try
            {
                LevelEventInfo info = levelEvent.info;
                if (info != null && info.propertiesInfo != null && info.propertiesInfo.ContainsKey(key))
                    return key;
            }
            catch { }
            return null;
        }

        /// <summary>读分组用的标签（属性名由 <see cref="GroupTagKeyOf"/> 决定）。</summary>
        private static string GetTag(LevelEvent levelEvent, string tagKey)
        {
            if (levelEvent == null || string.IsNullOrEmpty(tagKey))
                return "";
            try
            {
                if (levelEvent.TryGet<string>(tagKey, out string tag))
                    return tag ?? "";
            }
            catch { }
            return "";
        }

        private static bool IsHomogeneous(List<LevelEvent> events)
        {
            LevelEventType type = events[0] != null ? events[0].eventType : LevelEventType.AddDecoration;
            for (int i = 1; i < events.Count; i++)
                if (events[i] != null && events[i].eventType != type)
                    return false;
            return true;
        }

        // ---------------------------------------------------------------- shift 范围选择（§30.1）

        /// <summary>
        /// shift 范围选择：按**显示顺序**（槽位表）选中"锚点行 → 点击行"之间的所有装饰行。
        /// 跨组时就是屏幕中间经过的那些行（含其它组的行）—— 资源管理器语义，选区与眼睛看到的一致。
        /// 返回 false = 这次没法处理（没有槽位 / 点击的事件不在显示表里），让原版逻辑继续跑。
        ///
        /// **为什么不能直接用原版**：原版 `PropertyControl_List.SelectItemsInRange` 的区间取自
        /// `scrDecorationManager.GetDecorationIndex()` —— **全关卡装饰数组 `allDecorations` 的下标**
        /// （IL 核过：`GetDecoration(i)` 就是 `allDecorations[i]`），只有"列表顺序 == 关卡顺序"时才
        /// 等于屏幕顺序。分组会把 `filteredEvents` 重排成分组顺序（§16.2）⇒ 同一个下标区间会扫进
        /// 别的组的装饰：用户实测"从'图片'组最后一行往上选，把文本/对象/粒子也一起选进来"。
        /// </summary>
        internal static bool SelectDisplayRange(PropertyControl_List list, LevelEvent clicked)
        {
            scnEditor editor = scnEditor.instance;
            List<DecoGroupSlot> slots = DecoGroupState.Slots;
            if (editor == null || clicked == null || list == null || slots.Count == 0)
                return false;

            int clickedSlot = SlotIndexOfEvent(clicked);
            if (clickedSlot < 0)
                return false;                        // 点的那一行不在显示表里（理论上不会）

            int anchorSlot = ResolveAnchorSlot(list, clickedSlot);
            int from = Mathf.Min(anchorSlot, clickedSlot);
            int to = Mathf.Max(anchorSlot, clickedSlot);

            var picks = new List<LevelEvent>();
            for (int i = from; i <= to; i++)
            {
                if (slots[i].IsHeader)
                    continue;                        // 组头行不是装饰，跳过（但它占着显示位置）
                LevelEvent e = slots[i].Event;
                if (e != null && scrDecorationManager.GetDecoration(e) != null)
                    picks.Add(e);
            }
            if (picks.Count == 0)
                return false;

            // 与原版 SelectItemsInRange 一致（r148 IL 已核）：整段包在一个 SaveStateScope(editor, false, true, false) 里
            // （里面每次 SelectDecoration 自带的 scope 不再各自整关存快照），把范围**加进**现有选区（不 DeselectAll）；
            // 点的就是锚点且当前只选了一个 ⇒ 当普通单选处理。
            using (new SaveStateScope(editor, false, true, false))
            {
                List<LevelEvent> selected = editor.selectedDecorations;
                if (anchorSlot == clickedSlot && selected != null && selected.Count == 1)
                {
                    editor.SelectDecoration(clicked, false, false, false, false);
                    return true;
                }

                for (int i = 0; i < picks.Count; i++)
                    editor.SelectDecoration(picks[i], false, false, true, true);   // 累加选择，先不刷面板

                // 锚点钉住不动（与分页器 §26.2 同一套习惯：连按 shift 都在同一段上伸缩）——
                // 但原版 `SelectDecoration` 会把 lastSelectedIndex 改成"最后选中的那个"，这里写回锚点。
                LevelEvent anchorEvent = SlotEvent(anchorSlot);
                if (anchorEvent != null)
                {
                    int anchorGlobal = scrDecorationManager.GetDecorationIndex(anchorEvent);
                    if (anchorGlobal >= 0)
                        list.lastSelectedIndex = anchorGlobal;
                }
            }

            // 统一刷新右侧属性面板（多选 ⇒ 原版 fake event / 属性合并，见 §18.1/§29.2）
            editor.levelEventsPanel?.ShowInspector(true, false);
            editor.levelEventsPanel?.ShowPanel(clicked.eventType, 0);
            editor.propertyControlDecorationsList?.RefreshItemsList(false);
            return true;
        }

        /// <summary>事件在显示槽位表里的下标（找不到返回 -1）。</summary>
        private static int SlotIndexOfEvent(LevelEvent e)
        {
            List<DecoGroupSlot> slots = DecoGroupState.Slots;
            for (int i = 0; i < slots.Count; i++)
                if (!slots[i].IsHeader && ReferenceEquals(slots[i].Event, e))
                    return i;
            return -1;
        }

        /// <summary>槽位上的事件（头行返回 null）。</summary>
        private static LevelEvent SlotEvent(int slotIndex)
        {
            List<DecoGroupSlot> slots = DecoGroupState.Slots;
            if (slotIndex < 0 || slotIndex >= slots.Count || slots[slotIndex].IsHeader)
                return null;
            return slots[slotIndex].Event;
        }

        /// <summary>
        /// 范围选择的锚点（显示下标）：优先原版记的 `lastSelectedIndex`（上一次点击/选中的那个装饰，
        /// 全局下标 ⇒ 换算成显示下标），其次"当前唯一选中项"，最后退回点击行自身。
        /// </summary>
        private static int ResolveAnchorSlot(PropertyControl_List list, int clickedSlot)
        {
            int slot = SlotIndexOfEvent(EventAtGlobalIndex(list.lastSelectedIndex));
            if (slot >= 0)
                return slot;

            scnEditor editor = scnEditor.instance;
            if (editor != null)
            {
                List<LevelEvent> selected = editor.selectedDecorations;
                if (selected != null && selected.Count == 1)
                {
                    slot = SlotIndexOfEvent(selected[0]);
                    if (slot >= 0)
                        return slot;
                }
            }
            return clickedSlot;
        }

        /// <summary>全关卡装饰数组下标 → 装饰事件（越界/取不到返回 null）。</summary>
        private static LevelEvent EventAtGlobalIndex(int globalIndex)
        {
            if (globalIndex < 0)
                return null;
            try
            {
                scrDecoration decoration = scrDecorationManager.GetDecoration(globalIndex);
                return decoration != null ? decoration.sourceLevelEvent : null;
            }
            catch { }
            return null;
        }
    }
}
