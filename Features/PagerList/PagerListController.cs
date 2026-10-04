using ADOFAI;
using ADOFAI.LevelEditor.Controls;
using ADOFAIEditorExtension.Features.DecoGrouping;
using ADOFAIEditorExtension.Utils;
using DG.Tweening;
using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ADOFAIEditorExtension.Features.PagerList
{
    /// <summary>
    /// 挂在分页器文本（与分页器根对象）上，左键点击时打开直选列表。
    ///
    /// 本文件里所有 MonoBehaviour / EventTrigger 入口都先看 `Main.IsEnabled`：在 UMM 里禁用模组、
    /// 而编辑器没能重载（用户在"未保存更改"提示里点了取消）时，Harmony 补丁已经全部撤掉，
    /// 但这些挂在场景对象上的组件还活着 —— 不拦的话点击/拖拽仍会跑进我们的逻辑（改关卡数据、开弹窗）。
    /// </summary>
    internal sealed class PagerClickTarget : MonoBehaviour
    {
        internal InspectorTab Tab;

        internal void OnPointerClick(BaseEventData eventData)
        {
            if (!Main.IsEnabled)
                return;                // 不消费：点击照原版冒泡
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
            if (!Main.IsEnabled
                || eventData == null
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
            if (!Main.IsEnabled || eventData == null || eventData.button != PointerEventData.InputButton.Left)
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

        /// <summary>
        /// 这一行"该有的"组名颜色：没有自定义组头色时就是原版白，有的话是按底色算出来的对比色
        /// （见 <see cref="DecoGroupState.ContrastTextColor"/>）。取消拖放高亮时按它还原 ——
        /// 早先写死回 <c>Color.white</c>，浅色组头取消高亮后组名（连同写进文本里的 ▼/▶）又变回看不清。
        /// </summary>
        internal Color OriginalLabelColor = Color.white;

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
            if (!Main.IsEnabled || eventData == null || eventData.button != PointerEventData.InputButton.Left || Owner == null)
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
            if (!Main.IsEnabled || eventData == null || eventData.button != PointerEventData.InputButton.Left)
                return;
            PagerListController.LogPopupBlankClick();
        }
    }

    /// <summary>
    /// 用户自己滚动列表（滚轮 / 拖动滚动条 / 惯性）时掐掉"滚到当前行"的动画。
    /// 挂在 ScrollRect 自己的 GameObject 上：uGUI 会把事件广播给该对象上的所有处理器组件，
    /// 所以 ScrollRect 照常滚动，这里只是顺手把动画停下来，不让它把手动滚动拽回去。
    /// </summary>
    internal sealed class PagerScrollInterrupt : MonoBehaviour, IScrollHandler, IBeginDragHandler
    {
        public void OnScroll(PointerEventData eventData)
        {
            PagerListController.StopScrollAnimation();
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            PagerListController.StopScrollAnimation();
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
            draggingIndex = -1;
            if (!Main.IsEnabled)
                return;                // 模组已禁用：不开始拖拽（OnDrag/OnEndDrag 随之全部早退）
            PagerRowClick click = GetComponent<PagerRowClick>();
            draggingIndex = click != null ? click.Index : -1;
            if (draggingIndex < 0)
                return;
            eventData?.Use();
            // 拖动期间不再关掉列表滚动：滚轮要照常滚列表（行是最内层的 IDragHandler，ScrollRect 抢不到这次拖拽）
            Vector2 position = eventData != null ? eventData.position : Vector2.zero;
            PagerListController.BeginRowDrag(draggingIndex, position);
            PagerListController.HideNoteTooltip();   // 顺手收起备注浮层，别让它跟着鼠标一路飘
            PagerListController.UpdateDropFeedback(position, draggingIndex);
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (draggingIndex < 0)
                return;
            eventData?.Use();
            Vector2 position = eventData != null ? eventData.position : Vector2.zero;
            PagerListController.UpdateRowDrag(position);
            PagerListController.UpdateDropFeedback(position, draggingIndex);
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            if (draggingIndex < 0)
                return;
            eventData?.Use();
            int index = draggingIndex;
            draggingIndex = -1;
            PagerListController.EndRowDrag();
            if (!Main.IsEnabled)
            {
                // 拖到一半模组被禁用：只做收尾（收掉落点反馈），不改任何关卡数据
                PagerListController.ClearDropFeedback();
                return;
            }

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
            PagerListController.EndRowDrag();
            PagerListController.ClearDropFeedback();
        }
    }

    /// <summary>
    /// §47 分页标签页的三种口径：
    ///  · All —— 当前选中那一块砖上的**全部**事件（数组顺序的平铺列表）；
    ///  · Type —— 今天的老口径：某一个事件类型在这一块砖上的全部事件；
    ///  · Group —— 合并的**「自定义分组」一页**：这一块砖上归入任一自定义**事件**分组
    ///             （<c>DecoGroupState.GroupSet.Event</c>，成员键 <c>aeeGroupEvent</c>）的全部事件，
    ///             按组头分桶显示（不再一个分组一页）。
    /// </summary>
    internal enum PagerTabKind
    {
        All,
        Type,
        Group
    }

    /// <summary>
    /// §47 标签页标识（**值相等**：要跨帧比较、要当字典键）。
    /// Type 口径只用 <see cref="EventType"/>；All 与 Group 口径两个字段都不用
    /// （Group 是合并的「自定义分组」一页，只有一个，<see cref="GroupIndex"/> 恒为 -1）。
    /// </summary>
    internal struct PagerTabKey : System.IEquatable<PagerTabKey>
    {
        internal PagerTabKind Kind;
        internal LevelEventType EventType;
        internal int GroupIndex;

        internal static PagerTabKey MakeAll() => new PagerTabKey { Kind = PagerTabKind.All, EventType = LevelEventType.None, GroupIndex = -1 };

        internal static PagerTabKey MakeType(LevelEventType type) => new PagerTabKey { Kind = PagerTabKind.Type, EventType = type, GroupIndex = -1 };

        internal static PagerTabKey MakeCustomGroups() => new PagerTabKey { Kind = PagerTabKind.Group, EventType = LevelEventType.None, GroupIndex = -1 };

        /// <summary>混排（非单一类型）口径：All / Group 都是 —— 行可能属于任意类型，下标与类型内下标不再等价。</summary>
        internal bool IsMixed => Kind != PagerTabKind.Type;

        public bool Equals(PagerTabKey other) => Kind == other.Kind && EventType == other.EventType && GroupIndex == other.GroupIndex;

        public override bool Equals(object obj) => obj is PagerTabKey other && Equals(other);

        public override int GetHashCode() => ((int)Kind * 397) ^ ((int)EventType * 17) ^ GroupIndex;

        public static bool operator ==(PagerTabKey a, PagerTabKey b) => a.Equals(b);

        public static bool operator !=(PagerTabKey a, PagerTabKey b) => !a.Equals(b);
    }

    /// <summary>
    /// §47 标签页的描述符（步骤 3 的标签条 UI 只读这些字段，不自己算内容）。
    /// <see cref="Icon"/> 只有 Type 口径有（<c>GCS.levelEventIcons[类型]</c>，取不到为 null）；
    /// <see cref="HasColor"/>/<see cref="Color"/> 目前没有页签会带色（Group 口径已合并成一页，
    /// 挂不上单个分组的组头色），字段保留给以后按页上色的口径。
    /// </summary>
    internal sealed class PagerTabInfo
    {
        internal PagerTabKey Key;
        internal string Title;
        internal Sprite Icon;
        internal bool HasColor;
        internal Color Color;
        internal int Count;
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
        // 列表滚到当前行的动画时长（点行不再瞬移，§47）。关窗/换页/开窗不滑，见 ScrollCurrentRowIntoView
        private const float ScrollAnimSeconds = 0.22f;
        private const float ScrollSnapDistance = 0.5f;   // 差这么点以内不值得动
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
        // 折叠标记：原先的 ▾/▸（U+25BE/U+25B8）游戏的 CJK 字体（SourceHanSans）没有这些码位，
        // TMP 缺字不报错、直接画方框。首选实心三角 ▼/▶（U+25BC/U+25B6），建组头时再按这行实际用的字体
        // 自检一次（见 ResolveHeaderMarks）：候选表里**只要 HasCharacter 认了就一定画得出来**，
        // 所以多列几个同方向的备选码位（空心三角 / 媒体符号）不会画出方框，只会多一次极便宜的查询。
        // 末位一定是 ASCII（任何字体都有）：它同时是"没候选可用"的标记（见 PickHeaderMark 的 resolved）。
        // 真机曾出现"展开画成 ASCII 的 v、折叠却是 ▶"：根因不在字体缺字，而在自检时
        // HasCharacter 的第三参传成了 false（= 不按需补进动态图集），见 PickHeaderMark。
        private static readonly string[] ExpandedMarkCandidates = { "\u25BC", "\u25BE", "\u23F7", "\u25BD", "v" };   // ▼ ▾ ⏷ ▽ v
        private static readonly string[] CollapsedMarkCandidates = { "\u25B6", "\u25B8", "\u23F5", "\u25B7", ">" };  // ▶ ▸ ⏵ ▷ >
        private static string ExpandedMark = ExpandedMarkCandidates[0];
        private static string CollapsedMark = CollapsedMarkCandidates[0];
        private static TMP_FontAsset resolvedMarkFont;
        private static bool headerMarksFellBack;            // 上一次自检退到了 ASCII 兜底 ⇒ 下次建行再试一遍
        private static bool headerMarkDetectErrorLogged;    // 自检本身抛异常只记一次
        private const float HeaderArrowWidth = 22f;
        // 组名（含 ▼/▶ 标记）距行左沿的内缩：▼ 的字形左上角比 "v" 更靠左，贴着 x=0 起笔会探出组头色块的圆角
        private const float HeaderLabelLeftInset = 8f;
        private const float HeaderArrowZoneMax = 160f;      // 箭头点击区上限：再宽就会把组名区挤没了

        private static GameObject popupRoot;
        private static PagerWindowInteraction windowInteraction;
        private static TMP_Text titleText;
        private static Button closeButton;
        private static ScrollRect scrollRect;
        private static RectTransform listContent;
        // 滚动动画：任何时刻只留一条。重建列表、用户自己滚动都要先掐掉它，否则会和手动滚动打架
        private static Tween scrollTween;
        private static bool snapNextScroll;
        // 点组头（折叠箭头 / 组名）只换列表内容，不该把视野拉到当前选中的事件行上；
        // 点事件行照旧滚动。一次性标记，ScrollCurrentRowIntoView 取用后即清。
        private static bool suppressScrollToRow;
        private static GameObject rowTemplate;
        private static Canvas uiCanvas;
        private static RectTransform noteTooltip;   // 备注悬停浮层（深色底 + 白字，不吃射线）
        private static TMP_Text noteTooltipText;
        private static RectTransform frameTopCap;   // 顶部圆角盖（外框贴图底部圆角带的镜像）
        private static Image frameTopCapImage;
        private static RectTransform frameBody;     // 外框本体（根 Image 的替身，矩形矮掉顶部圆角带）
        private static Image frameFillImage;        // 顶部条带的底色补丁（贴图中心那一像素）
        private static Sprite frameFillSprite;      // 上面那块补丁用的 1×1 sprite（Sprite.Create 出来的，要销毁）

        // §47 弹窗左侧的标签条（步骤 3）：只读 Tabs / ActiveTab；页签表变了由 OnTabsChanged 驱动重建
        private static PagerTabStrip tabStrip;
        private static bool tabStripHooked;       // TabsChanged 是静态事件，只挂一次，别每开一次窗就多一个处理器

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

        // §47 分页标签页（数据层，步骤 1 只建模型不建 UI）：
        //  tabs = 当前这块砖算出来的标签页（永不为空，除非砖已经不满足打开条件）；activeTabKey = 正在显示的那一页。
        private static readonly List<PagerTabInfo> tabs = new List<PagerTabInfo>();
        private static PagerTabKey activeTabKey = PagerTabKey.MakeAll();
        private static List<LevelEvent> floorEvents;    // 当前砖的全部事件（数组顺序）= All 标签页的内容，也是其它口径的取材
        private static string tabsSignature;           // 只在标签页表**真的**变了时才发 TabsChanged（重算很频繁）

        /// <summary>
        /// §47 当前事件在**显示列表**（<see cref="currentStack"/>）里的下标 —— 混排标签页里它不等于类型内下标，
        /// 所以传给原生代码的下标必须由 <see cref="ShowEventInPanel"/> 现算。Type 口径下两者恒等（与今天一致）。
        /// </summary>
        private static int displayedIndex = -1;

        /// <summary>
        /// 打开被原版弹窗动画挡回去的那次点击（见 <see cref="ShowWindow"/>）：原版 `ShowPopup(true, …)` 在
        /// `popupIsAnimating` 时直接 return（IL 核过），最常见的就是"刚点 OK 关窗、0.5 秒的收起动画还没播完
        /// 就又点分页器"。记下标签页，由 <see cref="Tick"/> 等动画结束后补开一次；超过期限就作废。
        /// </summary>
        private static InspectorTab pendingOpenTab;
        /// <summary>§47 被动画挡回去的那一次想停在哪一页（决定补开时走哪个入口的路）。</summary>
        private static PagerTabKey pendingOpenKey = PagerTabKey.MakeAll();
        private static float pendingOpenDeadline = -1f;
        private const float PendingOpenSeconds = 1.5f;
        private static bool openAbortedByAnimation;

        /// <summary>当前显示的事件（跨重建保持；行下标会因分组/排序变化，所以记对象）。</summary>
        private static LevelEvent currentEvent;

        /// <summary>
        /// 多选批量态（§34）：fake 事件**关窗也不丢**，右侧面板持续显示批量视图；`fakeEvent.realEvents`
        /// 就是"批量覆盖的事件集合"（退出判定、重开恢复高亮都用它），`fakeTab` 记它属于哪个标签页。
        /// 退出（清 fake + 面板回单事件）的条件见 `ExitMultiSelect` 的调用点。
        /// </summary>
        private static InspectorTab fakeTab;

        /// <summary>
        /// §47 批量态属于哪一个**分页标签页口径**：混排标签页里 <see cref="openTab"/> 会随当前事件换到别的
        /// 原生类型标签页上，只按 InspectorTab 对象比会把自己的批量态认成"别的标签页的"。
        /// </summary>
        private static PagerTabKey fakeTabKey = PagerTabKey.MakeAll();

        /// <summary>
        /// §47 跨类型多选的那条提示是不是已经提过了：同一批混排选中只说一次，
        /// 连续 ctrl 加选别刷屏（重新回到单选 / 建起批量 / 关窗时重新武装）。
        /// </summary>
        private static bool mixedToastAnnounced;

        /// <summary>
        /// 我们**自己**正在把 fake 挂到面板上（`BindFakeToPanel` 内部的 ShowInspector 会连带触发
        /// 一次 `ShowPanel` 的后置）。这段时间不能按"面板选中的事件不在批量里"判定退出 ——
        /// 那一刻 `selectedEvent` 恰恰还是某个真实事件，退出来就是自己把自己的多选掐掉（§39 H2）。
        /// 同一段时间里**写回也一律不做**：`SetProperties(fake)` 刷控件时有的控件会顺手"保存"一次
        /// （见 <see cref="BindFakeToPanel"/>），那不是用户编辑。
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
        private static TMP_Text countLabel;            // 多选整批拖动时方块旁的 "×N"
        private static bool dropObjectsResolved;
        private static bool dropFeedbackLogged;

        // 正在拖的行（-1 = 没在拖）与指针最后一次出现的屏幕位置：拖动期间滚轮会挪内容而指针可能不动，
        // 落点得按"光标底下现在是谁"重算，所以这两份状态要留在控制器里
        private static int rowDragIndex = -1;
        private static Vector2 rowDragPointer;

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
            internal int OriginalIndex;     // 事件行 = 该事件在**显示列表**（currentStack）里的下标
            internal LevelEvent Event;      // §47 事件行指向的那个事件（混排页里"当前行"只能按对象比，不能按下标）
            internal LevelEventType Type;   // §47 事件自己的类型（混排页里每行的标签要用它，不再用整页的类型）
            internal string Tag;            // 事件自己的分组标签（GroupTagKeyOf 那个键的值；没有该属性 = ""）
            internal bool UseEventTag;      // Tag 读的是 eventTag（行上用 eventTag= 前缀）
            internal string TargetTag;      // 砖上事件的 "tag"（MoveDecorations/SetText 等的**目标装饰选择器**，只显示、不参与分组）
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

        // ------------------------------------------------------------------ §47 「全部事件」入口（原生 inspector 头部按钮）

        /// <summary>
        /// 「这块砖上的一个事件」的判定（= <see cref="BuildTabs"/> 的取材口径）。
        /// 入口按钮的可见性计数与 <c>BuildTabs</c> 共用这一条，别再在别处抄一份「floor 相同且不是设置型」。
        /// </summary>
        private static bool IsFloorEventOf(LevelEvent evt, int floor)
        {
            return evt != null && evt.floor == floor && !evt.eventType.IsSetting();
        }

        /// <summary>入口的两道共用门槛（模组开 + 功能开 + 已经在编辑器里）。</summary>
        private static bool EntryAllowed()
        {
            return Main.IsEnabled && Main.IsPagerListEnabled && scnEditor.instance != null;
        }

        /// <summary>
        /// 「全部事件」入口**点击那一刻**的权威判定：单选砖 + 整块砖 &gt;= 2 个非设置型事件（不限类型）。
        /// 列表直接复用 §47 的 <see cref="RecomputeTabs"/> + <see cref="floorEvents"/>，不另起一份枚举。
        /// 这条路会发 <see cref="TabsChanged"/>，所以只用在点击/补开上；按钮可见性走轻量的
        /// <see cref="EntryShouldShow"/>。
        /// </summary>
        internal static bool CanOpenAll(out List<LevelEvent> all)
        {
            all = null;
            if (!EntryAllowed() || !scnEditor.instance.SelectionIsSingle())
                return false;
            if (!RecomputeTabs(activeTabKey))
                return false;
            all = floorEvents;
            return all != null && all.Count >= 2;
        }

        // §47 可见性计数的缓存键：（选中的砖，关卡事件总数）。增删事件必然改总数、换砖必然改 seqID，
        // 而改属性不动总数也不动门槛 ⇒ 这两个值没变就意味着结果不可能变（省掉高频钩子上的全表扫描）。
        private static int entryCacheFloor = int.MinValue;
        private static int entryCacheTotal = -1;
        private static bool entryCacheResult;

        /// <summary>
        /// 入口按钮的缓存键（选中的砖 + 关卡事件总数），两者都是 O(1) —— 给 <see cref="Tick"/> 用：
        /// 键没变就不扫描、不重排。取不到键（没在编辑器里 / 没数据）返回 false。
        /// </summary>
        private static bool TryEntryCacheKey(out int floor, out int total)
        {
            floor = int.MinValue;
            total = -1;
            scnEditor editor = scnEditor.instance;
            if (editor == null || editor.levelData == null)
                return false;
            try
            {
                if (!editor.SelectionIsSingle())
                    return false;
                floor = editor.selectedFloors[0].seqID;
                List<LevelEvent> events = editor.levelData.levelEvents as List<LevelEvent>;
                total = events != null ? events.Count : -1;
            }
            catch { return false; }
            return true;
        }

        /// <summary>
        /// 入口按钮该不该显示（轻量判定：数到 2 就早退，不建列表、不发 TabsChanged ——
        /// 它在 <c>ShowPanel</c>／换砖这些钩子上跑，框选时一帧能来几十次）。
        /// </summary>
        private static bool EntryShouldShow(bool forceRescan)
        {
            if (!EntryAllowed())
                return false;
            if (!TryEntryCacheKey(out int floor, out int total))
                return false;
            if (!forceRescan && floor == entryCacheFloor && total == entryCacheTotal)
                return entryCacheResult;

            List<LevelEvent> events = scnEditor.instance.levelData.levelEvents as List<LevelEvent>;
            entryCacheFloor = floor;
            entryCacheTotal = total;
            entryCacheResult = HasEnoughFloorEvents(events, floor, 2);
            return entryCacheResult;
        }

        /// <summary>§47 这块砖上够不够 <paramref name="atLeast"/> 个事件（早退，不建列表、不发通知）。</summary>
        private static bool HasEnoughFloorEvents(List<LevelEvent> all, int floor, int atLeast)
        {
            if (all == null)
                return false;
            int count = 0;
            for (int i = 0; i < all.Count; i++)
            {
                if (!IsFloorEventOf(all[i], floor))
                    continue;
                if (++count >= atLeast)
                    return true;
            }
            return false;
        }

        /// <summary>刷新头部入口按钮（建一次 + 可见性 + 对齐 + 高亮）；异常一律吞掉：它在原版的布局路径上。</summary>
        private static void RefreshEntryButton(bool forceRescan)
        {
            try
            {
                if (!EntryAllowed())
                {
                    PagerEntryButton.Detach();     // 模组/功能被关掉而编辑器没重载：拆掉，别留假入口
                    entryCacheFloor = int.MinValue;
                    return;
                }
                scnEditor editor = scnEditor.instance;
                InspectorPanel panel = editor != null ? editor.levelEventsPanel : null;
                if (panel == null)
                    return;
                PagerEntryButton.Attach(panel);
                PagerEntryButton.Refresh(EntryShouldShow(forceRescan));   // §47 只做可见性/对齐；高亮态已改成悬停染色
            }
            catch (Exception e)
            {
                Main.Logger?.Log("刷新分页器入口按钮失败（已吞掉）: " + e.Message);
            }
        }

        /// <summary>
        /// 原生事件面板刚为某块砖重排完标签页（<c>InspectorPanel.ShowTabsForFloor</c> 后置）：
        /// 头部那两个按钮可能挪了位置，入口要重新对齐、门槛要重数。
        /// </summary>
        internal static void OnFloorPanelRelaidOut()
        {
            entryCacheFloor = int.MinValue;
            RefreshEntryButton(true);
        }

        /// <summary>
        /// 入口按钮被点。没开窗 ⇒ 从 All 页打开；**已经开着** ⇒ 切到 All 页，
        /// 而已经在 All 页了 ⇒ 关窗（按钮当开关用，与它"进入/退出这一页"的语义一致）。
        /// </summary>
        internal static void OnEntryClick()
        {
            if (!EntryAllowed())
                return;
            if (isOpen)
            {
                if (activeTabKey.Kind == PagerTabKind.All)
                    Close();
                else
                {
                    SwitchTab(PagerTabKey.MakeAll());
                    SyncTabStrip();
                }
                RefreshEntryButton(true);
                return;
            }
            if (!CanOpenAll(out List<LevelEvent> all))
            {
                entryCacheFloor = int.MinValue;    // 门槛已经变了（别处删了事件）：让按钮当场收起
                RefreshEntryButton(true);
                return;
            }
            OpenAll(all);
        }

        /// <summary>
        /// 从「全部事件」入口打开：停在 All 页，起点事件 = 原生面板当前显示的那个
        /// （不在这块砖上 / 是设置型 / 是批量 fake ⇒ 取 All 列表第一个）。
        /// <c>openTab</c> 用那个事件类型对应的**原生标签页** —— 它是驱动右侧面板的把手
        /// （<see cref="ReloadRows"/> 没有它就整个不做事），拿不到就放弃打开，不留半个开着的窗。
        /// </summary>
        private static void OpenAll(List<LevelEvent> all)
        {
            if (all == null || all.Count < 2)
                return;
            scnEditor editor = scnEditor.instance;
            InspectorPanel panel = editor != null ? editor.levelEventsPanel : null;
            if (panel == null)
                return;

            LevelEvent start = panel.selectedEvent;
            if (start == null || start.isFake || all.IndexOf(start) < 0)
                start = all[0];
            InspectorTab tab = null;
            try
            {
                tab = panel.GetTabForEventType(start.eventType);
            }
            catch (Exception e)
            {
                Main.Logger?.Log("分页器入口：取原生标签页失败 " + e.Message);
            }
            if (tab == null)
            {
                Main.Logger?.Log("分页器入口：该事件类型没有原生标签页，放弃打开");
                return;
            }
            OpenWith(tab, all, PagerTabKey.MakeAll(), all.IndexOf(start), true);
        }

        // ------------------------------------------------------------------ §47 分页标签页（数据层）

        /// <summary>标签页表（步骤 3 的标签条只读它；顺序永远是 All → 各类型 → 各自定义分组）。</summary>
        internal static IReadOnlyList<PagerTabInfo> Tabs => tabs;

        /// <summary>当前显示的那一页。</summary>
        internal static PagerTabKey ActiveTab => activeTabKey;

        /// <summary>
        /// 当前这块砖上的全部事件（数组顺序，已滤掉设置型）= All 页的内容；不满足分页条件时为 null。
        /// 步骤 3 的标签条用它算"All 页有几个 / 要不要整条收起"，别的地方要列表请走
        /// <see cref="TryGetActiveList"/>（那一页才是用户看着的东西）。
        /// </summary>
        internal static IReadOnlyList<LevelEvent> FloorEvents => floorEvents;

        /// <summary>
        /// 标签页表**内容**变了才发一次（重算很频繁 —— 每次刷新列表都要算一遍，签名一样就不吵步骤 3 的 UI）。
        /// 处理器里的异常一律吞掉：这个通知挂在关窗/刷新路径上，抛出去会把整条刷新链打断。
        /// </summary>
        internal static event Action TabsChanged;

        /// <summary>标签页 → 这一页要显示的事件列表（与 <see cref="tabs"/> 同批重建，不会脱拍）。</summary>
        private static readonly Dictionary<PagerTabKey, List<LevelEvent>> tabLists = new Dictionary<PagerTabKey, List<LevelEvent>>();

        /// <summary>
        /// 数据可能变了之后的统一收尾（增删事件 / 改分组 / 换砖 / 关窗）：重算标签页表，
        /// 变了就通知步骤 3。重算失败（不再是单选砖等）时 <see cref="BuildTabs"/> 会把表清成空的，
        /// 所以这里不用另外处理"没有标签页"的状态。
        /// </summary>
        internal static void RefreshTabs()
        {
            entryCacheFloor = int.MinValue;        // §47 数据可能变了（增删/改分组/撤销）：入口按钮的计数缓存作废
            RecomputeTabs(activeTabKey);
            RefreshEntryButton(true);
        }

        /// <summary>重算 + 只在表真变了时发通知（<see cref="BuildTabs"/> 才是真正干活的）。</summary>
        private static bool RecomputeTabs(PagerTabKey preferred)
        {
            bool ok = BuildTabs(preferred);
            string signature = SignatureOfTabs();
            if (!string.Equals(signature, tabsSignature, StringComparison.Ordinal))
            {
                tabsSignature = signature;
                PublishTabsChanged();
            }
            return ok;
        }

        private static void PublishTabsChanged()
        {
            Action handler = TabsChanged;
            if (handler == null)
                return;
            try
            {
                handler();
            }
            catch (Exception e)
            {
                Main.Logger?.Log("分页标签页变更通知失败（已吞掉）: " + e.Message);
            }
        }

        private static string SignatureOfTabs()
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < tabs.Count; i++)
            {
                PagerTabInfo info = tabs[i];
                sb.Append((int)info.Key.Kind).Append('/').Append((int)info.Key.EventType)
                    .Append('/').Append(info.Key.GroupIndex)
                    .Append('/').Append(info.Count).Append('/').Append(info.Title).Append(';');
            }
            return sb.ToString();
        }

        /// <summary>
        /// 按当前选中的那一块砖重算标签页表，并把 <paramref name="preferred"/> 落到有效的一页
        /// （那一页已经没了 ⇒ 退回 All）。返回 false = 这块砖不满足分页器条件。
        ///
        /// 取材口径（**与原生同源，别自己发明**）：
        ///  · 砖 = <c>editor.selectedFloors[0].seqID</c>（先过 <c>SelectionIsSingle()</c> 这道门）；
        ///  · 事件 = <c>levelData.levelEvents</c>（= <c>editor.events</c>）里 floor 相同、且**不是设置型**的那些，
        ///    按数组顺序 —— 原生 <c>GetFloorEvents</c> 对 <c>IsSetting()</c> 的类型直接返回 null
        ///    （本模组把 812 = 我们的自定义事件类型也 patch 成设置型，所以它同样进不来），
        ///    所以 Type 页拿到的列表与今天 <c>GetSelectedFloorEvents</c> 的结果逐元素一致；
        ///  · 门槛：<c>SelectionIsSingle() && 这一砖全部事件 >= 2</c>（步骤 2 换打开条件用的就是这条）；
        ///  · <c>EditorConstants.soloTypes</c>（Twirl / Checkpoint / Bookmark 这些"每砖至多一个"的类型）：
        ///    **All 页里保留**（它们确实是这块砖上的事件，用户要能看见、能排序），
        ///    但**不单独建 Type 页** —— 原生分页器对它们连箭头都不给（<c>InspectorTab.SetSelected</c>），
        ///    一页只有一个事件也没有分页的意义。
        /// 类型顺序取原生 inspector 标签页从上到下的顺序（<c>levelEventsPanel.tabs</c>），
        /// 没有标签页的类型按**首次出现顺序**补在后面（读不到标签条时全部按首次出现）。
        /// 一页都不空：事件为空就不建这一页，All 永远在第一。
        /// </summary>
        private static bool BuildTabs(PagerTabKey preferred)
        {
            tabs.Clear();
            tabLists.Clear();
            floorEvents = null;

            scnEditor editor = scnEditor.instance;
            if (editor == null || !Main.IsPagerListEnabled || !editor.SelectionIsSingle())
                return false;

            int floor;
            try
            {
                floor = editor.selectedFloors[0].seqID;
            }
            catch
            {
                return false;
            }

            List<LevelEvent> all = editor.levelData != null ? editor.levelData.levelEvents as List<LevelEvent> : null;
            if (all == null || all.Count == 0)
                return false;

            var flat = new List<LevelEvent>();
            var byType = new Dictionary<LevelEventType, List<LevelEvent>>();
            var firstSeen = new List<LevelEventType>();
            for (int i = 0; i < all.Count; i++)
            {
                LevelEvent evt = all[i];
                if (!IsFloorEventOf(evt, floor))
                    continue;                        // §47 判定见 IsFloorEventOf（与入口按钮的计数同源）
                flat.Add(evt);
                if (!byType.TryGetValue(evt.eventType, out List<LevelEvent> bucket))
                {
                    bucket = new List<LevelEvent>();
                    byType[evt.eventType] = bucket;
                    firstSeen.Add(evt.eventType);
                }
                bucket.Add(evt);
            }

            if (flat.Count < 2)
                return false;                      // 这块砖上就 1 个事件：没什么可分页的（与今天一致）

            floorEvents = flat;
            AddTab(PagerTabKey.MakeAll(), flat, L("aee.pager.tab.all"), null, false, default(Color));

            // —— Type 页：顺序照原生标签条
            var ordered = new List<LevelEventType>(firstSeen.Count);
            try
            {
                InspectorPanel panel = editor.levelEventsPanel;
                if (panel != null && panel.tabs != null)
                {
                    foreach (Transform tabTransform in panel.tabs)
                    {
                        InspectorTab nativeTab = tabTransform != null && tabTransform.gameObject != null
                            ? tabTransform.gameObject.GetComponent<InspectorTab>()
                            : null;
                        if (nativeTab == null || !byType.ContainsKey(nativeTab.levelEventType))
                            continue;
                        if (!ordered.Contains(nativeTab.levelEventType))
                            ordered.Add(nativeTab.levelEventType);
                    }
                }
            }
            catch { }
            for (int i = 0; i < firstSeen.Count; i++)
                if (!ordered.Contains(firstSeen[i]))
                    ordered.Add(firstSeen[i]);

            for (int i = 0; i < ordered.Count; i++)
            {
                LevelEventType type = ordered[i];
                // 2.9.8 的 soloTypes 是 LevelEventType[]（v3 是 HashSet），不能用 Contains
                if (Array.IndexOf(EditorConstants.soloTypes, type) >= 0)
                    continue;                        // §47 solo 类型只在 All 页里出现，理由见上面的注释
                List<LevelEvent> bucket = byType[type];
                Sprite icon = null;
                try
                {
                    if (GCS.levelEventIcons != null)
                        GCS.levelEventIcons.TryGetValue(type, out icon);
                }
                catch { }
                AddTab(PagerTabKey.MakeType(type), bucket, EventTypeLabel(type), icon, false, default(Color));
            }

            // —— 「自定义分组」页：合并成一页，只装归入任一自定义分组的事件（数组顺序）。
            // 归属判定走 FlatGroupKey —— 它与 BuildSlots 的分桶规则逐条对齐，键以 custom: 开头
            // 就是"进了某个自定义组头"；这一页一个成员都没有就不建（组头建没建在 BuildSlots 里判）。
            List<(string Name, string Tag)> customGroups = DecoGroupState.ReadCustomGroups(DecoGroupState.GroupSet.Event);
            bool customOnly = Main.AutoGroupMode == AutoGroupMode.Custom;
            int fallback = DecoGroupState.FirstFallbackGroupIndex(customGroups);
            var customMembers = new List<LevelEvent>();
            for (int i = 0; i < flat.Count; i++)
                if (FlatGroupKey(flat[i], customGroups, customOnly, fallback)
                        .StartsWith("custom:", StringComparison.Ordinal))     // 与 PagerGroupKey 同一格式
                    customMembers.Add(flat[i]);
            if (customMembers.Count > 0)
                AddTab(PagerTabKey.MakeCustomGroups(), customMembers, L("aee.pager.tab.custom"), null, false, default(Color));

            activeTabKey = tabLists.ContainsKey(preferred) ? preferred : PagerTabKey.MakeAll();
            return true;
        }

        private static void AddTab(PagerTabKey key, List<LevelEvent> list, string title, Sprite icon, bool hasColor, Color color)
        {
            tabs.Add(new PagerTabInfo
            {
                Key = key,
                Title = title,
                Icon = icon,
                HasColor = hasColor,
                Color = color,
                Count = list != null ? list.Count : 0
            });
            tabLists[key] = list;
        }

        /// <summary>这个自定义分组在 <see cref="BuildSlots"/> 里会不会真的建出一个桶（没名字也没 tag 的分组不建）。</summary>
        private static bool HasGroupBucket(List<(string Name, string Tag)> customGroups, int index)
        {
            if (index < 0 || index >= customGroups.Count)
                return false;
            return customGroups[index].Tag.Length > 0 || !string.IsNullOrEmpty(customGroups[index].Name);
        }

        /// <summary>
        /// 单个事件归哪个分组桶（<see cref="BuildSlots"/> 的分桶规则）：手动归属 → 组 tag 精确匹配 →
        /// 自定义模式下的兜底组，都不中才是 tag 桶/未分组（键以 <c>tag:</c> 开头）。
        /// 四条判定与 <see cref="BuildSlots"/> 的分桶规则**逐条对齐**，否则「自定义分组」页与 All 页
        /// 里的组头会数不上。**改 BuildSlots 的分桶时必须同步改这里。**
        /// </summary>
        private static string FlatGroupKey(LevelEvent evt, List<(string Name, string Tag)> customGroups, bool customOnly, int fallback)
        {
            int manualExcluded = customOnly ? fallback : -1;
            int manual = DecoGroupState.GetManualGroup(evt, DecoGroupState.GroupSet.Event);
            if (manual >= 0 && manual < customGroups.Count && manual != manualExcluded
                && customGroups[manual].Tag.Length == 0 && HasGroupBucket(customGroups, manual))
                return PagerGroupKey(manual);

            string key = GroupTagOf(evt);
            if (key.Length > 0)
            {
                for (int g = 0; g < customGroups.Count; g++)
                {
                    if (customGroups[g].Tag.Length > 0 && string.Equals(customGroups[g].Tag, key, StringComparison.Ordinal)
                        && HasGroupBucket(customGroups, g))
                        return PagerGroupKey(g);
                }
            }
            if (customOnly && fallback >= 0 && HasGroupBucket(customGroups, fallback))
                return PagerGroupKey(fallback);
            return key.Length == 0 ? "tag:" : "tag:" + key;
        }

        /// <summary>
        /// §47 取**当前标签页口径**下的显示列表 —— 控制器里凡是过去用
        /// <c>CanOpen(openTab, out stack)</c> 或 <c>GetSelectedFloorEvents(openTab.levelEventType)</c>
        /// 现推列表的地方（刷新、拖动、撤销重做、剪切粘贴、快捷键、退出判定…）都改走这里。
        ///
        /// 返回 false ⇒ 这块砖已经不满足分页器条件（不再是单选砖 / 全砖事件 &lt; 2）
        /// ⇒ 调用方按今天的方式关窗。
        /// 当前这一页空了/没了 ⇒ 退回 All 页（<see cref="BuildTabs"/> 里就已经退回去了）。
        ///
        /// **不**在这里换页（§47）：显示下标、选中集、批量态记的都是"当前这一页"的下标域，
        /// 一次静默换页会把高亮落到别的事件上；换页只走 <see cref="SwitchTab"/>
        /// 与 <see cref="BuildTabs"/> 那条"当前页整页没了才退回 All"的显式路径。
        /// </summary>
        internal static bool TryGetActiveList(out List<LevelEvent> list)
        {
            list = null;
            if (!RecomputeTabs(activeTabKey))
                return false;
            if (!tabLists.TryGetValue(activeTabKey, out list) || list == null || list.Count == 0)
            {
                list = null;
                return false;
            }
            // §47 老的"这一型只剩 1 个事件就关窗"作废：新模型的打开条件是**整块砖 >= 2 个事件**
            //（不满足时上面 RecomputeTabs 已经返回 false 了），某一型缩到 1 个不代表没得分页 ——
            // 原版分页器这时不显示箭头，但我们的列表本身还有"看别的同型事件/排序"的用处。
            return true;
        }

        /// <summary>当前口径是不是混排（All / Group）：行可能是任意类型，显示下标 ≠ 类型内下标。</summary>
        private static bool IsMixedTab => activeTabKey.IsMixed;

        /// <summary>
        /// §47 折叠状态的键 = 标签页 + 分组桶键。桶键（<c>custom:N</c> / <c>tag:X</c>）在不同页会重名
        /// （All 页和某一型页里都有同一个自定义组），不带页前缀就会跨页串折叠状态；
        /// 带前缀后换页不需要清 <see cref="collapsedGroups"/>，各页各自记住自己的样子。
        /// </summary>
        private static string CollapsedKey(string bucketKey)
        {
            return activeTabKey.Kind + "/" + (int)activeTabKey.EventType + "/" + activeTabKey.GroupIndex
                + "|" + bucketKey;
        }

        /// <summary>当前这一页里有没有这个类型的事件（混排页的"面板切换不算换标签页"判定用）。</summary>
        private static bool ActiveTabHasType(LevelEventType type)
        {
            if (!tabLists.TryGetValue(activeTabKey, out List<LevelEvent> list))
                return false;
            for (int i = 0; i < list.Count; i++)
                if (list[i] != null && list[i].eventType == type)
                    return true;
            return false;
        }

        /// <summary>
        /// §47 把右侧属性面板（以及原生分页器）指到某个**具体事件**上 ——
        /// 凡是过去"显示下标直接喂给原生 <c>ShowPanel(类型, 下标)</c> + 写 <c>tab.eventIndex</c>"的地方都改成调它。
        ///
        /// 混排标签页里显示下标与类型内下标不是一回事，所以：
        ///  ① 类型内下标现算（<see cref="IndexOfInPanelStack"/>：原生 ShowPanel 内部也是再取一次
        ///     <c>GetSelectedFloorEvents(类型)</c> 来选事件的，见 InspectorPanel.cs:403）；
        ///  ② 把 <see cref="openTab"/> 重指向该类型的原生标签页并写它的 <c>eventIndex</c>
        ///     （原生 ShowPanel 自己也会写同一个标签页，见 InspectorPanel.cs:431 —— 两边一致才不会
        ///      "行高亮与分页器 1/3 文本打架"）；
        ///  ③ 先 <see cref="SuppressAutoClose"/> 再 ShowPanel（否则"面板切换就关窗"的补丁立刻把自己关了）。
        ///
        /// <c>editor.cacheSelectedEventIndex</c> 非 0 时原生 ShowPanel 会**覆盖**我们传的下标
        /// （InspectorPanel.cs:219，只在"试玩退回编辑器"那段时间非 0，退回流程里会被清 0）。
        /// 今天传类型内下标的地方没有管它（Type 口径下被覆盖也只是停在同一类型的旧位置，看不出来）；
        /// 混排口径下会把面板拽到另一个事件上、和行高亮直接矛盾 ⇒ 这里在调用前清 0，
        /// 让"用户点的那一行"说话（与今天点行的实际效果一致）。
        /// </summary>
        private static void ShowEventInPanel(LevelEvent evt)
        {
            if (evt == null)
                return;
            scnEditor editor = scnEditor.instance;
            InspectorPanel panel = editor != null ? editor.levelEventsPanel : null;
            if (panel == null)
                return;

            int perTypeIndex = IndexOfInPanelStack(evt);
            try
            {
                InspectorTab typeTab = panel.GetTabForEventType(evt.eventType);
                if (typeTab != null)
                {
                    // §47 openTab 跟着当前事件的类型走（原生分页器就挂在那一页上）；
                    // 弹窗关着时**不**写它 —— "openTab == null" 是"没开着窗"的记号（Close 里清过），
                    // 从清 fake 这类关窗后的收尾里把它立起来会留下半个"像是开着窗"的状态。
                    if (isOpen)
                        openTab = typeTab;
                    typeTab.eventIndex = perTypeIndex;
                }
                else if (openTab != null)
                {
                    openTab.eventIndex = perTypeIndex;
                }
            }
            catch { }

            SuppressAutoClose();
            try
            {
                if (editor.cacheSelectedEventIndex > 0)
                    editor.cacheSelectedEventIndex = 0;
                panel.ShowPanel(evt.eventType, perTypeIndex);
            }
            catch (Exception e)
            {
                Main.Logger?.Log("切换直选事件失败: " + e.Message);
            }
        }

        /// <summary>
        /// §47 切到另一个标签页（步骤 3 的标签条就调这个；步骤 1 没有 UI，暂时只有内部/测试用）。
        /// 行为：退出多选批量（批量记的是**这一页**的显示下标，换页之后无从对齐）→ 保持当前事件（它在新页里就还在，
        /// 不在就取新页第一个）→ 面板跟到那个事件 → 走一次 <see cref="ReloadRows"/> 重建行与高度
        /// （**只排版一次**，不在这里另调 LayoutList）。
        /// </summary>
        internal static void SwitchTab(PagerTabKey key)
        {
            if (!isOpen)
            {
                // 没开着：只记一下"下次要停在哪一页"。步骤 1 的入口还是原生分页器箭头
                //（OpenCore 里固定从类型页开始），步骤 2 的入口按钮会改走这里。
                activeTabKey = key;
                return;
            }
            if (key == activeTabKey && currentStack != null && currentStack.Count > 0)
                return;                            // 已经在这一页：不重复重建

            if (HasBatch())
                ExitMultiSelect("切换分页标签页");

            activeTabKey = key;
            if (!TryGetActiveList(out List<LevelEvent> list))
            {
                Close();
                return;
            }

            if (currentEvent == null || !list.Contains(currentEvent))
                currentEvent = list[0];
            displayedIndex = list.IndexOf(currentEvent);
            ShowEventInPanel(currentEvent);
            snapNextScroll = true;   // 换页 = 换了一份列表，位置直接落位，不从旧页那一行滑过去
            ReloadRows();
            SyncTabStrip();
        }

        // ------------------------------------------------------------------ §47 弹窗左侧标签条（步骤 3 的 UI 接线）

        /// <summary>
        /// 页签被点（<see cref="PagerTabStrip"/> 的按钮回调）。功能关掉时什么都不做：
        /// 那时页签本该已经被拆掉了，留下来的只是禁用模组没重载编辑器导致的残留组件。
        /// </summary>
        internal static void OnTabStripClicked(PagerTabKey key)
        {
            if (!Main.IsEnabled || !Main.IsPagerListEnabled)
                return;
            SwitchTab(key);
            SyncTabStrip();
        }

        /// <summary>
        /// 标签页表内容变了（<see cref="TabsChanged"/>）：先报窗口最小高度，再重建页签。
        /// 窗口还没显示出来时 <see cref="SyncTabStrip"/> 自己会跳过（§28.1：未激活的层级里建 TMP 会丢文本），
        /// 等 <see cref="ResizeHost"/>（ShowWindow 之后必走）那一次统一补建。
        /// </summary>
        private static void OnTabsChanged()
        {
            PushStripHeightToWindow();
            SyncTabStrip();
        }

        /// <summary>
        /// 让标签条与当前页对齐（幂等：<see cref="PagerTabStrip.Rebuild"/> 内部比对签名，
        /// 页签表没变就只重排 + 换高亮，不重建对象）。打开、刷新、换页三条路径都走这里，
        /// 所以控制器自己退回 All 页（页签表没变但 ActiveTab 变了）时高亮也不会脱拍。
        /// </summary>
        private static void SyncTabStrip()
        {
            if (tabStrip == null || popupRoot == null || !popupRoot.activeInHierarchy)
                return;
            tabStrip.Rebuild();
        }

        /// <summary>
        /// 把"标签条要多高"写进窗口几何（最小高度的一项，见 <see cref="PagerWindowInteraction.MinStripHeight"/>；
        /// §47 标签条整条在窗口外，上下还各占 <c>TopInset + BottomInset</c> 的内缩，那两段由窗口几何自己加）。
        /// 开着窗时页签变多 ⇒ 顺手重跑一次几何把它撑到新的最小值（一次性调用，不是每帧）。
        /// </summary>
        private static void PushStripHeightToWindow()
        {
            if (windowInteraction == null)
                return;
            windowInteraction.MinStripHeight = PagerTabStrip.RequiredHeight(tabs.Count);
            if (isOpen && popupRoot != null && popupRoot.activeInHierarchy)
                windowInteraction.StripHeightChanged();
        }

        /// <summary>
        /// 建标签条容器：§47 整条**在弹窗外面**、贴在窗口左边框线上，几何照原生 inspector 的 <c>tabs</c> 容器
        /// （anchors (0,0)-(0,1)、pivot (1,1)、pos (3,-24)、sizeDelta (48,-44)）⇒ 右沿压进窗口左沿 3 个单位
        /// （压住边框线，开口与窗口连成一片），上下各内缩 24 / 20。
        /// **挂在弹窗根上、不能挂在 ScrollRect 里面** —— 挂进去会被视口 RectMask2D 裁掉，
        /// 而且指针停在页签上时滚轮就不再作用于列表。
        /// 根上再铺一块透明 Image 当射线挡板：弹窗的 <c>aee_pagerFrameBlock</c> 只盖窗口矩形，
        /// 标签条已在矩形之外，页签之间的空隙没有它兜着会把点击漏给身后的编辑器。
        /// </summary>
        private static void BuildTabStrip(Transform host)
        {
            if (tabStrip != null)
                return;

            var go = new GameObject("aee_pagerTabStrip", typeof(RectTransform), typeof(Image));
            var rect = (RectTransform)go.transform;
            rect.SetParent(host, false);
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(1f, 1f);                     // 右上角 = 定位点：右沿贴窗口、顶边往下挪 TopInset
            rect.sizeDelta = new Vector2(PagerTabStrip.Width,
                -(PagerTabStrip.TopInset + PagerTabStrip.BottomInset));
            rect.anchoredPosition = new Vector2(PagerTabStrip.BorderOverlap, -PagerTabStrip.TopInset);

            Image catcher = go.GetComponent<Image>();
            catcher.sprite = null;
            catcher.color = new Color(0f, 0f, 0f, 0f);            // 只吃射线，不画任何东西
            catcher.raycastTarget = true;
            // 与弹窗空白处同一套诊断：点到页签之间的空隙也留一条日志，别再出现"点了没反应、日志空白"
            go.AddComponent<PagerBackgroundClickLog>();

            tabStrip = go.AddComponent<PagerTabStrip>();
            tabStrip.Attach(rect, titleText);      // 字体沿用弹窗标题（游戏自己的字体 + 材质）
        }

        // ------------------------------------------------------------------ 打开 / 关闭

        /// <summary>
        /// 打开弹窗。
        ///
        /// **从 `ShowWindow()` 那一刻起，原版的 `showingPopup` 就已经是 true 了**（`ShowPopup`
        /// 一进来就置位），而它一为 true，`scnEditor.HandleKeyboardActions` 就只处理 Esc 然后 return ——
        /// 表现为"ctrl+S/复制/粘贴全废"。所以建行/排版这些后续步骤任何一处抛异常，都必须把窗口收回去
        /// （`Close()` 负责 `ShowPopup(false, 233)`），否则标志就永久卡住。
        /// </summary>
        private static void Open(InspectorTab tab, List<LevelEvent> stack)
        {
            // §47 原生分页器箭头那条入口（行为逐字不变）：停在 `tab` 那一型的页上，
            // 起点事件取原生 `tab.eventIndex`（类型内下标，Type 口径下它就是显示下标）。
            OpenWith(tab, stack, PagerTabKey.MakeType(tab.levelEventType),
                stack != null && stack.Count > 0 ? Mathf.Clamp(tab.eventIndex, 0, stack.Count - 1) : -1,
                false);
        }

        /// <summary>
        /// 两个入口共用的打开外壳（§47）：差别只在"停在哪一页 / 起点是哪一个事件 / 要不要把面板推过去"。
        /// </summary>
        private static void OpenWith(InspectorTab tab, List<LevelEvent> stack, PagerTabKey tabKey, int startIndex, bool pushPanel)
        {
            if (!EnsureBuilt())
                return;

            try
            {
                OpenCore(tab, stack, tabKey, startIndex, pushPanel);
            }
            catch (Exception e)
            {
                Main.Logger?.Log("分页器列表打开失败，强制收窗: " + e);
                isOpen = false;
                try
                {
                    Close();
                }
                catch (Exception e2)
                {
                    Main.Logger?.Log("分页器列表收窗也失败，直接清 showingPopup: " + e2);
                    try
                    {
                        if (popupRoot != null)
                            popupRoot.SetActive(false);
                        scnEditor.instance?.ShowPopup(false, (scnEditor.PopupType)233, false);
                    }
                    catch { }
                }
            }
        }

        private static void OpenCore(InspectorTab tab, List<LevelEvent> stack, PagerTabKey tabKey, int startIndex, bool pushPanel)
        {
            openTab = tab;
            // §47 停在哪一页由**入口**决定：分页器箭头 = 该类型的页，头部「全部事件」按钮 = All 页。
            activeTabKey = tabKey;
            openedFrame = Time.frameCount;
            PagerClipboard.InvalidateCache();   // 打开时重读一次原版键位表（用户可能刚改过键位）
            // 折叠状态**不在这里清**：它是会话级的（关窗再开、切换事件后都该保持），
            // 只有编辑器重载（Reset）才清空（§23.3）。
            // 多选批量态**关窗也不丢**（§34.2），这里只是把它的行高亮恢复出来；
            // 事件已失效（被删/换类型）就退出多选，按单选开始。
            RestoreBatchSelection(stack);
            // 标签页表在这里算一次：标题、步骤 3 的标签条、以及后面每一次刷新都从同一份口径出发
            RecomputeTabs(activeTabKey);
            currentEvent = startIndex >= 0 ? stack[startIndex] : null;
            displayedIndex = startIndex;
            // §47 「全部事件」入口：右侧属性面板与原生分页器要跟着落到这一行上
            //（箭头入口时面板本来就停在这个事件，不用动 ⇒ pushPanel = false，行为与今天一致）
            if (pushPanel && currentEvent != null)
                ShowEventInPanel(currentEvent);

            UpdateTitle(stack);
            // **先把窗口显示出来，再建行**（§28.1）：行与备注文本如果是在"父层级还没激活"时创建的，
            // 备注那次 `text = …` 会被 TMP 吞掉（网格不重建）—— 表现就是"打开弹窗时备注列全空，
            // 随便点一行触发重建才出现"。而点行重建时弹窗已经是激活状态，所以那条路一直是好的。
            // 这一步同时把原版的 showingPopup 置了位 ⇒ 从这里出去的任何异常都得走 OpenWith() 的收窗兜底。
            if (!ShowWindow())
            {
                // 原版弹窗还在播动画，ShowPopup(true) 没生效（showingPopup 仍是 false）：窗口不能就这么显示
                // （非模态 ⇒ 原版快捷键照跑、我们的快捷键又不接管），按"没打开"收拾干净，等动画完了补开。
                AbortOpenForAnimation(tab, tabKey);
                return;
            }

            BuildSlots(stack);
            int rowCount = CreateRows();
            if (rowCount <= 0)
            {
                Close();       // 建不出行就恢复成"没打开"的样子（窗口已经显示出来了）
                return;
            }

            ResizeHost(rowCount);
            LayoutList();
            isOpen = true;
            pendingOpenTab = null;
            RefreshEntryButton(false);   // §47 开窗后重刷头部入口（亮底已取消，这里只校可见性与对齐）

            // 批量态还活着：重开时按当前数据重建一次 fake（值/混合标记刷新）并挂回面板。
            // 这是**恢复**不是新选择 ⇒ 不再提示一遍"已选择 N 个事件"（§39 M2）。
            if (selectedIndices.Count >= 2)
                ApplySelectionToPanel(false);
        }

        /// <summary>
        /// `ShowWindow` 没能让原版进入弹窗态时的收尾：只撤掉 OpenCore 已经改过的弹窗状态
        /// （不调 `ShowPopup(false)` —— 那会再起一段收起动画，把"动画中"拖得更久），批量态原样保留；
        /// 然后登记一次延后打开（同一个标签页的重试保持最初的期限，不会无限续期）。
        /// <paramref name="tabKey"/> 一起记下：补开时必须走**当初那个入口**那条路
        /// （箭头 = 该类型的页，头部按钮 = All 页），否则点一次按钮却开成了类型页。
        /// </summary>
        private static void AbortOpenForAnimation(InspectorTab tab, PagerTabKey tabKey)
        {
            isOpen = false;
            openTab = null;
            currentEvent = null;
            displayedIndex = -1;
            selectedIndices.Clear();
            selectionAnchor = -1;
            openAbortedByAnimation = true;
            if (popupRoot != null)
                popupRoot.SetActive(false);
            if (pendingOpenTab != tab)
            {
                pendingOpenTab = tab;
                pendingOpenKey = tabKey;
                pendingOpenDeadline = Time.unscaledTime + PendingOpenSeconds;
                LogMultiEdit("打开弹窗时原版弹窗动画未结束（ShowPopup 未生效）⇒ 延后到动画结束再打开");
                return;
            }
            // §47 同一个把手、不同入口（原生箭头与头部按钮可能都指向这一型的标签页）：以最后一次点击为准
            if (pendingOpenKey != tabKey)
                pendingOpenKey = tabKey;
        }

        /// <summary>补开被动画挡回去的那次打开（<see cref="Tick"/> 每帧调；动画没完就继续等，过期作废）。</summary>
        private static void RetryPendingOpen()
        {
            InspectorTab tab = pendingOpenTab;
            if (tab == null)
                return;
            if (isOpen || !Main.IsEnabled || Time.unscaledTime > pendingOpenDeadline)
            {
                pendingOpenTab = null;
                return;
            }
            scnEditor editor = scnEditor.instance;
            if (editor == null || EditorPopupAnimating(editor))
                return;                            // 还在动画中：下一帧再看
            if (pendingOpenKey.Kind == PagerTabKind.All)
            {
                // §47 头部入口那一次：门槛重查一遍（换砖/事件被删的话这次点击就作废）
                if (!CanOpenAll(out List<LevelEvent> all))
                {
                    pendingOpenTab = null;
                    return;
                }
                openAbortedByAnimation = false;
                OpenAll(all);
            }
            else
            {
                if (!CanOpen(tab, out List<LevelEvent> stack))
                {
                    pendingOpenTab = null;             // 条件已经变了（换砖/换类型）：这次点击作废
                    return;
                }
                openAbortedByAnimation = false;
                Open(tab, stack);
            }
            // 打开成功，或者是因为别的原因失败（那种失败每帧重试只会刷日志）⇒ 都不再重试
            if (isOpen || !openAbortedByAnimation)
                pendingOpenTab = null;
        }

        /// <summary>原版 `popupIsAnimating`（私有字段；读不到按"没在动画"处理）。</summary>
        private static bool EditorPopupAnimating(scnEditor editor)
        {
            if (editor == null)
                return false;
            try { return editor.Get("popupIsAnimating") is bool animating && animating; }
            catch { return false; }
        }

        /// <summary>
        /// 原版的 `showingPopup`（私有字段；沿用 <see cref="PagerClipboard"/> 的写法：按名字反射读、
        /// 非泛型 Get + is 判断，成员缺失时不会在值类型上转炸）。
        /// </summary>
        private static bool EditorShowsPopup(scnEditor editor)
        {
            if (editor == null)
                return false;
            try { return editor.Get("showingPopup") is bool showing && showing; }
            catch { return false; }
        }

        /// <summary>
        /// 重新取事件列表并重建行。切换当前事件（点行/组名循环）、折叠展开、拖动落点之后都走这里 ——
        /// 事件列表顺序是"打开时的快照"，任何改动都要重取，否则下标会对不上
        /// （§47：这一页的下标 = <see cref="currentStack"/> 的下标，原生 `tab.eventIndex` 是**类型内**下标，
        ///  只有 ShowEventInPanel 会写它）。
        /// </summary>
        internal static void ReloadRows()
        {
            InspectorTab tab = openTab;
            if (tab == null || !isOpen)
                return;
            // §47 列表按**当前标签页口径**重取（内部会重算标签页表）；不再按 openTab 的类型现推
            PagerTabKey tabBefore = activeTabKey;
            List<LevelEvent> selectedBefore = null;   // 只在"这一次重算把用户换到别的一页上了"时才用
            if (!TryGetActiveList(out List<LevelEvent> stack))
            {
                Close();
                return;
            }

            // §47 当前那一页整页没了（这一型的事件被删光）⇒ BuildTabs 退回了 All 页。
            // 选中集与批量态记的下标都是**旧那一页**的显示下标，直接夹到新表长度上会把高亮
            // 落到别的事件上 ⇒ 先按对象存一份，建完槽位再映射回新下标。
            if (tabBefore != activeTabKey)
            {
                snapNextScroll = true;   // BuildTabs 静默退回别的一页 ⇒ 同样是换了列表，别滑
                selectedBefore = SelectedPopupEvents();
                if (HasBatch())
                    ExitMultiSelect("当前标签页已不存在（退回全部事件页）");   // fake 只有一个类型，跨页无意义
            }

            UpdateTitle(stack);
            BuildSlots(stack);

            // 当前事件按对象重新定位（分组/排序/换页之后下标会变）
            currentEvent = ResolveCurrent(stack);
            displayedIndex = currentEvent != null ? stack.IndexOf(currentEvent) : -1;
            if (selectedBefore != null)
            {
                selectedIndices.Clear();
                selectionAnchor = -1;
                for (int i = 0; i < selectedBefore.Count; i++)
                {
                    int index = stack.IndexOf(selectedBefore[i]);
                    if (index >= 0)
                        selectedIndices.Add(index);
                }
            }
            else
                selectedIndices.RemoveWhere(index => index < 0 || index >= stack.Count);

            int rowCount = CreateRows();
            if (rowCount <= 0)
            {
                Close();
                return;
            }
            ResizeHost(rowCount);
            LayoutList();
        }

        /// <summary>标题：Type 页照今天（类型名 + 个数）；All / Group 页用这一页的名字（§47）。</summary>
        private static void UpdateTitle(List<LevelEvent> stack)
        {
            if (titleText == null)
                return;
            int count = stack != null ? stack.Count : 0;
            if (IsMixedTab)
            {
                string tabTitle = TitleOfActiveTab();
                titleText.text = string.Format(L("aee.pager.title.mixed"), tabTitle, count);
                return;
            }
            titleText.text = string.Format(L("aee.pager.title"), EventTypeLabel(activeTabKey.EventType), count);
        }

        /// <summary>当前标签页的显示名（标签页表还没有时按口径现推一个，不返回空串）。</summary>
        private static string TitleOfActiveTab()
        {
            for (int i = 0; i < tabs.Count; i++)
                if (tabs[i].Key == activeTabKey)
                    return tabs[i].Title;
            return activeTabKey.Kind == PagerTabKind.All ? L("aee.pager.tab.all") : string.Empty;
        }

        /// <summary>当前显示的事件：优先用记下来的对象，找不到再退回显示下标（<see cref="displayedIndex"/>）。</summary>
        private static LevelEvent ResolveCurrent(List<LevelEvent> stack)
        {
            if (currentEvent != null && stack.Contains(currentEvent))
                return currentEvent;
            int index = displayedIndex >= 0 ? Mathf.Clamp(displayedIndex, 0, stack.Count - 1) : 0;
            return stack[index];
        }

        internal static void CloseIfOpen()
        {
            // §47 头部入口按钮跟着这里刷新：模组禁用（Main.Stop 调本方法）与功能开关（设置页调本方法）
            // 都会走到，而这两处正是"必须把按钮拆掉"的时机；其余情况靠缓存，不重复扫事件表。
            RefreshEntryButton(false);
            if (!Main.IsEnabled)
            {
                // 模组被禁用（StopMod 在置 IsEnabled=false 之后调这里）：无条件收掉，不看抑制窗口。
                // 补丁撤掉之后，这块挂在 Canvas 下的自建窗口就再没人管了（原版 Esc 只收它自己的遮罩）。
                CloseForDisable();
                return;
            }
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
        /// 禁用模组时的收窗：弹窗开着 ⇒ 走普通关窗（退出多选、`ShowPopup(false)` 放掉 showingPopup）；
        /// 弹窗标记为关、但窗口还显示着（打开到一半的残局）⇒ 收起窗口并放掉 showingPopup；
        /// 都不是 ⇒ 只退出多选（批量写回的钩子马上就不在了，fake 留在面板上只会让编辑落空）。
        /// 挂起的"动画结束后补开"一并作废。
        /// </summary>
        private static void CloseForDisable()
        {
            pendingOpenTab = null;
            openAbortedByAnimation = false;
            suppressCloseUntilFrame = -1;
            // 防重入：Close → ExitMultiSelect → ClearFakeEvent 会 ShowPanel，补丁还挂着时又会绕回 CloseIfOpen
            if (closingForDisable)
                return;
            closingForDisable = true;
            try
            {
                if (isOpen)
                {
                    Close();
                    return;
                }
                ExitMultiSelect("模组已禁用");
                if (popupRoot != null && popupRoot.activeSelf)
                {
                    popupRoot.SetActive(false);
                    ClearDropFeedback();
                    if (EditorShowsPopup(scnEditor.instance))
                        scnEditor.instance.ShowPopup(false, (scnEditor.PopupType)233, false);
                }
            }
            catch (Exception e)
            {
                Main.Logger?.Log("禁用时收起分页器列表失败: " + e.Message);
            }
            finally
            {
                closingForDisable = false;
            }
        }

        private static bool closingForDisable;

        /// <summary>
        /// 面板切换时的自动关窗，按标签页口径分开判（§47）：
        ///  · **Type 页**（今天的行为，逐字保持）：只有切到**别的类型**（真正换了标签页/别的事件堆）才关；
        ///    同类型的刷新（我们选事件、多选 fake 刷新、原版重渲染）一律不关（§26.2）；
        ///    批量态**关窗也活着**，所以这里还要按类型判一次：切到别的类型 ⇒ 退出多选（§34.3）。
        ///  · **All / Group 页**：这一页里**有**这个类型的事件就不算"换了标签页"（混排页里点一行本来就会
        ///    把面板切到那一行的类型上）；只有切到这一页里根本没有的类型才算换页 ⇒ 关窗 + 退多选。
        ///    我们自己发起的切换另外被 <see cref="SuppressAutoClose"/> 压着，这里是给原版/其它模组的
        ///    ShowPanel 兜底（键盘选事件、别的模组直接切面板……）。
        ///    同页内的类型切换**不动批量态**：批量的事件集合是按对象记的，跟面板当前显示哪个类型无关
        ///    （批量只在"选中的全是同一类型"时建立，见 <see cref="SelectionSharesOneType"/>；
        ///     跨页由 <see cref="SwitchTab"/> 显式退出）。
        /// </summary>
        internal static void CloseIfOpenOnOtherPanel(LevelEventType eventType)
        {
            if (IsMixedTab)
            {
                if (isOpen && ActiveTabHasType(eventType))
                    return;
                if (fakeEvent != null && fakeEvent.eventType != eventType)
                    ExitMultiSelect("切到其他事件类型 / 面板 tab");
                CloseIfOpen();
                return;
            }

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

        /// <summary>拖行开始：记下"正在拖哪一行 + 指针屏幕位置"，列表被滚轮挪动时靠它重算落点（见 OnListScrolled）。</summary>
        internal static void BeginRowDrag(int draggingIndex, Vector2 pointerPosition)
        {
            StopScrollAnimation();   // 拖动期间落点按屏幕坐标算，列表不能再被动画挪走
            rowDragIndex = draggingIndex;
            rowDragPointer = pointerPosition;
        }

        /// <summary>拖行中：只更新指针位置（落点反馈由调用方自己算）。</summary>
        internal static void UpdateRowDrag(Vector2 pointerPosition)
        {
            if (rowDragIndex >= 0)
                rowDragPointer = pointerPosition;
        }

        /// <summary>拖行结束（松手 / 行被回收）。</summary>
        internal static void EndRowDrag()
        {
            rowDragIndex = -1;
        }

        /// <summary>
        /// 编辑器里"选中的砖"变了（`scnEditor.OnSelectedFloorChange` 的补丁入口）：
        /// **换砖 ⇒ 退出多选**（§34.3 的退出条件之一），然后照旧关窗。
        /// 我们自己触发的选砖（点行、粘贴收尾）在抑制窗口内，不动多选状态。
        /// </summary>
        internal static void OnSelectedFloorChanged()
        {
            if (Time.frameCount > suppressCloseUntilFrame)
            {
                ExitMultiSelect("换砖");
                pendingOpenTab = null;             // 换砖了：还没补开的那次点击属于旧砖，作废
            }
            CloseIfOpen();
            // §47 换砖 ⇒ 标签页表整个是旧砖的，必须重算。关着窗时表本来就是空的（Close 里清过），
            // 这时不重算 —— 这个方法在框选时会连着跑几十次，别白 allocating 一整份事件表副本。
            if (isOpen || tabs.Count > 0)
                RefreshTabs();
        }

        /// <summary>
        /// OK 按钮：关窗但**保留**多选批量态（§34.1/§34.2）。弹窗打开时是模态的、窗口外点不了，
        /// 所以批量编辑恰恰要在关窗后用 ⇒ 关窗只收 UI，不动 fake 与面板。
        /// </summary>
        private static void CloseKeepingBatch()
        {
            // 模组已被禁用（补丁全撤了、编辑器没能重载）：批量写回的钩子已经不在，留着 fake 挂在面板上
            // 只会让编辑落空 ⇒ 这时 OK 按普通关窗处理（退出多选、面板回到真实事件）。关窗本身照做：
            // 这是禁用后收掉这块自建窗口的入口之一（原版 Esc 只收它自己的遮罩）。
            Close(Main.IsEnabled);
        }

        private static void Close()
        {
            Close(false);
        }

        private static void Close(bool keepBatch)
        {
            StopScrollAnimation();   // 窗口要收起了：动画还在跑的话会把已隐藏的列表继续挪
            snapNextScroll = false;  // 换页中途关窗时别让这个标记漏到下一次打开
            suppressScrollToRow = false;   // 同理：弹窗已关，不把"不滚动"留给下一次打开
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
            displayedIndex = -1;
            selectedIndices.Clear();
            selectionAnchor = -1;
            mixedToastAnnounced = false;           // §47 下次开窗的选中提示重新武装
            // collapsedGroups 同理保留：下次打开还是上次折叠的样子（§23.3）；§47 键带页前缀 ⇒ 各页各自记自己的折叠态
            ClearDropFeedback();
            // §47 关窗后标签页表也要跟着当前砖重算（重算失败就清成空表，步骤 3 的标签条据此收起）
            RefreshTabs();
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
            RefreshEntryButton(false);   // §47 关窗后重刷头部入口（只校可见性与对齐：入口不再表达"停在 All 页"）
        }

        /// <summary>
        /// <summary>
        /// 重开弹窗时把批量覆盖的事件映射回**显示下标**（§34.2）：还是同一个标签页口径、
        /// 事件还在当前这一页的列表里才算数。
        /// §47 这里不再比 <see cref="InspectorTab"/> 对象 —— 混排页里 <see cref="openTab"/> 会随当前事件
        /// 换到别的原生标签页上，只按对象比会把自己的批量态认成"别的标签页的"，改比 <see cref="fakeTabKey"/>。
        /// 死掉的（被删/被剪切/换类型/撤销后换对象）剔除；不足 2 个 ⇒ 退出多选、面板回单事件。
        /// 批量集合直接取自 `fakeEvent.realEvents`（fake 关窗不再丢），所以这里只是"恢复高亮"。
        /// </summary>
        private static void RestoreBatchSelection(List<LevelEvent> stack)
        {
            selectedIndices.Clear();
            selectionAnchor = -1;
            LevelEvent batch = fakeEvent;
            if (batch == null || batch.realEvents == null || batch.realEvents.Count < 2)
                return;
            if (fakeTabKey != activeTabKey)
                return;                            // 不是这一页的批量态：这次打开按单选走

            int dropped = 0;
            for (int i = 0; i < batch.realEvents.Count; i++)
            {
                LevelEvent e = batch.realEvents[i];
                if (e == null || stack == null || stack.IndexOf(e) < 0
                    || e.eventType != batch.eventType)   // §47 批量只覆盖同一类型（见 SelectionSharesOneType），混进来的不是这一批
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
            StopScrollAnimation();   // 滚动区随场景一起没了，动画目标已经是死引用
            suppressScrollToRow = false;   // 标记也别漏到下一次的控制器实例
            if (windowInteraction != null)
                UnityEngine.Object.Destroy(windowInteraction);
            windowInteraction = null;
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
            // §47 标签条随场景一起销毁：静态事件要摘（下次 EnsureBuilt 重挂），引用要清（否则 Rebuild 会写进已销毁的对象）
            if (tabStripHooked)
            {
                TabsChanged -= OnTabsChanged;
                tabStripHooked = false;
            }
            tabStrip = null;
            // §47 头部入口按钮随 inspector 面板一起销毁：静态引用要清（下一次 ShowTabsForFloor 会重建）
            PagerEntryButton.Reset();
            entryCacheFloor = int.MinValue;
            entryCacheTotal = -1;
            entryCacheResult = false;
            pendingOpenKey = PagerTabKey.MakeAll();
            // 贴图中心那块 1×1 sprite 是运行时 Sprite.Create 出来的原生对象，不随场景销毁
            if (frameFillSprite != null)
            {
                UnityEngine.Object.Destroy(frameFillSprite);
                frameFillSprite = null;
            }
            dropLine = null;
            cursorMark = null;
            countLabel = null;
            dropObjectsResolved = false;
            dropFeedbackLogged = false;
            rowDragIndex = -1;            // 行对象随场景销毁，OnDisable 不一定跑得到，拖拽状态这里兜底
            // 备注浮层是弹窗的子对象，随场景一起销毁，这里只清引用
            noteTooltip = null;
            noteTooltipText = null;
            // 原版键位表里的 action 实例随旧编辑器一起作废，缓存也丢掉（下次打开弹窗重读）
            PagerClipboard.InvalidateCache();
            slots.Clear();
            groupKeyByIndex.Clear();
            currentStack = null;
            currentEvent = null;
            // §47 标签页表整个是"上一块关卡/上一块砖"的，编辑器重载后必须归零（连签名一起清，
            // 不然新算出来一模一样的表也不会发 TabsChanged，步骤 3 的标签条会留着旧页签）
            tabs.Clear();
            tabLists.Clear();
            floorEvents = null;
            tabsSignature = null;
            activeTabKey = PagerTabKey.MakeAll();
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
            popupFlagStuck = null;
            pendingOpenTab = null;
            openAbortedByAnimation = false;
            resolvedMarkFont = null;
            headerMarksFellBack = false;
            headerMarkDetectErrorLogged = false;
        }

        // ------------------------------------------------------------------ 列表内容

        /// <returns>建出的行数（0 表示构造失败）。</returns>
        private static int CreateRows()
        {
            // 行被销毁时鼠标可能正停在其中一行上（收不到 OnPointerExit），所以重建前先收浮层
            HideNoteTooltip();
            // Destroy 是延后到帧末才生效的，而紧接着的 LayoutList 会立刻强制重排 listContent ——
            // 不先摘下来，旧行在这一帧里仍是布局子对象（行高/滚动位置按"新旧两批行"算）。
            // 先停用（VerticalLayoutGroup 不计未激活的子对象）再脱离父节点，最后才 Destroy。
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i] == null)
                    continue;
                rows[i].SetActive(false);
                rows[i].transform.SetParent(null, false);
                UnityEngine.Object.Destroy(rows[i]);
            }
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

            // §47 混排标签页里每行自己的类型才是标签（All 页一行 Jump、下一行 Hold 是常态），
            // 所以按类型缓存一份本地化名，避免每行都去查一次 RDString。
            var labelByType = new Dictionary<LevelEventType, string>();
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

                // §47 "当前行"按**对象**认：显示下标与原生 tab.eventIndex（类型内下标）在混排页里不是一回事
                bool isCurrent = slot.Event != null
                    ? ReferenceEquals(slot.Event, currentEvent)
                    : slot.OriginalIndex == displayedIndex;
                bool isMulti = selectedIndices.Contains(slot.OriginalIndex);
                if (isCurrent)
                    currentRowOrder = i;

                GameObject row = CreatePagerRow(isCurrent, isMulti, slot.Note, out Button clickButton);
                if (row == null)
                    break;
                row.name = "aee_pagerRow" + slot.OriginalIndex;

                TMP_Text label = row.GetComponentInChildren<TMP_Text>(true);
                if (label != null)
                {
                    if (!labelByType.TryGetValue(slot.Type, out string typeLabel))
                    {
                        typeLabel = EventTypeLabel(slot.Type);
                        labelByType[slot.Type] = typeLabel;
                    }
                    label.text = BuildRowLabel(slot, typeLabel, anyTagged, isCurrent);
                }

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

            // 自定义分组的组头色层（建在 StripPagerRow 之后：它会把不保留的子对象全关掉）。
            // 外观整套复制原版选中白底（圆角 + 四周留白），与白底同父、插在白底前面 ⇒ 画在白底与组名之下。
            // 取色用"事件"那一套自定义分组定义（弹窗里的分组键来自 PagerGroupKey）。
            Image tint = ApplyHeaderTint(row, background, slot.Key);
            if (label != null)
            {
                for (Transform t = label.transform; t != null && t != row.transform; t = t.parent)
                    if (!t.gameObject.activeSelf)
                        t.gameObject.SetActive(true);
                PinHeaderLabelToRowLeft(row, label);   // 组名要从行的左沿起笔（挪过 StripPagerRow / ApplyHeaderTint 之后：那时父子层级与色层都定下来了）
                label.alignment = TextAlignmentOptions.Left;   // 显式左对齐：rect 已拉满整行，居中/右对齐会让组名看起来"缩进"
                label.raycastTarget = false;
                ResolveHeaderMarks(label.font);     // 先按这行的字体定好字形（缺字会画成方框）
                label.text = BuildHeaderLabel(slot);
            }

            LayoutPagerRow(row);

            PagerGroupHeader marker = row.AddComponent<PagerGroupHeader>();
            marker.Key = slot.Key;
            // 组名（含写进文本里的 ▼/▶ 标记）的颜色：有色层就按"色层叠在原版行底色上"的亮度取对比色，
            // 否则原版白。算好的颜色存进 marker，取消拖放高亮时按它还原（SetGroupHighlight）。
            marker.OriginalLabelColor = ResolveHeaderLabelColor(row, tint);
            if (label != null)
                label.color = marker.OriginalLabelColor;
            // 箭头区必须盖住**看得见的那个 ▼/▶**：弹窗组头是把标记直接写进标签文本的
            // （`BuildHeaderLabel` 前缀），而组名现在从行左沿 x=0 起笔（PinHeaderLabelToRowLeft）
            // ⇒ 箭头区的宽度就是"一个字形宽"，固定 22 单位盖不住大字号的字形，点箭头会落进"组名区"，
            // 于是变成"全选该组"而不是折叠（§23.1）。
            float arrowZone = ResolveHeaderArrowZone(row, label);
            AddHeaderHitArea(row, marker, PagerHeaderArea.Arrow, 0f, arrowZone);
            AddHeaderHitArea(row, marker, PagerHeaderArea.Name, arrowZone, 0f);
            return row;
        }

        /// <summary>
        /// 把组头名称文本挪到行根节点下、拉满整行且**左边距 0** —— 组名于是从行的左沿起笔，
        /// 与弹窗标题（同一列，见 <see cref="ListPaddingX"/>）对齐。
        ///
        /// 内缩的来源：行 prefab 是装饰列表的行，名称文本 <c>itemName</c> 在 rect 左边留了一截给
        /// **类型图标**（<c>ListItem.itemTypeImage</c>）。事件行有那个图标，这截留白是必要的；
        /// 组头没有图标（<see cref="StripPagerRow"/> 把它连同容器一起关掉了），留白却还在
        /// ⇒ 组名与事件行文本落在同一条竖线上，看起来像"组头缩进了一格"。
        ///
        /// 做法是不管留白在 rect 偏移、还是在父容器（内缩 / 布局组）上，一律把 rect 直接挂到行下、
        /// 锚点拉满、offsetMin.x 归零，右边距沿用原来距行右沿的距离；再清掉 TMP 自己的左 margin。
        /// 对齐 / 字体 / 字号一概不动。重新挂到行末 ⇒ 画在色层与高亮底之上（兄弟顺序即绘制顺序）。
        /// </summary>
        private static void PinHeaderLabelToRowLeft(GameObject row, TMP_Text label)
        {
            RectTransform rect = label != null ? label.rectTransform : null;
            if (row == null || rect == null)
                return;

            float rightInset = HeaderLabelRightInset(row, rect);   // 要在改锚点/换父之前量（改完就没有"原来"了）
            rect.SetParent(row.transform, false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(HeaderLabelLeftInset, 0f);   // 箭头点击区由 ResolveHeaderArrowZone 按实际左沿换算，自动跟着右移
            rect.offsetMax = new Vector2(-rightInset, 0f);

            // TMP 自己的左 margin 也清掉（prefab 一般是 0，这里只是兜底；行是 UI 文本，取具体类型）
            TextMeshProUGUI ugui = label as TextMeshProUGUI;
            if (ugui != null && ugui.margin.x != 0f)
            {
                Vector4 margin = ugui.margin;
                ugui.margin = new Vector4(0f, margin.y, margin.z, margin.w);
            }
        }

        /// <summary>组头名称文本距行右沿的留白：按世界坐标换算（锚点怎么设都算得对），行矩形还没量出来时退回它自己的 <c>offsetMax.x</c>。</summary>
        private static float HeaderLabelRightInset(GameObject row, RectTransform rect)
        {
            RectTransform rowRect = row != null ? row.GetComponent<RectTransform>() : null;
            if (rowRect != null && rect != null && rowRect.rect.width > 1f && rect.rect.width > 1f)
            {
                Vector3 world = rect.TransformPoint(new Vector3(rect.rect.xMax, 0f, 0f));
                float inset = rowRect.rect.xMax - rowRect.InverseTransformPoint(world).x;
                return inset > 0f ? inset : 0f;
            }
            // 兜底（刚 Instantiate 出来的行还没跑过布局、`rect.width` 还是 0 时会走到这里）：
            // 退回标签自己在 prefab 里的右内缩，按"没有内缩"夹到非负。
            return rect != null && rect.offsetMax.x < 0f ? -rect.offsetMax.x : 0f;
        }

        /// <summary>
        /// 给分页器组头铺一层自定义分组的颜色（分组定义取"事件"那一套）。只有**用户自定义分组**
        /// （键 "custom:N"，见 <see cref="PagerGroupKey"/>）且设过颜色才建这一层；自动分组 /「未分组」/
        /// 没设颜色（alpha 为 0）都不建 ⇒ 组头保持原样。行是每次重建时新建的（不像装饰栏走对象池），
        /// 所以不需要考虑复用残留。异常只记日志：弹窗重建不能因为上色失败而中断。
        ///
        /// 色层外观整套复制 <paramref name="background"/>（= 原版选中白底）⇒ 圆角与四周留白和白底一致；
        /// 与白底同父、插在白底的 siblingIndex 上（紧挨在它前面）⇒ 画在白底与组名之下。
        /// 白底在本函数返回后会被 <c>SetGroupHighlight(null)</c> 关掉，色层是独立对象，不受影响。
        /// </summary>
        private static Image ApplyHeaderTint(GameObject row, Transform background, string groupKey)
        {
            if (row == null)
                return null;
            try
            {
                if (!DecoGroupState.TryGetCustomIndex(groupKey, out int index)
                    || !DecoGroupState.TryGetCustomGroupColor(DecoGroupState.GroupSet.Event, index, out Color color))
                    return null;

                Image tint = DecoGroupState.CreateTintLayer(row.transform, background, "aee_pagerGroupTint", groupKey, WarnMissingTintBackground);
                if (tint == null)
                    return null;
                tint.color = color;
                tint.raycastTarget = false;  // 不吃射线：组头的箭头/组名点击区照旧生效
                return tint;
            }
            catch (Exception e)
            {
                Main.Logger?.Log("分页器组头上色失败: " + e.Message);
                return null;
            }
        }

        /// <summary>"白底没有 Image 可抄"只记一次：弹窗每次重建都会为每个自定义分组再走一遍，不去重会刷屏。</summary>
        private static readonly HashSet<string> tintBackgroundWarned = new HashSet<string>(StringComparer.Ordinal);

        private static void WarnMissingTintBackground(string message)
        {
            if (tintBackgroundWarned.Add(message))
                Main.Logger?.Log(message);
        }

        /// <summary>
        /// 组头该有的组名颜色：行底下有我们铺的自定义色层时，按"色层叠在原版行底色上"的亮度取对比色
        /// （浅底黑字、深底白字），没有色层就是原版白。三角 ▼/▶ 是写进同一段文本里的前缀，跟着一起变。
        /// </summary>
        private static Color ResolveHeaderLabelColor(GameObject row, Image tint)
        {
            if (tint == null)
                return Color.white;
            return DecoGroupState.ContrastTextColor(tint.color, DecoGroupState.HeaderBaseColor(row.GetComponent<Image>()), Color.white);
        }

        /// <summary>折叠标记按字体自检（同一字体只查一次；每次建行可能有好几个组头）。
        /// 上次**没能**在候选表里取到字形（退到了 ASCII 兜底）时不锁死结果：下次建行再自检一次 ——
        /// 动态字体图集是按需补字的（见 <see cref="PickHeaderMark"/>），首次检测有可能早于图集就绪。</summary>
        private static void ResolveHeaderMarks(TMP_FontAsset font)
        {
            if (font == null)
                return;
            if (ReferenceEquals(font, resolvedMarkFont) && !headerMarksFellBack)
                return;
            resolvedMarkFont = font;
            bool expandedResolved;
            bool collapsedResolved;
            ExpandedMark = PickHeaderMark(font, ExpandedMarkCandidates, out expandedResolved);
            CollapsedMark = PickHeaderMark(font, CollapsedMarkCandidates, out collapsedResolved);
            headerMarksFellBack = !expandedResolved || !collapsedResolved;
        }

        /// <summary>
        /// 候选里第一个字体（含回退字体）真有的字形；一个都没有就退回**末位**（ASCII 兜底，一定画得出来），
        /// 并把 <paramref name="resolved"/> 置 false ⇒ 下一次建行再自检（别把一次失败永久缓存成 "v"）。
        ///
        /// 2.9.8 的 TMP 签名是**三参**：<c>HasCharacter(char character, bool searchFallbacks, bool tryAddCharacter)</c>
        /// （v2 的 Unity.TextMeshPro 反编译：<c>HasCharacter</c> :11362、
        /// <c>if (tryAddCharacter &amp;&amp; m_AtlasPopulationMode == AtlasPopulationMode.Dynamic &amp;&amp; TryAddCharacterInternal(character, out _)) return true;</c> :11376）。
        /// 第三参就是"**按需把字形补进动态图集**"，不是"只查当前活动字表"。先前传 false 时：
        /// ▼/▶ 这类字形只有被渲染过才会进 <c>m_CharacterLookupDictionary</c>，而动态字体资产
        /// （AtlasPopulationMode.Dynamic）里"源字体有、图集里还没画过"的字**必然**被判成缺字。
        /// 真机现象与之一致：▶ 因为别处已经渲染过而通过、▼ 一路退到 ASCII 的 "v"。
        /// 传 true 后检测结果就等于"渲染时到底画不画得出来"，与 v3（<c>PagerListController.cs:2251</c>）一致。
        /// searchFallbacks 本来就把 <c>fallbackFontAssetTable</c> / <c>TMP_Settings</c> 的回退字体与默认字体一并查了
        /// （:11380-:11423），且对它们同样传 tryAddCharacter。
        /// </summary>
        private static string PickHeaderMark(TMP_FontAsset font, string[] candidates, out bool resolved)
        {
            resolved = false;
            try
            {
                for (int i = 0; i < candidates.Length; i++)
                {
                    if (font.HasCharacter(candidates[i][0], true, true))
                    {
                        resolved = true;
                        return candidates[i];
                    }
                }
            }
            catch (Exception e)
            {
                if (!headerMarkDetectErrorLogged)
                {
                    headerMarkDetectErrorLogged = true;
                    Main.Logger?.Log("检测组头箭头字形失败，按 ASCII 兜底处理（只记一次）: " + e.Message);
                }
            }
            return candidates[candidates.Length - 1];
        }

        /// <summary>
        /// 组头箭头点击区的右边界（行内坐标）= 标签左内缩 + 一个字形宽 + 余量。
        /// 组头的标签已经被 <see cref="PinHeaderLabelToRowLeft"/> 钉在 x=0 ⇒ 正常情况下这一项是 0，
        /// 边界就等于"一个字形宽"（下限 <see cref="HeaderArrowWidth"/>）。
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
            text.enableWordWrapping = false;   // 2.9.8 的 TMP 只有 enableWordWrapping，没有 v3 的 textWrappingMode（下面几处同理）
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
        /// §47 标签条在窗口**外面**也会用这套浮层（页签的 tooltip）：夹取矩形仍是窗口矩形 ——
        /// 浮层不吃射线，落在窗口内比跟着页签跑到屏幕边上更稳，这里刻意不改。
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

        /// <summary>列表滚动回调（ScrollRect.onValueChanged）：把浮层收掉（§28.2）；正在拖行时顺便重算落点。</summary>
        private static void OnListScrolled(Vector2 position)
        {
            HideNoteTooltip();
            // 拖行时滚轮挪了内容、指针可能没动 ⇒ 按记下的位置重算落点（别调 LayoutList，它会把当前行滚回中间）
            if (rowDragIndex >= 0)
                UpdateDropFeedback(rowDragPointer, rowDragIndex);
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

        /// <summary>
        /// 当前显示的事件列表快照（§47 = <see cref="TryGetActiveList"/> 给的那一页；Type 口径下它就是
        /// 原生 <c>GetSelectedFloorEvents</c> 的结果）；**事件行下标 = 这个列表的下标**，不是类型内下标。
        /// </summary>
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
        ///   ④ 其余按事件**自己的**标签分组（`DecoGroupActions.GroupTagKeyOf` ⇒ 砖上事件读 eventTag；
        ///      事件上的 "tag" 是目标装饰选择器，不参与分组；不少类型根本没注册 eventTag ⇒ 视为无标签），
        ///      没有标签的进「未分组」。
        /// 桶内还会把**同类型事件聚成连续段**（只动显示顺序，数组顺序不变，见 <see cref="ClusterMembersByType"/>），
        /// 这样"拖到同型最近边界"（<see cref="SnapDropTargetToTypeRun"/>）才有意义。
        /// 与装饰列表的区别：这里没有"按类型"这一层（§47 All 页也走同一套分桶，混排类型的行都进各自的桶）；
        /// 组头显示规则见 <see cref="BuildSlots"/> 里的 showHeaders（有自定义分组就一直显示）。见 §16.2。
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

            // §47 每一页都走这套分桶（分桶是逐事件判的，混排天然成立）：All 页如此，合并的
            // 「自定义分组」页里列表只含自定义分组的事件 ⇒ 建出的只有自定义组头（tag 桶/「未分组」
            // 根本不会有成员），Type 页照旧。这样用户建的分组在各页都看得见、能折叠、能整组选中。
            // 空的自定义分组桶保留（KeepWhenEmpty）⇒ 仍是拖动的落点，与 All 页一致。
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

                // 分组标签 = 事件**自己的**标签（DecoGroupActions.GroupTagKeyOf：砖上事件是 eventTag）。
                // 事件上的 "tag" 是 MoveDecorations / SetText 等的目标装饰选择器，不能拿来分组；
                // 该类型没注册 eventTag ⇒ 视为无标签（只能手动归属）。
                string key = GroupTagOf(evt);

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

            // 桶内成员先按类型聚一下（显示顺序），再往下游发放：groupKeyByIndex / visible / showHeaders
            // 都只数成员个数、不看顺序，聚在这里不影响它们；聚类在 `slots` 生成之前 ⇒ 槽位顺序就是最终显示顺序。
            for (int b = 0; b < buckets.Count; b++)
                ClusterMembersByType(stack, buckets[b].Members);

            for (int b = 0; b < buckets.Count; b++)
                for (int m = 0; m < buckets[b].Members.Count; m++)
                    groupKeyByIndex[buckets[b].Members[m]] = buckets[b].Key;

            int visible = 0;
            int customBucketsBuilt = 0;
            for (int i = 0; i < buckets.Count; i++)
            {
                if (buckets[i].IsCustom)
                    customBucketsBuilt++;
                if (buckets[i].Members.Count > 0 || buckets[i].KeepWhenEmpty)
                    visible++;
            }
            // §47 自定义分组是用户自己建的，**只要存在就一直显示组头**（哪怕它现在是唯一的一桶：
            // 把唯一的 Flash 事件移进组 "1" 后「未分组」空掉，过去 visible > 1 会把组头整条藏掉，
            // 用户以为分组丢了）。没有自定义分组时才退回"只有一桶不显示组头"的旧观感。
            bool showHeaders = customBucketsBuilt > 0 || visible > 1;

            for (int b = 0; b < buckets.Count; b++)
            {
                Bucket bucket = buckets[b];
                if (bucket.Members.Count == 0 && !bucket.KeepWhenEmpty)
                    continue;
                if (showHeaders)
                    slots.Add(new Slot { IsHeader = true, Key = bucket.Key, Label = bucket.Label, Count = bucket.Members.Count });
                if (showHeaders && collapsedGroups.Contains(CollapsedKey(bucket.Key)))
                    continue;

                for (int m = 0; m < bucket.Members.Count; m++)
                {
                    int index = bucket.Members[m];
                    LevelEvent evt = stack[index];
                    string tagKey = DecoGroupActions.GroupTagKeyOf(evt, DecoGroupState.GroupSet.Event);
                    slots.Add(new Slot
                    {
                        IsHeader = false,
                        Key = bucket.Key,
                        OriginalIndex = index,
                        Event = evt,
                        Type = evt != null ? evt.eventType : LevelEventType.None,
                        Tag = tagKey != null ? GetTag(evt, tagKey) : "",
                        UseEventTag = tagKey == "eventTag",
                        TargetTag = tagKey != "tag" ? GetTag(evt, "tag") : "",
                        Note = Features.Notes.EventNote.GetNote(evt)
                    });
                }
            }
        }

        /// <summary>
        /// 桶内成员按类型聚成连续段（就地重排 <paramref name="members"/>，成员仍是那些下标）。
        /// 稳定：同型段内保持原来的（数组）顺序，段与段之间按"该类型在本桶成员顺序里第一次出现"排，
        /// 所以本来就聚好的桶一个字都不会动。null 事件按 <see cref="LevelEventType.None"/> 处理。
        /// </summary>
        private static void ClusterMembersByType(List<LevelEvent> stack, List<int> members)
        {
            if (stack == null || members == null || members.Count < 2)
                return;

            var typeOrder = new List<LevelEventType>();
            var indicesByType = new Dictionary<LevelEventType, List<int>>();
            for (int m = 0; m < members.Count; m++)
            {
                int index = members[m];
                if (index < 0 || index >= stack.Count)
                    return;                        // 越界成员（理论上不会）⇒ 整体不重排，避免丢行
                LevelEventType type = stack[index] != null ? stack[index].eventType : LevelEventType.None;
                if (!indicesByType.TryGetValue(type, out List<int> run))
                {
                    run = new List<int>();
                    indicesByType[type] = run;
                    typeOrder.Add(type);
                }
                run.Add(index);
            }
            if (typeOrder.Count < 2)
                return;                            // 只有一种类型（Type 页就是这样）⇒ 已经是聚好的

            members.Clear();
            for (int t = 0; t < typeOrder.Count; t++)
                members.AddRange(indicesByType[typeOrder[t]]);
        }

        private static string BuildHeaderLabel(Slot slot)
        {
            string mark = collapsedGroups.Contains(CollapsedKey(slot.Key)) ? CollapsedMark : ExpandedMark;
            return mark + " " + slot.Label + (Main.ShowGroupCounts ? " (" + slot.Count + ")" : "");
        }

        private static string BuildRowLabel(Slot entry, string typeLabel, bool anyTagged, bool isCurrent)
        {
            string text = (entry.OriginalIndex + 1) + ". " + typeLabel;
            if (entry.Tag.Length > 0)
                text += "  " + L(entry.UseEventTag ? "aee.pager.eventTagPrefix" : "aee.pager.tagPrefix") + entry.Tag;
            else if (anyTagged)
                text += "  " + L("aee.pager.noTag");
            // 目标装饰选择器（"tag"）照旧显示在行上 —— 同一堆 MoveDecorations 往往就靠它区分 —— 但它不是分组标签
            string target = entry.TargetTag ?? "";
            if (target.Length > 0)
                text += "  " + L("aee.pager.tagPrefix") + target;
            if (isCurrent)
                text = L("aee.pager.current") + " " + text;
            return text;
        }

        /// <summary>
        /// 事件用来分组的标签值（<see cref="DecoGroupActions.GroupTagKeyOf"/> 决定读哪个键；
        /// 该类型没有可分组的标签属性 ⇒ ""，即"未分组"，也不能被拖进按标签的组）。
        /// </summary>
        private static string GroupTagOf(LevelEvent evt)
        {
            string tagKey = DecoGroupActions.GroupTagKeyOf(evt, DecoGroupState.GroupSet.Event);
            return tagKey != null ? GetTag(evt, tagKey) : "";
        }

        private static string GetTag(LevelEvent evt, string key)
        {
            if (evt == null || string.IsNullOrEmpty(key))
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
                FocusPanelOnRow(originalIndex);
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
                FocusPanelOnRow(originalIndex);
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
        /// shift 范围选择的核心（纯逻辑，离线 harness 直接断言）：**先清空再填 [锚点, 被点行]**，
        /// 所以同一段范围既能加选也能减选；锚点由调用方给（shift 点击本身不移动锚点）。
        ///
        /// 范围按**列表里看得到的顺序**取（`slots` 的事件行：已按分组/排序排好，折叠组的成员根本不在里面），
        /// 而不是 stack 下标 —— 分组后第 2 行未必是下标 1，按下标取会选中视觉上不相邻、甚至藏在折叠组里的事件。
        /// 锚点不在可见行里（所在组被折叠了）⇒ 退化成只选被点的那一行。
        /// 槽位表里一行事件都没有（离线 harness 直接调用时）才退回按下标取。
        /// </summary>
        internal static void ApplyRangeSelection(int anchor, int clickedIndex)
        {
            selectedIndices.Clear();
            var order = new List<int>(slots.Count);
            for (int i = 0; i < slots.Count; i++)
                if (!slots[i].IsHeader)
                    order.Add(slots[i].OriginalIndex);

            if (order.Count == 0)
            {
                int from = Mathf.Min(anchor, clickedIndex);
                int to = Mathf.Max(anchor, clickedIndex);
                for (int i = from; i <= to; i++)
                {
                    if (i >= 0 && currentStack != null && i < currentStack.Count)
                        selectedIndices.Add(i);
                }
                return;
            }

            int clickedPos = order.IndexOf(clickedIndex);
            if (clickedPos < 0)
                return;                            // 点的行不在列表里（不该发生）：什么都不选
            int anchorPos = order.IndexOf(anchor);
            if (anchorPos < 0)
                anchorPos = clickedPos;
            int start = Mathf.Min(anchorPos, clickedPos);
            int end = Mathf.Max(anchorPos, clickedPos);
            for (int p = start; p <= end; p++)
            {
                int index = order[p];
                if (index >= 0 && currentStack != null && index < currentStack.Count)
                    selectedIndices.Add(index);
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
        /// ctrl 播种用的"当前事件"下标：与渲染层 isCurrent 同一口径（**显示列表里的下标**
        /// <see cref="displayedIndex"/>，§47 混排页里它不等于原生 `tab.eventIndex` 的类型内下标），
        /// 越界或该位置没有事件就返回 -1（= 不播种）。
        /// </summary>
        private static int CurrentEventIndexForSelection()
        {
            if (currentStack == null)
                return -1;
            int index = displayedIndex;
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
            if (tab == null)
                return;
            // §47 按当前标签页口径取列表（All/Group 页里 originalIndex 是显示下标，不是类型内下标）
            if (!TryGetActiveList(out List<LevelEvent> stack))
            {
                Close();
                return;
            }
            if (originalIndex < 0 || originalIndex >= stack.Count)
                return;

            currentEvent = stack[originalIndex];
            displayedIndex = originalIndex;

            // 面板 + 原生分页器：内部算类型内下标、必要时把 openTab 换到该类型的标签页、并压住自动关窗
            ShowEventInPanel(currentEvent);

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
            // ExitMultiSelect → ClearFakeEvent：面板还指着 fake 时把它指回"还活着"的真实事件（当前事件被剪掉时
            // 按标签页推）；原版收尾已经把面板切到真实事件上（剪切的 ShowPanel、粘贴的 selectAfterward）就不动它
            ExitMultiSelect(followPanel ? "粘贴改动了事件" : "剪切改动了事件");

            if (followPanel)
            {
                LevelEvent panelEvent = PanelSelectedEvent();
                if (panelEvent != null && !panelEvent.isFake && currentStack != null && currentStack.Contains(panelEvent))
                    currentEvent = panelEvent;
            }

            ReloadRows();
        }

        /// <summary>
        /// §47 粘贴之后把**贴上去的全部新事件**在弹窗里选中（跨类型剪贴板：原版只把面板切到剪贴板里
        /// 第一个事件的类型上，剩下几个类型的事件用户既看不到也选不到）。
        ///
        /// 换页规则（"停在哪一页才看得见这些行"）：
        ///  · 当前是 All / Group 页 ⇒ 原地不动（混排页装得下任何类型，能看到几个就选几个）；
        ///  · 当前是 Type 页、贴上去的是**同一类型** ⇒ 跳到那一型的页（那一型没成页就退回 All）；
        ///  · 当前是 Type 页、贴上去的**跨类型** ⇒ 退回 All 页（Type 页根本装不下它们）。
        /// 刷新统一走 <see cref="ReloadRows"/>（模式感知），不碰 <c>CanOpen</c>；砖上事件不足 2 个时
        /// 它自己会关窗（与今天一致）。
        /// </summary>
        internal static void SelectEventsAfterPaste(List<LevelEvent> pasted)
        {
            if (!isOpen || pasted == null || pasted.Count == 0)
                return;

            if (!IsMixedTab)
            {
                // 单个新事件也有明确类型（SelectionSharesOneType 的门槛是 ≥2，这里单独放行）
                PagerTabKey target = PagerTabKey.MakeAll();
                if (pasted.Count == 1 && pasted[0] != null)
                    target = PagerTabKey.MakeType(pasted[0].eventType);
                else if (SelectionSharesOneType(pasted, out LevelEventType pastedType))
                    target = PagerTabKey.MakeType(pastedType);
                if (target.Kind == PagerTabKind.Type && !tabLists.ContainsKey(target))
                    target = PagerTabKey.MakeAll();  // 那一型在这一砖上没成页（被原版的 solo 规则挡掉 / 贴到了别的砖上）
                if (target != activeTabKey)
                    SwitchTab(target);             // 换页会重算列表 + 退出批量
                if (!isOpen)
                    return;
            }

            ReloadRows();
            if (!isOpen || currentStack == null)
                return;

            selectedIndices.Clear();
            selectionAnchor = -1;
            int last = -1;
            for (int i = 0; i < pasted.Count; i++)
            {
                int index = pasted[i] != null ? currentStack.IndexOf(pasted[i]) : -1;
                if (index < 0)
                    continue;                        // 不在这一页（被原版的 solo 规则跳过 / 贴到了别的砖上）
                selectedIndices.Add(index);
                last = index;
            }
            if (last < 0)
            {
                LogMultiEdit("粘贴后未能选中：这批新事件都不在当前这一页里");
                return;
            }
            currentEvent = currentStack[last];
            displayedIndex = last;
            ShowEventInPanel(currentEvent);
            RebuildRowsOnly();
            LogMultiEdit("粘贴后选中 " + selectedIndices.Count + " 行（新增事件 " + pasted.Count + " 个）");
            // 选中 ≥2 行时按门控决定：同一类型 ⇒ 恢复批量视图（不重复提示），跨类型 ⇒ 保持普通多选
            if (selectedIndices.Count >= 2)
                ApplySelectionToPanel(false);
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
            if (openTab == null || !isOpen)
                return;
            // §47 "当前行"改成按**对象**认（Slot.Event == currentEvent），所以这里不再往原生 tab.eventIndex
            // 上写显示下标 —— 混排页里那个字段是"类型内下标"，写错了会把分页器文本 1/3 指到别的事件上。
            // 真正要动原生 eventIndex 的只有 ShowEventInPanel（它会自己算类型内下标）。
            if (currentStack != null && currentEvent != null)
                displayedIndex = currentStack.IndexOf(currentEvent);
            int rowCount = CreateRows();
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
            bool expanded = collapsedGroups.Remove(CollapsedKey(key));   // Remove 成功 = 原来是折叠的 ⇒ 这次是展开
            if (!expanded)
                collapsedGroups.Add(CollapsedKey(key));
            // §37：组头点击以前完全没有日志，"点了没反应"时是个盲区 ⇒ 记一条
            //（key 是分组桶键，存进 collapsedGroups 时按页加前缀 ⇒ 各页折叠态互不干扰）
            LogMultiEdit("组头（箭头）点击：key=" + key + " ⇒ " + (expanded ? "展开" : "折叠")
                + "，当前折叠组数 " + collapsedGroups.Count);
            SuppressNextScrollToRow();   // 折叠/展开只改列表，视野留在原处
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
                SuppressNextScrollToRow();   // 点组名退化成的单选，同样不该挪视野
                SelectEvent(only);
                return;
            }

            LogMultiEdit("组头（组名）点击：key=" + key + " 组内 " + selectedIndices.Count + " 个 ⇒ 全选该组并绑定批量面板");
            selectionAnchor = -1;
            SuppressNextScrollToRow();   // 全选该组只换列表，视野留在原处
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
        ///  · 选中 >=2 **且全是同一类型** ⇒ 造一个 fake 事件（isFake + realEvents）喂给原版右侧面板，
        ///    值变化由写值钩子回写各真实事件；
        ///  · 选中 >=2 但**跨类型** ⇒ 不建批量（只保留列表高亮 + 提一次"只能拖动/删除/复制"）；
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
                mixedToastAnnounced = false;       // §47 已经不是"跨类型多选"了 ⇒ 下次重新提一次
                // 方向很重要：**反选**（已有批量态、被点掉到 <2）才退出；
                // **累积**（0→1，还没到过 2）什么都不做 —— 以前在这里无条件退出，
                // 而 ExitMultiSelect 会清空 selectedIndices ⇒ 下一次 ctrl 点击又从空集开始，
                // 结果"ctrl 永远只能选中 1 个"（§36 的回归）。
                if (ShouldExitMultiSelect(selected.Count, BatchCount()))
                    ExitMultiSelect("反选到不足 2 个");
                return;
            }

            // §47 批量门控（取代旧的"混排页一律不开批量"）：判定只看**选中事件的类型**，不看在哪一页 ——
            // fake 只有一份属性表、一个 eventType，跨类型批量会把不相干的事件一起写掉；
            // 同类型的多选在 All / Group 页里同样是安全的批量。
            // 跨类型多选不是"什么都不支持"：拖动排序、删除、复制/剪切都走各自的路（见 PagerClipboard）。
            if (!SelectionSharesOneType(selected, out LevelEventType batchType))
            {
                if (BatchCount() >= 2)
                    ExitMultiSelect("选中事件跨多个类型（批量编辑只支持同一类型）");
                if (announce)
                    QueueMixedSelectionToast(selected.Count);
                return;
            }
            mixedToastAnnounced = false;

            LevelEvent fake = BuildFakeEvent(selected);
            if (fake == null)
                return;

            fakeEvent = fake;
            // §47 fake 挂的是**这一类型**的原生标签页：混排页里 openTab 会随当前事件换到别的类型上，
            // 直接拿它当 fakeTab 会让关窗后的兜底（RealCurrentEvent / CurrentEventOfTab）按错的类型推
            fakeTab = TabForEventType(batchType) ?? openTab;
            fakeTabKey = activeTabKey;             // §47 混排页里 openTab 会换，批量归属只认标签页口径
            BindFakeToPanel(fake);
            if (announce)
                QueueSelectionToast(selected.Count);
            else
                LogMultiEdit("恢复批量视图：" + selected.Count + " 个事件（重开弹窗，不重复提示）");
        }

        /// <summary>
        /// §47 批量门控条件：选中集 **≥2 个、没有一个为 null、且 eventType 全部相同** ⇒ true（并给出那个类型）。
        /// `LevelEventType.None` 是 PagerTabKey 用的哨兵，不是真类型，故"共同类型 == None"也判不通过。
        /// </summary>
        internal static bool SelectionSharesOneType(List<LevelEvent> selected, out LevelEventType type)
        {
            type = LevelEventType.None;
            if (selected == null || selected.Count < 2)
                return false;
            for (int i = 0; i < selected.Count; i++)
            {
                LevelEvent e = selected[i];
                if (e == null)
                    return false;
                if (i == 0)
                    type = e.eventType;
                else if (e.eventType != type)
                {
                    type = LevelEventType.None;
                    return false;
                }
            }
            return type != LevelEventType.None;
        }

        /// <summary>§47 某个事件类型对应的原生标签页（面板没建 / 读不到时 null）。</summary>
        private static InspectorTab TabForEventType(LevelEventType type)
        {
            InspectorPanel panel = scnEditor.instance != null ? scnEditor.instance.levelEventsPanel : null;
            if (panel == null)
                return null;
            try { return panel.GetTabForEventType(type); }
            catch { return null; }
        }

        /// <summary>
        /// §47 (a) 没有批量态时让右侧面板跟着**最后点击的那一行**走：混排页里那一行的类型可能和面板当前的
        /// 不是一型，必须走 <see cref="ShowEventInPanel"/> 现算类型内下标。有批量态时面板是 fake，不动它。
        /// </summary>
        private static void FocusPanelOnRow(int index)
        {
            if (HasBatch())
                return;
            if (currentStack == null || index < 0 || index >= currentStack.Count)
                return;
            LevelEvent evt = currentStack[index];
            if (evt == null || ReferenceEquals(evt, currentEvent))
                return;
            currentEvent = evt;
            displayedIndex = index;
            ShowEventInPanel(evt);
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

            bool wasBinding = bindingBatch;
            try
            {
                // 这次面板切换是**弹窗自己**发起的：先声明一下，否则"面板切换就关窗"的补丁
                // 会在 shift 加/减选（多选态刷新）时把弹窗关掉（§26.2）。
                SuppressAutoClose();
                // 连带压住"面板选中事件已不在批量里"的判定（§39 H2），以及**写回**：
                // `SetProperties(fake)` 刷控件时，有的控件会顺手回调一次保存（例如 MinMaxGradient 的
                // SetValue → 下拉框 SelectItem → onValueChanged → Save() → `selectedEvent[key] = …` + OnValueChange）
                // —— 那不是用户编辑，放过去就会把 fake 上"第一个事件的值"写回全部真实事件（混合值被抹平）。
                // 标志覆盖 ShowInspector → SetProperties → 标混合/放开混合行的整段（finally 里恢复原值）。
                bindingBatch = true;
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
                bindingBatch = wasBinding;
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
        /// **撤销点：我们自己从不开 `SaveStateScope`，只跟着原版走**。控件自己的回调大多已经开着一个
        /// （IL 核过：除了 `FloatPair::SaveWithoutRecording` / `Toggle::ProcessFile` / `Toggle::OggEncodeCallback`
        /// 都有）⇒ 我们的写回落在它里面，一次编辑一个撤销点。没开作用域的那几条路径，原版对单个事件
        /// 也**不记**撤销点 —— 尤其 `FloatPair` 拖动时每一步都走 `SaveWithoutRecording`
        /// （`set_Item` + `OnValueChange`，没有作用域），只在结束编辑的 `Save()` 上开一次。
        /// 以前"没人在管撤销就自己开一个"，拖一下就是每步一个 SaveState（撤销栈有上限，
        /// 一次拖动就能把历史全挤掉）；现在这些步骤只写值不记录，由结束编辑那次原版作用域统一记一个点。
        ///
        /// **幂等**：两个钩子对同一次编辑都会进来、控件也可能连续改多个键；值已经一致就什么都不做。
        ///
        /// **绑定期间不写回**（`bindingBatch`）：见 <see cref="BindFakeToPanel"/> —— 那时控件回调的"保存"
        /// 是刷新面板的副作用，不是用户编辑。
        ///
        /// `changedKey` 拿不到时（理论上不会）退回原来的整体写回，行为与修改前一致。
        /// </summary>
        internal static void ApplyFakeToRealEvents(string changedKey)
        {
            if (bindingBatch)
                return;
            try
            {
                FlushPendingToastIfSettled();      // 有交互了 ⇒ 把上次"待弹"的数量提示冲掉（§35.3）
                LevelEvent fake = fakeEvent;
                if (fake == null)
                    return;                        // 没有批量态 ⇒ 这次改动只作用于面板当前那个事件（每次编辑都会进来，不记日志）
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
                // 撤销点完全跟着原版：原版开着作用域（changingState > 0）就落在它里面；
                // 没开 = 原版这一步本来就不记录（拖动中的 SaveWithoutRecording 等）⇒ 我们也只写值（见方法注释）。
                bool recording = editor.changingState > 0;

                if (string.IsNullOrEmpty(key))
                {
                    // 兜底：不知道改的是哪个键 ⇒ 走整体写回（与修改前一致）
                    try
                    {
                        fake.ApplyPropertiesToRealEvents();
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
                if (!hasValue && fake.data.ContainsKey(key))
                    hasValue = fake.data.TryGetValue(key, out value);      // TryGet 的类型转换失败、但键确实在时兜一手（直接取原值）
                if (!hasValue)
                {
                    // fake 上没有这个键（理论上不该发生）：直接退出，别把 null 写进所有事件
                    LogMultiEdit("写回跳过：fake 上没有键 " + key);
                    return;
                }

                // 先干算一遍"哪些事件真的需要写"：
                //  · 值已经一致的跳过（幂等 —— 两个钩子对同一次编辑各跑一次、以及"原值没变"的提交都不会重复写）
                //  · 全都一致且没有"关"的标记 ⇒ 什么都不写（也不扫关卡、不记日志）
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
                    WriteKeyToRealEvents(fake, editor, key, value, isFloorKey, recording);

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

        /// <summary>
        /// 改这个键要不要重建弹窗列表：行上显示的东西都算 ——
        /// eventTag（分组标签，见 <see cref="GroupTagOf"/>，改了要重新分桶）、
        /// tag（砖上事件的目标装饰选择器：**不参与分组**，但行上照样显示它）、备注。
        /// </summary>
        private static bool IsGroupingKey(string key)
        {
            return key == "tag" || key == "eventTag" || key == Features.Notes.EventNote.KeyNote;
        }

        // 非记录写回（拖动中的每一步）的日志节流：每个键最多每 0.5 秒记一条，其余只计数
        private const float LiveWriteLogSeconds = 0.5f;
        private static string liveWriteLogKey;
        private static float nextLiveWriteLogTime = -1f;
        private static int suppressedLiveWrites;

        /// <summary>
        /// 把一个键的值写到 fake 的全部真实事件上（撤销点跟着原版，见 <see cref="ApplyFakeToRealEvents"/>）。
        /// `recording = false`（原版没开作用域，典型是拖动中的每一步）时**不扫关卡**
        /// （僵尸计数要把整关事件收一遍，O(N)）并对日志节流 —— 拖一下就是几十上百步。
        /// </summary>
        private static void WriteKeyToRealEvents(LevelEvent fake, scnEditor editor, string key, object value, bool isFloorKey, bool recording)
        {
            int written = 0;
            // 僵尸（已经被删出关卡、但仍留在 fake.realEvents 里）单独计数（§39 H3）：
            // 写进它们等于没写，日志里"实际数 < 应写数"到底是哪一类原因造成的，就看这个数。
            HashSet<LevelEvent> inLevel = recording ? CollectLevelEvents() : null;
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

            if (!recording)
            {
                float now = Time.unscaledTime;
                if (key == liveWriteLogKey && now < nextLiveWriteLogTime)
                {
                    suppressedLiveWrites++;
                    return;
                }
                string skipped = suppressedLiveWrites > 0 ? "，期间另有 " + suppressedLiveWrites + " 步未记" : "";
                liveWriteLogKey = key;
                nextLiveWriteLogTime = now + LiveWriteLogSeconds;
                suppressedLiveWrites = 0;
                LogMultiEdit("写回（不记撤销，跟随原版的非记录写值）：" + key + " → " + written + " 个事件" + skipped);
                return;
            }

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
            // §47 混排页里这一页已经没有这一类型的事件（被删光 / 换砖没退干净）⇒ 别把 fake 绑到一个
            // 用户在列表里看不到的批量上，直接退出（先重算一次页签表，免得拿旧表误杀）
            if (isOpen && IsMixedTab)
            {
                RecomputeTabs(activeTabKey);
                if (!ActiveTabHasType(batch.eventType))
                {
                    ExitMultiSelect("当前标签页里已经没有这一类型的事件");
                    return;
                }
            }
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
        /// 自己把窗口收掉（`Close` 里已经 `ShowPopup(false, …)`，与原版 Esc 分支做的是同一件事）。
        /// **收掉之后这一帧必须跳过原版方法体**（返回 true ⇒ 前缀返回 false）：原版是在我们之后才读
        /// `showingPopup` 的，那时它已经是 false，于是不走 Esc 分支、改走普通快捷键
        /// ⇒ 同一次 Esc 又被当成普通快捷键执行一遍（切换文件操作面板 + DeselectAll，连带退出多选）。
        ///
        /// 顺带兜一种半死状态：我们的弹窗已经标记为关（`isOpen == false`），但窗口还显示着、
        /// `showingPopup` 仍是 true（打开/关闭途中出过异常）—— 那时原版快捷键全停摆而 Esc 又不接管。
        /// 只认"我们的窗口对象还显示着"这一种（别的弹窗开着时的 Esc 照旧交给原版），
        /// 且只做最小动作：收起窗口 + 清标志，不跑完整 Close()（弹窗对象可能只建了一半）。
        ///
        /// 先看 Esc 再读状态：这个方法每帧都跑，没按 Esc 的帧就别付反射的开销。
        /// 返回 true = 这一帧的 Esc 由我们处理掉了。
        /// </summary>
        internal static bool HandlePopupEscape()
        {
            bool esc;
            // 用游戏自己的输入包装（RDInput）：本工程没有引用 UnityEngine.InputLegacyModule，
            // 直接调 Input.GetKeyDown 编译不过；RDInput.WentDown 就是"这一帧按下"，
            // 与 EditorKeybind.IsPressed 同源（EditorKeybind 对 Esc 的判定就走它）。
            try { esc = RDInput.WentDown(KeyCode.Escape); }
            catch { return false; }
            if (!esc)
                return false;

            if (!isOpen)
            {
                bool stuck = popupRoot != null && popupRoot.activeSelf;
                scnEditor editor = scnEditor.instance;
                if (!stuck || !EditorShowsPopup(editor))
                    return false;                  // 不是我们的窗 ⇒ 不接管 Esc
                LogMultiEdit("Esc：弹窗已标记关闭但窗口仍显示、showingPopup 卡住 ⇒ 收起窗口并清标志");
                try
                {
                    popupRoot.SetActive(false);
                    ClearDropFeedback();
                    editor.ShowPopup(false, (scnEditor.PopupType)233, false);
                }
                catch (Exception e) { Main.Logger?.Log("清 showingPopup 失败: " + e.Message); }
                return true;
            }

            LogMultiEdit("Esc 关窗（等价于点 OK，保留多选：" + BatchCount() + " 个事件）");
            Close(true);
            return true;
        }

        // ------------------------------------------------------ showingPopup 卡住的自检（§42 bug 2）

        /// <summary>上一次自检看到的"卡住"状态；null = 还没记过（保证第一跳必打一条）。</summary>
        private static bool? popupFlagStuck;

        /// <summary>自检节流：`showingPopup` 要反射读（DynamicInvoke），每 0.5 秒查一次就够（§42 bug 2）。</summary>
        private const float FlagStuckCheckSeconds = 0.5f;
        private static float nextFlagStuckCheck = -1f;

        /// <summary>
        /// 诊断：原版弹窗标志为 true、而我们和消息弹窗都没开 ⇒ 记一条"showingPopup 卡住"。
        /// 由 <see cref="PagerListPatches.PagerKeybindPatch"/> 每帧调，但内部按
        /// <see cref="FlagStuckCheckSeconds"/> 节流，且**只在状态变化时打**，不每帧刷。
        /// </summary>
        internal static void WarnIfPopupFlagStuck()
        {
            if (Time.unscaledTime < nextFlagStuckCheck)
                return;
            scnEditor editor = scnEditor.instance;
            if (editor == null)
                return;
            nextFlagStuckCheck = Time.unscaledTime + FlagStuckCheckSeconds;
            bool stuck = EditorShowsPopup(editor) && !isOpen && !IsMessagePopupUp();
            if (popupFlagStuck == stuck)
                return;
            bool wasStuck = popupFlagStuck.GetValueOrDefault();
            popupFlagStuck = stuck;
            if (stuck)
                Main.Logger?.Log("showingPopup 卡住：原版弹窗标志为 true 但我们的弹窗与消息弹窗都没开"
                    + "（此状态会让 ctrl+S 等所有编辑器快捷键失效，按一次 Esc 可清）");
            else if (wasStuck)
                Main.Logger?.Log("showingPopup 恢复正常");
        }

        /// <summary>我们的消息弹窗（<see cref="Popup"/>）当前是否显示中。</summary>
        private static bool IsMessagePopupUp()
        {
            try { return ADOFAIEditorExtension.Utils.Popup.popup != null && ADOFAIEditorExtension.Utils.Popup.popup.activeSelf; }
            catch { return false; }
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
        /// §47 跨类型多选的提示（"只能拖动、删除、复制"）：与"已选择 N 个"同一套去抖与通道
        /// （<see cref="PagerClipboard.Toast"/> → 游戏自己的 `ShowNotification`）。
        /// 一批混排选中**只提一次**：连续 ctrl 加选不刷屏；<see cref="mixedToastAnnounced"/> 在
        /// 选中集缩到 1 个 / 建起同类型批量 / 关窗时重新武装。
        /// </summary>
        private static void QueueMixedSelectionToast(int count)
        {
            if (mixedToastAnnounced)
                return;
            mixedToastAnnounced = true;
            lastToastCount = count;
            lastSelectionChangeTime = Time.unscaledTime;
            pendingToastKey = "aee.pager.mixedSelection";
            pendingToastCount = count;
            LogMultiEdit("提示排队：跨类型选中 " + count + " 个（只能拖动 / 删除 / 复制）");
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
        /// 数量稳定后把待弹的提示弹出去；顺带补做欠一次的批量存活校验（§39 H3），
        /// 以及补开被原版弹窗动画挡回去的那次打开（见 <see cref="AbortOpenForAnimation"/>）。</summary>
        internal static void Tick()
        {
            if (pendingLivenessCheck)
                VerifyBatchEventsAlive();
            FlushPendingToastIfSettled();
            if (pendingOpenTab != null)
                RetryPendingOpen();
            TickEntryButton();
        }

        /// <summary>
        /// §47 头部入口按钮的兜底刷新：其它触发点（换砖 / 面板重排 / 我们的增删收尾）都可能在
        /// 原版**没走那些方法**的路径上漏掉（键盘直接删事件、别的模组改数据），而按钮还挂在那儿就会
        /// 变成一个点不开的假入口。这里每帧只做一次 O(1) 的缓存键比较（选中的砖 + 事件总数），
        /// 键变了才重数、才重排 —— 平时什么都不干。
        /// </summary>
        private static void TickEntryButton()
        {
            if (!EntryAllowed())
                return;
            if (!TryEntryCacheKey(out int floor, out int total))
                return;
            if (floor == entryCacheFloor && total == entryCacheTotal)
                return;
            RefreshEntryButton(true);
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
            // §47 批量态归哪一页按**标签页口径**认（混排页里 openTab 会随当前事件换到别的原生标签页，
            // 只比 InspectorTab 对象会把"我们自己换过去的那一页"认成别人的箭头）。
            // 批量覆盖的事件永远是同一类型（SelectionSharesOneType）⇒ 只有**那一型**的箭头才算数，
            // 混排页里别的类型的箭头与我们的批量无关。
            if (tab != null && tab.levelEventType != fakeEvent.eventType)
                return;                            // 别的标签页的箭头，与我们的批量无关

            // 混排页里箭头动的是"当前事件那个类型的那堆"，我们的当前事件以面板为准
            LevelEvent current = IsMixedTab ? PanelSelectedEvent() : CurrentEventOfTab(tab);
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
        /// 撤销 / 重做之后（§34.5）：**直接退出多选**。
        /// 原版 `UndoOrRedo` 会把关卡里的全部事件对象换成撤销快照里的副本（IL 核过），
        /// 批量里的事件对象一个都不会留在新关卡里。以前这里"按当前数据重建 fake"的分支永远走不到，
        /// 反而在选中砖为空（取不到 stack）时直接 return，把一个全是僵尸的批量态留了下来。
        /// </summary>
        internal static void OnLevelUndoRedo(string what)
        {
            LevelEvent batch = fakeEvent;
            if (batch == null)
                return;
            ExitMultiSelect(what + "会整体换掉事件对象，批量里的事件已失效");
        }

        /// <summary>
        /// 弹窗开着时按撤销 / 重做快捷键（§45）：执行一次关卡撤销/重做，然后**不关窗**把弹窗按撤销后的
        /// 数据重刷（事件顺序、分组归属、组头、选中高亮、右侧属性面板）。
        ///
        /// **为什么要自己做**：本弹窗是 `scnEditor.ShowPopup` 打开的（`showingPopup == true`），
        /// 原版 `HandleKeyboardActions` 一见它就"只处理 Esc 然后 return"（IL 已核，见
        /// <see cref="PagerUndo"/>）⇒ 弹窗期间 Ctrl+Z / Ctrl+Y / Ctrl+Shift+Z 全部停摆。
        /// 键位判定、以及"我们处理掉按键的那一帧跳过原版方法体"沿用剪贴板那一套
        /// （<see cref="PagerClipboard.HandleKeybinds"/> + `PagerKeybindPatch`），所以一次按键只撤一步。
        ///
        /// **撤销会换掉/删掉事件对象**（原版撤销把整份关卡数据换成快照副本），
        /// 所以进撤销之前按**对象**记下选中集、当前事件与它的下标，撤销之后按对象重映射回新下标
        /// （纯逻辑见 <see cref="PagerUndo.RemapSelection"/>）：当前事件没了就停在原下标的邻居上
        /// （越界夹到末尾）；这块砖已经不满足分页条件（不再是单选砖 / 全部事件 &lt; 2）⇒ 回到
        /// "原版分页器本来就不显示"的状态，交给 <see cref="ReloadRows"/> 里的
        /// <see cref="TryGetActiveList"/> 关窗（§47：某一型缩到 1 个事件**不再关窗**）。
        /// </summary>
        internal static void UndoRedoInPopup(bool redo)
        {
            string what = redo ? "重做" : "撤销";
            scnEditor editor = scnEditor.instance;
            InspectorTab tab = openTab;
            if (!Main.IsEnabled || editor == null || tab == null || !isOpen)
                return;

            // 文本输入框有焦点 ⇒ 不拦截（输入框里 Ctrl+Z 是文本撤销，原版也是这道门）
            if (PagerUndo.SkipForInputField(editor))
            {
                Main.Logger?.Log("分页器直选：弹窗内" + what + "未拦截（文本输入框有焦点）");
                return;
            }

            // 撤销前的快照：全是**对象**（下标撤销后会变，对象本身也可能被换掉）
            List<LevelEvent> selectedBefore = SelectedPopupEvents();
            LevelEvent currentBefore = currentEvent;
            // §47 "撤销前停在第几行"用的是**显示下标**（混排页里原生 tab.eventIndex 是类型内下标，对不上号）
            int indexBefore = currentStack != null && currentStack.Count > 0
                ? Mathf.Clamp(displayedIndex >= 0 ? displayedIndex : 0, 0, currentStack.Count - 1)
                : 0;
            bool hadBatch = HasBatch();
            List<LevelEvent> stackBefore = currentStack != null ? new List<LevelEvent>(currentStack) : null;

            // 撤销 / 重做的收尾会 DeselectFloors + 按记录重选砖 + ShowPanel（原版 `UndoOrRedo` 自己做的），
            // 那会命中"面板切换就关窗"/"换砖"两条补丁 ⇒ **先**声明这是我们自己触发的，弹窗才不会被顺手关掉
            SuppressAutoClose();
            UndoOrRedoLevel(editor, redo);

            // §45.2：撤销之后的整段刷新（ReloadRows / ShowPanel / RefreshList）都放进一个**不存档**的作用域
            // （skipSaving = true ⇒ 只把 changingState + 1，不调 SaveState）。原版 `InspectorPanel.ShowPanel` 自己开的是
            // `SaveStateScope(editor,false,false,false)` ⇒ 在作用域外被调用时会压一个"只记选择"的撤销点（data == null），
            // 于是下一次 Ctrl+Z 撤掉的正是这个刚压进去的选择点，关卡本身一步也退不回去
            // —— 实测"弹窗内按撤销没反应"就是它（连按 4 次撤销，撤销栈每次都是 21→20、重做栈 1→2→3→4，levelData 一次都没换）。
            // 原版撤销自己的收尾也在它的作用域里做，所以不会有这个问题。
            using (new SaveStateScope(editor, false, false, true))
                RefreshAfterUndoRedo(redo, hadBatch, stackBefore, selectedBefore, currentBefore, indexBefore);
        }

        /// <summary>
        /// 执行一次关卡撤销 / 重做：只走原版入口（v2 没有 PACL2，撤销栈就是原版那一套；
        /// `Undo()` = `UndoOrRedo(false)`、`Redo()` = `UndoOrRedo(true)`，IL 已核）。
        /// 抛异常时不再补一次：可能已经回滚到一半，宁可这次没生效也不能撤两步。
        /// </summary>
        private static void UndoOrRedoLevel(scnEditor editor, bool redo)
        {
            try
            {
                if (redo)
                    editor.Redo();
                else
                    editor.Undo();
            }
            catch (Exception e)
            {
                Main.Logger?.Log("弹窗内" + (redo ? "重做" : "撤销") + "失败（不再重试）: " + e);
            }
        }

        private static void RefreshAfterUndoRedo(bool redo, bool hadBatch,
            List<LevelEvent> stackBefore, List<LevelEvent> selectedBefore, LevelEvent currentBefore, int indexBefore)
        {

            // 批量态：撤销把整份关卡数据换成快照副本 ⇒ 先按引用剔除已经不在关卡里的事件
            // （不足 2 个就退出多选，与原版撤销后"批量失效 ⇒ 退出"的旧行为一致）
            if (hadBatch)
                VerifyBatchEventsAlive();

            // 重新取**当前标签页口径**的列表（§47：撤销/重做之后标签页表也会跟着变 ——
            // 类型没了就少一页，All 页缩到 1 个事件就整个关掉），并按对象重映射选中集与当前事件
            List<LevelEvent> stack;
            bool listOk = TryGetActiveList(out stack);

            if (!isOpen)
            {
                // 撤销途中的面板切换/选砖（原版 UndoOrRedo 的 ShowPanel / SelectFloor）已经按原规则把弹窗关掉了
                LogUndoRedo(redo, stack != null ? stack.Count : 0, 0);
                return;
            }

            if (!listOk || stack == null || stack.Count == 0)
            {
                LogUndoRedo(redo, stack != null ? stack.Count : 0, 0);
                Close();       // 这块砖已经不满足分页条件（换砖 / 事件被撤光；§47 某一型只剩 1 个不算）
                return;
            }

            selectedIndices.Clear();
            selectionAnchor = -1;
            // 原版撤销换掉了事件对象 ⇒ 按内容指纹（忽略分组标签键）把选中与当前事件对回去（§45.2），
            // 否则高亮会停在原下标上，而不是跟着被撤回原位的那个事件走。
            // §47 混排列表里每个事件的分组标签键**可能不同**（不同类型注册的标签属性不一样），
            // 所以忽略哪个键要**逐事件**现取，不能拿 stack[0] 的一个键套整表。
            PagerUndo.RemapSelection(stack, stackBefore, selectedBefore, currentBefore, indexBefore, selectedIndices,
                out int currentIndex, FingerprintForRemap);
            if (currentIndex < 0 || currentIndex >= stack.Count)
                currentIndex = 0;
            currentEvent = stack[currentIndex];
            displayedIndex = currentIndex;

            // 顺序 / 分组归属 / 组头 / 选中高亮 / 列表高度全部按新数据重建（它内部会再判一次能不能继续）
            ReloadRows();
            if (!isOpen)
            {
                LogUndoRedo(redo, stack.Count, 0);   // 已经不满足分页条件（换砖/事件被撤光）⇒ 已经关窗了
                return;
            }

            if (HasBatch())
            {
                // 批量态还活着（原版撤销会换掉对象 ⇒ 理论上走不到这里，留着兜底）：按新数据重建 fake 并挂回面板
                ApplySelectionToPanel(false);
            }
            else
            {
                // 右侧属性面板：原版撤销实现自己会 ShowPanel（IL 已核），但那用的是撤销快照里的
                // 类型/下标；这里对齐到我们重映射后的当前事件
                //（§47 走 ShowEventInPanel —— 混排页里当前事件的类型可能已经不是当初那一页的了）
                ShowEventInPanel(currentEvent);
            }

            // 装饰栏（分组列表）：原版撤销走 UpdateDecorationObjects → 每个装饰 CallDecorationUpdate
            // → `PropertyControl_DecorationsList.OnDecorationUpdate` 置 applyOnDecorationUpdate，
            // 下一帧 `LateUpdate` → ApplyOnDecorationUpdate → FilterSearchResults（我们的分组 Build 挂在这）
            // ⇒ **原版自己会刷**；这里同帧再刷一次，免得撤销后这一帧里装饰栏还显示旧分组/旧成员。
            DecoGroupRenderer.RefreshList();

            LogUndoRedo(redo, stack.Count, LiveSelectionCount());
        }

        /// <summary>
        /// §47 撤销/重做后按内容对事件用的指纹：忽略"分组用的标签键"，而**这个键是哪个要因事件而异**
        /// （<see cref="DecoGroupActions.GroupTagKeyOf"/> 按类型给：有的类型是 eventTag、有的根本没有可分组的标签；
        /// 混排标签页里一趟列表就横跨好几个类型）⇒ 逐事件现取，不能整表套一个键。
        /// 读不到（属性缺失/异常）时退化成"不忽略任何键"，最坏情况是"拖进组之后撤销找不到同一个事件"、
        /// 停在原下标的邻居上，不会越界。
        /// </summary>
        private static string FingerprintForRemap(LevelEvent evt)
        {
            string ignoreKey = null;
            try
            {
                ignoreKey = DecoGroupActions.GroupTagKeyOf(evt, DecoGroupState.GroupSet.Event);
            }
            catch { }
            return PagerUndo.Fingerprint(evt, ignoreKey);
        }

        /// <summary>弹窗内撤销/重做各记一行（§45）：事件个数 = 撤销后这块砖该类型的事件数，选中 = 当前真实选中数。</summary>
        private static void LogUndoRedo(bool redo, int events, int selected)
        {
            Main.Logger?.Log(string.Format("分页器直选：弹窗内{0} → 事件 {1} 个，选中 {2} 个",
                redo ? "重做" : "撤销", events, selected));
        }

        /// <summary>
        /// `LevelEvent.set_Item` 的兜底入口（§32.2）：只有写的是我们的 fake 时才动。
        /// 覆盖绕过 `PropertyControl.OnValueChange` 的写值路径（OGG 编码回调、文件处理回调等），
        /// 以及"一次改多个键"的控件；幂等检查保证不会重复写/多开撤销点。
        /// </summary>
        internal static void OnFakeValueWritten(LevelEvent written, string key)
        {
            if (bindingBatch || written == null || key == null)
                return;                            // 绑定面板期间的写值是刷新副作用，不写回（见 BindFakeToPanel）
            if (fakeEvent == null || !ReferenceEquals(written, fakeEvent))
                return;
            ApplyFakeToRealEvents(key);
        }

        /// <summary>
        /// `PropertyControl.OnValueChange` 后置补丁的入口：**只有这个控件所在面板正在编辑我们的 fake**
        /// （`propertiesPanel.inspectorPanel.selectedEvent` 与 fake 是同一个对象）才写回。
        /// `OnValueChange` 是所有面板共用的出口 —— 关卡设置、粒子编辑器里改一个与混合键同名的属性，
        /// 不按归属过滤就会被当成批量编辑写到各真实事件上。控件的归属读不到时同样不动：
        /// 真正写进 fake 的编辑还有 `set_Item` 那个钩子（<see cref="OnFakeValueWritten"/>）兜着。
        /// </summary>
        internal static void OnControlValueChanged(PropertyControl control, string key)
        {
            LevelEvent fake = fakeEvent;
            if (fake == null || bindingBatch || control == null)
                return;
            LevelEvent edited = null;
            try
            {
                PropertiesPanel owner = control.propertiesPanel;
                InspectorPanel inspector = owner != null ? owner.inspectorPanel : null;
                edited = inspector != null ? inspector.selectedEvent : null;
            }
            catch { }
            if (!ReferenceEquals(edited, fake))
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
            LevelEvent oldFake = fakeEvent;
            bool had = oldFake != null;
            fakeEvent = null;
            fakeTab = null;
            fakeTabKey = PagerTabKey.MakeAll();    // §47 批量归属也一起清干净（下一次判定从"没有批量"开始）
            mixedToastAnnounced = false;           // §47 跨类型选中的提示重新武装（ ClearFakeEvent 是退批量的必经之路）
            mixedKeys.Clear();
            if (!had)
                return;
            try
            {
                InspectorPanel panel = scnEditor.instance != null ? scnEditor.instance.levelEventsPanel : null;
                // 只有面板**还指着这个 fake** 时才把它指回真实事件。原版已经把面板切到别的真实事件上
                // （切别的类型 tab、箭头切事件、换砖、撤销恢复、粘贴后选中新事件……）就别再改它 ——
                // 否则会把面板拽回旧类型 / 旧事件，覆盖掉原版刚做的选择。
                LevelEvent real = panel != null && ReferenceEquals(panel.selectedEvent, oldFake) ? RealCurrentEvent() : null;
                if (panel != null && real != null)
                {
                    panel.selectedEvent = real;
                    panel.selectedEventType = real.eventType;
                    // 面板要真的显示回单事件的值（行标签刚被摘掉 (Mixed)，值也得跟着换）
                    panel.ShowInspector(true, false);
                    // §47 走 ShowEventInPanel：混排页里 real 的类型未必是当初那一页的，直接 ShowPanel
                    // 会命中"面板切到别的类型就关窗/退多选"的补丁（把自己刚恢复的视图又关掉），
                    // 而且原生 ShowPanel 会被 editor.cacheSelectedEventIndex 覆盖掉我们传的下标。
                    ShowEventInPanel(real);
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
        /// 都推不出来就返回 null（调用方据此不动面板）—— 绝不返回一个已经不在关卡里的事件
        /// （剪切把这一堆剪空之后，`currentEvent` 就是个已删除的对象）。
        /// </summary>
        private static LevelEvent RealCurrentEvent()
        {
            InspectorTab tab = openTab ?? fakeTab;
            List<LevelEvent> stack;
            int fallbackIndex;
            if (isOpen && openTab != null && TryGetActiveList(out stack))
            {
                // §47 弹窗开着 ⇒ 按**当前标签页口径**的列表与显示下标（混排页里它不是"openTab 那一型的那堆"）
                fallbackIndex = displayedIndex;
            }
            else
            {
                // 弹窗关着（批量态还活着）：openTab 已经是 null，标签页口径无从定起 ⇒ 保持今天的口径，
                // 按 fake 那个原生标签页的类型堆推
                stack = null;
                try
                {
                    scnEditor editor = scnEditor.instance;
                    stack = editor != null && tab != null ? editor.GetSelectedFloorEvents(tab.levelEventType) : null;
                }
                catch { }
                fallbackIndex = tab != null ? tab.eventIndex : 0;
            }
            if (currentEvent != null && stack != null && stack.Contains(currentEvent))
                return currentEvent;
            if (stack != null && stack.Count > 0)
                return stack[Mathf.Clamp(fallbackIndex < 0 ? 0 : fallbackIndex, 0, stack.Count - 1)];
            if (currentEvent != null && stack == null)
            {
                // 取不到当前堆（标签页丢了等）：还在关卡事件表里才用；读不到事件表时宁可不用
                HashSet<LevelEvent> inLevel = CollectLevelEvents();
                if (inLevel != null && inLevel.Contains(currentEvent))
                    return currentEvent;
            }
            return null;
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

        /// <summary>
        /// 落点：目标分组键 + 插入锚点（AnchorIndex &lt; 0 = 落在组头上 ⇒ 插到该组末尾）。
        /// 组键可能带同型聚类（<see cref="ClusterMembersByType"/>），所以真正生效前还会被
        /// <see cref="SnapDropTargetToTypeRun"/> 夹到"被拖那一型在目标桶里那一段"的最近边界
        /// ⇒ 组头落点通常会变成一个行锚点，线也画在同一处。
        /// </summary>
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

        /// <summary>把落点夹进"被拖事件那一型在目标桶显示顺序里的那一段"的最近边界。</summary>
        private static DropTarget SnapDropTargetToTypeRun(int draggingIndex, DropTarget target)
        {
            if (currentStack == null || string.IsNullOrEmpty(target.Key) || slots.Count == 0)
                return target;

            // 目标桶的显示成员（非头行、同组键），顺序 = 槽位顺序（已经按类型聚过）
            var members = new List<int>();
            var types = new List<LevelEventType>();
            for (int i = 0; i < slots.Count; i++)
            {
                if (slots[i].IsHeader || slots[i].Key != target.Key)
                    continue;
                members.Add(slots[i].OriginalIndex);
                types.Add(slots[i].Type);
            }
            if (members.Count == 0)
                return target;                        // 空组 / 折叠组：没有段可言，照字面落点

            List<int> moving = CollectMovingIndices(draggingIndex, currentStack.Count);
            if (moving.Count == 0)
                return target;
            if (target.AnchorIndex >= 0 && moving.Contains(target.AnchorIndex))
                return target;                        // 落在正在移动的行上：走原有"不排序"分支

            // 被拖的这批必须同型，否则没有唯一的目标段可聚
            LevelEventType dragged = LevelEventType.None;
            for (int i = 0; i < moving.Count; i++)
            {
                int index = moving[i];
                if (index < 0 || index >= currentStack.Count)
                    return target;
                LevelEventType type = currentStack[index] != null ? currentStack[index].eventType : LevelEventType.None;
                if (i == 0)
                    dragged = type;
                else if (type != dragged)
                    return target;
            }

            int runStart = -1;
            for (int i = 0; i < types.Count; i++)
            {
                if (types[i] == dragged)
                {
                    runStart = i;
                    break;
                }
            }
            if (runStart < 0)
                return target;                        // 这一型第一次进该桶：尊重字面落点
            int runEnd = runStart;
            while (runEnd + 1 < types.Count && types[runEnd + 1] == dragged)
                runEnd++;

            // 期望插入位 pos = 插在 members[pos-1] 与 members[pos] 之间（pos == 成员数 ⇒ 桶尾）
            int position;
            if (target.IsHeaderDrop)
            {
                position = members.Count;             // 组头 = 插到组尾，然后再夹
            }
            else
            {
                int anchor = members.IndexOf(target.AnchorIndex);
                if (anchor < 0)
                    return target;                    // 锚点行不在这个桶（不该发生）
                position = target.Before ? anchor : anchor + 1;
            }
            position = ClampInsertPosToTypeRun(position, runStart, runEnd);

            int count = members.Count;
            DropTarget snapped = position >= count
                ? new DropTarget { Key = target.Key, AnchorIndex = members[count - 1], Before = false }
                : new DropTarget { Key = target.Key, AnchorIndex = members[position], Before = true };
            // 组头落点在这里必然变成行落点（线就画在聚类边界上）；行落点没被夹动则原样返回
            if (snapped.AnchorIndex == target.AnchorIndex && snapped.Before == target.Before)
                return target;
            return snapped;
        }

        /// <summary>纯函数：把插入位夹进同型段 [runStart, runEnd] 的两端之内（就近，绝不远跳）。</summary>
        internal static int ClampInsertPosToTypeRun(int pos, int runStart, int runEnd)
        {
            if (runStart < 0)
                return pos;
            return Mathf.Clamp(pos, runStart, runEnd + 1);
        }

        internal static void UpdateDropFeedback(Vector2 screenPosition, int draggingIndex)
        {
            // 写不进去的落点（按标签的组、但这类事件没有 eventTag 属性）不给反馈：松手也不会有任何效果
            if (!TryFindDropTarget(screenPosition, out DropTarget target) || !IsDropAcceptable(draggingIndex, target))
            {
                ClearDropFeedback();
                return;
            }
            // 先夹进被拖类型的最近段边界，再算一切反馈 ⇒ 白线与 ApplyDrop 的结果用的是同一个落点
            target = SnapDropTargetToTypeRun(draggingIndex, target);
            List<int> moving = currentStack != null ? CollectMovingIndices(draggingIndex, currentStack.Count) : new List<int> { draggingIndex };
            bool hasReassign = HasPagerReassign(draggingIndex, target);
            // 落在正在移动的行上 ⇒ 松手不排序（见 ApplyDrop）：没有要改归属的就什么都不会发生 ⇒ 不给反馈；
            // 有的话只改归属 ⇒ 保留分组高亮与跟随方块，但不画插入线
            bool anchorInMoving = !target.IsHeaderDrop && moving.Contains(target.AnchorIndex);
            if (anchorInMoving && !hasReassign)
            {
                ClearDropFeedback();
                return;
            }
            SetGroupHighlight(target.Key);
            // 落在正在移动的行上 ⇒ 不排序（见 ApplyDrop）；有改归属的只改归属、没有的什么都不做 ⇒ 不画插入线
            if (anchorInMoving)
            {
                ResolveDropObjects();
                if (dropLine != null && dropLine.gameObject.activeSelf)
                    dropLine.gameObject.SetActive(false);
                ShowCursorMark(screenPosition, moving.Count);
                return;
            }
            ShowDropIndicator(target, screenPosition, moving);
        }

        internal static void ClearDropFeedback()
        {
            SetGroupHighlight(null);
            HideDropIndicator();
            dropFeedbackLogged = false;
        }

        private static void HideDropIndicator()
        {
            if (dropLine != null && dropLine.gameObject.activeSelf)
                dropLine.gameObject.SetActive(false);
            if (cursorMark != null && cursorMark.gameObject.activeSelf)
                cursorMark.gameObject.SetActive(false);
            if (countLabel != null && countLabel.gameObject.activeSelf)
                countLabel.gameObject.SetActive(false);
        }

        /// <summary>
        /// 高亮落点分组的头行（原版选中行样式：白底黑字），传 null 还原。
        /// 还原时回到"这一行该有的颜色"（<see cref="PagerGroupHeader.OriginalLabelColor"/>，有自定义组头色
        /// 时就是按底色算出来的对比色），而不是写死白色 —— 否则浅色组头上组名与 ▼/▶ 又会看不清。
        /// </summary>
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
                {
                    PagerGroupHeader marker = rows[i].GetComponent<PagerGroupHeader>();
                    label.color = on ? Color.black : (marker != null ? marker.OriginalLabelColor : Color.white);
                }
            }
        }

        /// <summary>
        /// 落点白线的世界 Y：插到锚点行之前 ⇒ 该行上沿，之后 ⇒ 该行下沿；落在组头 ⇒ 该组末尾。
        /// 传进来的 target 已经过 <see cref="SnapDropTargetToTypeRun"/> ⇒ 线画在被拖类型那一段的边界上，
        /// 组头落点多半已变成行锚点（剩下真没得夹的才走组尾分支）。
        /// 组尾按"不算正在移动的行"找（与 ApplyDrop 里 FindGroupLastEvent 的锚点一致）。
        /// </summary>
        private static bool TryGetIndicatorWorldY(DropTarget target, List<int> moving, out float worldY)
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
                if (moving != null && moving.Contains(slots[i].OriginalIndex))
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

        private static void ShowDropIndicator(DropTarget target, Vector2 screenPosition, List<int> moving)
        {
            ResolveDropObjects();
            if (dropLine == null || !TryGetIndicatorWorldY(target, moving, out float worldY))
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

            ShowCursorMark(screenPosition, moving != null ? moving.Count : 1);
        }

        /// <summary>跟随鼠标的小方块；<paramref name="count"/> ≥ 2（多选整批）时在它右边显示 "×N"。</summary>
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

        /// <summary>懒建 "×N" 标签：字体/字号照抄弹窗里事件行名的 TMP（取不到就用 TMP 默认字体）。</summary>
        private static void EnsureCountLabel(RectTransform parent)
        {
            if (countLabel != null || parent == null)
                return;
            try
            {
                var go = new GameObject("aee_pagerDropCount", typeof(RectTransform), typeof(TextMeshProUGUI));
                var rect = (RectTransform)go.transform;
                rect.SetParent(parent, false);
                rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0f, 0.5f);
                rect.sizeDelta = new Vector2(80f, 24f);

                TextMeshProUGUI text = go.GetComponent<TextMeshProUGUI>();
                TMP_Text style = null;
                for (int i = 0; i < rows.Count && style == null; i++)
                {
                    ListItem item = rows[i] != null ? rows[i].GetComponent<ListItem>() : null;
                    TMP_Text label = item != null ? item.Get<TMP_Text>("itemName") : null;
                    if (label != null && label.font != null)
                        style = label;
                }
                if (style != null)
                {
                    text.font = style.font;
                    text.fontSharedMaterial = style.fontSharedMaterial;
                    text.fontSize = style.fontSize;
                }
                text.color = Color.white;
                text.alignment = TextAlignmentOptions.Left;
                text.enableWordWrapping = false;
                text.raycastTarget = false;
                go.SetActive(false);
                countLabel = text;
            }
            catch (Exception e)
            {
                Main.Logger?.Log("分页器拖动数量标签创建失败（不影响拖动）: " + e.Message);
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
        /// 落点生效：改归属（复用装饰分组那套：写分组标签 / 记手动归属 / 退出分组）+ 在
        /// `levelData.levelEvents` 里移到锚点旁边。多选时整批一起移动（保持原相对顺序）。
        ///
        /// 几条规则：
        ///  · **只给"不在目标组里"的事件改归属**：已经在这个组里的只排序 —— 否则拖到自己组里排个序，
        ///    也可能被"规范化"一遍（例如兜底组里的事件会被清掉标签）；
        ///  · 按标签的组、但这一类事件没有 eventTag 属性（`GroupTagKeyOf` 为 null）⇒ 写不进去 ⇒ **逐个**剔除，
        ///    剩下的照改、被剔的只跟着排序（拖进/拖出手动分组不受影响；§47 改归属在每一页都开放）；
        ///  · 落在**自己或别的正在移动的行**上 ⇒ 不排序（以前会退化成"挪到组尾"）；若其中有别组的事件，
        ///    仍按"加入这一组"改归属；
        ///  · 先干算"归属要不要改 / 顺序会不会变"，都没有就什么都不做 —— **不开 SaveStateScope**（它一开就记撤销点）。
        /// 生效后保留弹窗选中集（按对象重映射到新下标），批量态按新数据重新挂一次面板（值/混合标记与改后一致）。
        /// 顺序变了还要在同一个作用域里 `ApplyEventsToFloors()`：数组顺序改了，编辑器按砖缓存的事件表
        /// （`scnGame.ApplyEventsToFloors`）不会自己跟上（它只重分配每砖事件并重画辅助线，没有别的副作用）。
        /// </summary>
        internal static void ApplyDrop(int draggingIndex, DropTarget target)
        {
            scnEditor editor = scnEditor.instance;
            if (editor == null || currentStack == null || string.IsNullOrEmpty(target.Key))
                return;
            // §47 落点/拖动下标都是**当前标签页显示列表**里的下标
            if (!TryGetActiveList(out List<LevelEvent> stack))
            {
                Close();
                return;
            }
            if (draggingIndex < 0 || draggingIndex >= stack.Count)
                return;

            List<int> moving = CollectMovingIndices(draggingIndex, stack.Count);

            // 落点夹进被拖类型在目标桶里那一段的最近边界（与 UpdateDropFeedback 同一个函数 ⇒ 线与结果一致）。
            // 只夹锚点：Key 不变，下面的改归属 / TryResolveAssignment 仍按原目标组走。
            target = SnapDropTargetToTypeRun(draggingIndex, target);

            // 要改归属的 = 现在不在目标组里的那些。
            // §47 改归属在每一页都开放（每一页都有组头，落点才表达"进这一组"）。
            // 被拖的这批可能跨类型 ⇒ 可写性**逐事件**判（下面的 CanWriteAssignment），
            // 类型没有可写分组标签的事件只跟着排序、不写键。
            var reassign = new List<int>();
            for (int i = 0; i < moving.Count; i++)
                if (!IsInPagerGroup(moving[i], target.Key))
                    reassign.Add(moving[i]);
            DecoGroupActions.GroupAssignment assignment = default;
            bool needsTag = false;
            if (reassign.Count > 0 && !DecoGroupActions.TryResolveAssignment(target.Key, DecoGroupState.GroupSet.Event, out assignment))
            {
                LogMultiEdit("拖动忽略：落点 " + target.Key + " 解析不出归属语义（不是任何分组）");
                return;
            }
            if (reassign.Count > 0)
            {
                needsTag = assignment.Kind == DecoGroupActions.GroupAssignKind.Tag;
                // §47 逐个核可写性，不拿样本一行套整批：`GroupTagKeyOf` 按事件类型给（有的类型根本没有
                // 可写的分组标签属性），写不进去的**跳过 + 日志**（它们的顺序照样排），
                // 绝不往没注册该属性的类型里塞键。放开到混排页时这条判定直接生效。
                int unwritable = 0;
                for (int i = reassign.Count - 1; i >= 0; i--)
                {
                    int idx = reassign[i];
                    if (idx >= 0 && idx < stack.Count && CanWriteAssignment(stack[idx], assignment, needsTag))
                        continue;
                    unwritable++;
                    reassign.RemoveAt(i);
                }
                if (unwritable > 0)
                    LogMultiEdit("拖动落点 " + target.Key + "：" + unwritable
                        + " 个事件的类型没有可写的分组标签属性 ⇒ 这些只改顺序、不改归属");
            }

            bool anchorInMoving = !target.IsHeaderDrop && moving.Contains(target.AnchorIndex);
            LevelEvent anchor = null;
            bool before = target.Before;
            if (!anchorInMoving)
            {
                if (!target.IsHeaderDrop && target.AnchorIndex >= 0 && target.AnchorIndex < stack.Count)
                {
                    anchor = stack[target.AnchorIndex];
                }
                else
                {
                    anchor = FindGroupLastEvent(stack, target.Key, moving);
                    before = false;   // 组头 = 插到该组末尾（能被同型聚类夹过的组头已在上游变成行锚点，
                                      // 走到这里的是没得夹的情况：空组 / 折叠 / 混排块 / 该型第一次进桶）
                }
            }

            // 干算：哪些事件的归属真的要改；排序会不会真的改变顺序
            var toAssign = new List<LevelEvent>();
            for (int i = 0; i < reassign.Count; i++)
            {
                LevelEvent evt = stack[reassign[i]];
                if (evt != null && DecoGroupActions.NeedsChange(evt, assignment, DecoGroupState.GroupSet.Event))
                    toAssign.Add(evt);
            }
            List<LevelEvent> movingEvents = null;
            int insertAt = -1;
            bool move = !anchorInMoving && TryPlanMove(editor, stack, moving, anchor, before, out movingEvents, out insertAt);

            // 提交流程（同一个 SaveStateScope 里改完归属再改顺序 ⇒ 一步撤销）与"移到最上/最下"
            // 快捷键共用，见 <see cref="CommitOrderChange"/>。

            // 诊断：拖的是哪一行、当时选中了哪些、实际搬了哪些（排查"多选只进了一个"一类问题靠这行）
            Main.Logger?.Log(string.Format(
                "分页器直选：拖动 index={0}，选中集=[{1}]，移动=[{2}]，目标={3}，锚点={4}，改归属={5} 个，排序={6}",
                draggingIndex, string.Join(",", SortedSelection()), string.Join(",", moving), target.Key,
                target.IsHeaderDrop ? "组尾" : anchorInMoving ? "移动集合内部" : (target.Before ? "行前" : "行后"),
                toAssign.Count, move ? "是" : "否"));

            if (toAssign.Count == 0 && !move)
                return;                            // 落在自己身上 / 已经在目标位置 / 归属本来就是这个组：不留撤销点

            // 选中集按对象记下来：排序/改归属之后下标会变
            List<LevelEvent> selectedBefore = CaptureSelectedObjects(stack);

            // 改归属 + 排序都在同一个 SaveStateScope 里做完 ⇒ 一步撤销（细节见 <see cref="CommitOrderChange"/>）
            OrderCommitResult commit = CommitOrderChange(editor, move, usable =>
            {
                for (int i = 0; i < toAssign.Count; i++)
                    DecoGroupActions.ApplyAssignment(toAssign[i], assignment, DecoGroupState.GroupSet.Event);
                if (usable)
                {
                    ApplyMove(editor, movingEvents, insertAt);
                    editor.ApplyEventsToFloors();
                }
            }, "事件拖动失败: ");

            if (!commit.Applied)
                return;                            // 委托中途抛了（已记日志）：不刷列表、不重映射选中集

            RemapSelectionAfterDrop(editor, selectedBefore);
            ReloadRows();

            if (HasBatch())
            {
                // 批量态：realEvents 还是这批对象，但归属/顺序变了 ⇒ 按新数据重建 fake 并挂回面板
                // （不重复提示"已选择 N 个"）；选中集已按对象重映射，与批量保持一致
                ApplySelectionToPanel(false);
            }
            else if (toAssign.Count > 0 && currentEvent != null && currentStack != null)
            {
                // 归属变了的话，让右侧面板跟着刷新一次（eventTag 显示等）
                // §47 走 ShowEventInPanel：混排页里显示下标不是原生 ShowPanel 认的类型内下标
                ShowEventInPanel(currentEvent);
            }
        }

        /// <summary>
        /// 一次"改归属 + 改顺序"提交的结果（见 <see cref="CommitOrderChange"/>）。
        /// </summary>
        private struct OrderCommitResult
        {
            /// <summary>提交没出异常（改动与撤销点都已落盘）；false ⇒ 调用方直接返回，不刷列表。</summary>
            internal bool Applied;

            /// <summary>顺序改动确实做了（本来就计划改，而且提交没出异常）。</summary>
            internal bool OrderChanged;
        }

        /// <summary>
        /// 顺序改动的共享提交流程（<see cref="ApplyDrop"/> 与"移到最上/最下"快捷键都用它）：
        /// 一次 <c>SaveStateScope(editor, false, true, false)</c> 里跑 <paramref name="mutate"/>，
        /// 归属与顺序都落在同一个作用域 ⇒ 一步撤销。
        ///
        /// 委托的参数 <c>usable</c> 表示"顺序改动能不能做"：v2 只有原版撤销栈（整份关卡数据换快照副本），
        /// 顺序改动总是记得下来，所以恒为 true；保留这个参数是为了和调用方的写法保持一致，委托不必分支。
        /// 委托抛异常 ⇒ 只记日志、<see cref="OrderCommitResult.Applied"/> 为 false，调用方据此不刷列表
        /// （已经改下去的那部分仍由这个作用域记进撤销栈）。
        /// </summary>
        private static OrderCommitResult CommitOrderChange(scnEditor editor, bool orderChangePlanned,
            Action<bool> mutate, string failureLogPrefix)
        {
            var result = new OrderCommitResult();
            try
            {
                using (new SaveStateScope(editor, false, true, false))
                    mutate(true);

                result.Applied = true;
                result.OrderChanged = orderChangePlanned;
            }
            catch (Exception e)
            {
                Main.Logger?.Log(failureLogPrefix + e.Message);
            }
            return result;
        }

        /// <summary>
        /// 按**对象**记下当前多选集（排序 / 改归属之后下标会变，事后要按对象把选中集与当前行对回去）。
        /// </summary>
        private static List<LevelEvent> CaptureSelectedObjects(List<LevelEvent> stack)
        {
            var selected = new List<LevelEvent>();
            if (stack == null)
                return selected;
            foreach (int index in selectedIndices)
                if (index >= 0 && index < stack.Count && stack[index] != null)
                    selected.Add(stack[index]);
            return selected;
        }

        /// <summary>选中集的升序快照（日志用）。</summary>
        private static List<int> SortedSelection()
        {
            var list = new List<int>(selectedIndices);
            list.Sort();
            return list;
        }

        /// <summary>这次拖动要移动哪些行（stack 下标，升序 = 数组顺序）：拖的是选中行 ⇒ 整个选中集，否则只有它自己。</summary>
        private static List<int> CollectMovingIndices(int draggingIndex, int stackCount)
        {
            var moving = new List<int>();
            if (selectedIndices.Contains(draggingIndex))
            {
                for (int i = 0; i < stackCount; i++)
                    if (selectedIndices.Contains(i))
                        moving.Add(i);
            }
            else
            {
                moving.Add(draggingIndex);
            }
            moving.Sort();
            return moving;
        }

        private static bool IsInPagerGroup(int index, string key)
        {
            return groupKeyByIndex.TryGetValue(index, out string owner) && owner == key;
        }

        /// <summary>
        /// 落点 → 归属语义（在 `DecoGroupActions.TryResolveAssignment` 之上补一条）：要写**分组标签**的落点
        /// （按标签的组 / 填了 tag 的自定义分组）只有在这类事件确实有分组标签属性时才有效 ——
        /// `GroupTagKeyOf` 为 null（没注册 eventTag）的事件写不进去，分组读取那边也把它们当成无标签。
        /// 手动归属 / 清除归属不受影响。
        /// §47 判定**逐事件**做（旧版拿被拖那一行当样本，混排页里一拖跨好几个类型就取样取歪了）。
        /// </summary>
        private static bool CanWriteAssignment(LevelEvent evt, DecoGroupActions.GroupAssignment assignment, bool needsTag)
        {
            if (evt == null)
                return false;
            if (!needsTag)
                return true;
            return DecoGroupActions.GroupTagKeyOf(evt, DecoGroupState.GroupSet.Event) != null;
        }

        /// <summary>§47 这批移动的行里**至少有一个**写得进这个落点（写得进就值得给落点反馈，其余只改顺序）。</summary>
        private static bool AnyMovingAssignable(string key, List<int> moving)
        {
            if (currentStack == null || moving == null || moving.Count == 0)
                return false;
            if (!DecoGroupActions.TryResolveAssignment(key, DecoGroupState.GroupSet.Event, out DecoGroupActions.GroupAssignment assignment))
                return false;
            bool needsTag = assignment.Kind == DecoGroupActions.GroupAssignKind.Tag;
            for (int i = 0; i < moving.Count; i++)
            {
                int index = moving[i];
                if (index < 0 || index >= currentStack.Count)
                    continue;
                if (CanWriteAssignment(currentStack[index], assignment, needsTag))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 这次拖动有没有"要改归属"的行：全部已在目标组里 ⇒ 纯排序（不改任何值）。
        /// 拖动反馈据此判断"落在正在移动的行上"时值不值得给反馈（纯排序时什么都不会发生 ⇒ 不给），
        /// <see cref="ApplyDrop"/> 里与之对应的干算条件完全一致。
        /// </summary>
        private static bool HasPagerReassign(int draggingIndex, DropTarget target)
        {
            if (currentStack == null || draggingIndex < 0 || draggingIndex >= currentStack.Count || string.IsNullOrEmpty(target.Key))
                return false;
            List<int> moving = CollectMovingIndices(draggingIndex, currentStack.Count);
            for (int i = 0; i < moving.Count; i++)
                if (!IsInPagerGroup(moving[i], target.Key))
                    return true;
            return false;
        }

        /// <summary>拖动反馈用：这个落点松手后会不会有效（全在目标组里 = 纯排序，总是有效；否则要能改归属）。</summary>
        private static bool IsDropAcceptable(int draggingIndex, DropTarget target)
        {
            if (currentStack == null || draggingIndex < 0 || draggingIndex >= currentStack.Count || string.IsNullOrEmpty(target.Key))
                return false;
            // §47 每一页都有组头 ⇒ 要不要改归属统一按分组键 + 可写性判
            List<int> moving = CollectMovingIndices(draggingIndex, currentStack.Count);
            for (int i = 0; i < moving.Count; i++)
                if (!IsInPagerGroup(moving[i], target.Key))
                    return AnyMovingAssignable(target.Key, moving);
            return true;
        }

        /// <summary>拖动生效后把选中集按对象映射到新下标（顺序变了下标就变了），当前行的**显示下标**也跟上。</summary>
        private static void RemapSelectionAfterDrop(scnEditor editor, List<LevelEvent> selectedBefore)
        {
            // §47 用当前标签页口径的列表（过去按 openTab 的类型现推 ⇒ 混排页里映射到错的列表上）；
            // editor 参数保留给调用方（两条刷新路径都已经有 editor 在手）
            List<LevelEvent> after = null;
            TryGetActiveList(out after);
            selectedIndices.Clear();
            selectionAnchor = -1;
            if (after == null)
                return;
            for (int i = 0; i < selectedBefore.Count; i++)
            {
                int index = after.IndexOf(selectedBefore[i]);
                if (index >= 0)
                    selectedIndices.Add(index);
            }
            if (currentEvent != null)
            {
                int current = after.IndexOf(currentEvent);
                if (current >= 0)
                    displayedIndex = current;      // 原生 tab.eventIndex 由 ShowEventInPanel 管，这里不碰
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
        ///
        /// 分两步：这里只**干算**（在副本上做一遍"移除 → 插到锚点旁"，与原数组逐个按引用比较），
        /// 结果与现状完全一样就返回 false（多选整批已经连在锚点旁、落在自己原位等 ⇒ 不留撤销点；
        /// 以前的"已在末尾"只看最后一个移动事件，多选时前面几个不在末尾也会被当成无需移动）；
        /// 真正改数组的是 <see cref="ApplyMove"/>（`insertAt` 是"移除正在移动的事件之后"的下标）。
        /// </summary>
        private static bool TryPlanMove(scnEditor editor, List<LevelEvent> stack, List<int> moving, LevelEvent anchor, bool before,
            out List<LevelEvent> movingEvents, out int insertAt)
        {
            movingEvents = null;
            insertAt = -1;
            var list = editor.levelData != null ? editor.levelData.levelEvents as List<LevelEvent> : null;
            if (list == null || list.Count == 0 || moving.Count == 0)
                return false;

            var events = new List<LevelEvent>(moving.Count);
            for (int i = 0; i < moving.Count; i++)
            {
                LevelEvent evt = stack[moving[i]];
                if (evt == null || !list.Contains(evt))
                    return false;
                events.Add(evt);
            }

            var planned = new List<LevelEvent>(list);
            for (int i = 0; i < events.Count; i++)
                planned.Remove(events[i]);
            int at;
            if (anchor == null)
            {
                at = planned.Count;
            }
            else
            {
                int anchorIdx = planned.IndexOf(anchor);
                at = anchorIdx < 0 ? planned.Count : (before ? anchorIdx : anchorIdx + 1);
            }
            at = Mathf.Clamp(at, 0, planned.Count);
            planned.InsertRange(at, events);

            bool changed = false;
            for (int i = 0; i < list.Count; i++)
            {
                if (!ReferenceEquals(list[i], planned[i]))
                {
                    changed = true;
                    break;
                }
            }
            if (!changed)
                return false;                      // 顺序与现状一致：不用动

            movingEvents = events;
            insertAt = at;
            return true;
        }

        /// <summary>按 <see cref="TryPlanMove"/> 的结果真正改 `levelData.levelEvents`（先移除，再在 insertAt 处按原相对顺序插回）。</summary>
        private static void ApplyMove(scnEditor editor, List<LevelEvent> movingEvents, int insertAt)
        {
            var list = editor.levelData != null ? editor.levelData.levelEvents as List<LevelEvent> : null;
            if (list == null || movingEvents == null)
                return;
            for (int i = 0; i < movingEvents.Count; i++)
                list.Remove(movingEvents[i]);
            insertAt = Mathf.Clamp(insertAt, 0, list.Count);
            for (int i = 0; i < movingEvents.Count; i++)
                list.Insert(insertAt + i, movingEvents[i]);
        }

        // ------------------------------------------------------------------ 快捷键：移到最上 / 最下

        /// <summary>
        /// 弹窗内 Ctrl+Shift+↑ / Ctrl+Shift+↓ 的键位对象。
        /// 本工程没引 UnityEngine.InputLegacyModule，自己读不了 <c>Input.GetKeyDown</c> ⇒ 复用游戏自己的
        /// <c>EditorKeybind.IsPressed()</c>（修饰键**精确匹配**：只按 Shift、或多按 Alt 都不算命中）。
        /// 原版的 ↑/↓ 只挂了 None（切事件标签页）与 Shift（切选中事件）两组，没有 Ctrl+Shift 版本；
        /// 而且弹窗期间原版一条快捷键都不跑（showingPopup），所以不会与任何动作抢键。
        /// </summary>
        private static readonly ADOFAI.Editor.EditorKeybind MoveToTopKeybind = new ADOFAI.Editor.EditorKeybind(
            ADOFAI.Editor.KeyModifier.Control | ADOFAI.Editor.KeyModifier.Shift, KeyCode.UpArrow);

        private static readonly ADOFAI.Editor.EditorKeybind MoveToBottomKeybind = new ADOFAI.Editor.EditorKeybind(
            ADOFAI.Editor.KeyModifier.Control | ADOFAI.Editor.KeyModifier.Shift, KeyCode.DownArrow);

        /// <summary>
        /// 弹窗开着时的 Ctrl+Shift+↑/↓ 入口（<c>PagerKeybindPatch</c> 里排在剪贴板之后，同一棒）。
        /// 返回 true = 这一帧的按键由我们接手 ⇒ 前缀跳过原版方法体（理由同 <see cref="HandlePopupEscape"/>）。
        /// </summary>
        internal static bool HandleMoveToEdgeKeybind()
        {
            if (!Main.IsEnabled || !isOpen || openTab == null || currentStack == null)
                return false;
            scnEditor editor = scnEditor.instance;
            if (editor == null)
                return false;
            // 原版没被弹窗挡住时不接手（弹窗半死状态，按键照旧交给原版；同 PagerClipboard.HandleKeybinds 的门）
            if (!EditorShowsPopup(editor))
                return false;
            // 文本输入框有焦点 ⇒ 不抢按键（与弹窗内撤销/重做同一道门，见 PagerUndo.SkipForInputField）
            if (PagerUndo.SkipForInputField(editor))
                return false;

            bool toTop;
            bool toBottom;
            try
            {
                toTop = MoveToTopKeybind.IsPressed();
                toBottom = !toTop && MoveToBottomKeybind.IsPressed();
            }
            catch (Exception e)
            {
                Main.Logger?.Log("分页器直选：读取移到最上/最下的键位失败: " + e.Message);
                return false;
            }
            if (!toTop && !toBottom)
                return false;

            try
            {
                MoveSelectedEventsToEdge(editor, toTop);
            }
            catch (Exception e)
            {
                Main.Logger?.Log("分页器直选：移到最上/最下失败: " + e.Message);
            }
            return true;                         // 按下了就接手：弹窗期间原版本来也不会执行这条键位
        }

        /// <summary>
        /// 把选中的事件（没多选就用当前行那一个）移到**当前标签页显示的那份列表**里的最上 / 最下：
        /// All 页 = 这块砖上的全部事件（可跨类型）；Type 页 = 该类型的事件；「自定义分组」页 = 自定义分组里的事件。
        /// 全程以 LevelEvent 对象为单位 ⇒ 列表之外（同砖别的类型 / 别的砖）的事件一个位置都不动。
        /// 一次按键 = 一个撤销点（提交与拖动共用 <see cref="CommitOrderChange"/>，弹窗内 Ctrl+Z 一步撤回）。
        /// </summary>
        private static void MoveSelectedEventsToEdge(scnEditor editor, bool toTop)
        {
            string edge = toTop ? "上" : "下";
            List<LevelEvent> targets = SelectedPopupEvents();
            var list = editor.levelData != null ? editor.levelData.levelEvents as List<LevelEvent> : null;
            if (targets.Count == 0 || list == null || list.Count == 0)
            {
                Main.Logger?.Log("分页器直选：移到最" + edge + "忽略（没有可用事件）");
                return;
            }

            // 排序范围 = 当前标签页显示的列表（currentStack 就是 BuildSlots 记下的那一页的列表，与拖动同一份）
            if (!TryPlanEdgeMove(list, currentStack, targets, toTop, out List<LevelEvent> planned))
            {
                Main.Logger?.Log("分页器直选：已经在最" + edge + " ⇒ 什么都不做（不开撤销点、不刷列表）");
                return;
            }

            // 选中集按对象记下来：排序之后下标会变（同 ApplyDrop）
            List<LevelEvent> selectedBefore = CaptureSelectedObjects(currentStack);
            Main.Logger?.Log(string.Format("分页器直选：移到最{0}，选中集=[{1}]，参与重排 {2} 个事件",
                edge, string.Join(",", SortedSelection()), targets.Count));

            OrderCommitResult commit = CommitOrderChange(editor, true, usable =>
            {
                if (!usable)
                    return;                        // 这条快捷键只改顺序 ⇒ 顺序不被接受时就什么都不做
                for (int i = 0; i < planned.Count; i++)
                    list[i] = planned[i];          // 索引器赋值：只换位置，不增删事件
                editor.ApplyEventsToFloors();      // 数组顺序变了，按砖缓存的事件表要跟上（同 ApplyDrop）
            }, "事件移到最上/最下失败: ");

            if (!commit.Applied)
                return;
            if (!commit.OrderChanged)
                return;                            // 提交成功却没真改顺序 ⇒ 数据没变，不刷列表也不刷面板

            // 刷新整段包在 skipSaving 的作用域里（同 UndoRedoInPopup）：
            // 原版 `InspectorPanel.ShowPanel` 自己就 `new SaveStateScope(editor, false, false)` ⇒ 会再压一个
            // "只记选择"的撤销点，一次按键就要按两次 Ctrl+Z 才撤回顺序；这里把它按住 ⇒ 一步撤销。
            using (new SaveStateScope(editor, false, false, true))
                RefreshAfterEdgeMove(editor, selectedBefore);
        }

        /// <summary>移到最上/最下生效后的刷新（选中集按对象重映射 → 重建列表 → 批量态与右侧面板跟上）。</summary>
        private static void RefreshAfterEdgeMove(scnEditor editor, List<LevelEvent> selectedBefore)
        {
            RemapSelectionAfterDrop(editor, selectedBefore);
            ReloadRows();
            ScrollCurrentRowIntoView();            // 一次性：长列表里保证移动后的那一行在视野内（ReloadRows 也会滚）
            if (HasBatch())
            {
                ApplySelectionToPanel(false);      // 批量态按新数据重挂面板，不重复提示"已选择 N 个"（同 ApplyDrop）
            }
            else if (currentEvent != null && currentStack != null)
            {
                // 右侧属性面板跟到当前事件的新下标（§47 由 ShowEventInPanel 现算类型内下标；
                // 混排页里同一次移动会牵动好几个类型的分页器位置）
                ShowEventInPanel(currentEvent);
            }
        }

        /// <summary>
        /// 纯逻辑（离线可测，同 <see cref="TryPlanMove"/>）：算出"把 <paramref name="targets"/> 在
        /// <paramref name="scope"/>（= 当前标签页显示的那份列表）里移到最上 / 最下"之后的整张事件表。
        /// 成员 = <paramref name="list"/> 里属于 scope 的那些事件（按引用判等：LevelEvent 没重写
        /// Equals ⇒ HashSet 天然按引用）；新顺序只写回这些成员**当前占用的那些下标**
        /// ⇒ scope 之外的事件（别的类型、别的砖）一个位置都不动。
        /// 选中的与没选中的两组各自保持原相对顺序；targets 里不是成员的不参与。
        /// 无事可做（没选中成员 / 成员全被选中）、成员不足 2 个（最上 = 最下）或结果与现状完全一样
        /// ⇒ 返回 false，调用方据此不留撤销点。
        /// </summary>
        internal static bool TryPlanEdgeMove(List<LevelEvent> list, ICollection<LevelEvent> scope,
            List<LevelEvent> targets, bool toTop, out List<LevelEvent> planned)
        {
            planned = null;
            if (list == null || list.Count == 0 || scope == null || targets == null || targets.Count == 0)
                return false;

            var inScope = new HashSet<LevelEvent>(scope);
            var wanted = new HashSet<LevelEvent>();
            for (int i = 0; i < targets.Count; i++)
                if (targets[i] != null)
                    wanted.Add(targets[i]);
            if (wanted.Count == 0)
                return false;

            // 成员（数组顺序）与它们占用的下标（升序）
            var members = new List<LevelEvent>();
            var slots = new List<int>();
            for (int i = 0; i < list.Count; i++)
            {
                LevelEvent ev = list[i];
                if (ev == null || !inScope.Contains(ev))
                    continue;
                members.Add(ev);
                slots.Add(i);
            }
            if (slots.Count < 2)
                return false;                        // 成员不足 2 个：最上 = 最下

            var moved = new List<LevelEvent>();
            var rest = new List<LevelEvent>();
            for (int i = 0; i < members.Count; i++)
            {
                if (wanted.Contains(members[i]))
                    moved.Add(members[i]);
                else
                    rest.Add(members[i]);
            }
            if (moved.Count == 0 || rest.Count == 0)
                return false;                        // 一个都没选 / 全都选了 ⇒ 相对顺序本来就一样

            var ordered = new List<LevelEvent>(members.Count);
            if (toTop)
            {
                ordered.AddRange(moved);
                ordered.AddRange(rest);
            }
            else
            {
                ordered.AddRange(rest);
                ordered.AddRange(moved);
            }

            planned = new List<LevelEvent>(list);
            bool changed = false;
            for (int i = 0; i < slots.Count; i++)
            {
                if (!ReferenceEquals(list[slots[i]], ordered[i]))
                    changed = true;
                planned[slots[i]] = ordered[i];
            }
            if (!changed)
            {
                planned = null;
                return false;
            }
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
            BuildTabStrip(popupRoot.transform);
            BuildTopCap();
            windowInteraction = popupRoot.AddComponent<PagerWindowInteraction>();
            windowInteraction.Initialise((RectTransform)popupRoot.transform, scrollRect, rowHeight,
                HeaderHeight, FooterHeight, ListPaddingY, RowSpacing);
            // §47 标签条要跟着标签页表重建；静态事件只挂一次（Reset 里摘掉），处理器自己会看窗口是否已显示
            if (!tabStripHooked)
            {
                TabsChanged += OnTabsChanged;
                tabStripHooked = true;
            }
            windowInteraction.MinStripHeight = PagerTabStrip.RequiredHeight(tabs.Count);

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
            StopScrollAnimation();   // 重建滚动区：旧 content 即将作废
            var scrollGO = new GameObject("aee_pagerScroll", typeof(RectTransform), typeof(ScrollRect));
            var scrollRT = (RectTransform)scrollGO.transform;
            scrollRT.SetParent(host, false);
            scrollRT.anchorMin = Vector2.zero;
            scrollRT.anchorMax = Vector2.one;
            // 顶部留给标题、底部留给关闭按钮，位置固定，不需要测量宿主控件
            // §47 标签条整条在窗口**外面**（贴左边框线），不再吃列表宽度 ⇒ 左边回到普通内边距
            scrollRT.offsetMin = new Vector2(ListPaddingX, FooterHeight + ListPaddingY);
            scrollRT.offsetMax = new Vector2(-ListPaddingX, -(HeaderHeight + ListPaddingY));

            scrollRect = scrollGO.GetComponent<ScrollRect>();
            // 滚轮/拖动的处理器挂在 ScrollRect 自己的对象上：uGUI 会广播给该对象所有处理器组件，
            // ScrollRect 照常滚动，这里只把"滚到当前行"的动画掐掉，不让它和手动滚动抢位置
            scrollGO.AddComponent<PagerScrollInterrupt>();
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
        internal static void OnWindowSettingsChanged()
        {
            if (popupRoot == null || !popupRoot.activeInHierarchy || windowInteraction == null)
                return;
            // §47 交互层记下的行数可能还是"上一个页"的（手动→自动切换就是这样）：先按 ResizeHost
            // 同一套口径换算成自动模式的 All 页行数，再重算，否则开关一翻窗口高度就跟着旧页走
            windowInteraction.TrackedRows = AutoSizeRowCount(windowInteraction.TrackedRows);
            windowInteraction.SettingsChanged();
        }

        /// <summary>
        /// "自动调节窗口"开关翻转：窗口位置重新初始化到屏幕中心（只动位置，记住的宽高不动）。
        /// 弹窗开着时由交互层按"收尾拖拽 → 写中心 → 重算几何"的顺序做（顺序反了会被拖拽几何盖掉），
        /// 没开时只写偏好，下次打开 Configure 自然居中。
        /// </summary>
        internal static void RecenterWindow()
        {
            if (popupRoot != null && popupRoot.activeInHierarchy && windowInteraction != null)
            {
                windowInteraction.TrackedRows = AutoSizeRowCount(windowInteraction.TrackedRows);   // §47 同上：别拿旧页的行数定高
                windowInteraction.Recenter();
                return;
            }
            PagerWindowInteraction.WriteCenteredPreferences();
        }

        /// <summary>
        /// §47 自动尺寸模式下窗口该按多少行来定高：**恒按 All 页**（= 这块砖的事件总数，不含组头），
        /// 与当前停在哪一页无关 ⇒ 切页不再让窗口高度跳一下（Type 页多了组头就在列表里滚动）。
        /// 手动模式原样返回当前显示行数；标签页表还没算出来时（<see cref="floorEvents"/> 为空）也原样返回。
        /// </summary>
        private static int AutoSizeRowCount(int displayedRows)
        {
            if (!Main.PagerAutoWindowSize)
                return displayedRows;
            if (tabs.Count == 0 || floorEvents == null || floorEvents.Count == 0)
                return displayedRows;
            return floorEvents.Count;
        }

        private static void ResizeHost(int rowCount)
        {
            if (popupRoot == null)
                return;
            rowCount = AutoSizeRowCount(rowCount);   // §47 自动尺寸：定高只看 All 页行数
            // §47 这里是"窗口已经显示出来"的统一时点：开窗前跳过的重建（§28.1）都在这一次补上，
            // 顺带按当前页换高亮（Rebuild 比过签名，页签表没变时不重建对象）
            SyncTabStrip();
            if (windowInteraction != null)
            {
                windowInteraction.Configure(rowCount);
                return;
            }
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
            // §47 再夹一层"标签条极限重叠也摆得下"的高度：标签条上下各内缩 TopInset/BottomInset，
            //    所以这一段还要再加 24 + 20（放在屏幕夹取之后，与 MinPopupHeight 同样优先）
            wanted = Mathf.Max(wanted,
                PagerTabStrip.TopInset + PagerTabStrip.BottomInset + PagerTabStrip.RequiredHeight(tabs.Count));
            wanted = Mathf.Max(wanted, MinPopupHeight);
            // 只改高度：宽度必须保持 popupRoot 自身的原始 sizeDelta（ShowWindow 之后它就是弹窗宽度）。
            // 宽度退化成 0/负数时兜底成默认宽，避免任何负宽把水平拉伸的子元素压没。
            float width = hostRT.sizeDelta.x;
            if (width < 1f)
                width = FallbackWidth;
            hostRT.sizeDelta = new Vector2(width, wanted);
        }

        /// <summary>
        /// 显示弹窗窗口（照 MultiTrackHelper 的消息弹窗做法：先摘出去，再打开窗口，最后挂回）。
        ///
        /// 返回 false = 原版没能进入弹窗态：`ShowPopup(true, …)` 开头就是
        /// `if (popupIsAnimating &amp;&amp; show) return;`（IL 核过，`showingPopup` 在这句之后才赋值），
        /// 那时窗口虽然显示得出来，但 `showingPopup` 仍是 false ⇒ 非模态、原版快捷键照跑、我们的快捷键又不接管。
        /// 所以动画中就不调它，调完再核一次标志；失败时窗口收回（调用方负责其余状态）。
        /// </summary>
        private static bool ShowWindow()
        {
            scnEditor editor = scnEditor.instance;
            if (editor == null || popupRoot == null)
                return false;

            bool animating = EditorPopupAnimating(editor);
            popupRoot.transform.SetParent(null, false);
            popupRoot.SetActive(true);
            if (!animating)
                editor.ShowPopup(true, (scnEditor.PopupType)233, false);
            // 标志读不到（字段改名等）时按"已生效"处理：宁可沿用旧行为，也别让弹窗从此打不开
            bool shown = !animating;
            try
            {
                if (shown && editor.Get("showingPopup") is bool showing && !showing)
                    shown = false;
            }
            catch { }

            // 原版 popupWindow 是“贴顶容器”（实测弹窗落在屏幕顶部、压住关卡名栏），所以不要挂回它，
            // 改成挂到 Canvas 下、锚定 Canvas 正中心；显示/隐藏的窗口动画仍由上面的 ShowPopup 负责。
            // 代价：挂到 Canvas 后原版的隐藏路径管不到我们，Close() 里要自己 SetActive(false)。
            Canvas canvas = ResolveCanvas();
            Transform parent = canvas != null ? canvas.transform : editor.popupWindow.transform;
            popupRoot.transform.SetParent(parent, false);
            if (!shown)
            {
                popupRoot.SetActive(false);        // 挂回原处再收起，下次打开照常走上面的流程
                return false;
            }

            var popupRect = (RectTransform)popupRoot.transform;
            popupRect.anchorMin = new Vector2(0.5f, 0.5f);
            popupRect.anchorMax = new Vector2(0.5f, 0.5f);
            popupRect.pivot = new Vector2(0.5f, 0.5f);
            popupRect.anchoredPosition = Vector2.zero;
            popupRect.localScale = Vector3.one;
            // 放到最后：盖住 popupPanel 的全屏遮罩，点击也优先落在弹窗上。
            popupRoot.transform.SetAsLastSibling();

            LayoutList();
            return true;
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

        /// <summary>
        /// 滚动到当前显示的那一行（列表较长时也能一眼看到"我现在在第几个"）。
        /// 同一页内跳行用动画滑过去（点行不再瞬移）；开窗、换页、距离过短都直接落位。
        /// </summary>
        private static void ScrollCurrentRowIntoView()
        {
            // 先取走一次性标记：即使下面提前 return，标记也不会漏到下一次滚动
            bool suppress = suppressScrollToRow;
            suppressScrollToRow = false;

            if (currentRowOrder < 0 || scrollRect == null || listContent == null || scrollRect.viewport == null)
                return;

            StopScrollAnimation();   // 新的目标接管：旧动画继续跑会把位置算歪

            float contentHeight = listContent.rect.height;
            float viewportHeight = scrollRect.viewport.rect.height;

            // 点组头（折叠 / 组名）：不居中、不滑，只把当前偏移夹回合法区间
            //（列表被折叠 / 重建后可能变短，停在原处会落到范围之外）
            if (suppress)
            {
                float currentY = listContent.anchoredPosition.y;
                listContent.anchoredPosition = new Vector2(0f,
                    Mathf.Clamp(currentY, 0f, Mathf.Max(0f, contentHeight - viewportHeight)));
                return;
            }

            float target = 0f;       // 内容不足一屏 ⇒ 回到顶部
            if (contentHeight > viewportHeight && viewportHeight > 1f)
            {
                float rowStride = rowHeight + RowSpacing;
                target = currentRowOrder * rowStride - (viewportHeight - rowHeight) * 0.5f;
                target = Mathf.Clamp(target, 0f, contentHeight - viewportHeight);
            }

            bool snap = !isOpen       // 开窗那次：窗口还在淡入，滑过去只会看到内容在遮罩底下动
                || snapNextScroll     // 换页 / 换到别的列表：那是另一份内容，没有"滑过去"的意义
                || Mathf.Abs(target - listContent.anchoredPosition.y) < ScrollSnapDistance;
            snapNextScroll = false;
            if (snap)
            {
                listContent.anchoredPosition = new Vector2(0f, target);
                return;
            }

            scrollTween = DOTween.To(
                () => listContent != null ? listContent.anchoredPosition.y : 0f,
                y => { if (listContent != null) listContent.anchoredPosition = new Vector2(0f, y); },
                target, ScrollAnimSeconds).SetEase(Ease.OutCubic).SetUpdate(true);
        }

        /// <summary>
        /// 掐掉滚动动画（不补完，停在当前位置）：关窗、开始拖行、用户自己滚动、
        /// 以及滚动区被销毁/重建时都调它，免得动画把已销毁的对象当目标或跟手动滚动抢位置。
        /// </summary>
        internal static void StopScrollAnimation()
        {
            if (scrollTween != null && scrollTween.active)
                scrollTween.Kill(false);
            scrollTween = null;
        }

        /// <summary>
        /// 让接下来的那次"滚到当前行"只保持现有视野：点组头（折叠箭头 / 组名）只改列表内容，
        /// 不该把视野拉到当前选中的事件行；点事件行仍然照常滚动。
        /// 顺带掐掉在跑的滑动动画 —— 用户点组头时列表还在滑的话，得先停下来。
        /// </summary>
        internal static void SuppressNextScrollToRow()
        {
            suppressScrollToRow = true;
            StopScrollAnimation();
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
