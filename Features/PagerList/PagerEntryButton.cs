using ADOFAI;
using System;
using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ADOFAIEditorExtension.Features.PagerList
{
    /// <summary>
    /// §47 分页器「全部事件」的入口按钮：挂在原生事件面板头部（<c>titleCanvas</c>），
    /// **紧贴 <c>deleteButton</c> 的左边**，点开 = 分页器从 **All 页**开始。
    ///
    /// 为什么要有它：分页器箭头那三个入口的门槛是「**这一型** >= 2 个事件」，而 All 页的门槛放宽成
    /// 「整块砖 >= 2 个事件（任意类型）」—— 一块砖上 jump 1 个 + hold 1 个时没有任何箭头可点，
    /// 所以需要一个类型无关的入口。
    ///
    /// 位置（scene dump：deleteButton anchors (1,1)、pos (-8,-32)、size 40×40、pivot (1,0.5)）：
    ///  · 与 deleteButton **同锚点、同 pivot、同尺寸**，只把 anchoredPosition.x 往左挪 (宽 + 8) ⇒
    ///    落点 pos (-56,-32)，占 x W-96..W-56，与删除按钮（W-48..W-8）之间留 8 的空隙；
    ///  · 挪完把原生 <c>title</c> 的**右边界**同样缩 (宽 + 8)，长事件名才不会再钻到按钮底下；
    ///    隐藏 / 拆掉 / 场景重载时复原（见 <see cref="RestoreTitle"/>）。
    ///  · 不再摆到面板左上的 disableButton 外侧 —— 那一版会盖到原生标签列（真机 bug B）。
    ///
    /// 硬约束（都是这里绕开的坑）：
    ///  · **不能挂在 <c>InspectorPanel.tabs</c> 下面** —— 原版 <c>ShowTabsForFloor</c> 结尾会按名字
    ///    逐个 SetActive 那一层的每个子对象（InspectorPanel.cs:626），挂进去就会被当成标签页关掉；
    ///  · 图标自己画三根白杠，不用字体的特殊字形（本模组要出 4 种语言的字集不一）；
    ///  · **不复制参照按钮的 sprite** —— 参照是删除按钮，抄过来就是一个垃圾桶上压三根杠（真机 bug B）。
    ///    底板只当**透明点击区**（alpha 0 但吃射线），字形是子物体三根杠，静止色取自参照按钮 Image 的
    ///    底色，明暗与邻居一致；
    ///  · 反馈照邻居删除按钮那套做**悬停染色**（它是 Button 的 ColorTint 在 prefab 里配的红）：我们的字形
    ///    是三个 Image，ColorTint 只作用在透明底板上 ⇒ 由 <see cref="BarTint"/> 接指针事件把三根杠一起
    ///    染成天蓝（按下更深一档），淡入淡出时长取参照按钮的 <c>ColorBlock.fadeDuration</c>；
    ///  · 位置**每次按 deleteButton 的实际矩形现算**，并且 <c>LayoutElement.ignoreLayout</c>
    ///    —— 否则父级有布局组时会把我们挪走。
    ///
    /// 生命周期照 <c>DecoGroupModeButton</c> 的 Attach / Refresh / Detach / Reset 四件套，
    /// 由 <see cref="PagerListController"/> 在换砖、面板重排、功能开关这些时机驱动。
    /// </summary>
    internal static class PagerEntryButton
    {
        private const string ButtonName = "aee_pagerEntryButton";
        private const float Gap = 8f;             // 与删除按钮之间的留白（期望落点 pos (-56,-32)）
        private const float MinUsableSize = 6f;   // 参照按钮小到这个程度 = 布局还没跑过，这一轮先不摆
        private const float BarsWidth = 22f;      // 三根杠的整体字形 22×16，居中，不随按钮尺寸缩放
        private const float BarsThickness = 3f;
        private const float BarsStep = 6.5f;

        private static GameObject buttonRoot;
        private static RectTransform rect;
        private static Image background;          // 透明点击区
        private static BarTint tint;              // §47 悬停/按下给三根杠染色
        private static readonly Image[] bars = new Image[3];
        private static InspectorPanel owner;      // 建在哪个面板上（面板/场景换了要重建）

        // 为让位给按钮而缩过右边界的原生标题（复原只在真改过时做）
        private static RectTransform titleRect;
        private static Vector2 titleOriginalOffsetMax;
        private static bool titleShrunk;

        /// <summary>定位参照：只要删除按钮（它就在头部右端，我们要贴着它左边）。</summary>
        private static Button Reference(InspectorPanel panel)
        {
            Button delete = panel != null ? panel.deleteEventButton : null;
            if (delete != null && delete.transform.parent != null)
                return delete;
            return null;
        }

        /// <summary>
        /// 建按钮（幂等：已经挂在这个面板上就直接返回，每次刷新都会走到这里）。
        /// 参照按钮拿不到就什么都不建 —— 没有对齐依据时宁可不出现。
        /// </summary>
        internal static void Attach(InspectorPanel panel)
        {
            Button reference = Reference(panel);
            if (reference == null)
                return;
            RectTransform parent = reference.transform.parent as RectTransform;
            if (parent == null)
                return;

            if (buttonRoot != null && ReferenceEquals(owner, panel) && rect != null && rect.parent == parent)
                return;

            Destroy();

            // 纯自建 UGUI（Image + Button）：不 Instantiate 原版按钮 ——
            // 克隆会把原版那一堆组件和**已挂好的持久监听**（删除事件！）一起带过来，得逐个剥。
            var go = new GameObject(ButtonName, typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
            rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            background = go.GetComponent<Image>();
            // 底板只当点击区：alpha 0 但吃射线（Image 的命中判定不看 alpha）。
            // 不抄参照按钮的 sprite —— 那是垃圾桶，压三根杠上去就是上一版的错（真机 bug B）
            background.sprite = null;
            background.color = new Color(1f, 1f, 1f, 0f);
            background.raycastTarget = true;

            // 子件只有三根杠（uGUI 同级按 sibling 顺序绘制）：不再叠"圆角框亮底"，
            // 悬停反馈改成像邻居删除按钮那样把字形染色（见 <see cref="BarTint"/>）
            for (int i = 0; i < bars.Length; i++)
            {
                bars[i] = CreateChild(rect, "bar" + i);
                bars[i].color = Color.white;                    // 三根白杠：不依赖字体，任何语言都画得出来
            }

            Button button = go.GetComponent<Button>();
            button.targetGraphic = background;
            // 底板是透明的，ColorTint 只会作用在一个看不见的 Graphic 上 ⇒ 明确关掉过渡，
            // 悬停/按下由 BarTint 直接染三根杠（选中态不再用亮底表达，位置与 tooltip 由原版头部语义承担）
            button.transition = Selectable.Transition.None;
            // 必须换掉：ButtonClickedEvent 是**持久监听**，将来若改成克隆原版按钮，
            // 原版「删除事件」的回调会跟着克隆体过来（现在就明写着，免得以后踩）
            button.onClick = new Button.ButtonClickedEvent();
            button.onClick.AddListener(OnClick);

            tint = go.AddComponent<BarTint>();
            tint.Bind(bars);
            ApplyGlyphColor(reference);

            // 父级若有布局组，它会覆盖 anchoredPosition ⇒ 声明忽略布局，位置完全由我们算
            LayoutElement element = go.GetComponent<LayoutElement>();
            element.ignoreLayout = true;

            rect.SetAsLastSibling();                            // 画在头部其它件之上，避免被相邻按钮盖住
            go.SetActive(false);
            buttonRoot = go;
            owner = panel;
        }

        private static void OnClick()
        {
            if (!Main.IsEnabled)
                return;                // 补丁已撤而组件还在（禁用模组没重载编辑器）：不要响应
            PagerListController.OnEntryClick();
        }

        /// <summary>
        /// 刷新可见性与位置（位置每次都重算：删除按钮摆在哪是原版布局说了算）。
        /// §47 不再有"弹窗开着且停在 All 页"的亮底状态 —— 反馈交给悬停染色。
        /// </summary>
        internal static void Refresh(bool visible)
        {
            if (buttonRoot == null)
                return;
            Button reference = Reference(owner);
            bool headAlive = reference != null && reference.gameObject.activeInHierarchy;
            bool active = visible && headAlive && Place(reference);
            if (buttonRoot.activeSelf != active)
                buttonRoot.SetActive(active);
            if (!active)
                RestoreTitle();                 // 按钮不在位 ⇒ 标题不该留着被缩掉的右边界
            if (active)
                ApplyGlyphColor(reference);
        }

        /// <summary>
        /// 静止色取参照按钮 Image 的底色（明暗与邻居一致），淡入淡出时长取参照按钮的 ColorBlock；
        /// 实际染色由 <see cref="BarTint"/> 在悬停/按下时覆盖成强调色。
        /// </summary>
        private static void ApplyGlyphColor(Button reference)
        {
            Image image = reference != null ? reference.targetGraphic as Image : null;
            if (image == null && reference != null)
                image = reference.GetComponent<Image>();
            Color tone = image != null ? image.color : Color.white;
            if (tone.a < 0.05f)
                tone.a = 1f;                    // 有些头部按钮靠 sprite 自身着色、Image 颜色是透明的：按满不透明画
            if (tint == null)
            {
                for (int i = 0; i < bars.Length; i++)
                    if (bars[i] != null)
                        bars[i].color = tone;
                return;
            }
            float fade = 0.1f;                  // ColorBlock 的默认档（读不到参照按钮时用）
            try { fade = reference.colors.fadeDuration; } catch { }
            tint.SetFade(fade);
            tint.SetIdle(tone);
        }

        /// <summary>
        /// 贴到删除按钮**左边**：同锚点、同 pivot、同尺寸，只把 anchoredPosition.x 往左挪 (宽 + 8)。
        /// 参照它的实际矩形现算而不是写死 (-56,-32)：原版头部尺寸一改我们不用跟着改代码。
        /// </summary>
        private static bool Place(Button reference)
        {
            var del = reference.transform as RectTransform;
            if (del == null)
                return false;
            Vector2 size = del.rect.size;
            if (size.x < MinUsableSize || size.y < MinUsableSize)
                return false;          // 布局还没跑过（矩形为 0）：等下一次重排再摆

            rect.anchorMin = del.anchorMin;
            rect.anchorMax = del.anchorMax;
            rect.pivot = del.pivot;
            rect.sizeDelta = del.sizeDelta;
            rect.localScale = del.localScale;
            rect.anchoredPosition = del.anchoredPosition + new Vector2(-(size.x + Gap), 0f);
            LayoutBars();
            ShrinkTitle(size.x + Gap);
            return true;
        }

        /// <summary>三根杠固定 22×16 居中（字形不随按钮尺寸缩放，视觉重量才和邻居的图标一致）。</summary>
        private static void LayoutBars()
        {
            for (int i = 0; i < bars.Length; i++)
            {
                Image bar = bars[i];
                if (bar == null)
                    return;
                RectTransform barRect = bar.rectTransform;
                barRect.anchorMin = barRect.anchorMax = new Vector2(0.5f, 0.5f);
                barRect.pivot = new Vector2(0.5f, 0.5f);
                barRect.sizeDelta = new Vector2(BarsWidth, BarsThickness);
                barRect.anchoredPosition = new Vector2(0f, (1f - i) * BarsStep);
            }
        }

        /// <summary>
        /// 把原生标题的右边界往左缩 <paramref name="delta"/>（= 按钮宽 + 间距），长事件名就不会伸到按钮底下。
        /// 改 <c>offsetMax.x</c>：它是"矩形右上角相对锚点右上角的偏移"，动它就等于动右边界，
        /// 与锚点/pivot 的组合无关（原版标题是 (0,1)-(1,1) 拉伸，右边界正好落在面板内侧）。
        /// 只在第一次改时记下原值，之后每次都按记下的原值重算 —— 原版重排把标题改回去我们也补得回来。
        /// </summary>
        private static void ShrinkTitle(float delta)
        {
            TMP_Text title = owner != null ? owner.title : null;
            RectTransform next = title != null ? title.rectTransform : null;
            if (next == null)
            {
                RestoreTitle();                  // 标题取不到：不能把上一次缩掉的右边界留在原地
                return;
            }
            if (!ReferenceEquals(next, titleRect))
            {
                RestoreTitle();                  // 面板重建换了标题对象：先把旧的复原，再认领新的
                titleRect = next;
            }
            if (!titleShrunk)
            {
                titleOriginalOffsetMax = next.offsetMax;
                titleShrunk = true;
            }
            float wanted = titleOriginalOffsetMax.x - delta;
            if (Mathf.Abs(next.offsetMax.x - wanted) > 0.01f)
                next.offsetMax = new Vector2(wanted, next.offsetMax.y);
        }

        /// <summary>复原标题右边界（只在我们真改过时才碰；对象已随场景销毁时 fake-null 判空跳过）。</summary>
        private static void RestoreTitle()
        {
            if (titleShrunk && titleRect != null)
                titleRect.offsetMax = titleOriginalOffsetMax;
            titleShrunk = false;
            titleRect = null;
            titleOriginalOffsetMax = default(Vector2);
        }

        private static Image CreateChild(RectTransform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            var child = (RectTransform)go.transform;
            child.SetParent(parent, false);
            child.anchorMin = Vector2.zero;
            child.anchorMax = Vector2.one;
            child.offsetMin = Vector2.zero;
            child.offsetMax = Vector2.zero;
            var image = go.GetComponent<Image>();
            image.sprite = null;
            image.raycastTarget = false;        // 射线统一交给按钮本体，子件不吃
            return image;
        }

        /// <summary>模组被禁用（编辑器没重载）：把按钮拆掉并复原标题，别留一个点不动的假入口。</summary>
        internal static void Detach()
        {
            Destroy();
            owner = null;
        }

        /// <summary>编辑器重载：对象随场景一起没了，只清引用（下一次 <c>ShowTabsForFloor</c> 会重建）。
        /// 标题的复原靠 <see cref="RestoreTitle"/> 的 fake-null 判断：随场景销毁就什么都不做。</summary>
        internal static void Reset()
        {
            RestoreTitle();
            buttonRoot = null;
            rect = null;
            background = null;
            tint = null;
            for (int i = 0; i < bars.Length; i++)
                bars[i] = null;
            owner = null;
        }

        private static void Destroy()
        {
            RestoreTitle();
            if (buttonRoot != null)
                UnityEngine.Object.Destroy(buttonRoot);
            buttonRoot = null;
            rect = null;
            background = null;
            tint = null;
            for (int i = 0; i < bars.Length; i++)
                bars[i] = null;
        }

        /// <summary>
        /// §47 悬停/按下染色：邻居删除按钮的"悬停变红"是 Button 的 ColorTint（prefab 里配的 ColorBlock），
        /// 而我们的字形是三根独立的 Image、底板透明 —— ColorTint 只会染在一个看不见的 Graphic 上。
        /// 所以这里自己接指针事件，把三根杠一起染成天蓝（按下深一档），淡入淡出时长与开关时机
        /// 都照参照按钮的 ColorBlock（fadeDuration、忽略 Time.timeScale、保持 alpha）。
        /// </summary>
        private sealed class BarTint : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler, IPointerUpHandler
        {
            private static readonly Color HoverColor = new Color(0.40f, 0.78f, 1f, 1f);     // 天蓝（悬停）
            private static readonly Color PressColor = new Color(0.26f, 0.60f, 0.84f, 1f);  // 同色深一档（按下）

            private Image[] targets;            // 就是那三根杠（外部数组，元素可能随重建变 null）
            private Color idle = Color.white;   // 静止色：参照按钮 Image 的底色
            private float duration = 0.1f;
            private bool hovering, pressing;
            private Coroutine routine;

            internal void Bind(Image[] bars) => targets = bars;

            internal void SetFade(float seconds) => duration = seconds > 0f ? seconds : 0f;

            /// <summary>换砖/换主题后重设静止色；没在悬停/按下时立刻落到这个色。</summary>
            internal void SetIdle(Color color)
            {
                idle = color;
                if (!hovering && !pressing)
                    Slide(idle);
            }

            public void OnPointerEnter(PointerEventData eventData)
            {
                hovering = true;
                Apply();
            }

            public void OnPointerExit(PointerEventData eventData)
            {
                hovering = pressing = false;
                Apply();
            }

            public void OnPointerDown(PointerEventData eventData)
            {
                pressing = true;
                Apply();
            }

            public void OnPointerUp(PointerEventData eventData)
            {
                pressing = false;
                Apply();
            }

            /// <summary>不可点（interactable = false，例如原版在批量态里把头部按钮禁了）时不染色。</summary>
            private void Apply()
            {
                Button button = GetComponent<Button>();
                if (button == null || !button.interactable)
                {
                    Slide(idle);
                    return;
                }
                Slide(pressing ? WithIdleAlpha(PressColor) : hovering ? WithIdleAlpha(HoverColor) : idle);
            }

            /// <summary>强调色只换 rgb，alpha 跟着静止色（字形原本的透明度）。</summary>
            private Color WithIdleAlpha(Color accent) => new Color(accent.r, accent.g, accent.b, idle.a);

            private void Slide(Color to)
            {
                if (targets == null)
                    return;
                if (routine != null)
                {
                    StopCoroutine(routine);
                    routine = null;
                }
                Color from = targets.Length > 0 && targets[0] != null ? targets[0].color : to;
                if (duration <= 0f || !isActiveAndEnabled || ColorSame(from, to))
                {
                    SetBars(to);
                    return;
                }
                routine = StartCoroutine(SlideRoutine(from, to));
            }

            private IEnumerator SlideRoutine(Color from, Color to)
            {
                float elapsed = 0f;
                while (elapsed < duration)
                {
                    elapsed += Time.unscaledDeltaTime;            // 与 Selectable 一致：ignoreTimeScale
                    SetBars(Color.Lerp(from, to, Mathf.Clamp01(elapsed / duration)));
                    yield return null;
                }
                routine = null;
                SetBars(to);
            }

            private void SetBars(Color color)
            {
                if (targets == null)
                    return;
                for (int i = 0; i < targets.Length; i++)
                    if (targets[i] != null)
                        targets[i].color = color;
            }

            private static bool ColorSame(Color a, Color b)
            {
                return Mathf.Abs(a.r - b.r) < 0.004f && Mathf.Abs(a.g - b.g) < 0.004f
                    && Mathf.Abs(a.b - b.b) < 0.004f && Mathf.Abs(a.a - b.a) < 0.004f;
            }

            /// <summary>收起/换场景：回到静止色并忘掉悬停态（不然再显示时还压着天蓝）。</summary>
            private void OnDisable()
            {
                hovering = pressing = false;
                StopAllCoroutines();
                routine = null;
                SetBars(idle);
            }
        }
    }
}
