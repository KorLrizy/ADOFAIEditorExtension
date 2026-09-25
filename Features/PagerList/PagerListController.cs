using ADOFAI;
using ADOFAI.LevelEditor.Controls;
using ADOFAIEditorExtension.Features.DecoGrouping;
using ADOFAIEditorExtension.Utils;
using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ADOFAIEditorExtension.Features.PagerList
{
    /// <summary>挂在分页器文本（与分页器根对象）上，左键点击时打开直选列表。</summary>
    internal sealed class PagerClickTarget : MonoBehaviour
    {
        internal InspectorTab Tab;

        internal void OnPointerClick(BaseEventData eventData)
        {
            PagerListController.OnPagerClick(Tab, eventData as PointerEventData);
        }
    }

    /// <summary>
    /// 备注列的悬停目标（§21.3 / §28.2）：鼠标停到**事件行右侧备注列那一段**、且该行真有备注时，
    /// 在弹窗里固定位置（右沿贴着备注列右边界、垂直跟着这一行）弹出完整备注；移出备注列立即收起。
    ///
    /// 挂在整行的透明点击层上（它才是射线命中的对象），触发与否**靠指针是否落在备注列的矩形里**判断，
    /// 而不是另铺一层射线目标 —— 那层会盖住行按钮，让整行的悬停配色在备注列上失效，
    /// 还得把点击/拖拽处理器抄一份过去（§28.2 的取舍）。
    /// </summary>
    internal sealed class PagerRowHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerMoveHandler
    {
        internal string Note;

        /// <summary>备注列自己的矩形（备注文本那一块），判定与浮层定位都用它。</summary>
        internal RectTransform NoteArea;

        public void OnPointerEnter(PointerEventData eventData) => RefreshHover(eventData);

        public void OnPointerMove(PointerEventData eventData) => RefreshHover(eventData);

        public void OnPointerExit(PointerEventData eventData)
        {
            PagerListController.HideNoteTooltip();
        }

        /// <summary>
        /// 判定指针在不在备注列里、据此显示/收起浮层。
        /// 名字**不能叫 `Update`** —— 带参数的 `Update` 会被 Unity 当作帧回调而报
        /// `Script error: Update() can not take parameters.`（日志里刷了一条），
        /// 虽然我们只手动调用、不影响悬停，但错误日志不能留。
        /// </summary>
        private void RefreshHover(PointerEventData eventData)
        {
            if (eventData == null
                || ADOFAIEditorExtension.Features.Notes.EventNote.IsEmpty(Note)
                || !PagerListController.PointerInRect(NoteArea, eventData))
            {
                PagerListController.HideNoteTooltip();
                return;
            }
            PagerListController.ShowNoteTooltip(Note, NoteArea);
        }
    }

    /// <summary>事件行的点击目标：普通点=切到该事件（不关窗）；Ctrl=切换多选；Shift=区间多选。</summary>
    internal sealed class PagerRowClick : MonoBehaviour, IPointerClickHandler
    {
        internal int Index;

        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData == null || eventData.button != PointerEventData.InputButton.Left)
                return;
            // 用游戏自己的输入状态（RDInput）判断修饰键，跟编辑器其它地方一致
            PagerListController.OnRowClick(Index, RDInput.holdingControl, RDInput.holdingShift);
        }
    }

    /// <summary>弹出列表里分组头行的四个可点区域（目前用到箭头与组名两个）。</summary>
    internal enum PagerHeaderArea
    {
        Arrow,
        Name
    }

    /// <summary>挂在分组头行上，记住自己是哪个组。</summary>
    internal sealed class PagerGroupHeader : MonoBehaviour
    {
        internal string Key;

        internal void OnArrow() => PagerListController.ToggleGroupCollapsed(Key);
        internal void OnName() => PagerListController.SelectGroupMembers(Key);
    }

    /// <summary>分组头行上的点击目标（箭头 / 组名）。</summary>
    internal sealed class PagerHeaderClick : MonoBehaviour, IPointerClickHandler
    {
        internal PagerGroupHeader Owner;
        internal PagerHeaderArea Area;

        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData == null || eventData.button != PointerEventData.InputButton.Left || Owner == null)
                return;
            eventData.Use();
            if (Area == PagerHeaderArea.Arrow)
                Owner.OnArrow();
            else
                Owner.OnName();
        }
    }

    /// <summary>
    /// 弹窗空白处（透明挡板）的点击记录（§37）：只打一条日志，用来消除"点了没反应、日志什么都没有"的盲区 ——
    /// 下一次就能区分"点在行/组头上"还是"点在弹窗空白处"。
    /// </summary>
    internal sealed class PagerBackgroundClickLog : MonoBehaviour, IPointerClickHandler
    {
        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData == null || eventData.button != PointerEventData.InputButton.Left)
                return;
            PagerListController.LogPopupBlankClick();
        }
    }

    /// <summary>
    /// 事件行的拖拽源：拖到分组头/组内行 = 加入该组并插到落点位置，同组内拖动 = 纯排序。
    /// 多选状态下拖动 = 一起移动。
    /// </summary>
    internal sealed class PagerRowDragTarget : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        private int draggingIndex = -1;

        public void OnBeginDrag(PointerEventData eventData)
        {
            PagerRowClick click = GetComponent<PagerRowClick>();
            draggingIndex = click != null ? click.Index : -1;
            if (draggingIndex < 0)
                return;
            eventData?.Use();
            // 拖动行时先关掉列表自身的滚动，免得一边拖一边把列表滚走（松手/失效时恢复）；
            // 顺手收起备注浮层，别让它跟着鼠标一路飘
            PagerListController.SetScrollEnabled(false);
            PagerListController.HideNoteTooltip();
            PagerListController.UpdateDropFeedback(eventData != null ? eventData.position : Vector2.zero, draggingIndex);
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (draggingIndex < 0)
                return;
            eventData?.Use();
            PagerListController.UpdateDropFeedback(eventData != null ? eventData.position : Vector2.zero, draggingIndex);
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            if (draggingIndex < 0)
                return;
            eventData?.Use();
            int index = draggingIndex;
            draggingIndex = -1;
            PagerListController.SetScrollEnabled(true);

            Vector2 position = eventData != null ? eventData.position : Vector2.zero;
            bool hasTarget = PagerListController.TryFindDropTarget(position, out PagerListController.DropTarget target);
            PagerListController.ClearDropFeedback();
            if (hasTarget)
                PagerListController.ApplyDrop(index, target);
        }

        private void OnDisable()
        {
            if (draggingIndex < 0)
                return;
            draggingIndex = -1;
            PagerListController.SetScrollEnabled(true);
            PagerListController.ClearDropFeedback();
        }
    }

    /// <summary>
    /// 同砖同类型多事件分页器（◀ 1/3 ▶）的直选列表。
    ///
    /// 弹窗：克隆原版 okPopupContainer（居中模态）挂在 popupWindow 下；宿主自带的文本/按钮全部隐藏，
    /// 标题、关闭按钮、列表区都由本类用**固定几何**自己锚定（不再依赖测量宿主内部控件的位置，
    /// 那是上一版"行被裁掉一半 / 只看到一行"的根源之一）。
    /// 行：克隆装饰列表自己的行 prefab（`listItemPool.itemPrefab`），行高取原版 `itemHeight`，
    /// 只保留名称文本（+ 原版 selectionBackground 作为当前行高亮）+ 右侧一段备注文本（§21.2），
    /// 整行铺一层透明点击层；悬停**备注那一段**时在固定位置弹完整备注（§28.2）。
    /// 列表内容按"事件分组"排成 组头行 + 事件行（规则与装饰分组同源，见 docs §16.2），
    /// 支持折叠、点组名轮换预览、Ctrl/Shift 多选、以及"拖到组上加入 / 拖出 / 组内排序"（§16.3）。
    /// 点事件行**不关窗**（§16.1）；顶边用一个垂直翻转的外框贴图做圆角盖（§16.4）。
    /// **窗口必须先显示再建行**（§28.1）：行/备注如果是在未激活的层级里建的，备注那次 `text = …` 会丢。
    /// 窗口显示/隐藏沿用 ShowPopup(true/false, (PopupType)233, false)（与 MTH 的消息弹窗同一套做法：
    /// 233 不在 PopupType 枚举里，只会打开/关闭弹窗窗口、不激活任何原版容器）。
    /// </summary>
    internal static class PagerListController
    {
        // 固定几何：弹窗内部布局完全由这里决定
        private const float ListPaddingX = 16f;
        private const float ListPaddingY = 8f;
        private const float HeaderHeight = 46f;
        private const float FooterHeight = 52f;
        // 标题与窗口顶边留出的间距：原版边框是圆角的、上边线就画在矩形顶部，
        // 标题紧贴顶边会把那条线盖掉（实测"上边线不闭合"）
        private const float TitleTopInset = 12f;
        private const float FooterBottomInset = 8f;
        private const float RowSpacing = 3f;
        private const float FallbackRowHeight = 32f;
        // popupRoot 宽度异常时的兜底宽度（正常情况永远是 prefab 自带的宽度）
        private const float FallbackWidth = 520f;
        private const int MaxVisibleRows = 8;
        // 外框贴图 `popup_border` 的顶边是"开口"的（IL/贴图已核，见 §13.1）：
        // 贴图 256×128、border = (左 93, 下 125, 右 94, 上 0)，只有左右两条白线一直画到贴图顶边，
        // 顶边那一条横线压根不存在（原版窗口贴着屏幕顶边，上边在屏幕外所以看不出来）。
        // 弹窗挪到屏幕中央后就得自己补一条顶线，四边才闭合。
        private const float FallbackLineThickness = 3.5f;   // 换算失败时的兜底线宽（UI 单位）
        private const float ScreenMargin = 24f;             // 弹窗高度最多占到画布高度减这么多
        // 顶盖（圆角）：把外框贴图**垂直翻转**盖在弹窗顶部 —— 贴图本身只有下方圆角、顶边是开口的
        // （§13.1/§15.1），翻转后那对圆角就跑到上方，与下方圆角对称；顶盖高度取贴图下边框的尺寸。
        private const float MinPopupHeight = 120f;          // 太矮时上下圆角会挤在一起，给个下限
        private const float LabelAnchorMaxX = 0.58f;        // 事件行标签最宽到这里，右侧留给备注（§21.2）
        private const float NoteAnchorMinX = 0.55f;         // 备注区左边界
        private const float NoteGap = 10f;                  // 标签与备注之间的最小间距
        private const float NoteRightInset = 12f;           // 备注距行右沿的留白
        private const float NoteTipWidth = 340f;            // 悬停浮层宽度上限（浮层宽度取备注列宽，见 §28.2）
        private const float NoteTipMinWidth = 200f;         // 备注列很窄时浮层的兜底宽度
        private const float NoteTipPadding = 8f;            // 浮层内边距
        private const float NoteTipMargin = 6f;             // 浮层与弹窗边缘的最小距离
        private const string ExpandedMark = "\u25BE";       // ▾（与装饰分组头一致）
        private const string CollapsedMark = "\u25B8";      // ▸
        private const float HeaderArrowWidth = 22f;
        private const float HeaderArrowZoneMax = 160f;      // 箭头点击区上限：再宽就会把组名区挤没了

        private static GameObject popupRoot;
        private static TMP_Text titleText;
        private static Button closeButton;
        private static ScrollRect scrollRect;
        private static RectTransform listContent;
        private static GameObject rowTemplate;
        private static Canvas uiCanvas;
        private static RectTransform noteTooltip;   // 备注悬停浮层（深色底 + 白字，不吃射线）
        private static TMP_Text noteTooltipText;
        private static RectTransform frameTopCap;   // 顶部圆角盖（外框贴图底部圆角带的镜像）
        private static Image frameTopCapImage;
        private static RectTransform frameBody;     // 外框本体（根 Image 的替身，矩形矮掉顶部圆角带）
        private static Image frameFillImage;        // 顶部条带的底色补丁（贴图中心那一像素）
        private static Sprite frameFillSprite;      // 上面那块补丁用的 1×1 sprite（Sprite.Create 出来的，要销毁）

        // 行高来自装饰列表（原版 itemHeight），字体/配色一律沿用行 prefab 自身的设置
        private static float rowHeight = FallbackRowHeight;

        // 右侧备注的配色：压暗一档当作次要信息；当前行/多选行是白底，得跟着翻成黑字
        private static readonly Color NoteColor = new Color(1f, 1f, 1f, 0.75f);
        private static readonly Color NoteColorSelected = new Color(0f, 0f, 0f, 0.75f);

        // 备注浮层的锚点（§28.2）：pivot = 右中 ⇒ `localPosition` 就是"备注列右边界 × 该行垂直中心"那一点，
        // 于是浮层右沿与备注列右沿对齐、垂直方向居中在悬停的那一行上（位置固定，不跟鼠标）。
        // 单独提出来是为了能离线断言（const 会被编译进 IL，读不出来）。
        private static readonly Vector2 NoteTipPivot = new Vector2(1f, 0.5f);

        private static readonly List<GameObject> rows = new List<GameObject>();
        private static InspectorTab openTab;
        private static bool isOpen;
        private static int openedFrame = -1;
        private static int currentRowOrder = -1;

        /// <summary>当前显示的事件（跨重建保持；行下标会因分组/排序变化，所以记对象）。</summary>
        private static LevelEvent currentEvent;

        /// <summary>
        /// 多选批量态（§34）：fake 事件**关窗也不丢**，右侧面板持续显示批量视图；`fakeEvent.realEvents`
        /// 就是"批量覆盖的事件集合"（退出判定、重开恢复高亮都用它），`fakeTab` 记它属于哪个标签页。
        /// 退出（清 fake + 面板回单事件）的条件见 `ExitMultiSelect` 的调用点。
        /// </summary>
        private static InspectorTab fakeTab;

        /// <summary>
        /// 我们**自己**正在把 fake 挂到面板上（`BindFakeToPanel` 内部的 ShowInspector 会连带触发
        /// 一次 `ShowPanel` 的后置）。这段时间不能按"面板选中的事件不在批量里"判定退出 ——
        /// 那一刻 `selectedEvent` 恰恰还是某个真实事件，退出来就是自己把自己的多选掐掉（§39 H2）。
        /// </summary>
        private static bool bindingBatch;

        /// <summary>
        /// "事件增删的出口跑在半途中（原版撤销作用域还开着），批量存活校验欠做一次"（§39 H3）：
        /// 那种时刻关卡是半成品，查了会误判，所以只记个账，由下一帧的 <see cref="Tick"/> 补查。
        /// </summary>
        private static bool pendingLivenessCheck;

        // 左上角提示的去抖（§34.4）：连续 ctrl 加选时不刷屏，数量稳定后弹一次
        private const float ToastSettleSeconds = 0.35f;
        private static string pendingToastKey;
        private static int pendingToastCount;
        private static int lastToastCount = -1;
        private static float lastSelectionChangeTime = -10f;
        private static float lastToastTime = -10f;

        /// <summary>哪一帧弹过操作提示（那帧的"已退出多选"要让位，§39 M1）。</summary>
        private static int suppressExitToastFrame = -1;

        /// <summary>折叠的分组键（会话级，只在这次弹窗打开期间有效）。</summary>
        private static readonly HashSet<string> collapsedGroups = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Ctrl/Shift 多选中的行（= 事件在"该砖该类型事件列表"里的下标）。</summary>
        private static readonly HashSet<int> selectedIndices = new HashSet<int>();
        private static int selectionAnchor = -1;

        private static readonly List<Slot> slots = new List<Slot>();

        /// <summary>事件下标 → 所属分组键（折叠时槽位表里没有成员行，用它反查）。</summary>
        private static readonly Dictionary<int, string> groupKeyByIndex = new Dictionary<int, string>();

        // 拖拽落点反馈（弹窗内自建：白横线 + 跟随鼠标的小方块，规格同装饰列表那套）
        private static RectTransform dropLine;
        private static RectTransform cursorMark;
        private static bool dropObjectsResolved;
        private static bool dropFeedbackLogged;

        /// <summary>
        /// 我们自己触发 ShowPanel/选砖时，别让"面板切换就关窗"的补丁把弹窗关掉。
        /// 存的是"抑制到哪一帧为止"（当前帧 + 之后 2 帧）：面板刷新有时会延后一帧（布局/UpdatePanel），
        /// 只压当前帧不够，shift 加减选就曾因此把弹窗关掉（§26.2）。
        /// </summary>
        private static int suppressCloseUntilFrame = -1;

        /// <summary>一个显示槽位：分组头行或事件行。</summary>
        private struct Slot
        {
            internal bool IsHeader;
            internal string Key;            // 头行的分组键
            internal string Label;          // 头行的显示名
            internal int Count;             // 头行的组内数量
            internal int OriginalIndex;     // 事件行 = 该事件在 stack 里的下标
            internal string Tag;
            internal bool UseEventTag;
            internal string Note;           // 事件行右侧显示的备注（§21），空串 = 不显示
        }

        // ------------------------------------------------------------------ 点击入口

        /// <summary>标签页创建时挂点击入口：分页器文本 + 分页器根对象（扩大点击范围，箭头仍然是它自己的 Button 优先）。</summary>
        internal static void AttachToTab(InspectorTab tab)
        {
            if (tab == null || tab.cycleButtons == null)
                return;
            CycleButtons cycle = tab.cycleButtons;
            if (cycle.text != null)
                AttachClickTarget(tab, cycle.text.gameObject);
            AttachClickTarget(tab, cycle.gameObject);
        }

        private static void AttachClickTarget(InspectorTab tab, GameObject target)
        {
            if (target == null || target.GetComponent<PagerClickTarget>() != null)
                return;

            // 分页器根对象本身可能没有 Graphic，补一个全透明 Image 才能收到射线（子对象里的箭头/文本优先命中）
            if (target.GetComponent<Graphic>() == null)
            {
                Image image = target.AddComponent<Image>();
                image.color = new Color(1f, 1f, 1f, 0f);
                image.raycastTarget = true;
            }

            PagerClickTarget marker = target.AddComponent<PagerClickTarget>();
            marker.Tab = tab;

            EventTrigger trigger = target.GetComponent<EventTrigger>();
            if (trigger == null)
                trigger = target.AddComponent<EventTrigger>();
            EventTrigger.Entry entry = new EventTrigger.Entry { eventID = EventTriggerType.PointerClick };
            entry.callback.AddListener(marker.OnPointerClick);
            trigger.triggers.Add(entry);
        }

        internal static void OnPagerClick(InspectorTab tab, PointerEventData eventData)
        {
            // 消费点击：分页器文本不是 Button，不消费的话会继续冒泡到标签页按钮导致切换标签
            eventData?.Use();

            if (!CanOpen(tab, out List<LevelEvent> stack))
                return;

            Open(tab, stack);
        }

        /// <summary>是否满足直选条件（开关、单选砖、当前堆叠数 &gt; 1、原版分页器确实在显示）。</summary>
        private static bool CanOpen(InspectorTab tab, out List<LevelEvent> stack)
        {
            stack = null;
            if (tab == null || tab.cycleButtons == null || scnEditor.instance == null)
                return false;
            if (!Main.IsPagerListEnabled)
                return false;
            // 原版分页器没显示（非单选砖 / solo 类型 / 设置类型 / 数量 ≤ 1）时不做任何事
            if (!tab.cycleButtons.gameObject.activeInHierarchy)
                return false;
            if (!scnEditor.instance.SelectionIsSingle())
                return false;
            stack = scnEditor.instance.GetSelectedFloorEvents(tab.levelEventType);
            return stack != null && stack.Count > 1;
        }

        // ------------------------------------------------------------------ 打开 / 关闭

        private static void Open(InspectorTab tab, List<LevelEvent> stack)
        {
            if (!EnsureBuilt())
                return;

            openTab = tab;
            openedFrame = Time.frameCount;
            PagerClipboard.InvalidateCache();   // 打开时重读一次原版键位表（用户可能刚改过键位）
            // 折叠状态**不在这里清**：它是会话级的（关窗再开、切换事件后都该保持），
            // 只有编辑器重载（Reset）才清空（§23.3）。
            // 多选批量态**关窗也不丢**（§34.2），这里只是把它的行高亮恢复出来；
            // 事件已失效（被删/换类型）就退出多选，按单选开始。
            RestoreBatchSelection(tab, stack);
            currentEvent = stack.Count > 0 ? stack[Mathf.Clamp(tab.eventIndex, 0, stack.Count - 1)] : null;

            titleText.text = string.Format(L("aee.pager.title"), EventTypeLabel(tab.levelEventType), stack.Count);
            // **先把窗口显示出来，再建行**（§28.1）：行与备注文本如果是在"父层级还没激活"时创建的，
            // 备注那次 `text = …` 会被 TMP 吞掉（网格不重建）—— 表现就是"打开弹窗时备注列全空，
            // 随便点一行触发重建才出现"。而点行重建时弹窗已经是激活状态，所以那条路一直是好的。
            ShowWindow();

            BuildSlots(stack);
            int rowCount = CreateRows(tab);
            if (rowCount <= 0)
            {
                Close();       // 建不出行就恢复成"没打开"的样子（窗口已经显示出来了）
                return;
            }

            ResizeHost(rowCount);
            LayoutList();
            isOpen = true;

            // 批量态还活着：重开时按当前数据重建一次 fake（值/混合标记刷新）并挂回面板。
            // 这是**恢复**不是新选择 ⇒ 不再提示一遍"已选择 N 个事件"（§39 M2）。
            if (selectedIndices.Count >= 2)
                ApplySelectionToPanel(false);
        }

        /// <summary>
        /// 重新取事件列表并重建行。切换当前事件（点行/组名循环）、折叠展开、拖动落点之后都走这里 ——
        /// 事件列表顺序是"打开时的快照"，任何改动都要重取，否则下标会对不上（`tab.eventIndex` 就是列表下标）。
        /// </summary>
        internal static void ReloadRows()
        {
            InspectorTab tab = openTab;
            if (tab == null || !isOpen)
                return;
            if (!CanOpen(tab, out List<LevelEvent> stack))
            {
                Close();
                return;
            }

            titleText.text = string.Format(L("aee.pager.title"), EventTypeLabel(tab.levelEventType), stack.Count);
            BuildSlots(stack);

            // 当前事件按对象重新定位（分组/排序后下标会变）
            currentEvent = ResolveCurrent(stack, tab);
            selectedIndices.RemoveWhere(index => index < 0 || index >= stack.Count);

            int rowCount = CreateRows(tab);
            if (rowCount <= 0)
            {
                Close();
                return;
            }
            ResizeHost(rowCount);
            LayoutList();
        }

        /// <summary>当前显示的事件：优先用记下来的对象，找不到再退回 tab.eventIndex。</summary>
        private static LevelEvent ResolveCurrent(List<LevelEvent> stack, InspectorTab tab)
        {
            if (currentEvent != null && stack.Contains(currentEvent))
                return currentEvent;
            int index = Mathf.Clamp(tab.eventIndex, 0, stack.Count - 1);
            return stack[index];
        }

        internal static void CloseIfOpen()
        {
            // 我们自己切事件/折叠/多选刷新时也会走 ShowPanel／选砖，那几帧不要关窗
            if (Time.frameCount <= suppressCloseUntilFrame)
                return;
            if (isOpen)
            {
                LogAutoClose();
                Close();
            }
        }

        /// <summary>
        /// 面板切换时的自动关窗：**只有切到别的类型**（真正换了标签页/别的事件堆）才关；
        /// 同类型的刷新（我们选事件、多选 fake 刷新、原版重渲染）一律不关（§26.2）。
        /// 批量态**关窗也活着**，所以这里还要按类型判一次：切到别的类型 ⇒ 退出多选（§34.3）。
        /// </summary>
        internal static void CloseIfOpenOnOtherPanel(LevelEventType eventType)
        {
            if (fakeEvent != null && fakeEvent.eventType != eventType)
                ExitMultiSelect("切到其他事件类型 / 面板 tab");
            if (isOpen && openTab != null && openTab.levelEventType == eventType)
                return;
            CloseIfOpen();
        }

        /// <summary>自动关窗时留一行"谁关的"（取调用栈最上面几层），便于定位误关。</summary>
        private static void LogAutoClose()
        {
            if (Main.Logger == null)
                return;
            try
            {
                var trace = new System.Diagnostics.StackTrace();
                var frames = new List<string>(3);
                for (int i = 1; i < trace.FrameCount && frames.Count < 3; i++)
                {
                    System.Reflection.MethodBase method = trace.GetFrame(i)?.GetMethod();
                    if (method != null)
                        frames.Add((method.DeclaringType != null ? method.DeclaringType.Name : "?") + "." + method.Name);
                }
                Main.Logger.Log("关闭直选列表（自动）: " + string.Join(" <- ", frames));
            }
            catch { }
        }

        /// <summary>
        /// 声明"接下来这次面板切换/选砖变化是我们自己触发的"：压住当前帧与之后 2 帧的自动关窗，
        /// 覆盖"面板刷新延后一帧"的情况。
        /// </summary>
        internal static void SuppressAutoClose()
        {
            suppressCloseUntilFrame = Time.frameCount + 2;
        }

        /// <summary>拖动行期间关掉列表滚动（免得一边拖一边滚），松手/行被回收时恢复。</summary>
        internal static void SetScrollEnabled(bool enabled)
        {
            if (scrollRect != null)
                scrollRect.enabled = enabled;
        }

        /// <summary>
        /// 编辑器里"选中的砖"变了（`scnEditor.OnSelectedFloorChange` 的补丁入口）：
        /// **换砖 ⇒ 退出多选**（§34.3 的退出条件之一），然后照旧关窗。
        /// 我们自己触发的选砖（点行、粘贴收尾）在抑制窗口内，不动多选状态。
        /// </summary>
        internal static void OnSelectedFloorChanged()
        {
            if (Time.frameCount > suppressCloseUntilFrame)
                ExitMultiSelect("换砖");
            CloseIfOpen();
        }

        /// <summary>
        /// OK 按钮：关窗但**保留**多选批量态（§34.1/§34.2）。弹窗打开时是模态的、窗口外点不了，
        /// 所以批量编辑恰恰要在关窗后用 ⇒ 关窗只收 UI，不动 fake 与面板。
        /// </summary>
        private static void CloseKeepingBatch()
        {
            Close(true);
        }

        private static void Close()
        {
            Close(false);
        }

        private static void Close(bool keepBatch)
        {
            // 非 OK 的关窗（自动关/建不出行/切换面板）都算"退出多选"：fake 与选中集一起清
            if (!keepBatch)
            {
                ExitMultiSelect("关闭弹窗");
            }
            else
            {
                FlushPendingToastIfSettled();
                int kept = BatchCount();
                LogMultiEdit(kept >= 2
                    ? "关窗（保留多选：" + kept + " 个事件，面板继续显示批量视图）"
                    : "关窗（当前没有多选批量态，面板是单事件视图）");
            }

            isOpen = false;
            openTab = null;
            currentEvent = null;
            selectedIndices.Clear();
            selectionAnchor = -1;
            // collapsedGroups 同理保留：下次打开还是上次折叠的样子（§23.3）
            ClearDropFeedback();
            try
            {
                // 隐藏弹窗窗口（与打开时同一套 API）：关掉遮罩、并让 popupWindow 滑走
                scnEditor.instance?.ShowPopup(false, (scnEditor.PopupType)233, false);
                // 弹窗自己挂在 Canvas 下，原版的隐藏路径管不到，这里主动收起（下次 Open 会重新启用）
                if (popupRoot != null)
                    popupRoot.SetActive(false);
            }
            catch (Exception e)
            {
                Main.Logger?.Log("关闭分页器列表失败: " + e.Message);
            }
        }

        /// <summary>
        /// <summary>
        /// 重开弹窗时把批量覆盖的事件映射回行下标（§34.2）：同一标签页、事件还在当前砖的这堆里才算数。
        /// 死掉的（被删/被剪切/换类型/撤销后换对象）剔除；不足 2 个 ⇒ 退出多选、面板回单事件。
        /// 批量集合直接取自 `fakeEvent.realEvents`（fake 关窗不再丢），所以这里只是"恢复高亮"。
        /// </summary>
        private static void RestoreBatchSelection(InspectorTab tab, List<LevelEvent> stack)
        {
            selectedIndices.Clear();
            selectionAnchor = -1;
            LevelEvent batch = fakeEvent;
            if (batch == null || batch.realEvents == null || batch.realEvents.Count < 2)
                return;
            if (fakeTab != null && tab != null && fakeTab != tab)
                return;                            // 不是这个标签页的批量态：这次打开按单选走

            int dropped = 0;
            for (int i = 0; i < batch.realEvents.Count; i++)
            {
                LevelEvent e = batch.realEvents[i];
                if (e == null || stack == null || stack.IndexOf(e) < 0)
                {
                    dropped++;                     // 被删/被剪切/不在这个类型的事件堆里
                    continue;
                }
                selectedIndices.Add(stack.IndexOf(e));
            }

            if (selectedIndices.Count < 2)
            {
                int left = selectedIndices.Count;   // 先存数量再清：ExitMultiSelect 会把选中集清空（§39 L1）
                selectedIndices.Clear();
                ExitMultiSelect("批量里的选中事件只剩 " + left + " 个可用（剔除 " + dropped + " 个失效的）");
                return;
            }
            LogMultiEdit("恢复多选高亮：" + selectedIndices.Count + " 个事件（剔除 " + dropped + " 个失效的）");
        }

        /// <summary>批量覆盖的事件数（0 表示没有批量态）。</summary>
        private static int BatchCount()
        {
            LevelEvent batch = fakeEvent;
            return batch != null && batch.realEvents != null ? batch.realEvents.Count : 0;
        }

        /// <summary>这个事件在不在当前批量覆盖范围里（箭头导航的退出判定用）。</summary>
        private static bool IsInBatch(LevelEvent e)
        {
            LevelEvent batch = fakeEvent;
            if (batch == null || e == null || batch.realEvents == null)
                return false;
            for (int i = 0; i < batch.realEvents.Count; i++)
                if (ReferenceEquals(batch.realEvents[i], e))
                    return true;
            return false;
        }

        /// <summary>
        /// 退出多选（§34.3）：清 fake、清选中集、面板回到该单事件、摘掉 (Mixed) 标记，并提示"已退出多选"。
        /// 触发点：弹窗内单选其他行 / 弹窗外换砖 / 切到别的事件类型或面板 tab / 分页器箭头切到范围外 /
        /// 关卡重载 / 剪切粘贴改动事件 / 撤销重做后批量事件失效。
        /// </summary>
        internal static void ExitMultiSelect(string reason)
        {
            bool had = fakeEvent != null && BatchCount() >= 2;
            selectedIndices.Clear();
            selectionAnchor = -1;
            pendingLivenessCheck = false;
            ClearFakeEvent();                      // 内部会把面板指回真实事件 + 摘掉 (Mixed)
            if (had)
            {
                pendingToastKey = null;            // 退出提示直接弹，别等去抖
                lastToastCount = -1;               // 下次重新选到同样数量也要提示
                LogMultiEdit("退出多选（" + reason + "）");
                if (Time.frameCount == suppressExitToastFrame)
                    LogMultiEdit("退出多选提示让位给同帧的操作提示（不然会把『已粘贴 N』顶掉）");
                else
                    PagerClipboard.Toast("aee.notify.multiExit", 0);
            }
        }

        /// <summary>
        /// 声明"这一帧已经弹过操作提示了"（粘贴：提示由 `PasteEventsNotifyPatch` 在原版 action 收尾时弹出，
        /// 紧随其后的退出多选同帧又弹一条"已退出多选"，游戏左上角只有一条位置 ⇒ 后弹的把先弹的顶掉，§39 M1）。
        /// 记的是帧号，所以过了这一帧自然失效，不会把之后的退出提示一起吞掉。
        /// </summary>
        internal static void SuppressExitToastThisFrame()
        {
            suppressExitToastFrame = Time.frameCount;
        }

        /// <summary>编辑器（重新）加载：弹窗对象随场景销毁，清掉引用。</summary>
        internal static void Reset()
        {
            popupRoot = null;
            titleText = null;
            closeButton = null;
            scrollRect = null;
            listContent = null;
            rowTemplate = null;
            uiCanvas = null;
            frameTopCap = null;
            frameTopCapImage = null;
            frameBody = null;
            frameFillImage = null;
            // 贴图中心那块 1×1 sprite 是运行时 Sprite.Create 出来的原生对象，不随场景销毁
            if (frameFillSprite != null)
            {
                UnityEngine.Object.Destroy(frameFillSprite);
                frameFillSprite = null;
            }
            dropLine = null;
            cursorMark = null;
            dropObjectsResolved = false;
            dropFeedbackLogged = false;
            // 备注浮层是弹窗的子对象，随场景一起销毁，这里只清引用
            noteTooltip = null;
            noteTooltipText = null;
            // 原版键位表里的 action 实例随旧编辑器一起作废，缓存也丢掉（下次打开弹窗重读）
            PagerClipboard.InvalidateCache();
            slots.Clear();
            groupKeyByIndex.Clear();
            currentStack = null;
            currentEvent = null;
            selectedIndices.Clear();
            selectionAnchor = -1;
            collapsedGroups.Clear();
            // 关卡重载 ⇒ 批量态一起清掉（§34.3）。走 ClearFakeEvent 而不是裸赋值：
            // 只有它会摘掉面板行标签上残留的 (Mixed)（§39 L2）。
            ClearFakeEvent();
            pendingToastKey = null;
            pendingToastCount = 0;
            lastToastCount = -1;
            lastToastTime = -10f;
            lastSelectionChangeTime = -10f;
            suppressExitToastFrame = -1;
            pendingLivenessCheck = false;
            rowHeight = FallbackRowHeight;
            rows.Clear();
            openTab = null;
            isOpen = false;
            openedFrame = -1;
            currentRowOrder = -1;
        }

        // ------------------------------------------------------------------ 列表内容

        /// <returns>建出的行数（0 表示构造失败）。</returns>
        private static int CreateRows(InspectorTab tab)
        {
            // 行被销毁时鼠标可能正停在其中一行上（收不到 OnPointerExit），所以重建前先收浮层
            HideNoteTooltip();
            for (int i = 0; i < rows.Count; i++)
                if (rows[i] != null)
                    UnityEngine.Object.Destroy(rows[i]);
            rows.Clear();

            if (rowTemplate == null || listContent == null || slots.Count == 0)
                return 0;

            bool anyTagged = false;
            for (int i = 0; i < slots.Count; i++)
                if (!slots[i].IsHeader && slots[i].Tag.Length > 0)
                {
                    anyTagged = true;
                    break;
                }

            string typeLabel = EventTypeLabel(tab.levelEventType);
            currentRowOrder = -1;

            for (int i = 0; i < slots.Count; i++)
            {
                Slot slot = slots[i];
                if (slot.IsHeader)
                {
                    GameObject header = CreateHeaderRow(slot);
                    if (header == null)
                        break;
                    rows.Add(header);
                    continue;
                }

                bool isCurrent = slot.OriginalIndex == tab.eventIndex;
                bool isMulti = selectedIndices.Contains(slot.OriginalIndex);
                if (isCurrent)
                    currentRowOrder = i;

                GameObject row = CreatePagerRow(isCurrent, isMulti, slot.Note, out Button clickButton);
                if (row == null)
                    break;
                row.name = "aee_pagerRow" + slot.OriginalIndex;

                TMP_Text label = row.GetComponentInChildren<TMP_Text>(true);
                if (label != null)
                    label.text = BuildRowLabel(slot, typeLabel, anyTagged, isCurrent);

                // 事件分发要点：Unity 的 ExecuteEvents 只会把点击/拖拽交给"射线命中的那个对象"上的处理器。
                // 射线命中的是我们铺的透明点击层（它自带 Button），所以 PagerRowClick / PagerRowDragTarget
                // 必须挂在这一层上；挂到父行上会被 Button 拦掉、永远收不到。（同一对象上的多个
                // IPointerClickHandler 都会被调用，所以 Button 的悬停配色不受影响。）
                GameObject target = clickButton != null ? clickButton.gameObject : row;
                PagerRowClick click = target.GetComponent<PagerRowClick>();
                if (click == null)
                    click = target.AddComponent<PagerRowClick>();
                click.Index = slot.OriginalIndex;
                if (target.GetComponent<PagerRowDragTarget>() == null)
                    target.AddComponent<PagerRowDragTarget>();

                // 原版按钮只留悬停配色
                if (clickButton != null)
                    clickButton.onClick.RemoveAllListeners();

                rows.Add(row);
            }
            return rows.Count;
        }

        /// <summary>分组头行：只留名称文本，额外铺两个点击区（箭头 = 折叠，组名 = 跳到组内下一个事件）。</summary>
        private static GameObject CreateHeaderRow(Slot slot)
        {
            GameObject row = UnityEngine.Object.Instantiate(rowTemplate, listContent, false);
            row.transform.localScale = Vector3.one;
            row.SetActive(true);
            row.name = "aee_pagerGroup" + slot.Key;

            CanvasGroup group = row.GetComponent<CanvasGroup>();
            if (group != null)
            {
                group.alpha = 1f;
                group.interactable = true;
                group.blocksRaycasts = true;
            }

            ListItem item = row.GetComponent<ListItem>();
            TMP_Text label = item != null ? item.Get<TMP_Text>("itemName") : null;
            Transform background = item != null ? item.selectionBackground : null;
            if (label == null)
                label = row.GetComponentInChildren<TMP_Text>(true);

            if (item != null)
                item.SetSelectedState(false);
            StripPagerRow(row, label, background);
            if (background != null)
                background.gameObject.SetActive(false);

            if (label != null)
            {
                for (Transform t = label.transform; t != null && t != row.transform; t = t.parent)
                    if (!t.gameObject.activeSelf)
                        t.gameObject.SetActive(true);
                label.raycastTarget = false;
                label.text = BuildHeaderLabel(slot);
            }

            LayoutPagerRow(row);

            PagerGroupHeader marker = row.AddComponent<PagerGroupHeader>();
            marker.Key = slot.Key;
            // 箭头区必须盖住**看得见的那个 ▾/▸**：弹窗组头是把标记直接写进标签文本的
            // （`BuildHeaderLabel` 前缀），而标签自带左内缩（prefab 给类型图标留的位置）
            // ⇒ 固定 22 单位宽的老箭头区根本盖不到字形，点箭头其实落进"组名区"，
            // 于是变成"全选该组"而不是折叠（§23.1）。
            float arrowZone = ResolveHeaderArrowZone(row, label);
            AddHeaderHitArea(row, marker, PagerHeaderArea.Arrow, 0f, arrowZone);
            AddHeaderHitArea(row, marker, PagerHeaderArea.Name, arrowZone, 0f);
            return row;
        }

        /// <summary>
        /// 组头箭头点击区的右边界（行内坐标）= 标签左内缩 + 一个字形宽 + 余量。
        /// 左内缩用世界坐标换算（锚点怎么设都不会算错），行矩形还没量出来时退回 `offsetMin.x`。
        /// 字形宽按字号估，抗住不同语言的字体大小；上下限保证"老行为不变 + 组名区仍占大部分行宽"。
        /// </summary>
        private static float ResolveHeaderArrowZone(GameObject row, TMP_Text label)
        {
            float labelLeft = 0f;
            float fontSize = 24f;
            if (label != null)
            {
                RectTransform rect = label.rectTransform;
                RectTransform rowRect = row != null ? row.GetComponent<RectTransform>() : null;
                if (rect != null)
                {
                    if (rowRect != null && rowRect.rect.width > 1f)
                    {
                        Vector3 world = rect.TransformPoint(new Vector3(rect.rect.xMin, 0f, 0f));
                        labelLeft = rowRect.InverseTransformPoint(world).x - rowRect.rect.xMin;
                    }
                    else
                    {
                        labelLeft = rect.offsetMin.x;
                    }
                }
                if (label.fontSize > 1f)
                    fontSize = label.fontSize;
            }
            if (labelLeft < 0f)
                labelLeft = 0f;
            // 标记占"字形 + 一个空格"≈ 1em，边界就压在名字第一个字之前
            return Mathf.Clamp(labelLeft + fontSize, HeaderArrowWidth, HeaderArrowZoneMax);
        }

        private static void AddHeaderHitArea(GameObject row, PagerGroupHeader owner, PagerHeaderArea area, float leftInset, float rightInset)
        {
            var go = new GameObject("aee_pagerHit" + area, typeof(RectTransform), typeof(Image));
            var rect = (RectTransform)go.transform;
            rect.SetParent(row.transform, false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(leftInset, 0f);
            rect.offsetMax = new Vector2(-rightInset, 0f);

            Image image = go.GetComponent<Image>();
            image.color = new Color(1f, 1f, 1f, 0f);
            image.raycastTarget = true;

            PagerHeaderClick click = go.AddComponent<PagerHeaderClick>();
            click.Owner = owner;
            click.Area = area;
        }

        private static void LayoutPagerRow(GameObject row)
        {
            RectTransform rect = row.GetComponent<RectTransform>();
            if (rect != null)
            {
                rect.anchorMin = new Vector2(0f, 1f);
                rect.anchorMax = new Vector2(1f, 1f);
                rect.pivot = new Vector2(0.5f, 1f);
                rect.sizeDelta = new Vector2(0f, rowHeight);
            }
            LayoutElement element = row.GetComponent<LayoutElement>();
            if (element == null)
                element = row.AddComponent<LayoutElement>();
            element.minHeight = rowHeight;
            element.preferredHeight = rowHeight;
            element.flexibleHeight = 0f;
        }

        /// <summary>
        /// 造一行：克隆装饰列表的行 prefab，清掉原版行为与无关子对象，
        /// 只留名称文本（+ 当前行的原版 selectionBackground），整行铺一层透明按钮负责点击与悬停配色；
        /// 有备注时右侧再挂一段右对齐的截断文本（§21.2），整行悬停时弹完整备注。
        /// </summary>
        private static GameObject CreatePagerRow(bool isCurrent, bool isMultiSelected, string note, out Button clickButton)
        {
            clickButton = null;
            GameObject row = UnityEngine.Object.Instantiate(rowTemplate, listContent, false);
            row.transform.localScale = Vector3.one;
            row.SetActive(true);

            CanvasGroup group = row.GetComponent<CanvasGroup>();
            if (group != null)
            {
                group.alpha = 1f;
                group.interactable = true;
                group.blocksRaycasts = true;
            }

            Transform background = null;
            ListItem item = row.GetComponent<ListItem>();
            TMP_Text label = item != null ? item.Get<TMP_Text>("itemName") : null;
            if (item != null && item.selectionBackground != null)
                background = item.selectionBackground;
            if (label == null)
                label = row.GetComponentInChildren<TMP_Text>(true);

            // 当前行与多选中的行走同一套选中样式：White 底高亮 + 黑字（公开的 SetSelectedState，
            // 内部即 ShowSelectionBackground，装饰列表选中行用的是同一套）。两者靠标签文字区分：
            // 当前行前面带 aee.pager.current 前缀。
            // 之前多选行用的是原版拖拽高亮 ShowHighlight（selectionHighlight），那是个**白色圆角线框**，
            // 套在行上像"多画了一个框"，与弹窗外框混在一起（见 §19.4），所以不再使用。
            bool highlighted = isCurrent || isMultiSelected;
            if (item != null)
                item.SetSelectedState(highlighted);

            StripPagerRow(row, label, background);

            if (background != null)
                background.gameObject.SetActive(highlighted);

            if (label != null)
            {
                for (Transform t = label.transform; t != null && t != row.transform; t = t.parent)
                    if (!t.gameObject.activeSelf)
                        t.gameObject.SetActive(true);
                label.raycastTarget = false;   // 点击统一交给覆盖层，避免文本挡住行边缘
            }

            // 备注：必须在 StripPagerRow 之后建（它会把不在 keep 名单里的子对象全关掉）
            TMP_Text noteText = CreateRowNote(row, label, note, highlighted);

            LayoutPagerRow(row);

            // 整行点击区（透明覆盖层）
            var hit = new GameObject("aee_pagerRowHit", typeof(RectTransform), typeof(Image), typeof(Button));
            var hitRect = (RectTransform)hit.transform;
            hitRect.SetParent(row.transform, false);
            hitRect.anchorMin = Vector2.zero;
            hitRect.anchorMax = Vector2.one;
            hitRect.offsetMin = Vector2.zero;
            hitRect.offsetMax = Vector2.zero;

            Image hitImage = hit.GetComponent<Image>();
            hitImage.color = new Color(1f, 1f, 1f, 0f);
            hitImage.raycastTarget = true;

            Button hitButton = hit.GetComponent<Button>();
            clickButton = hitButton;
            hitButton.targetGraphic = hitImage;
            hitButton.transition = Selectable.Transition.ColorTint;
            ColorBlock colors = hitButton.colors;
            colors.normalColor = new Color(1f, 1f, 1f, 0f);
            colors.highlightedColor = new Color(1f, 1f, 1f, 0.08f);
            colors.pressedColor = new Color(1f, 1f, 1f, 0.16f);
            colors.selectedColor = new Color(1f, 1f, 1f, 0f);
            colors.colorMultiplier = 1f;
            hitButton.colors = colors;

            // 悬停弹完整备注（挂在整行的点击层上：它才是射线命中的目标；
            // 触发范围限制在备注列内、浮层位置也锚在备注列上 —— §28.2）
            PagerRowHover hover = hit.GetComponent<PagerRowHover>();
            if (hover == null)
                hover = hit.AddComponent<PagerRowHover>();
            hover.Note = note;
            hover.NoteArea = noteText != null ? noteText.rectTransform : null;

            return row;
        }

        /// <summary>
        /// 事件行右侧的备注（§21.2）：单行截断、右对齐，字体沿用行自身的标签。
        /// 当前行/多选行是白底黑字，备注颜色也要跟着翻，否则白字压在白底上等于看不见。
        /// 标签在"拉满整行"的锚点下才收窄给它让位（不动 prefab 原本的偏移）。
        /// </summary>
        private static TMP_Text CreateRowNote(GameObject row, TMP_Text label, string note, bool highlighted)
        {
            if (row == null || label == null)
                return null;

            RectTransform labelRect = label.rectTransform;
            if (labelRect != null && labelRect.anchorMax.x > LabelAnchorMaxX)
            {
                labelRect.anchorMax = new Vector2(LabelAnchorMaxX, labelRect.anchorMax.y);
                labelRect.offsetMax = new Vector2(-NoteGap, labelRect.offsetMax.y);
            }

            var go = new GameObject("aee_pagerNote", typeof(RectTransform), typeof(TextMeshProUGUI));
            var rect = (RectTransform)go.transform;
            rect.SetParent(row.transform, false);
            rect.anchorMin = new Vector2(NoteAnchorMinX, 0f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(1f, 0.5f);
            rect.offsetMin = new Vector2(NoteGap, 0f);
            rect.offsetMax = new Vector2(-NoteRightInset, 0f);

            TextMeshProUGUI text = go.GetComponent<TextMeshProUGUI>();
            text.font = label.font;
            text.fontSharedMaterial = label.fontSharedMaterial;
            text.fontSize = label.fontSize;
            text.color = highlighted ? NoteColorSelected : NoteColor;
            text.alignment = TextAlignmentOptions.Right;
            text.enableWordWrapping = false;
            text.overflowMode = TextOverflowModes.Ellipsis;
            text.raycastTarget = false;
            // 备注是用户输入：关掉富文本，免得里面出现 <...> 时被 TMP 当标签解析
            text.richText = false;
            text.text = Features.Notes.EventNote.IsEmpty(note) ? "" : note;
            return text;
        }

        // ------------------------------------------------------------------ 备注悬停浮层（§21.3 / §28.2）

        /// <summary>指针是否落在这个 UI 矩形里（备注列的悬停判定）。</summary>
        internal static bool PointerInRect(RectTransform rect, PointerEventData eventData)
        {
            if (rect == null || eventData == null)
                return false;
            try
            {
                if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(rect, eventData.position, ResolveCamera(), out Vector2 local))
                    return false;
                return rect.rect.Contains(local);
            }
            catch { }
            return false;
        }

        /// <summary>
        /// 弹出完整备注（§28.2）：**位置固定、不跟鼠标**。
        /// 右沿对齐被悬停行的备注列右边界、宽度取备注列宽度 ⇒ 水平方向正好盖在备注列上（不会横跨到左侧标签列），
        /// 垂直方向居中于那一行，再整体夹进弹窗矩形里。于是长短备注的浮层总是出现在同一个地方
        /// （鼠标在备注列里怎么动都不动），眼睛不用追着指针跑。
        /// </summary>
        internal static void ShowNoteTooltip(string note, RectTransform noteArea)
        {
            if (popupRoot == null || noteArea == null || Features.Notes.EventNote.IsEmpty(note))
                return;
            EnsureNoteTooltip();
            if (noteTooltip == null || noteTooltipText == null)
                return;

            noteTooltipText.text = note;
            noteTooltip.gameObject.SetActive(true);
            noteTooltip.SetAsLastSibling();      // 浮在最上层（不吃射线，所以压住行也没关系）

            var hostRect = (RectTransform)popupRoot.transform;
            Rect host = hostRect.rect;           // 与 localPosition 同一坐标系（以弹窗 pivot 为原点）
            Rect area = noteArea.rect;
            // 宽度：跟备注列一样宽（弹窗太窄时兜底），高度仍由 ContentSizeFitter 按内容算
            float width = Mathf.Clamp(area.width, NoteTipMinWidth, Mathf.Min(NoteTipWidth, Mathf.Max(NoteTipMinWidth, host.width - 2f * NoteTipMargin)));
            noteTooltip.sizeDelta = new Vector2(width, noteTooltip.sizeDelta.y);
            LayoutRebuilder.ForceRebuildLayoutImmediate(noteTooltip);

            Vector2 size = noteTooltip.rect.size;
            // 备注列右边界 / 该行垂直中心 → 弹窗本地坐标（pivot = (1, 0.5)，所以这个点就是浮层的右中角）
            Vector2 local = hostRect.InverseTransformPoint(noteArea.TransformPoint(new Vector2(area.xMax, area.center.y)));
            float minX = host.xMin + NoteTipMargin + size.x;
            float maxX = host.xMax - NoteTipMargin;
            float x = minX > maxX ? host.xMin + NoteTipMargin : Mathf.Clamp(local.x, minX, maxX);
            float y = Mathf.Clamp(local.y, host.yMin + NoteTipMargin + size.y * 0.5f, host.yMax - NoteTipMargin - size.y * 0.5f);
            noteTooltip.localPosition = new Vector3(x, y, 0f);
        }

        /// <summary>收起浮层：移出备注列、行重建、关窗、开始拖动、列表滚动时都要调。</summary>
        internal static void HideNoteTooltip()
        {
            if (noteTooltip != null && noteTooltip.gameObject.activeSelf)
                noteTooltip.gameObject.SetActive(false);
        }

        /// <summary>列表滚动回调（ScrollRect.onValueChanged）：把浮层收掉（§28.2）。</summary>
        private static void OnListScrolled(Vector2 position)
        {
            HideNoteTooltip();
        }

        /// <summary>
        /// 备注浮层：自建 uGUI（深色底 + 白字，风格同弹窗内其它自建件），高度按内容自适应
        /// （VerticalLayoutGroup + ContentSizeFitter），**所有 Graphic 的 raycastTarget 都是 false**
        /// —— 浮层压在行上也不会把行的悬停/点击抢走（需求里的"不抢射线"）。
        /// </summary>
        private static void EnsureNoteTooltip()
        {
            if (noteTooltip != null || popupRoot == null)
                return;

            var go = new GameObject("aee_pagerNoteTip", typeof(RectTransform), typeof(Image));
            noteTooltip = (RectTransform)go.transform;
            noteTooltip.SetParent(popupRoot.transform, false);
            noteTooltip.anchorMin = new Vector2(0.5f, 0.5f);
            noteTooltip.anchorMax = new Vector2(0.5f, 0.5f);
            noteTooltip.pivot = NoteTipPivot;                    // 右中角挂在"备注列右边界 × 该行中心"上（§28.2）
            noteTooltip.sizeDelta = new Vector2(NoteTipWidth, 1f);

            Image background = go.GetComponent<Image>();
            background.sprite = null;
            background.color = new Color(0.06f, 0.06f, 0.06f, 0.95f);
            background.raycastTarget = false;

            var layout = go.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset((int)NoteTipPadding, (int)NoteTipPadding, (int)NoteTipPadding, (int)NoteTipPadding);
            // childAlignment 用默认值（UpperLeft）——TextAnchor 在 UnityEngine.TextRenderingModule 里，
            // 本工程没引用那个程序集，所以这里不显式设置
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            var fitter = go.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var textGO = new GameObject("text", typeof(RectTransform), typeof(TextMeshProUGUI));
            ((RectTransform)textGO.transform).SetParent(noteTooltip, false);
            noteTooltipText = textGO.GetComponent<TextMeshProUGUI>();
            // 字体沿用弹窗标题（标题在 EnsureBuilt 时就从宿主文本抄好了字体/材质）
            if (titleText != null)
            {
                noteTooltipText.font = titleText.font;
                noteTooltipText.fontSharedMaterial = titleText.fontSharedMaterial;
                noteTooltipText.fontSize = Mathf.Max(12f, titleText.fontSize - 4f);
            }
            noteTooltipText.color = Color.white;
            noteTooltipText.alignment = TextAlignmentOptions.TopLeft;
            noteTooltipText.enableWordWrapping = true;   // 长备注在这里换行
            noteTooltipText.overflowMode = TextOverflowModes.Overflow;
            noteTooltipText.raycastTarget = false;
            noteTooltipText.richText = false;                              // 用户输入按字面显示

            noteTooltip.gameObject.SetActive(false);
            noteTooltip.SetAsLastSibling();
        }

        /// <summary>去掉行的一切交互与无关视觉：只留名称文本与（可选的）选中背景。</summary>
        private static void StripPagerRow(GameObject row, TMP_Text label, Transform background)
        {
            var keep = new HashSet<Transform>();
            if (label != null)
                for (Transform t = label.transform; t != null && t != row.transform; t = t.parent)
                    keep.Add(t);
            if (background != null)
                keep.Add(background);

            var descendants = new List<Transform>();
            CollectDescendants(row.transform, descendants);
            foreach (Transform child in descendants)
            {
                if (child != null && !keep.Contains(child))
                    child.gameObject.SetActive(false);
            }

            foreach (AdofaiEventTrigger trigger in row.GetComponentsInChildren<AdofaiEventTrigger>(true))
                UnityEngine.Object.Destroy(trigger);
            foreach (Button button in row.GetComponentsInChildren<Button>(true))
                UnityEngine.Object.Destroy(button);

            Image own = row.GetComponent<Image>();
            if (own != null)
                own.raycastTarget = false;
            if (label != null)
                label.raycastTarget = false;
        }

        private static void CollectDescendants(Transform root, List<Transform> result)
        {
            for (int i = 0; i < root.childCount; i++)
            {
                Transform child = root.GetChild(i);
                result.Add(child);
                CollectDescendants(child, result);
            }
        }

        /// <summary>当前显示的事件列表快照（= GetSelectedFloorEvents）；事件行下标就是它的下标。</summary>
        private static List<LevelEvent> currentStack;

        /// <summary>分组桶（内部用，不进槽位表）。</summary>
        private sealed class Bucket
        {
            internal string Key;
            internal string Label;
            internal bool IsCustom;
            internal bool KeepWhenEmpty;
            internal readonly List<int> Members = new List<int>();
        }

        /// <summary>分组键（与装饰分组同一套字符串格式，落点语义可以直接复用 TryResolveAssignment）。</summary>
        private static string PagerGroupKey(int customIndex) => "custom:" + customIndex;

        /// <summary>
        /// 把事件列表排成"分组头 + 事件行"的槽位表。分组规则与装饰分组同源（§14.1）：
        ///   ① 手动归属优先（事件 data 里的 `aeeGroup`，键名与装饰完全一致）；
        ///   ② 自定义分组里 tag 精确匹配（按定义顺序先命中先归）；
        ///   ③ 兜底组 = **仅自定义模式**下"第一个 tag 留空的自定义分组"；
        ///   ④ 其余按事件 tag（tag ?? eventTag）分组，没有 tag 的进「未分组」。
        /// 与装饰列表的区别：这里的事件都来自同一块砖、同一类型，所以没有"按类型"这一层；
        /// 而且只有**一个**可见分组时不显示组头（与旧的"按 tag 排序"观感一致）。见 §16.2。
        /// </summary>
        private static void BuildSlots(List<LevelEvent> stack)
        {
            slots.Clear();
            groupKeyByIndex.Clear();
            currentStack = stack;
            if (stack == null || stack.Count == 0)
                return;

            List<(string Name, string Tag)> customGroups = DecoGroupState.ReadCustomGroups(DecoGroupState.GroupSet.Event);
            bool customOnly = Main.AutoGroupMode == AutoGroupMode.Custom;
            int fallback = DecoGroupState.FirstFallbackGroupIndex(customGroups);

            var buckets = new List<Bucket>();
            var customBuckets = new List<Bucket>(customGroups.Count);
            for (int i = 0; i < customGroups.Count; i++)
            {
                if (customGroups[i].Tag.Length == 0 && string.IsNullOrEmpty(customGroups[i].Name))
                {
                    customBuckets.Add(null);
                    continue;
                }
                var bucket = new Bucket
                {
                    Key = PagerGroupKey(i),
                    Label = string.IsNullOrEmpty(customGroups[i].Name) ? customGroups[i].Tag : customGroups[i].Name,
                    IsCustom = true,
                    KeepWhenEmpty = true
                };
                customBuckets.Add(bucket);
                buckets.Add(bucket);
            }

            var tagBuckets = new List<Bucket>();
            var tagByKey = new Dictionary<string, Bucket>(StringComparer.Ordinal);
            Bucket untagged = null;

            for (int i = 0; i < stack.Count; i++)
            {
                LevelEvent evt = stack[i];
                if (evt == null)
                    continue;

                // ① 手动归属优先（兜底下标只在自定义模式下排除，见 §14.1.1）
                int manual = DecoGroupState.GetManualGroup(evt, DecoGroupState.GroupSet.Event);
                int manualExcluded = customOnly ? fallback : -1;
                if (manual >= 0 && manual < customGroups.Count && manual != manualExcluded
                    && customGroups[manual].Tag.Length == 0 && customBuckets[manual] != null)
                {
                    customBuckets[manual].Members.Add(i);
                    continue;
                }

                string tag = GetTag(evt, "tag");
                string eventTag = tag.Length == 0 ? GetTag(evt, "eventTag") : "";
                string key = tag.Length > 0 ? tag : eventTag;

                // ② 自定义分组的 tag 精确匹配
                int matched = -1;
                if (key.Length > 0)
                {
                    for (int g = 0; g < customGroups.Count; g++)
                    {
                        if (customGroups[g].Tag.Length > 0 && string.Equals(customGroups[g].Tag, key, StringComparison.Ordinal))
                        {
                            matched = g;
                            break;
                        }
                    }
                }
                if (matched >= 0)
                {
                    customBuckets[matched].Members.Add(i);
                    continue;
                }

                // ③ 兜底组（仅自定义模式）
                if (customOnly && fallback >= 0 && customBuckets[fallback] != null)
                {
                    customBuckets[fallback].Members.Add(i);
                    continue;
                }

                // ④ 按 tag 分组 / 未分组
                if (key.Length == 0)
                {
                    untagged = untagged ?? new Bucket { Key = "tag:", Label = L("aee.group.untagged") };
                    untagged.Members.Add(i);
                }
                else
                {
                    if (!tagByKey.TryGetValue(key, out Bucket bucket))
                    {
                        bucket = new Bucket { Key = "tag:" + key, Label = key };
                        tagByKey[key] = bucket;
                        tagBuckets.Add(bucket);
                    }
                    bucket.Members.Add(i);
                }
            }

            tagBuckets.Sort((a, b) => string.CompareOrdinal(a.Label, b.Label));
            buckets.AddRange(tagBuckets);
            if (untagged != null)
                buckets.Add(untagged);

            for (int b = 0; b < buckets.Count; b++)
                for (int m = 0; m < buckets[b].Members.Count; m++)
                    groupKeyByIndex[buckets[b].Members[m]] = buckets[b].Key;

            int visible = 0;
            for (int i = 0; i < buckets.Count; i++)
                if (buckets[i].Members.Count > 0 || buckets[i].KeepWhenEmpty)
                    visible++;
            bool showHeaders = visible > 1;

            for (int b = 0; b < buckets.Count; b++)
            {
                Bucket bucket = buckets[b];
                if (bucket.Members.Count == 0 && !bucket.KeepWhenEmpty)
                    continue;
                if (showHeaders)
                    slots.Add(new Slot { IsHeader = true, Key = bucket.Key, Label = bucket.Label, Count = bucket.Members.Count });
                if (showHeaders && collapsedGroups.Contains(bucket.Key))
                    continue;

                for (int m = 0; m < bucket.Members.Count; m++)
                {
                    int index = bucket.Members[m];
                    LevelEvent evt = stack[index];
                    string rowTag = GetTag(evt, "tag");
                    bool useEventTag = false;
                    if (rowTag.Length == 0)
                    {
                        rowTag = GetTag(evt, "eventTag");
                        useEventTag = rowTag.Length > 0;
                    }
                    slots.Add(new Slot
                    {
                        IsHeader = false,
                        Key = bucket.Key,
                        OriginalIndex = index,
                        Tag = rowTag,
                        UseEventTag = useEventTag,
                        Note = Features.Notes.EventNote.GetNote(evt)
                    });
                }
            }
        }

        private static string BuildHeaderLabel(Slot slot)
        {
            string mark = collapsedGroups.Contains(slot.Key) ? CollapsedMark : ExpandedMark;
            return mark + " " + slot.Label + (Main.ShowGroupCounts ? " (" + slot.Count + ")" : "");
        }

        private static string BuildRowLabel(Slot entry, string typeLabel, bool anyTagged, bool isCurrent)
        {
            string text = (entry.OriginalIndex + 1) + ". " + typeLabel;
            if (entry.Tag.Length > 0)
                text += "  " + L(entry.UseEventTag ? "aee.pager.eventTagPrefix" : "aee.pager.tagPrefix") + entry.Tag;
            else if (anyTagged)
                text += "  " + L("aee.pager.noTag");
            if (isCurrent)
                text = L("aee.pager.current") + " " + text;
            return text;
        }

        private static string GetTag(LevelEvent evt, string key)
        {
            if (evt == null)
                return "";
            try
            {
                if (evt.TryGet<string>(key, out string tag))
                    return tag ?? "";
            }
            catch { }
            return "";
        }

        /// <summary>事件类型的本地化名："editor.&lt;类型名&gt;"（游戏自己的键），回退到注入表里的名字。</summary>
        private static string EventTypeLabel(LevelEventType type)
        {
            try
            {
                string text = RDString.GetWithCheck("editor." + type, out bool exists, null);
                if (exists && !string.IsNullOrEmpty(text))
                    return text;
            }
            catch { }

            if (GCS.levelEventTypeString != null && GCS.levelEventTypeString.TryGetValue(type, out string raw) && !string.IsNullOrEmpty(raw))
                return raw;
            return type.ToString();
        }

        // ------------------------------------------------------------------ 行点击 / 组头点击

        /// <summary>点事件行：普通点 = 切到该事件（弹窗保持打开）；Ctrl = 切换多选；Shift = 区间多选。</summary>
        internal static void OnRowClick(int originalIndex, bool ctrl, bool shift)
        {
            FlushPendingToastIfSettled();      // 有交互了 ⇒ 把上次"待弹"的数量提示冲掉（§35.3）
            // 刚打开的那一帧不响应：发起打开的那次点击不能顺手选中某一行
            if (Time.frameCount == openedFrame || !isOpen)
            {
                // §35.1 兜底：窗口明明显示着（能点到这些行）却标记成"关闭"= 状态不同步 ⇒ 修回来继续处理。
                // 这是"点了没反应、日志什么都没有"的一个可能原因；真发生的话日志会明确写出来。
                if (!isOpen && popupRoot != null && popupRoot.activeInHierarchy)
                {
                    isOpen = true;
                    LogMultiEdit("行点击：isOpen 与窗口状态不同步（窗口是显示的）⇒ 已修正为打开并继续处理");
                }
                else
                {
                    // 这两条早退以前是静默的，"点了没反应"时查不出是谁挡的 ⇒ 记一条（含状态）
                    LogMultiEdit("行点击被忽略：index=" + originalIndex + " ctrl=" + ctrl + " shift=" + shift
                        + " frame=" + Time.frameCount + " openedFrame=" + openedFrame + " isOpen=" + isOpen);
                    return;
                }
            }

            if (shift)
            {
                // shift = **用"锚点→点击行"重新算一次范围**（§26.2，纯逻辑见 ApplyRangeSelection）：
                //  · 先清空再填 ⇒ 既能加选也能**减选**（已选 1-4、锚点 1，shift 点 2 ⇒ 只剩 1-2）；
                //  · **锚点不动** —— 连点几次 shift 都是在同一段上伸缩，Windows 资源管理器那套习惯；
                //  · 锚点丢了（点过组名全选、跨过列表重建…）时兜底出一个，绝不退化成"逐个加选"。
                int anchor = ResolveSelectionAnchor(originalIndex);
                ApplyRangeSelection(anchor, originalIndex);
                LogMultiEdit("行点击（shift）：index=" + originalIndex + " 锚点=" + anchor
                    + " ⇒ 选中 " + selectedIndices.Count + " 个");
                // 先绑面板、再重建行：别在"正在派发点击事件"的行对象上做销毁/重建（§35.2）
                ApplySelectionToPanel();
                RebuildRowsOnly();
                return;
            }

            if (ctrl)
            {
                // 渲染层"当前行"和"选中行"是同一个白底高亮（isCurrent || isMultiSelected），
                // 所以选中集必须把当前事件算进去，否则用户看着两行白底、实际只选中 1 个（§40）。
                // 纯逻辑见 ToggleCtrlSelection（离线 harness 断言）。
                int current = CurrentEventIndexForSelection();
                bool seeded;
                bool added = ToggleCtrlSelection(selectedIndices, originalIndex, current, out seeded);
                selectionAnchor = originalIndex;
                LogMultiEdit("行点击（ctrl）：index=" + originalIndex + (added ? " 加入选中" : " 取消选中")
                    + (seeded ? "（含当前事件 " + current + "）" : "")
                    + " ⇒ 选中 " + selectedIndices.Count + " 个 [" + DumpSelectedIndices() + "]");
                ApplySelectionToPanel();
                RebuildRowsOnly();
                return;
            }

            // 普通点击 = 单选该行 ⇒ 退出多选（§34.3 的退出条件之一），随后切到该事件
            LogMultiEdit("行点击（普通）：index=" + originalIndex + " ⇒ 退出多选并切到该事件");
            ExitMultiSelect("弹窗内单选其他行");
            selectionAnchor = originalIndex;
            SelectEvent(originalIndex);
        }

        /// <summary>诊断用：当前选中下标列表（"0,2,5"）。</summary>
        private static string DumpSelectedIndices()
        {
            var ordered = new List<int>(selectedIndices);
            ordered.Sort();
            return string.Join(",", ordered.ConvertAll(i => i.ToString()).ToArray());
        }

        /// <summary>
        /// shift 范围选择的核心（纯逻辑，离线 harness 直接断言）：**先清空再填 [min,max]**，
        /// 所以同一段范围既能加选也能减选；锚点由调用方给（shift 点击本身不移动锚点）。
        /// </summary>
        internal static void ApplyRangeSelection(int anchor, int clickedIndex)
        {
            selectedIndices.Clear();
            int from = Mathf.Min(anchor, clickedIndex);
            int to = Mathf.Max(anchor, clickedIndex);
            for (int i = from; i <= to; i++)
            {
                if (i >= 0 && currentStack != null && i < currentStack.Count)
                    selectedIndices.Add(i);
            }
        }

        /// <summary>
        /// ctrl 点选的核心（纯逻辑，离线 harness 直接断言，§40）：**选中集为空时先把当前事件播种进去，再切换被点行**。
        /// 渲染层把当前行和选中行画成同一个白底（`isCurrent || isMultiSelected`），不播种就会出现
        /// "两行白底、实际只选中 1 个"—— 数量提示与 (Mixed) 都比观感少一个。
        /// 点当前行自己不播种（那就是"只留 / 只取消它自己"）；已经有选中集时也不播种（不凭空多塞）。
        /// 返回 true = 被点行这次是**加入**选中集。
        /// </summary>
        internal static bool ToggleCtrlSelection(ISet<int> selection, int clickedIndex, int currentIndex, out bool seededCurrent)
        {
            seededCurrent = false;
            if (selection == null)
                return false;

            if (selection.Count == 0 && currentIndex >= 0 && currentIndex != clickedIndex)
            {
                selection.Add(currentIndex);
                seededCurrent = true;
            }

            if (selection.Remove(clickedIndex))
                return false;
            selection.Add(clickedIndex);
            return true;
        }

        /// <summary>
        /// ctrl 播种用的"当前事件"下标：与渲染层 isCurrent 同一口径（`tab.eventIndex`），
        /// 越界或该位置没有事件就返回 -1（= 不播种）。
        /// </summary>
        private static int CurrentEventIndexForSelection()
        {
            InspectorTab tab = openTab;
            if (tab == null || currentStack == null)
                return -1;
            int index = tab.eventIndex;
            if (index < 0 || index >= currentStack.Count)
                return -1;
            return currentStack[index] != null ? index : -1;
        }

        /// <summary>
        /// shift 范围选择的锚点：优先用上次点击留下的；丢了就退回"当前唯一选中项"
        /// （这正是"先点第 1 行、再 shift 点第 4 行"该用的锚点），再退回当前行，最后以这次点击自身为锚。
        /// </summary>
        private static int ResolveSelectionAnchor(int clickedIndex)
        {
            if (currentStack == null)
                return clickedIndex;
            if (selectionAnchor >= 0 && selectionAnchor < currentStack.Count)
                return selectionAnchor;
            if (selectedIndices.Count == 1)
            {
                foreach (int index in selectedIndices)
                    return index;
            }
            if (currentEvent != null)
            {
                int current = currentStack.IndexOf(currentEvent);
                if (current >= 0)
                    return current;
            }
            return clickedIndex;
        }

        /// <summary>
        /// 切换当前显示的事件（**不关窗**）：更新 InspectorTab 与右侧面板、刷新当前行高亮并滚动到该行。
        /// </summary>
        internal static void SelectEvent(int originalIndex)
        {
            InspectorTab tab = openTab;
            if (tab == null || tab.panel == null)
                return;
            if (!CanOpen(tab, out List<LevelEvent> stack))
            {
                Close();
                return;
            }
            if (originalIndex < 0 || originalIndex >= stack.Count)
                return;

            currentEvent = stack[originalIndex];
            tab.eventIndex = originalIndex;

            // 面板切换会触发"ShowPanel 就关窗"的补丁，先声明这次是我们自己触发的
            SuppressAutoClose();
            try
            {
                tab.panel.ShowPanel(tab.levelEventType, originalIndex);
            }
            catch (Exception e)
            {
                Main.Logger?.Log("切换直选事件失败: " + e.Message);
            }

            RebuildRowsOnly();
        }

        /// <summary>弹窗是否开着（给 PagerClipboard 的快捷键处理用）。</summary>
        internal static bool IsPopupOpen => isOpen;

        /// <summary>
        /// 弹窗里"当前选中的事件"（复制/剪切的口径）：有多选集就返回它（按下标升序），
        /// 否则返回当前行那一个事件；都没有就返回空表。
        /// </summary>
        internal static List<LevelEvent> SelectedPopupEvents()
        {
            var result = new List<LevelEvent>();
            if (currentStack == null)
                return result;

            if (selectedIndices.Count > 0)
            {
                var ordered = new List<int>(selectedIndices);
                ordered.Sort();
                for (int i = 0; i < ordered.Count; i++)
                {
                    int index = ordered[i];
                    if (index >= 0 && index < currentStack.Count && currentStack[index] != null)
                        result.Add(currentStack[index]);
                }
            }

            if (result.Count == 0 && currentEvent != null && currentStack.Contains(currentEvent))
                result.Add(currentEvent);
            return result;
        }

        /// <summary>
        /// 弹窗里做完复制/剪切/粘贴之后：多选集与 fake 是否还有效，按操作类型分开处理（§31.2）。
        ///
        /// `followPanel = true`（粘贴）时把当前行跟到原版刚选中的事件上（原版 `selectAfterward` 会切面板）；
        /// `keepBatch = true`（**复制**）时**保留多选集与 fake** —— 复制不改变任何事件，
        /// 清掉的话用户"多选 → 复制 → 想顺手改属性"就会发现批量面板没了、属性也改不动（正是 §31 的实测现象）。
        /// 剪切/粘贴会改动事件（剪切甚至把事件删了），必须清掉重建。
        /// </summary>
        internal static void AfterPopupClipboardChange(bool followPanel, bool keepBatch)
        {
            if (keepBatch)
            {
                // 复制：事件没变 ⇒ 选区（stack 下标）与 fake（realEvents 指向同一批对象）都还有效，
                // 行列高亮与右侧批量面板一起留着；只重排一次列表。
                ReloadRows();
                return;
            }

            // 剪切 / 粘贴改动了事件（剪切甚至把事件删了）⇒ 退出多选（§34.3）；
            // ExitMultiSelect → ClearFakeEvent 会把面板指回"还活着"的真实事件（当前事件被剪掉时按标签页推）
            ExitMultiSelect(followPanel ? "粘贴改动了事件" : "剪切改动了事件");

            if (followPanel)
            {
                LevelEvent panelEvent = PanelSelectedEvent();
                if (panelEvent != null && !panelEvent.isFake && currentStack != null && currentStack.Contains(panelEvent))
                    currentEvent = panelEvent;
            }

            ReloadRows();
        }

        private static LevelEvent PanelSelectedEvent()
        {
            scnEditor editor = scnEditor.instance;
            InspectorPanel panel = editor != null ? editor.levelEventsPanel : null;
            return panel != null ? panel.selectedEvent : null;
        }

        /// <summary>只重建行与高度（不重取事件列表）：多选高亮、折叠展开等不改变数据顺序的场景用它。</summary>
        private static void RebuildRowsOnly()
        {
            InspectorTab tab = openTab;
            if (tab == null || !isOpen)
                return;
            if (currentStack != null && currentEvent != null)
            {
                int index = currentStack.IndexOf(currentEvent);
                if (index >= 0)
                    tab.eventIndex = index;
            }
            int rowCount = CreateRows(tab);
            if (rowCount > 0)
            {
                ResizeHost(rowCount);
                LayoutList();
            }
        }

        /// <summary>
        /// 折叠 / 展开一个分组。**必须走 ReloadRows**（它内部会 `BuildSlots`）：折叠是在槽位表里
        /// 跳过折叠组的成员行的（§16.2），只重建行（`RebuildRowsOnly`）的话槽位表没变，
        /// 组内事件行一行都不会少 —— 观感就是"点了没反应"（§23.2）。
        /// </summary>
        internal static void ToggleGroupCollapsed(string key)
        {
            if (string.IsNullOrEmpty(key))
                return;
            bool expanded = collapsedGroups.Remove(key);      // Remove 成功 = 原来是折叠的 ⇒ 这次是展开
            if (!expanded)
                collapsedGroups.Add(key);
            // §37：组头点击以前完全没有日志，"点了没反应"时是个盲区 ⇒ 记一条
            LogMultiEdit("组头（箭头）点击：key=" + key + " ⇒ " + (expanded ? "展开" : "折叠")
                + "，当前折叠组数 " + collapsedGroups.Count);
            ReloadRows();
        }

        /// <summary>
        /// 点组名：**全选该组事件**并进入多选批量编辑（原版 fake event 机制，见 §18.1）。
        /// 组内只有 1 个事件时退化成普通单选。
        /// </summary>
        internal static void SelectGroupMembers(string key)
        {
            if (string.IsNullOrEmpty(key) || currentStack == null)
                return;

            selectedIndices.Clear();
            for (int i = 0; i < currentStack.Count; i++)
                if (groupKeyByIndex.TryGetValue(i, out string owner) && owner == key)
                    selectedIndices.Add(i);

            if (selectedIndices.Count == 1)
            {
                int only = -1;
                foreach (int index in selectedIndices)
                    only = index;
                // §37：组头（组名）点击以前也没有日志，补一条（含"组里只有 1 个 ⇒ 退化成单选"这条路）
                LogMultiEdit("组头（组名）点击：key=" + key + " 组内 1 个 ⇒ 退化成单选 index=" + only);
                // 组里只有 1 个事件 ⇒ 回到单选：这时如果之前有多选批量态，同样要退出（§36.3）
                bool hadBatch = HasBatch();
                selectedIndices.Clear();
                if (hadBatch)
                    ExitMultiSelect("点组名切到单个事件");
                selectionAnchor = only;
                SelectEvent(only);
                return;
            }

            LogMultiEdit("组头（组名）点击：key=" + key + " 组内 " + selectedIndices.Count + " 个 ⇒ 全选该组并绑定批量面板");
            selectionAnchor = -1;
            RebuildRowsOnly();
            ApplySelectionToPanel();
        }

        // ------------------------------------------------------------------ 多选批量编辑（fake event）

        /// <summary>我们造的多选 fake 事件（面板正在编辑它时非 null）。</summary>
        private static LevelEvent fakeEvent;

        /// <summary>当前 fake 里"各选中事件取值不同"的键（§29.2：显示 (Mixed) 但**仍可输入**）。</summary>
        private static readonly HashSet<string> mixedKeys = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>混合值行标签上追加的标记（`MarkMixedLabels` 写、`ClearMixedLabel` 摘）。</summary>
        private const string MixedSuffix = " <color=#ff6666>(Mixed)</color>";

        /// <summary>这几个属性不参与批量编辑（原版内部字段，成对写入才有意义）。</summary>
        private static readonly string[] MultiEditBlacklist = { "filterProperties", "propertiesTemplate" };

        /// <summary>
        /// 按当前多选状态决定面板显示（§34.2：fake 关窗也不丢，面板持续显示批量视图）：
        ///  · 选中 >=2 ⇒ 造一个 fake 事件（isFake + realEvents）喂给原版右侧面板，值变化由写值钩子回写各真实事件；
        ///  · 选中 <2 ⇒ **只有原本就有批量态（被反选下去）才退出多选**；从 0 开始累积（0→1）绝不清空（§36.1）。
        /// **弹窗关着时不做任何事**（批量态此时仍然有效，不能被误清）。
        /// `announce = false` 用于"重开弹窗恢复高亮"这种**没有用户选择动作**的重放：面板照挂，
        /// 但不再提示"已选择 N 个事件"（§39 M2）。
        /// </summary>
        internal static void ApplySelectionToPanel(bool announce = true)
        {
            InspectorTab tab = openTab;
            if (tab == null || currentStack == null)
                return;
            if (!isOpen)
                return;                            // 弹窗关着 ⇒ 批量态保持原样，别动面板

            var selected = new List<LevelEvent>();
            for (int i = 0; i < currentStack.Count; i++)
                if (selectedIndices.Contains(i) && currentStack[i] != null)
                    selected.Add(currentStack[i]);

            if (selected.Count < 2)
            {
                // 方向很重要：**反选**（已有批量态、被点掉到 <2）才退出；
                // **累积**（0→1，还没到过 2）什么都不做 —— 以前在这里无条件退出，
                // 而 ExitMultiSelect 会清空 selectedIndices ⇒ 下一次 ctrl 点击又从空集开始，
                // 结果"ctrl 永远只能选中 1 个"（§36 的回归）。
                if (ShouldExitMultiSelect(selected.Count, BatchCount()))
                    ExitMultiSelect("反选到不足 2 个");
                return;
            }

            LevelEvent fake = BuildFakeEvent(selected);
            if (fake == null)
                return;

            fakeEvent = fake;
            fakeTab = tab;
            BindFakeToPanel(fake);
            if (announce)
                QueueSelectionToast(selected.Count);
            else
                LogMultiEdit("恢复批量视图：" + selected.Count + " 个事件（重开弹窗，不重复提示）");
        }

        /// <summary>
        /// "选中数变化后该不该退出多选"的判定（纯逻辑，离线 harness 直接断言，§36.1）：
        /// 只有**本来就有批量态**（batchCount ≥ 2）又被反选到 < 2 才退出。
        /// </summary>
        internal static bool ShouldExitMultiSelect(int selectedCount, int batchCount)
        {
            return selectedCount < 2 && batchCount >= 2;
        }

        /// <summary>当前有没有活的批量态（批量的标准就是 ≥2 个事件）。</summary>
        private static bool HasBatch()
        {
            return BatchCount() >= 2;
        }

        /// <summary>把 fake 挂到右侧面板上（面板切换、多选数量变化、撤销后重建都用它）。</summary>
        private static void BindFakeToPanel(LevelEvent fake)
        {
            scnEditor editor = scnEditor.instance;
            InspectorPanel panel = editor != null ? editor.levelEventsPanel : null;
            if (panel == null || fake == null)
                return;

            try
            {
                // 这次面板切换是**弹窗自己**发起的：先声明一下，否则"面板切换就关窗"的补丁
                // 会在 shift 加/减选（多选态刷新）时把弹窗关掉（§26.2）。
                SuppressAutoClose();
                bindingBatch = true;                 // 连带压住"面板选中事件已不在批量里"的判定（§39 H2）
                panel.ShowInspector(true, false);          // 先把面板显示出来（内部会用真实事件刷一次）
                panel.selectedEvent = fake;                // 再把它指向我们的 fake
                panel.selectedEventType = fake.eventType;
                PropertiesPanel target = FindEventPanel(panel, fake.eventType);
                if (target != null)
                    target.SetProperties(fake, true);
                MarkMixedLabels(target, fake);
                MakeMixedRowsEditable(target, fake);
                LogMultiEdit("绑定面板：" + fake.realEvents.Count + " 个事件，data " + fake.data.Count
                    + " 键，混合值 " + mixedKeys.Count + " 个");
            }
            catch (Exception e)
            {
                Main.Logger?.Log("多选面板绑定失败: " + e.Message);
            }
            finally
            {
                bindingBatch = false;
            }
        }

        /// <summary>
        /// 造 fake：**data 带上"该类型注册过的每一个属性键"**（面板上会出现的一行都算），
        /// 值取"第一个带这个键的选中事件"的值，都没有就取属性注册的默认值。
        ///
        /// 为什么不能只搬第一个事件的 data（§29.2 的根因之一）：第一个事件里没有这个键时，
        /// fake 的 data/disabled 里都没有它 ⇒ ① 面板那一行不会被刷成 (Mixed)，还留着上一个事件的值；
        /// ② 用户改了它之后 `LevelEvent.ApplyPropertiesToRealEvents` 会在 `disabled[key]` 上
        /// **抛 KeyNotFoundException**（它就是直接 `disabled[key]` 取，没有 TryGet），
        /// 整个写回被我们自己的 catch 吞掉 ⇒ "这个字段怎么改都没反应"。
        ///
        /// 不一致的键标记 `disabled = true`（照原版：写回时跳过），但我们另外记一份 `mixedKeys`，
        /// 面板上把它们画成"可输入 + (Mixed) 标签"，用户一改就把这个键放出来写回所有事件（§29.2）。
        /// </summary>
        private static LevelEvent BuildFakeEvent(List<LevelEvent> selected)
        {
            try
            {
                LevelEvent first = selected[0];
                LevelEventInfo info = first.info;
                var data = new Dictionary<string, object>();
                if (info != null && info.propertiesInfo != null)
                {
                    foreach (KeyValuePair<string, PropertyInfo> pair in info.propertiesInfo)
                    {
                        if (IsFakeDataExcluded(pair.Key, pair.Value))
                            continue;
                        data[pair.Key] = ValueOf(pair.Value, selected[0]);
                    }
                }
                else
                {
                    // 极端情况（没注册表）：退回"照搬第一个事件的 data"，至少还能用
                    foreach (KeyValuePair<string, object> pair in first.data)
                        data[pair.Key] = pair.Value;
                }

                mixedKeys.Clear();
                foreach (string key in CollectMixedKeys(selected, info))
                    mixedKeys.Add(key);

                var disabled = new Dictionary<string, bool>();
                foreach (string key in data.Keys)
                    disabled[key] = false;
                for (int i = 0; i < MultiEditBlacklist.Length; i++)
                    if (disabled.ContainsKey(MultiEditBlacklist[i]))
                        disabled[MultiEditBlacklist[i]] = true;
                foreach (string key in mixedKeys)
                    if (disabled.ContainsKey(key))
                        disabled[key] = true;

                var fake = new LevelEvent(-1, first.eventType, info, data, disabled, first.active, first.visible, false);
                fake.isFake = true;
                fake.floor = first.floor;
                fake.realEvents = new List<LevelEvent>(selected);
                return fake;
            }
            catch (Exception e)
            {
                Main.Logger?.Log("构造多选 fake 事件失败: " + e.Message);
                return null;
            }
        }

        /// <summary>这个键要不要排除在 fake 之外：面板上不建行的键（invisible / 分组归属键）与空名。</summary>
        private static bool IsFakeDataExcluded(string key, PropertyInfo info)
        {
            if (string.IsNullOrEmpty(key))
                return true;
            if (info != null && info.invisible)
                return true;
            return Features.DecoGrouping.DecoGroupState.IsMembershipKey(key);
        }

        /// <summary>黑名单键：进 fake（面板那一行不至于留着上一个事件的值）但不参与批量编辑。</summary>
        private static bool IsBlacklisted(string key)
        {
            for (int i = 0; i < MultiEditBlacklist.Length; i++)
                if (MultiEditBlacklist[i] == key)
                    return true;
            return false;
        }

        /// <summary>某个事件里这个键的值（没有就用属性注册的默认值）。</summary>
        private static object ValueOf(PropertyInfo info, LevelEvent e)
        {
            object value;
            return TryValueOf(info, e, out value) ? value : (info != null ? info.value_default : null);
        }

        /// <summary>某个事件里到底有没有这个键（`false` 时输出参数给属性的注册默认值）。</summary>
        private static bool TryValueOf(PropertyInfo info, LevelEvent e, out object value)
        {
            value = info != null ? info.value_default : null;
            if (e == null || info == null || string.IsNullOrEmpty(info.name))
                return false;
            try
            {
                if (e.TryGet<object>(info.name, out object found))
                {
                    value = found;
                    return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// "混合值"（各选中事件取值不一致 ⇒ 面板标 (Mixed)、`disabled=true` 不参与整体写回）的键。
        ///
        /// 两条规则（§29.2 + §31.1）：
        ///  · 比对用**原版自己的** `InspectorPanel.EventPropertyEquals`（Int32/String/Bool/Float 带容差、
        ///    Vector2 认 NaN），不是 `object.Equals` —— 否则 `duration` 这种 float 差个 1e-7 就被判成
        ///    "混合"，行被画成"关"（旧版直接没法改）；
        ///  · **有一个事件里没有这个键，也算混合**：那种键的"值"只是我们代填的注册默认值，
        ///    不能当成"全体一致"往外写（PACL2 还会把 fake 里 `disabled=false` 的键在各真实事件上
        ///    一并"打开"，代填的默认值会被真的写进去）。
        /// </summary>
        private static List<string> CollectMixedKeys(List<LevelEvent> selected, LevelEventInfo info)
        {
            var mixed = new List<string>();
            if (info == null || info.propertiesInfo == null || selected.Count < 2)
                return mixed;

            foreach (KeyValuePair<string, PropertyInfo> pair in info.propertiesInfo)
            {
                if (IsFakeDataExcluded(pair.Key, pair.Value) || IsBlacklisted(pair.Key))
                    continue;
                bool differ = false;
                object first;
                bool firstHas = TryValueOf(pair.Value, selected[0], out first);
                for (int i = 1; i < selected.Count; i++)
                {
                    object other;
                    bool has = TryValueOf(pair.Value, selected[i], out other);
                    if (has != firstHas || (has && !SameValue(pair.Value, first, other)))
                    {
                        differ = true;
                        break;
                    }
                }
                if (differ)
                    mixed.Add(pair.Key);
            }
            return mixed;
        }

        /// <summary>原版 `InspectorPanel.EventPropertyEquals`（**实例方法**，私有；反射拿一次缓存）。</summary>
        private static System.Reflection.MethodInfo eventPropertyEquals;
        private static bool eventPropertyEqualsResolved;

        /// <summary>
        /// 两个值算不算"同一个值"（口径见 `CollectMixedKeys`）。
        /// 原版那个方法虽然挂在 `InspectorPanel` 上，方法体只用参数、不碰 `this`，
        /// 所以拿任意一个面板实例当 target 调用即可（原来是 `Invoke(null, …)` —— 对实例方法会抛
        /// `TargetException` 被吞掉 ⇒ 一直退化成 `Equals`，浮点容差等于没有）。
        /// </summary>
        private static bool SameValue(PropertyInfo info, object a, object b)
        {
            if (a == null || b == null)
                return a == null && b == null;
            if (info != null)
            {
                if (!eventPropertyEqualsResolved)
                {
                    eventPropertyEqualsResolved = true;
                    try
                    {
                        eventPropertyEquals = HarmonyLib.AccessTools.Method(typeof(InspectorPanel), "EventPropertyEquals",
                            new[] { typeof(PropertyInfo), typeof(object), typeof(object) });
                    }
                    catch { }
                }
                if (eventPropertyEquals != null)
                {
                    object target = scnEditor.instance != null ? scnEditor.instance.levelEventsPanel : null;
                    if (target != null)
                    {
                        try { return (bool)eventPropertyEquals.Invoke(target, new object[] { info, a, b }); }
                        catch { }
                    }
                }
            }
            try { return a.Equals(b); } catch { return false; }
        }

        /// <summary>
        /// 混合值属性在标签上追加红色 (Mixed)，并把这一行**放出来变成可输入**（§29.2）。
        ///
        /// 为什么还要第二步：原版对 `disabled[key]` 的行是"关"的画法（`SetupCheckmark` 里
        /// `offText` 亮、`control` 整个隐藏）⇒ 混合值的字段**根本没法输入**。
        /// 需求是"值不同的显示 (Mixed)，但仍可输入新值并写回所有选中事件"，所以这里把控件显示、
        /// `offText` 收起；用户一改，`ApplyFakeToRealEvents` 就只把这个键放出来写回（见那里）。
        /// `disabled` 本身仍保留 true ⇒ 原版那几条批量写回路径依旧会跳过它，不会"顺手"把别的
        /// 混合字段改成第一个事件的值。
        /// </summary>
        private static void MarkMixedLabels(PropertiesPanel panel, LevelEvent fake)
        {
            if (panel == null || fake == null)
                return;
            try
            {
                var table = panel.Get<Dictionary<string, ADOFAI.Property>>("properties");
                if (table == null)
                    return;
                foreach (string key in mixedKeys)
                {
                    if (IsBlacklisted(key))
                        continue;
                    if (!table.TryGetValue(key, out ADOFAI.Property property) || property == null)
                        continue;
                    if (property.label != null && property.label.text != null && !property.label.text.Contains("(Mixed)"))
                        property.label.text = property.label.text + MixedSuffix;
                }
            }
            catch (Exception e)
            {
                Main.Logger?.Log("标记混合值失败: " + e.Message);
            }
        }

        /// <summary>把混合值的行从"关"改成"可输入"（原版 `SetupCheckmark` 对 disabled 行的画法，见 §29.2）。</summary>
        private static void MakeMixedRowsEditable(PropertiesPanel panel, LevelEvent fake)
        {
            if (panel == null || fake == null || mixedKeys.Count == 0)
                return;
            try
            {
                var table = panel.Get<Dictionary<string, ADOFAI.Property>>("properties");
                if (table == null)
                    return;
                foreach (string key in mixedKeys)
                {
                    if (IsBlacklisted(key))
                        continue;
                    if (!table.TryGetValue(key, out ADOFAI.Property property) || property == null)
                        continue;
                    if (property.offText != null)
                        property.offText.SetActive(false);
                    if (property.control != null)
                        property.control.gameObject.SetActive(true);
                }
            }
            catch (Exception e)
            {
                Main.Logger?.Log("放开混合值行失败: " + e.Message);
            }
        }

        /// <summary>某个键不再是混合值时，把标签上的 (Mixed) 标记摘掉。</summary>
        private static void ClearMixedLabel(string key)
        {
            if (string.IsNullOrEmpty(key) || fakeEvent == null)
                return;
            try
            {
                InspectorPanel inspector = scnEditor.instance != null ? scnEditor.instance.levelEventsPanel : null;
                PropertiesPanel panel = inspector != null ? FindEventPanel(inspector, fakeEvent.eventType) : null;
                var table = panel != null ? panel.Get<Dictionary<string, ADOFAI.Property>>("properties") : null;
                if (table == null || !table.TryGetValue(key, out ADOFAI.Property property)
                    || property == null || property.label == null || property.label.text == null)
                    return;
                property.label.text = property.label.text.Replace(MixedSuffix, "");
            }
            catch { }
        }

        private static PropertiesPanel FindEventPanel(InspectorPanel panel, LevelEventType type)
        {
            try
            {
                List<PropertiesPanel> list = panel.panelsList;
                return list != null ? list.Find(p => p != null && p.levelEventType == type) : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// 值变化时把"用户刚改的那个键"逐事件写回（§31.1/§32.2；两个钩子都调它：
        /// `PropertyControl.OnValueChange` 的后置补丁、以及写进 fake 的 `LevelEvent.set_Item` 后置补丁）。
        ///
        /// **只写这一个键、直接写**（§31.1 的回归修复）：不再走 `LevelEvent.ApplyPropertiesToRealEvents`
        /// 整体写回。原因是那条路要同时依赖三件我们控制不了的事：① `fake.disabled[key]` 必须是 false
        /// 才会被写（混合值/黑名单都是 true）；② 那个方法是**其它模组会替换**的热点
        /// （PACL2 就用 MonoMod 把它整段 Replace 成自己的版本，行为随人变）；
        /// ③ 它按 `data.Keys` 遍历，任何一处抛异常都会让**整次写回**失效。
        /// 用户直接编辑的那个键含义是明确的 ⇒ 逐事件 `ev[key] = fake[key]` 最稳。
        ///
        /// 顺手把该键在真实事件上的"启用"标记清掉（等价于原版/PACL2 的"多选视图里开着的属性对全体也开"），
        /// 否则属性在单事件视图里仍是"关"，用户会以为没生效。装饰还要刷新装饰对象。
        ///
        /// **撤销点**：控件自己的回调大多已经开着一个 `SaveStateScope`（IL 核过：除了
        /// `FloatPair::SaveWithoutRecording` / `Toggle::ProcessFile` / `Toggle::OggEncodeCallback`
        /// 都有），我们只在 `editor.changingState <= 0`（没人在管撤销）时才自己开一个
        /// ⇒ 一次编辑仍然只有一个撤销点。
        ///
        /// **幂等**：两个钩子对同一次编辑都会进来、控件也可能连续改多个键；值已经一致就什么都不做
        /// （连撤销点都不开）。
        ///
        /// `changedKey` 拿不到时（理论上不会）退回原来的整体写回，行为与修改前一致。
        /// </summary>
        internal static void ApplyFakeToRealEvents(string changedKey)
        {
            try
            {
                FlushPendingToastIfSettled();      // 有交互了 ⇒ 把上次"待弹"的数量提示冲掉（§35.3）
                LevelEvent fake = fakeEvent;
                if (fake == null)
                {
                    // §34 之后：没有批量态（没做过多选、或已退出）⇒ 这次改动就只作用于面板当前那个事件。
                    LogMultiEdit("写回跳过：当前没有多选批量态，这次改动只作用于面板当前那个事件");
                    return;
                }
                if (fake.realEvents == null || fake.realEvents.Count < 2)
                {
                    LogMultiEdit("写回跳过：fake.realEvents 不足 2 个（" + (fake.realEvents != null ? fake.realEvents.Count : -1) + "）");
                    return;
                }
                scnEditor editor = scnEditor.instance;
                if (editor == null)
                    return;

                string key = changedKey;
                // 黑名单键（原版内部字段，成对写入才有意义）不能逐键写：fake 里它们的 `disabled` 恒为 true，
                // 整体写回那条路会跳过，但逐键这条路不看 `disabled` ⇒ 不挡就会把 fake 的那一份写进每个事件（§39 M3）。
                if (IsBlacklisted(key))
                {
                    LogMultiEdit("写回跳过：" + key + " 在批量编辑黑名单里");
                    return;
                }
                // 控件自己的回调大多已经开着 SaveStateScope（IL 核过）；只有没人在管撤销时才自己开一个
                // ⇒ 一次编辑仍然只有一个撤销点（§32.2）。
                bool ownScope = editor.changingState <= 0;

                if (string.IsNullOrEmpty(key))
                {
                    // 兜底：不知道改的是哪个键 ⇒ 走整体写回（与修改前一致）
                    try
                    {
                        if (ownScope)
                        {
                            using (new SaveStateScope(editor, false, true, false))
                                fake.ApplyPropertiesToRealEvents();
                        }
                        else
                        {
                            fake.ApplyPropertiesToRealEvents();
                        }
                        LogMultiEdit("写回（整体）：" + fake.realEvents.Count + " 个事件");
                    }
                    catch (Exception e)
                    {
                        Main.Logger?.Log("多选写回失败（整体）: " + e);
                    }
                    return;
                }

                object value = null;
                bool hasValue = false;
                try { hasValue = fake.TryGet<object>(key, out value); }
                catch { }
                if (!hasValue && !fake.data.ContainsKey(key))
                    hasValue = fake.data.TryGetValue(key, out value);      // TryGet 的类型转换失败时兜一手
                if (!hasValue)
                {
                    // fake 上没有这个键（理论上不该发生）：直接退出，别把 null 写进所有事件
                    LogMultiEdit("写回跳过：fake 上没有键 " + key);
                    return;
                }

                // 先干算一遍"哪些事件真的需要写"：
                //  · 值已经一致的跳过（幂等 —— 两个钩子对同一次编辑各跑一次、以及"原值没变"的提交都不会重复写）
                //  · 全都一致且没有"关"的标记 ⇒ 直接返回，**连 SaveStateScope 都不开**（不产生空的撤销点）
                PropertyInfo propInfo = null;
                if (fake.info != null && fake.info.propertiesInfo != null)
                    fake.info.propertiesInfo.TryGetValue(key, out propInfo);

                bool isFloorKey = key == "floor";
                int pending = 0;
                for (int i = 0; i < fake.realEvents.Count; i++)
                {
                    LevelEvent ev = fake.realEvents[i];
                    if (ev == null || ReferenceEquals(ev, fake))
                        continue;
                    if (isFloorKey)
                    {
                        if (ev.floor != fake.floor)
                            pending++;
                        continue;
                    }
                    object current;
                    bool has = TryValueOf(propInfo, ev, out current);
                    bool same = has && SameValue(propInfo, current, value);
                    bool propertyOff = ev.disabled != null && ev.disabled.ContainsKey(key) && ev.disabled[key];
                    if (!same || propertyOff)
                        pending++;
                }

                if (pending > 0)
                {
                    if (ownScope)
                    {
                        using (new SaveStateScope(editor, false, true, false))
                            WriteKeyToRealEvents(fake, editor, key, value, isFloorKey);
                    }
                    else
                    {
                        WriteKeyToRealEvents(fake, editor, key, value, isFloorKey);
                    }
                }

                // 记账：这个键对全体一致了 ⇒ 不再是混合值，标签上的 (Mixed) 也摘掉
                if (mixedKeys.Remove(key) && fake.disabled.ContainsKey(key))
                    fake.disabled[key] = false;
                ClearMixedLabel(key);

                // 影响分桶/行文本的键（tag / eventTag / 备注）改完要把列表重建一遍，
                // 否则弹窗里还是旧分组、旧标签（用户验收要求"弹窗分桶随之重排"）。
                if (pending > 0 && isOpen && IsGroupingKey(key))
                    ReloadRows();
            }
            catch (Exception e)
            {
                // 入口这一段（取 fake 的值 / 读属性注册表）以前在 try 之外，抛出来就直接落到 Harmony 的
                // 全局日志里，"改了没反应"时什么都查不到 ⇒ 整个方法体兜住（§39 M5）
                Main.Logger?.Log("多选写回异常: " + e);
            }
        }

        /// <summary>改这个键要不要重建弹窗列表（分组键 + 行上显示的备注）。</summary>
        private static bool IsGroupingKey(string key)
        {
            return key == "tag" || key == "eventTag" || key == Features.Notes.EventNote.KeyNote;
        }

        /// <summary>把一个键的值写到 fake 的全部真实事件上（撤销点由调用方按 `changingState` 决定，见上）。</summary>
        private static void WriteKeyToRealEvents(LevelEvent fake, scnEditor editor, string key, object value, bool isFloorKey)
        {
            int written = 0;
            // 僵尸（已经被删出关卡、但仍留在 fake.realEvents 里）单独计数（§39 H3）：
            // 写进它们等于没写，日志里"实际数 < 应写数"到底是哪一类原因造成的，就看这个数。
            HashSet<LevelEvent> inLevel = CollectLevelEvents();
            int zombies = 0;
            for (int i = 0; i < fake.realEvents.Count; i++)
            {
                LevelEvent ev = fake.realEvents[i];
                if (ev == null || ReferenceEquals(ev, fake))
                    continue;
                if (inLevel != null && !inLevel.Contains(ev))
                    zombies++;
                if (isFloorKey)
                {
                    if (ev.floor == fake.floor)
                        continue;
                    ev.floor = fake.floor;
                    written++;
                    continue;
                }
                ev[key] = value;
                // 属性在这个事件上是"关"的话，值写进去也看不见 ⇒ 一并打开（与点启用勾选等价）
                if (ev.disabled != null)
                    ev.disabled[key] = false;
                written++;
            }
            // 装饰：值写完要刷新装饰对象（原版 ApplyPropertiesToRealEvents 末尾也做这件事）
            if (fake.IsDecoration)
                for (int i = 0; i < fake.realEvents.Count; i++)
                    if (fake.realEvents[i] != null)
                        editor.UpdateDecorationObject(fake.realEvents[i]);

            LogMultiEdit("写回：" + key + " → " + written + " 个事件（应写 " + ExpectedWrites(fake) + " 个"
                + (zombies > 0 ? "，其中 " + zombies + " 个已不在关卡里（僵尸）" : "") + "）");
        }

        /// <summary>
        /// 关卡事件表里还留着的那些事件的引用（读不到事件表时返回 null，表示"判不了"）。
        /// <see cref="LevelEvent"/> 直接继承 <see cref="object"/>、没重写 <c>Equals</c>/<c>GetHashCode</c>，
        /// 所以 <see cref="HashSet{T}"/> 这里就是纯引用比较（IL + 反射核过）。
        /// </summary>
        private static HashSet<LevelEvent> CollectLevelEvents()
        {
            try
            {
                scnEditor editor = scnEditor.instance;
                if (editor == null || !(editor.Get<object>("events") is IEnumerable events))
                    return null;
                var result = new HashSet<LevelEvent>();
                foreach (object obj in events)
                    if (obj is LevelEvent ev && ev != null)
                        result.Add(ev);
                return result;
            }
            catch { return null; }
        }

        /// <summary>
        /// 批量态的存活校验（§39 H3）：事件被**绕开剪贴板与撤销**的路径删掉（Delete 键、删砖、整砖粘贴、
        /// 其它模组）之后，fake 的 realEvents 里会留下已经不在关卡里的引用 —— 面板还显示着批量视图，
        /// 用户改属性就写给了不存在的事件，观感是"改了没生效"。挂在事件增删的公共出口上（见 PagerListPatches），
        /// 按引用过滤一遍：剔除失效的，剩下不足 2 个就退出多选。
        /// </summary>
        internal static void VerifyBatchEventsAlive()
        {
            LevelEvent batch = fakeEvent;
            if (batch == null || batch.realEvents == null || batch.realEvents.Count < 2)
                return;
            scnEditor editor = scnEditor.instance;
            if (editor == null)
                return;
            if (editor.changingState > 0)
            {
                pendingLivenessCheck = true;         // 原版流程中途：关卡还是半成品，这一刻查了也不算数
                return;
            }
            pendingLivenessCheck = false;

            HashSet<LevelEvent> inLevel = CollectLevelEvents();
            if (inLevel == null)
                return;                              // 事件表读不到，宁可不判也别误杀

            var kept = new List<LevelEvent>();
            for (int i = 0; i < batch.realEvents.Count; i++)
            {
                LevelEvent ev = batch.realEvents[i];
                if (ev != null && !ReferenceEquals(ev, batch) && inLevel.Contains(ev))
                    kept.Add(ev);
            }
            int dropped = batch.realEvents.Count - kept.Count;
            if (dropped <= 0)
                return;
            batch.realEvents = kept;

            string reason = "批量里的事件被删掉了（不走剪贴板/撤销的路径）：剔除 " + dropped
                + " 个失效的，只剩 " + kept.Count + " 个";
            if (kept.Count < 2)
            {
                ExitMultiSelect(reason);
                return;
            }
            LogMultiEdit(reason + " ⇒ 批量范围缩小，面板继续显示剩下的事件");
        }

        /// <summary>这次写回本该写几个事件（实际数对不上就说明循环被打断，日志里一眼能看出来）。</summary>
        private static int ExpectedWrites(LevelEvent fake)
        {
            int count = 0;
            for (int i = 0; i < fake.realEvents.Count; i++)
            {
                LevelEvent ev = fake.realEvents[i];
                if (ev != null && !ReferenceEquals(ev, fake))
                    count++;
            }
            return count;
        }

        /// <summary>
        /// 面板被原版切回"单事件视图"时把批量视图挂回去（§34.2 的"持续显示批量视图"）：
        /// 批量态活着的时候，原版的 ShowPanel / 选砖收尾都可能把 `selectedEvent` 设成某个真实事件，
        /// 那样面板就不再是批量视图了 —— 这里兜住。
        /// </summary>
        internal static void EnsureBatchPanelBound()
        {
            LevelEvent batch = fakeEvent;
            if (batch == null || batch.realEvents == null || batch.realEvents.Count < 2)
                return;
            if (bindingBatch)
                return;                            // 我们自己正在挂面板（§39 H2），这一刻的 selectedEvent 不算数
            InspectorPanel panel = scnEditor.instance != null ? scnEditor.instance.levelEventsPanel : null;
            if (panel == null)
                return;
            if (ReferenceEquals(panel.selectedEvent, batch))
                return;                            // 已经是批量视图
            if (panel.selectedEventType != batch.eventType)
                return;                            // 面板已切到别的类型（那时 CloseIfOpenOnOtherPanel 已经退出多选）
            // 面板选中的是**批量范围外**的真实事件 ⇒ §34.3 的退出条件 #2 成立。
            // 以前这里一律"挂回去"，于是任何不经过分页器箭头的选事件入口（原版键盘选事件、
            // 其它模组直接给 selectedEvent 赋值）都会把面板又拽回批量视图，多选死活退不掉（§39 H2）。
            LevelEvent selected = panel.selectedEvent;
            if (selected != null && !selected.isFake && !IsInBatch(selected))
            {
                ExitMultiSelect("面板选中的事件已不在批量范围内");
                return;
            }
            BindFakeToPanel(batch);
            LogMultiEdit("面板被切回单事件 ⇒ 重新挂回批量视图");
        }

        /// <summary>多选批量编辑的诊断日志（只打关键节点，量很小；出问题时靠它定位）。</summary>
        private static void LogMultiEdit(string message)
        {
            Main.Logger?.Log("多选批量编辑：" + message);
        }

        /// <summary>弹窗空白处被点击（§37）：消除"点了没反应、日志空白"的盲区。</summary>
        internal static void LogPopupBlankClick()
        {
            LogMultiEdit("点击落在弹窗空白处（不是事件行、也不是组头）");
        }

        /// <summary>
        /// 弹窗开着时按 Esc = **等价于点 OK**（§38.2）：关窗但保留多选批量态。
        ///
        /// 原版 `scnEditor.HandleKeyboardActions` 一开头就是 `if (showingPopup) { 只处理 Esc; ShowPopup(false,false,false); return; }`
        /// （IL 核过：Esc = `new EditorKeybind(KeyModifier.0, (KeyCode)27, true).IsPressed`）——
        /// 它只收掉**原版自己**的弹窗状态与遮罩，我们这块挂在 Canvas 下的自建窗口它管不到，
        /// 所以才出现"窗口不消失、但外面已经能点"。这里在同一个入口（我们的 Prefix 跑在它方法体之前）
        /// 自己把窗口收掉，之后原版照旧走它的 Esc 分支，互不打架。
        /// </summary>
        internal static void HandlePopupEscape()
        {
            if (!isOpen)
                return;                            // 只有我们的弹窗开着时才接管 Esc
            bool esc;
            // 用游戏自己的输入包装（RDInput）：本工程没有引用 UnityEngine.InputLegacyModule，
            // 直接调 Input.GetKeyDown 编译不过；RDInput.WentDown 就是"这一帧按下"，
            // 与 EditorKeybind.IsPressed 同源（EditorKeybind 对 Esc 的判定就走它）。
            try { esc = RDInput.WentDown(KeyCode.Escape); }
            catch { return; }
            if (!esc)
                return;
            LogMultiEdit("Esc 关窗（等价于点 OK，保留多选：" + BatchCount() + " 个事件）");
            Close(true);
        }

        // ---------------------------------------------------------------- 左上角提示（§34.4）

        /// <summary>
        /// 多选数量提示（"已选择 N 个事件"）：连续 ctrl 加选时不刷屏 —— 先记成"待弹"，
        /// 由 `Tick()`（每帧，挂在 `InspectorPanel.Update` 后置）在数量稳定 `ToastSettleSeconds`
        /// 之后再弹；如果距离上次提示已超过 0.6 秒就立刻弹（单击加选这种慢节奏有即时反馈）。
        /// §35：待弹的标记还会被"下一次交互"顺带冲掉（`FlushPendingToastIfSettled`），
        /// 这样即使每帧钩子没跑（或没挂上），提示也不会永远憋着。
        /// </summary>
        private static void QueueSelectionToast(int count)
        {
            if (count == lastToastCount && pendingToastKey == null && Time.unscaledTime - lastToastTime < 1f)
            {
                LogMultiEdit("提示跳过：数量仍为 " + count + "（刚提示过）");
                return;                            // 数量没变（例如重开弹窗的重新绑定）⇒ 不重复提示
            }
            lastToastCount = count;
            lastSelectionChangeTime = Time.unscaledTime;
            pendingToastKey = "aee.notify.multiSelected";
            pendingToastCount = count;
            LogMultiEdit("提示排队：已选择 " + count + " 个（稳定 " + ToastSettleSeconds + "s 后弹）");
            if (Time.unscaledTime - lastToastTime >= 0.6f)
                FlushToast();
        }

        /// <summary>
        /// 下一次交互时把"待弹"的提示冲掉（如果已经过了稳定时间）：不依赖每帧钩子（§35.3）。
        /// 在有用户操作的入口（行点击、写回、关窗）里调，保证"最后一次数量变化"总能提示出来。
        /// </summary>
        private static void FlushPendingToastIfSettled()
        {
            if (pendingToastKey == null)
                return;
            if (Time.unscaledTime - lastSelectionChangeTime < ToastSettleSeconds)
                return;
            FlushToast();
        }

        /// <summary>每帧的收尾（`InspectorPanel.Update` 后置补丁 + `HandleKeyboardActions` 前置补丁）：
        /// 数量稳定后把待弹的提示弹出去；顺带补做欠一次的批量存活校验（§39 H3）。</summary>
        internal static void Tick()
        {
            if (pendingLivenessCheck)
                VerifyBatchEventsAlive();
            FlushPendingToastIfSettled();
        }

        private static void FlushToast()
        {
            string key = pendingToastKey;
            pendingToastKey = null;
            if (string.IsNullOrEmpty(key))
                return;
            // §38.1：弹出这一刻取**当前真实数量**，不是排队时那个旧值 ——
            // 排队期间数量又变了（连点）时，旧写法会弹出上一个数量（"滞后一个"）。
            int count = key == "aee.notify.multiSelected" ? LiveSelectionCount() : pendingToastCount;
            if (key == "aee.notify.multiSelected" && count < 2)
            {
                LogMultiEdit("提示丢弃：当前选中已不足 2 个（" + count + "）");
                return;
            }
            lastToastTime = Time.unscaledTime;
            LogMultiEdit("提示弹出：" + (key == "aee.notify.multiSelected" ? "已选择 " + count + " 个" : key));
            PagerClipboard.Toast(key, count);
        }

        /// <summary>
        /// 当前"真实"的选中数量（§38.1）：有批量态就以批量为准（关窗后也成立），
        /// 否则看弹窗里累积中的选中集。
        /// </summary>
        private static int LiveSelectionCount()
        {
            int batch = BatchCount();
            if (batch >= 2)
                return batch;
            return selectedIndices.Count;
        }

        // ---------------------------------------------------------------- 退出多选的触发点（§34.3）

        /// <summary>
        /// 分页器箭头（◀ ▶）切了当前事件：**落到批量范围之外就退出多选**。
        /// 挂在原版 `CycleButtons.CycleEvent` 的后置上（箭头按钮的回调就是它）。
        /// 只认"箭头"这条路径 —— 我们自己在多选态里点行/批量操作引起的 selectedEvent 变化不走这里，
        /// 所以不会误判（点行本来也是"单选 ⇒ 退出"）。
        /// </summary>
        internal static void OnPagerArrowCycled(CycleButtons buttons)
        {
            if (fakeEvent == null || BatchCount() < 2)
                return;
            InspectorTab tab = buttons != null ? buttons.tab : null;
            if (tab != null && fakeTab != null && tab != fakeTab)
                return;                            // 别的标签页的箭头，与我们的批量无关

            LevelEvent current = CurrentEventOfTab(tab);
            if (current != null && IsInBatch(current))
            {
                // 还在批量范围里：多选保留，但原版刚把面板切到那个单事件上了 ⇒ 把批量视图挂回去
                BindFakeToPanel(fakeEvent);
                return;
            }
            ExitMultiSelect("分页器箭头切到批量范围外的事件" + (current == null ? "（取不到当前事件）" : ""));
        }

        /// <summary>某个标签页当前显示的事件（原版从"选中的砖 + eventIndex"推）。</summary>
        private static LevelEvent CurrentEventOfTab(InspectorTab tab)
        {
            InspectorTab use = tab ?? fakeTab ?? openTab;
            if (use == null)
                return null;
            try
            {
                scnEditor editor = scnEditor.instance;
                List<LevelEvent> stack = editor != null ? editor.GetSelectedFloorEvents(use.levelEventType) : null;
                if (stack != null && stack.Count > 0)
                    return stack[Mathf.Clamp(use.eventIndex, 0, stack.Count - 1)];
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 撤销 / 重做之后（§34.5）：批量里的值可能被回滚、事件对象也可能被换掉 ⇒ 按当前数据重建 fake
        /// （值 / 混合标记重算，面板继续保持批量视图）；事件全失效就退出多选。
        /// </summary>
        internal static void OnLevelUndoRedo(string what)
        {
            LevelEvent batch = fakeEvent;
            if (batch == null || batch.realEvents == null || batch.realEvents.Count < 2)
                return;
            InspectorTab tab = fakeTab;
            List<LevelEvent> stack = null;
            try
            {
                scnEditor editor = scnEditor.instance;
                stack = editor != null && tab != null ? editor.GetSelectedFloorEvents(tab.levelEventType) : null;
            }
            catch { }
            if (stack == null)
                return;

            var alive = new List<LevelEvent>();
            for (int i = 0; i < batch.realEvents.Count; i++)
            {
                LevelEvent e = batch.realEvents[i];
                if (e != null && stack.Contains(e))
                    alive.Add(e);
            }
            if (alive.Count < 2)
            {
                ExitMultiSelect(what + "后批量里的事件已失效");
                return;
            }

            LevelEvent rebuilt = BuildFakeEvent(alive);
            if (rebuilt == null)
                return;
            fakeEvent = rebuilt;
            BindFakeToPanel(rebuilt);
            LogMultiEdit(what + "后重建批量面板：" + alive.Count + " 个事件，混合值 " + mixedKeys.Count + " 个");
        }

        /// <summary>
        /// `LevelEvent.set_Item` 的兜底入口（§32.2）：只有写的是我们的 fake 时才动。
        /// 覆盖绕过 `PropertyControl.OnValueChange` 的写值路径（OGG 编码回调、文件处理回调等），
        /// 以及"一次改多个键"的控件；幂等检查保证不会重复写/多开撤销点。
        /// </summary>
        internal static void OnFakeValueWritten(LevelEvent written, string key)
        {
            if (written == null || key == null)
                return;
            if (fakeEvent == null || !ReferenceEquals(written, fakeEvent))
                return;
            ApplyFakeToRealEvents(key);
        }

        /// <summary>
        /// 退出多选态：收起 fake，让面板回到当前单事件，并**把批量视图留下的 (Mixed) 标记全部摘掉**（§33）。
        ///
        /// 为什么必须清：(Mixed) 是直接写在面板**行标签**上的（`MarkMixedLabels` 追加后缀），
        /// 而行只建一次、之后只刷新值不重建标签 ⇒ 丢掉 fake 后如果不主动清，单事件视图里会留着
        /// 假的 `(Mixed)`，让人以为"还在批量模式、改了应该对全组生效" —— 用户就是这么被误导的。
        /// 这里整表扫一遍（也顺手清掉历史残留）；重开弹窗重新绑定 fake 时 `MarkMixedLabels` 会再加回来。
        /// </summary>
        internal static void ClearFakeEvent()
        {
            ClearAllMixedLabels();
            bool had = fakeEvent != null;
            fakeEvent = null;
            fakeTab = null;
            mixedKeys.Clear();
            if (!had)
                return;
            try
            {
                InspectorPanel panel = scnEditor.instance != null ? scnEditor.instance.levelEventsPanel : null;
                LevelEvent real = RealCurrentEvent();
                if (panel != null && real != null)
                {
                    panel.selectedEvent = real;
                    panel.selectedEventType = real.eventType;
                    // 面板要真的显示回单事件的值（行标签刚被摘掉 (Mixed)，值也得跟着换）
                    panel.ShowInspector(true, false);
                    panel.ShowPanel(real.eventType, IndexOfInPanelStack(real));
                }
            }
            catch { }
        }

        /// <summary>
        /// 事件在"面板当前这堆"（当前砖 + 该类型）里的下标。退出多选时要把分页器停回这个事件上 ——
        /// 直接给 0 会跳到该类型的**第一个**事件（§39 H1）。取不到（砖换了 / 堆空）才退回 0。
        /// </summary>
        private static int IndexOfInPanelStack(LevelEvent e)
        {
            if (e == null)
                return 0;
            try
            {
                scnEditor editor = scnEditor.instance;
                List<LevelEvent> stack = editor != null ? editor.GetSelectedFloorEvents(e.eventType) : null;
                if (stack != null)
                {
                    int index = stack.IndexOf(e);
                    if (index >= 0)
                        return index;
                }
            }
            catch { }
            return 0;
        }

        /// <summary>
        /// 一个"真实"的当前事件（退出批量后把面板指回它）：优先弹窗记的 `currentEvent`（还得活着），
        /// 弹窗关着（`currentEvent` 为 null）或它已被删（剪切）时，按标签页的 eventIndex 从当前砖推。
        /// </summary>
        private static LevelEvent RealCurrentEvent()
        {
            InspectorTab tab = openTab ?? fakeTab;
            List<LevelEvent> stack = null;
            try
            {
                scnEditor editor = scnEditor.instance;
                stack = editor != null && tab != null ? editor.GetSelectedFloorEvents(tab.levelEventType) : null;
            }
            catch { }
            if (currentEvent != null && (stack == null || stack.Contains(currentEvent)))
                return currentEvent;
            if (stack != null && stack.Count > 0 && tab != null)
                return stack[Mathf.Clamp(tab.eventIndex, 0, stack.Count - 1)];
            return currentEvent;
        }

        /// <summary>把面板所有行标签上的 (Mixed) 后缀摘掉（丢掉 fake / 退回单事件视图时用）。</summary>
        private static void ClearAllMixedLabels()
        {
            try
            {
                InspectorPanel inspector = scnEditor.instance != null ? scnEditor.instance.levelEventsPanel : null;
                List<PropertiesPanel> panels = inspector != null ? inspector.panelsList : null;
                if (panels == null)
                    return;
                for (int i = 0; i < panels.Count; i++)
                {
                    PropertiesPanel panel = panels[i];
                    if (panel == null)
                        continue;
                    var table = panel.Get<Dictionary<string, ADOFAI.Property>>("properties");
                    if (table == null)
                        continue;
                    foreach (KeyValuePair<string, ADOFAI.Property> pair in table)
                    {
                        ADOFAI.Property property = pair.Value;
                        if (property == null || property.label == null || property.label.text == null)
                            continue;
                        if (property.label.text.Contains("(Mixed)"))
                            property.label.text = property.label.text.Replace(MixedSuffix, "");
                    }
                }
            }
            catch { }
        }

        // ------------------------------------------------------------------ 拖动：加入分组 / 组内排序

        /// <summary>落点：目标分组键 + 插入锚点（AnchorIndex &lt; 0 = 落在组头上 ⇒ 插到该组末尾）。</summary>
        internal struct DropTarget
        {
            internal string Key;
            internal int AnchorIndex;
            internal bool Before;

            internal bool IsHeaderDrop => AnchorIndex < 0;
        }

        internal static bool TryFindDropTarget(Vector2 screenPosition, out DropTarget target)
        {
            target = default;
            target.AnchorIndex = -1;

            RectTransform viewport = scrollRect != null ? scrollRect.viewport : null;
            Camera camera = ResolveCamera();
            if (viewport == null)
                return false;
            if (!RectTransformUtility.RectangleContainsScreenPoint(viewport, screenPosition, camera))
                return false;

            for (int i = 0; i < rows.Count && i < slots.Count; i++)
            {
                GameObject row = rows[i];
                if (row == null)
                    continue;
                var rect = row.transform as RectTransform;
                if (rect == null)
                    continue;
                if (!RectTransformUtility.RectangleContainsScreenPoint(rect, screenPosition, camera))
                    continue;

                Slot slot = slots[i];
                target.Key = slot.Key;
                if (slot.IsHeader)
                {
                    target.AnchorIndex = -1;    // 组头 = 插到组尾
                    target.Before = false;
                    return true;
                }
                target.AnchorIndex = slot.OriginalIndex;
                target.Before = true;
                if (RectTransformUtility.ScreenPointToLocalPointInRectangle(rect, screenPosition, camera, out Vector2 local))
                    target.Before = local.y > (rect.rect.yMin + rect.rect.yMax) * 0.5f;
                return true;
            }
            return false;
        }

        internal static void UpdateDropFeedback(Vector2 screenPosition, int draggingIndex)
        {
            if (!TryFindDropTarget(screenPosition, out DropTarget target))
            {
                ClearDropFeedback();
                return;
            }
            SetGroupHighlight(target.Key);
            ShowDropIndicator(target, screenPosition);
        }

        internal static void ClearDropFeedback()
        {
            SetGroupHighlight(null);
            if (dropLine != null && dropLine.gameObject.activeSelf)
                dropLine.gameObject.SetActive(false);
            if (cursorMark != null && cursorMark.gameObject.activeSelf)
                cursorMark.gameObject.SetActive(false);
            dropFeedbackLogged = false;
        }

        /// <summary>高亮落点分组的头行（原版选中行样式：白底黑字），传 null 还原。</summary>
        private static void SetGroupHighlight(string key)
        {
            for (int i = 0; i < rows.Count && i < slots.Count; i++)
            {
                if (!slots[i].IsHeader || rows[i] == null)
                    continue;
                ListItem item = rows[i].GetComponent<ListItem>();
                if (item == null)
                    continue;
                bool on = key != null && slots[i].Key == key;
                Transform background = item.selectionBackground;
                if (background != null)
                    background.gameObject.SetActive(on);
                TMP_Text label = item.Get<TMP_Text>("itemName");
                if (label != null)
                    label.color = on ? Color.black : Color.white;
            }
        }

        /// <summary>落点白线的世界 Y：插到锚点行之前 ⇒ 该行上沿，之后 ⇒ 该行下沿；落在组头 ⇒ 该组末尾。</summary>
        private static bool TryGetIndicatorWorldY(DropTarget target, out float worldY)
        {
            worldY = 0f;
            if (!target.IsHeaderDrop)
            {
                if (!TryGetRowForIndex(target.AnchorIndex, out RectTransform anchorRect))
                    return false;
                return target.Before ? TryGetTopEdge(anchorRect, out worldY) : TryGetBottomEdge(anchorRect, out worldY);
            }

            // 组头：插到组尾 ⇒ 该组最后一个成员行的下沿；组内没有可见行（空组/折叠）⇒ 组头下沿
            for (int i = rows.Count - 1; i >= 0 && i < slots.Count; i--)
            {
                if (slots[i].IsHeader || slots[i].Key != target.Key || rows[i] == null)
                    continue;
                return TryGetBottomEdge(rows[i].transform as RectTransform, out worldY);
            }
            for (int i = 0; i < rows.Count && i < slots.Count; i++)
            {
                if (!slots[i].IsHeader || slots[i].Key != target.Key || rows[i] == null)
                    continue;
                return TryGetBottomEdge(rows[i].transform as RectTransform, out worldY);
            }
            return false;
        }

        private static bool TryGetRowForIndex(int originalIndex, out RectTransform rect)
        {
            rect = null;
            for (int i = 0; i < rows.Count && i < slots.Count; i++)
            {
                if (slots[i].IsHeader || slots[i].OriginalIndex != originalIndex || rows[i] == null)
                    continue;
                rect = rows[i].transform as RectTransform;
                return rect != null;
            }
            return false;
        }

        private static bool TryGetWorldRange(RectTransform rect, out float topWorldY, out float bottomWorldY)
        {
            topWorldY = bottomWorldY = 0f;
            if (rect == null)
                return false;
            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);   // 0=左下 1=左上 2=右上 3=右下
            bottomWorldY = (corners[0].y + corners[3].y) * 0.5f;
            topWorldY = (corners[1].y + corners[2].y) * 0.5f;
            return true;
        }

        private static bool TryGetTopEdge(RectTransform rect, out float topWorldY)
        {
            return TryGetWorldRange(rect, out topWorldY, out _);
        }

        private static bool TryGetBottomEdge(RectTransform rect, out float bottomWorldY)
        {
            return TryGetWorldRange(rect, out _, out bottomWorldY);
        }

        private static void ShowDropIndicator(DropTarget target, Vector2 screenPosition)
        {
            ResolveDropObjects();
            if (dropLine == null || !TryGetIndicatorWorldY(target, out float worldY))
                return;

            RectTransform viewport = scrollRect != null ? scrollRect.viewport : null;
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

            if (!dropFeedbackLogged && Main.Logger != null)
            {
                dropFeedbackLogged = true;
                Main.Logger.Log(string.Format(
                    "分页器直选：拖动落点 键={0} 锚点={1} 插入点世界Y={2:F1} 视口Y=[{3:F1},{4:F1}] active={5}",
                    target.Key, target.IsHeaderDrop ? "组尾" : (target.Before ? "行上沿" : "行下沿"),
                    worldY, bottomY, topY, dropLine.gameObject.activeInHierarchy));
            }

            if (cursorMark != null)
            {
                var parentRect = cursorMark.parent as RectTransform;
                if (parentRect != null
                    && RectTransformUtility.ScreenPointToLocalPointInRectangle(parentRect, screenPosition, ResolveCamera(), out Vector2 local))
                {
                    cursorMark.localPosition = local;
                    cursorMark.gameObject.SetActive(true);
                }
            }
        }

        private static void ResolveDropObjects()
        {
            if (dropObjectsResolved || popupRoot == null)
                return;
            dropObjectsResolved = true;

            RectTransform viewport = scrollRect != null ? scrollRect.viewport : null;
            RectTransform parent = viewport != null ? viewport : popupRoot.transform as RectTransform;
            dropLine = CreateOverlay(parent, "aee_pagerDropLine", new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 3f));
            cursorMark = CreateOverlay(popupRoot.transform as RectTransform, "aee_pagerDropCursor",
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(16f, 16f));
        }

        /// <summary>纯色覆盖层（与装饰列表那套同规格：白线 3 单位 / 小方块 16×16，都不吃射线）。</summary>
        private static RectTransform CreateOverlay(RectTransform parent, string name, Vector2 anchorMin, Vector2 anchorMax, Vector2 size)
        {
            if (parent == null)
                return null;
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = size;
            rect.anchoredPosition = Vector2.zero;

            Image image = go.GetComponent<Image>();
            image.sprite = null;
            image.color = Color.white;
            image.raycastTarget = false;

            go.SetActive(false);
            rect.SetAsLastSibling();
            return rect;
        }

        /// <summary>
        /// 落点生效：改归属（复用装饰分组那套：写 tag / 记手动归属 / 退出分组）+ 在
        /// `levelData.levelEvents` 里移到锚点旁边。多选时整批一起移动（保持原相对顺序）。
        /// </summary>
        internal static void ApplyDrop(int draggingIndex, DropTarget target)
        {
            scnEditor editor = scnEditor.instance;
            if (editor == null || currentStack == null || string.IsNullOrEmpty(target.Key))
                return;
            if (!CanOpen(openTab, out List<LevelEvent> stack))
            {
                Close();
                return;
            }
            if (draggingIndex < 0 || draggingIndex >= stack.Count)
                return;

            var moving = new List<int>();
            if (selectedIndices.Contains(draggingIndex))
            {
                for (int i = 0; i < stack.Count; i++)
                    if (selectedIndices.Contains(i))
                        moving.Add(i);
            }
            else
            {
                moving.Add(draggingIndex);
            }
            moving.Sort();

            bool assignable = DecoGroupActions.TryResolveAssignment(target.Key, DecoGroupState.GroupSet.Event, out DecoGroupActions.GroupAssignment assignment);

            LevelEvent anchor = null;
            bool before = target.Before;
            if (!target.IsHeaderDrop && target.AnchorIndex >= 0 && target.AnchorIndex < stack.Count && !moving.Contains(target.AnchorIndex))
            {
                anchor = stack[target.AnchorIndex];
            }
            else
            {
                anchor = FindGroupLastEvent(stack, target.Key, moving);
                before = false;   // 组头 = 插到该组末尾
            }

            bool changed = false;
            try
            {
                using (new SaveStateScope(editor, false, true, false))
                {
                    if (assignable)
                    {
                        for (int i = 0; i < moving.Count; i++)
                        {
                            LevelEvent evt = stack[moving[i]];
                            if (!DecoGroupActions.NeedsChange(evt, assignment, DecoGroupState.GroupSet.Event))
                                continue;
                            DecoGroupActions.ApplyAssignment(evt, assignment, DecoGroupState.GroupSet.Event);
                            changed = true;
                        }
                    }
                    if (MoveInLevelEvents(editor, stack, moving, anchor, before))
                        changed = true;
                }
            }
            catch (Exception e)
            {
                Main.Logger?.Log("事件拖动失败: " + e.Message);
                return;
            }

            if (!changed)
                return;

            selectedIndices.Clear();
            ReloadRows();

            // 归属变了的话，让右侧面板跟着刷新一次（tag 显示等）
            if (assignable && currentEvent != null && currentStack != null)
            {
                int index = currentStack.IndexOf(currentEvent);
                if (index >= 0)
                {
                    SuppressAutoClose();
                    try { openTab?.panel?.ShowPanel(openTab.levelEventType, index); }
                    catch { }
                }
            }
        }

        /// <summary>该组里数组顺序最靠后的成员（排除正在移动的那几个）；没有则返回 null（= 追加到数组末尾）。</summary>
        private static LevelEvent FindGroupLastEvent(List<LevelEvent> stack, string key, List<int> moving)
        {
            for (int i = stack.Count - 1; i >= 0; i--)
            {
                if (moving.Contains(i))
                    continue;
                if (groupKeyByIndex.TryGetValue(i, out string owner) && owner == key)
                    return stack[i];
            }
            return null;
        }

        /// <summary>
        /// 把事件在 `levelData.levelEvents` 里移到锚点之前/之后（`anchor == null` ⇒ 追加到末尾）。
        /// 数组顺序 = 每块砖上事件的执行顺序；事件按砖、按类型在数组里与别的事件交错，
        /// 所以这里动的是**数组绝对位置**（紧贴锚点），见 §16.3。
        /// </summary>
        private static bool MoveInLevelEvents(scnEditor editor, List<LevelEvent> stack, List<int> moving, LevelEvent anchor, bool before)
        {
            var list = editor.levelData != null ? editor.levelData.levelEvents as List<LevelEvent> : null;
            if (list == null || list.Count == 0)
                return false;

            var movingEvents = new List<LevelEvent>(moving.Count);
            for (int i = 0; i < moving.Count; i++)
            {
                LevelEvent evt = stack[moving[i]];
                if (evt == null || !list.Contains(evt))
                    return false;
                movingEvents.Add(evt);
            }

            if (anchor != null && movingEvents.Count == 1)
            {
                int from = list.IndexOf(movingEvents[0]);
                int anchorIdx = list.IndexOf(anchor);
                if (anchorIdx >= 0)
                {
                    int target = before ? anchorIdx : anchorIdx + 1;
                    if (target > from)
                        target--;
                    if (target == from)
                        return false;   // 已经在目标位置
                }
            }
            else if (anchor == null)
            {
                int last = list.IndexOf(movingEvents[movingEvents.Count - 1]);
                if (last == list.Count - 1)
                    return false;       // 已经在末尾
            }

            for (int i = 0; i < movingEvents.Count; i++)
                list.Remove(movingEvents[i]);

            int insertAt;
            if (anchor == null)
            {
                insertAt = list.Count;
            }
            else
            {
                int anchorIdx = list.IndexOf(anchor);
                insertAt = anchorIdx < 0 ? list.Count : (before ? anchorIdx : anchorIdx + 1);
            }
            insertAt = Mathf.Clamp(insertAt, 0, list.Count);
            for (int i = 0; i < movingEvents.Count; i++)
                list.Insert(insertAt + i, movingEvents[i]);
            return true;
        }

        /// <summary>弹窗所在画布的相机（ScreenSpaceOverlay 时返回 null）。</summary>
        private static Camera ResolveCamera()
        {
            Canvas canvas = ResolveCanvas();
            if (canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay)
                return null;
            return canvas.worldCamera;
        }

        // ------------------------------------------------------------------ 弹窗构建

        private static bool EnsureBuilt()
        {
            if (popupRoot != null)
                return titleText != null && listContent != null && rowTemplate != null;

            scnEditor editor = scnEditor.instance;
            if (editor == null || editor.popupWindow == null)
                return false;

            GameObject prefab = editor.okPopupContainer != null ? editor.okPopupContainer : editor.largeOkPopupContainer;
            if (prefab == null)
            {
                Main.Logger?.Log("分页器列表：找不到 okPopupContainer，无法弹窗");
                return false;
            }

            CacheRowStyle();
            if (rowTemplate == null)
            {
                Main.Logger?.Log("分页器列表：找不到装饰列表行 prefab，无法建行");
                return false;
            }

            popupRoot = UnityEngine.Object.Instantiate(prefab, editor.popupWindow.transform);
            popupRoot.name = "aee_pagerListPopup";
            // 不要动 popupRoot 自己的锚点：外框就是它自身的 Image，跟随弹窗矩形天然铺满。
            // 一旦把 root 改成 0..1 拉伸锚点，ShowWindow 里再改回 (0.5,0.5) 时宽度会按 sizeDelta 塌成 0。
            // 去掉原版的文本替换组件，否则标题会被它们按固定本地化键改写
            foreach (scrTextChanger changer in popupRoot.GetComponentsInChildren<scrTextChanger>(true))
                UnityEngine.Object.Destroy(changer);

            // 宿主自带的文本与按钮全部隐藏：标题/关闭按钮用它们的副本，位置由我们自己锚定
            TMP_Text hostText = PickText(popupRoot);
            Button hostButton = PickButton(popupRoot);
            foreach (TMP_Text text in popupRoot.GetComponentsInChildren<TMP_Text>(true))
                text.gameObject.SetActive(false);
            foreach (Button button in popupRoot.GetComponentsInChildren<Button>(true))
                button.gameObject.SetActive(false);

            titleText = CloneTitle(hostText);
            closeButton = CloneCloseButton(hostButton);
            BuildScrollArea(popupRoot.transform);
            BuildTopCap();

            popupRoot.SetActive(false);
            return titleText != null && listContent != null;
        }

        /// <summary>行外观取自装饰列表的行 prefab：字体、字号、行高都跟列表一致。</summary>
        private static void CacheRowStyle()
        {
            PropertyControl_DecorationsList panel = DecoGroupRenderer.FindPanel();
            if (panel == null || panel.listItemPool == null || panel.listItemPool.itemPrefab == null)
                return;

            rowTemplate = panel.listItemPool.itemPrefab;

            float height = 0f;
            try
            {
                height = typeof(PropertyControl_List).Get<float>("itemHeight");
            }
            catch { }
            if (height <= 4f)
            {
                RectTransform rect = rowTemplate.GetComponent<RectTransform>();
                if (rect != null)
                    height = rect.rect.height;
            }
            rowHeight = height > 4f ? height : FallbackRowHeight;
        }

        private static TMP_Text CloneTitle(TMP_Text source)
        {
            var go = new GameObject("aee_pagerTitle", typeof(RectTransform), typeof(TextMeshProUGUI));
            var rect = (RectTransform)go.transform;
            rect.SetParent(popupRoot.transform, false);
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.offsetMin = new Vector2(ListPaddingX, -HeaderHeight);
            rect.offsetMax = new Vector2(-ListPaddingX, -TitleTopInset);

            TextMeshProUGUI text = go.GetComponent<TextMeshProUGUI>();
            if (source != null)
            {
                text.font = source.font;
                text.fontSharedMaterial = source.fontSharedMaterial;
                text.fontSize = source.fontSize;
                text.color = source.color;
                text.enableWordWrapping = false;
                text.overflowMode = TextOverflowModes.Ellipsis;
            }
            // 垂直方向强制居中：原版对齐可能是 Top*，会贴着顶边画，同样会盖住上边线
            text.alignment = MiddleRowAlignment(source != null ? source.alignment : TextAlignmentOptions.Center);
            text.raycastTarget = false;
            return text;
        }

        /// <summary>取原版对齐方式里"垂直居中"的那一档（TopLeft→Left、BottomRight→Right、其余→Center）。</summary>
        private static TextAlignmentOptions MiddleRowAlignment(TextAlignmentOptions alignment)
        {
            switch (alignment)
            {
                case TextAlignmentOptions.TopLeft:
                case TextAlignmentOptions.Left:
                case TextAlignmentOptions.BottomLeft:
                case TextAlignmentOptions.BaselineLeft:
                case TextAlignmentOptions.MidlineLeft:
                    return TextAlignmentOptions.Left;
                case TextAlignmentOptions.TopRight:
                case TextAlignmentOptions.Right:
                case TextAlignmentOptions.BottomRight:
                case TextAlignmentOptions.BaselineRight:
                case TextAlignmentOptions.MidlineRight:
                    return TextAlignmentOptions.Right;
                case TextAlignmentOptions.TopJustified:
                case TextAlignmentOptions.Justified:
                case TextAlignmentOptions.BottomJustified:
                case TextAlignmentOptions.BaselineJustified:
                case TextAlignmentOptions.MidlineJustified:
                    return TextAlignmentOptions.Justified;
                default:
                    return TextAlignmentOptions.Center;
            }
        }

        private static Button CloneCloseButton(Button source)
        {
            if (source == null)
                return null;

            GameObject go = UnityEngine.Object.Instantiate(source.gameObject, popupRoot.transform, false);
            go.name = "aee_pagerClose";
            go.SetActive(true);
            var rect = (RectTransform)go.transform;
            rect.anchorMin = new Vector2(0.5f, 0f);
            rect.anchorMax = new Vector2(0.5f, 0f);
            rect.pivot = new Vector2(0.5f, 0f);
            rect.sizeDelta = new Vector2(Mathf.Min(240f, Mathf.Max(120f, rect.sizeDelta.x)), FooterHeight - 14f);
            rect.anchoredPosition = new Vector2(0f, FooterBottomInset);

            foreach (Graphic graphic in go.GetComponentsInChildren<Graphic>(true))
            {
                if (graphic.color.a < 0.05f && graphic is Image)
                {
                    Color color = graphic.color;
                    color.a = 1f;
                    graphic.color = color;
                }
            }
            foreach (TMP_Text text in go.GetComponentsInChildren<TMP_Text>(true))
            {
                text.gameObject.SetActive(true);
                text.raycastTarget = false;
            }

            Button button = go.GetComponent<Button>();
            if (button == null)
                button = go.GetComponentInChildren<Button>(true);
            if (button == null)
            {
                UnityEngine.Object.Destroy(go);
                return null;
            }
            button.onClick.RemoveAllListeners();
            // OK 按钮：关窗但**保留**多选批量态（§34.1/§34.2），关窗后右侧面板仍是批量视图
            button.onClick.AddListener(CloseKeepingBatch);
            return button;
        }

        /// <summary>自建滚动列表：ScrollRect + RectMask2D 视口 + VerticalLayoutGroup/ContentSizeFitter 内容。</summary>
        private static void BuildScrollArea(Transform host)
        {
            var scrollGO = new GameObject("aee_pagerScroll", typeof(RectTransform), typeof(ScrollRect));
            var scrollRT = (RectTransform)scrollGO.transform;
            scrollRT.SetParent(host, false);
            scrollRT.anchorMin = Vector2.zero;
            scrollRT.anchorMax = Vector2.one;
            // 顶部留给标题、底部留给关闭按钮，位置固定，不需要测量宿主控件
            scrollRT.offsetMin = new Vector2(ListPaddingX, FooterHeight + ListPaddingY);
            scrollRT.offsetMax = new Vector2(-ListPaddingX, -(HeaderHeight + ListPaddingY));

            scrollRect = scrollGO.GetComponent<ScrollRect>();
            scrollRect.horizontal = false;
            scrollRect.vertical = true;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;
            scrollRect.scrollSensitivity = 30f;
            // 滚动时行会从指针底下溜走（uGUI 不会因此补发 enter/exit）⇒ 直接把备注浮层收掉，
            // 免得它停在原地指着另一行（§28.2）
            scrollRect.onValueChanged.AddListener(OnListScrolled);

            var viewportGO = new GameObject("aee_pagerViewport", typeof(RectTransform), typeof(RectMask2D));
            var viewportRT = (RectTransform)viewportGO.transform;
            viewportRT.SetParent(scrollRT, false);
            viewportRT.anchorMin = Vector2.zero;
            viewportRT.anchorMax = Vector2.one;
            viewportRT.offsetMin = Vector2.zero;
            viewportRT.offsetMax = Vector2.zero;
            scrollRect.viewport = viewportRT;

            var contentGO = new GameObject("aee_pagerContent", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            var contentRT = (RectTransform)contentGO.transform;
            contentRT.SetParent(viewportRT, false);
            contentRT.anchorMin = new Vector2(0f, 1f);
            contentRT.anchorMax = new Vector2(1f, 1f);
            contentRT.pivot = new Vector2(0.5f, 1f);
            contentRT.anchoredPosition = Vector2.zero;
            contentRT.sizeDelta = Vector2.zero;

            VerticalLayoutGroup layout = contentGO.GetComponent<VerticalLayoutGroup>();
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            layout.spacing = RowSpacing;
            layout.padding = new RectOffset(0, 0, 2, 2);

            ContentSizeFitter fitter = contentGO.GetComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scrollRect.content = contentRT;
            listContent = contentRT;
        }

        /// <summary>
        /// 顶部圆角：外框贴图只有**下方**有圆角、**顶边是开口的**（顶边框高度为 0，所以左右两条竖边
        /// 切片会一直画到矩形顶），这里把"底部的圆角带"垂直镜像到顶部补上圆角。三件事必须一起做：
        ///
        /// 1. **顶盖**：截"高度 = 下边框"的一条（宽度不变），以自身中心为轴翻转 ⇒ 贴图底部那对圆角
        ///    连同底横线镜到顶部；条带高度恰好等于下边框 ⇒ 贴图中间那两条竖边切片高度为 0，
        ///    顶盖不会在别处多画东西。
        /// 2. **主体**：把根 Image 的绘制挪到"弹窗矩形减去顶部圆角带"的矩形上（同样的贴图/倍率，
        ///    尺寸只差顶上一截）。关键就在这一截：贴图顶边框为 0 ⇒ 竖边切片会一直画到所属矩形的顶，
        ///    根 Image 的矩形是整幅弹窗，于是它的竖线**伸进了圆角带里**，与镜像过来的弧线交叉、
        ///    重合处还因两次绘制而更深（§19.1/§20.1）。主体矩形矮一截后，竖线正好停在圆角带下沿，
        ///    与镜像弧线的竖直段接上。根 Image 本体随后停用。
        /// 3. **填充补丁**：贴图正中心是半透明黑（`popup_border` 中心 = (0,0,0,142)），根 Image 停用后
        ///    顶部这一条会丢掉这层底色，所以用"贴图中心那一像素"做一个纯色 sprite 补上，
        ///    保证每一处仍只有一层填充（不多不少）。
        ///
        /// 三个对象都排在内容之下；找不到可用贴图/切片时退回"补一条顶边直线"。
        /// </summary>
        private static void BuildTopCap()
        {
            if (popupRoot == null)
                return;

            Image frame = FindFrameImage();
            if (frame == null || frame.sprite == null)
            {
                BuildStraightTopEdge();   // 找不到外框贴图就退回一条顶边直线，至少四边闭合
                return;
            }

            // 下边框在 UI 单位下的高度 = 根 Image 底部圆角带的高度（贴图像素 ÷ PPU 系数）
            float pixelsPerUnit = PixelsPerUnit(frame);
            float capHeight = pixelsPerUnit > 0f ? frame.sprite.border.y / pixelsPerUnit : 0f;
            // 退回直线的三种情况：外框不是九宫格（那整张图会被压进一条带里）、贴图没有下边框、
            // 圆角带高到无法只占顶部一条（弹窗最矮也有 MinPopupHeight）
            if (frame.type != Image.Type.Sliced || capHeight <= 1f || capHeight * 2f > MinPopupHeight)
            {
                BuildStraightTopEdge();
                return;
            }

            // ---- 填充补丁（最下，和主体不重叠）----
            // 只在原外框确实填了内部（fillCenter = true）时才补：不然会凭空多出一层半透明黑
            RectTransform fill = null;
            Image fillImage = null;
            if (frame.fillCenter)
            {
                frameFillSprite = null;
                try
                {
                    frameFillSprite = CreateFrameFillSprite(frame.sprite);
                }
                catch (Exception e)
                {
                    // 贴图取不到（不可读的图集等）就放弃补底色：顶部条带会透出后面的画面，
                    // 但边框本身照旧，总比整块外框建不出来好
                    Main.Logger?.Log("分页器列表：外框填充补丁创建失败，跳过底色: " + e.Message);
                }
                if (frameFillSprite != null)
                {
                    fill = CreateTopBand("aee_pagerFrameFill", capHeight);
                    fillImage = fill.GetComponent<Image>();
                    fillImage.sprite = frameFillSprite;
                    fillImage.type = Image.Type.Simple;
                    fillImage.color = frame.color;
                    fillImage.raycastTarget = false;
                }
            }

            // ---- 主体：根 Image 的绘制，矩形矮掉顶部一条 ----
            var bodyGO = new GameObject("aee_pagerFrameBody", typeof(RectTransform), typeof(Image));
            var body = (RectTransform)bodyGO.transform;
            body.SetParent(popupRoot.transform, false);
            body.anchorMin = Vector2.zero;
            body.anchorMax = Vector2.one;
            body.pivot = new Vector2(0.5f, 0.5f);
            body.offsetMin = Vector2.zero;
            body.offsetMax = new Vector2(0f, -capHeight);
            Image bodyImage = bodyGO.GetComponent<Image>();
            CopyFrameStyle(bodyImage, frame);   // 连 fillCenter 一起照抄：主体就是"根 Image 换了个矮矩形"

            // ---- 顶盖（最上）----
            RectTransform cap = CreateTopBand("aee_pagerFrameCap", capHeight);
            Image capImage = cap.GetComponent<Image>();
            CopyFrameStyle(capImage, frame);
            capImage.fillCenter = false;
            // 以带自身中心为轴镜向 ⇒ 贴图底部的圆角带翻上来正好落在弹窗顶部
            cap.localScale = new Vector3(1f, -1f, 1f);

            // ---- 根 Image 让位 ----
            // 原外框那层（半透明底 + 画到顶的边框）由主体/补丁/顶盖接管，本体必须停掉，否则顶部
            // 那条"伸进圆角带里的竖线"仍在。它原本还兼任弹窗的点击遮罩，所以按原值补一块透明挡板。
            bool frameBlockedClicks = frame.raycastTarget;
            frame.raycastTarget = false;
            frame.enabled = false;

            int sibling = 0;
            if (frameBlockedClicks)
            {
                var blockerGO = new GameObject("aee_pagerFrameBlock", typeof(RectTransform), typeof(Image));
                var blocker = (RectTransform)blockerGO.transform;
                blocker.SetParent(popupRoot.transform, false);
                blocker.anchorMin = Vector2.zero;
                blocker.anchorMax = Vector2.one;
                blocker.pivot = new Vector2(0.5f, 0.5f);
                blocker.offsetMin = Vector2.zero;
                blocker.offsetMax = Vector2.zero;
                Image blockerImage = blockerGO.GetComponent<Image>();
                blockerImage.color = new Color(0f, 0f, 0f, 0f);
                blockerImage.raycastTarget = true;
                blocker.SetSiblingIndex(sibling++);
                // §37：这块透明挡板接住"点在弹窗空白处"的点击（以前完全没痕迹，是诊断盲区）
                blockerGO.AddComponent<PagerBackgroundClickLog>();
            }
            body.SetSiblingIndex(sibling++);
            if (fill != null)
                fill.SetSiblingIndex(sibling++);
            cap.SetSiblingIndex(sibling);      // 画在补丁之上、内容之下

            frameBody = body;
            frameFillImage = fillImage;
            frameTopCap = cap;
            frameTopCapImage = capImage;
        }

        /// <summary>建一条"贴着弹窗顶部、高 height"的条带（宽度跟随弹窗），内容居中 pivot 便于再做镜像。</summary>
        private static RectTransform CreateTopBand(string name, float height)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            var rect = (RectTransform)go.transform;
            rect.SetParent(popupRoot.transform, false);
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            // pivot 必须居中：镜像/翻转都绕 pivot 做，pivot 偏到边上会把整条带挪到弹窗外面
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = new Vector2(0f, -height);
            rect.offsetMax = Vector2.zero;
            return rect;
        }

        /// <summary>照抄外框 Image 的绘制参数（贴图/九宫格/配色/倍率/内部填充），但不接管点击。</summary>
        private static void CopyFrameStyle(Image target, Image frame)
        {
            target.sprite = frame.sprite;
            target.type = frame.type;
            target.color = frame.color;
            target.pixelsPerUnitMultiplier = frame.pixelsPerUnitMultiplier;
            target.fillCenter = frame.fillCenter;
            target.raycastTarget = false;
            target.maskable = frame.maskable;
        }

        /// <summary>
        /// 用外框贴图**正中心那一像素**做一个 1×1 纯色 sprite —— 复刻它的内部填充色（半透明黑），
        /// 给顶部条带补底色用。只做 FullRect 网格（不需要读像素），用完在 <see cref="Reset"/> 里销毁。
        /// </summary>
        private static Sprite CreateFrameFillSprite(Sprite sprite)
        {
            Rect region = sprite.textureRect;
            var pixel = new Rect(
                region.x + Mathf.Floor(region.width * 0.5f),
                region.y + Mathf.Floor(region.height * 0.5f),
                1f, 1f);
            return Sprite.Create(sprite.texture, pixel, new Vector2(0.5f, 0.5f), sprite.pixelsPerUnit, 0,
                SpriteMeshType.FullRect);
        }

        /// <summary>兜底：找不到外框贴图时沿用上一版的"补一条顶边直线"。</summary>
        private static void BuildStraightTopEdge()
        {
            var go = new GameObject("aee_pagerFrameTop", typeof(RectTransform), typeof(Image));
            var rect = (RectTransform)go.transform;
            rect.SetParent(popupRoot.transform, false);
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.offsetMin = new Vector2(0f, -FallbackLineThickness);
            rect.offsetMax = Vector2.zero;

            Image image = go.GetComponent<Image>();
            image.sprite = null;
            image.color = new Color(1f, 1f, 1f, 0.6f);
            image.raycastTarget = false;
            frameTopCap = rect;
            frameTopCapImage = image;
            rect.SetAsFirstSibling();
        }

        /// <summary>弹窗自己的外框贴图（带 sprite、面积最大的那张 Image）。</summary>
        private static Image FindFrameImage()
        {
            if (popupRoot == null)
                return null;

            Image best = null;
            float bestArea = -1f;
            foreach (Image image in popupRoot.GetComponentsInChildren<Image>(true))
            {
                if (image == frameTopCapImage || image.sprite == null)
                    continue;
                RectTransform rect = image.rectTransform;
                if (rect == null)
                    continue;
                float area = Mathf.Abs(rect.rect.width * rect.rect.height);
                if (area > bestArea)
                {
                    bestArea = area;
                    best = image;
                }
            }
            return best;
        }

        /// <summary>精灵 PPU ÷（画布参考 PPU × 九宫格倍率）：贴图像素 → UI 单位的换算系数。</summary>
        private static float PixelsPerUnit(Image frame)
        {
            Sprite sprite = frame.sprite;
            if (sprite == null)
                return 0f;
            float referencePPU = 100f;
            Canvas canvas = frame.canvas;
            if (canvas != null && canvas.referencePixelsPerUnit > 0f)
                referencePPU = canvas.referencePixelsPerUnit;
            float multiplier = frame.pixelsPerUnitMultiplier;
            if (multiplier <= 0f)
                multiplier = 1f;
            return sprite.pixelsPerUnit / (referencePPU * multiplier);
        }

        /// <summary>把弹窗撑到刚好装下 min(行数, MaxVisibleRows) 行（多了在列表里滚动）。</summary>
        private static void ResizeHost(int rowCount)
        {
            if (popupRoot == null)
                return;
            var hostRT = (RectTransform)popupRoot.transform;
            int visible = Mathf.Clamp(rowCount, 1, MaxVisibleRows);
            float listHeight = visible * rowHeight + (visible - 1) * RowSpacing + 2f * ListPaddingY + 6f;
            float wanted = HeaderHeight + FooterHeight + listHeight;
            // 高度上限：行数多时若不夹取，弹窗会顶出画面（四边就又不闭合了）。列表本身可滚动，压扁不影响使用。
            Canvas canvas = ResolveCanvas();
            if (canvas != null)
            {
                var canvasRect = canvas.transform as RectTransform;
                if (canvasRect != null && canvasRect.rect.height > 1f)
                    wanted = Mathf.Min(wanted, canvasRect.rect.height - ScreenMargin);
            }
            // 高度下限：太矮时上下圆角会挤在一起，留出"标题 + 页脚 + 一行"的量
            wanted = Mathf.Max(wanted, MinPopupHeight);
            // 只改高度：宽度必须保持 popupRoot 自身的原始 sizeDelta（ShowWindow 之后它就是弹窗宽度）。
            // 宽度退化成 0/负数时兜底成默认宽，避免任何负宽把水平拉伸的子元素压没。
            float width = hostRT.sizeDelta.x;
            if (width < 1f)
                width = FallbackWidth;
            hostRT.sizeDelta = new Vector2(width, wanted);
        }

        /// <summary>显示弹窗窗口（照 MultiTrackHelper 的消息弹窗做法：先摘出去，再打开窗口，最后挂回）。</summary>
        private static void ShowWindow()
        {
            scnEditor editor = scnEditor.instance;
            if (editor == null || popupRoot == null)
                return;

            popupRoot.transform.SetParent(null, false);
            popupRoot.SetActive(true);
            editor.ShowPopup(true, (scnEditor.PopupType)233, false);

            // 原版 popupWindow 是“贴顶容器”（实测弹窗落在屏幕顶部、压住关卡名栏），所以不要挂回它，
            // 改成挂到 Canvas 下、锚定 Canvas 正中心；显示/隐藏的窗口动画仍由上面的 ShowPopup 负责。
            // 代价：挂到 Canvas 后原版的隐藏路径管不到我们，Close() 里要自己 SetActive(false)。
            Canvas canvas = ResolveCanvas();
            Transform parent = canvas != null ? canvas.transform : editor.popupWindow.transform;
            popupRoot.transform.SetParent(parent, false);

            var popupRect = (RectTransform)popupRoot.transform;
            popupRect.anchorMin = new Vector2(0.5f, 0.5f);
            popupRect.anchorMax = new Vector2(0.5f, 0.5f);
            popupRect.pivot = new Vector2(0.5f, 0.5f);
            popupRect.anchoredPosition = Vector2.zero;
            popupRect.localScale = Vector3.one;
            // 放到最后：盖住 popupPanel 的全屏遮罩，点击也优先落在弹窗上
            popupRoot.transform.SetAsLastSibling();

            LayoutList();
        }

        /// <summary>弹窗所在 Canvas（编辑器 UI 根）。找不到就退回 popupWindow。</summary>
        private static Canvas ResolveCanvas()
        {
            if (uiCanvas != null)
                return uiCanvas;
            scnEditor editor = scnEditor.instance;
            if (editor != null && editor.popupWindow != null)
                uiCanvas = editor.popupWindow.GetComponentInParent<Canvas>();
            if (uiCanvas == null && popupRoot != null)
                uiCanvas = popupRoot.GetComponentInParent<Canvas>();
            if (uiCanvas == null && Main.Logger != null)
                Main.Logger.Log("分页器直选列表：找不到 Canvas，弹窗退回 popupWindow 定位");
            return uiCanvas;
        }

        /// <summary>强制布局一遍并滚动到当前行（几何在 ResizeHost/BuildScrollArea 里已经固定）。</summary>
        private static void LayoutList()
        {
            if (scrollRect == null || listContent == null)
                return;

            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(listContent);
            Canvas.ForceUpdateCanvases();
            ScrollCurrentRowIntoView();
        }

        /// <summary>滚动到当前显示的那一行（列表较长时也能一眼看到"我现在在第几个"）。</summary>
        private static void ScrollCurrentRowIntoView()
        {
            if (currentRowOrder < 0 || scrollRect == null || listContent == null || scrollRect.viewport == null)
                return;

            float contentHeight = listContent.rect.height;
            float viewportHeight = scrollRect.viewport.rect.height;
            if (contentHeight <= viewportHeight || viewportHeight <= 1f)
            {
                listContent.anchoredPosition = Vector2.zero;
                return;
            }

            float rowStride = rowHeight + RowSpacing;
            float target = currentRowOrder * rowStride - (viewportHeight - rowHeight) * 0.5f;
            target = Mathf.Clamp(target, 0f, contentHeight - viewportHeight);
            listContent.anchoredPosition = new Vector2(0f, target);
        }

        private static TMP_Text PickText(GameObject host)
        {
            TMP_Text best = null;
            float bestHeight = -1f;
            foreach (TMP_Text text in host.GetComponentsInChildren<TMP_Text>(true))
            {
                float height = text.rectTransform != null ? text.rectTransform.rect.height : 0f;
                if (best == null || height > bestHeight)
                {
                    best = text;
                    bestHeight = height;
                }
            }
            return best;
        }

        private static Button PickButton(GameObject host)
        {
            Button best = null;
            float lowest = float.MaxValue;
            foreach (Button button in host.GetComponentsInChildren<Button>(true))
            {
                float y = button.transform.position.y;
                if (best == null || y < lowest)
                {
                    best = button;
                    lowest = y;
                }
            }
            return best;
        }

        private static string L(string key) => Main.Localizations?.GetValue(key) ?? key;
    }
}
