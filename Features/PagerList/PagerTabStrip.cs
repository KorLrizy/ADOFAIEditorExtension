using System;
using System.Collections.Generic;
using System.Text;
using ADOFAI;
using ADOFAIEditorExtension.Features.DecoGrouping;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ADOFAIEditorExtension.Features.PagerList
{
    /// <summary>
    /// §47 分页器标签条：**在弹窗外面**，贴在窗口左边框线上（与原版 inspector 的 <c>tabs</c> 容器同一套几何，
    /// 见 <see cref="BorderOverlap"/> 那一组常量）。内容 = All 一枚 + 每个事件类型一枚 + 「自定义分组」一枚
    /// （所有自定义事件分组合并成同一页）。
    ///
    /// 页签表完全由 <see cref="PagerListController.Tabs"/> 决定（空页签根本进不了那张表），
    /// 这里只负责画、排版、把点击转成 <see cref="PagerListController.SwitchTab"/>。
    /// 挂在弹窗根对象上、**不是** ScrollRect 的子对象：挂进去会被视口 RectMask2D 裁掉，
    /// 还会让指针停在标签条上时滚轮不再作用于列表。
    /// 因为现在整条都在窗口矩形**之外**，弹窗的点击挡板（<c>aee_pagerFrameBlock</c>）盖不到它 ⇒
    /// 标签条根自己带一块透明 Image 接住页签之间的空隙，免得点空的地方漏到身后的编辑器上。
    ///
    /// 外观（需求"要复刻原版右侧 inspector 页签：圆角框页签 + 图标"）：
    ///  · 几何与贴图都在运行时从原生页签 prefab <c>RDConstants.data.prefab_tab</c> 上取
    ///    （<see cref="ProbeNative"/>）：它的 <c>button</c> Image 提供圆角框 sprite / 绘制类型 /
    ///    九宫格倍数 / 材质，它的 <c>icon</c> Image 的 RectTransform 提供图标槽（48×60 页签里
    ///    anchors (0,0.5)、pos (23,0)、30×30）。**不 Instantiate** —— 那会带进 InspectorTab 逻辑、
    ///    cycleButtons（上一枚/下一枚箭头 + "1/8" 文本，我们不要）和原版持久监听。
    ///  · 取不到（资源未加载等）就退回"无 sprite 的半透明平板"，只记一条日志。
    ///  · 明暗完全照 <c>InspectorTab.SetSelected</c>（反编译 :96-:102）：底板 normalColor 白 @0.7 已选 /
    ///    @0.45 未选，图标 白 @1.0 已选 / @0.6 未选。§47 标题首字符（取不到图标的类型页签）走**图标那一档**（白 + 内容透明度）—— 底板是描边框、内部透出深色底，
    ///    按底板算对比色会在选中态给出黑字（文字"消失"）。不做 hover 变色（Button 保持 Transition.None，
    ///    选中态由 <see cref="BackgroundColor"/> 统一算，悬停信息交给提示浮层）。
    ///  · 镜像照 <c>InspectorTab.FlipTab</c>（:64-:69）：原版页签在面板**左侧**、圆角开口朝面板（右），
    ///    对整个页签 ScaleX(-1)、再对图标 ScaleX(-1) 转回来（图标本身不镜像）。我们这条也在窗口左侧、
    ///    开口同样朝窗口 ⇒ 只镜像底板 Image、图标槽按页签中心镜像落位、槽内容正立 —— 视觉效果与原版一致，
    ///    而页签根保持正缩放（根带负缩放会把悬停判定/浮层定位的 x 一起翻掉）。
    ///
    /// 窗口变矮时的规则（需求里的"可以部分重叠，但不许全叠成一坨"）：
    ///  · 标称间距 = 68（= 原生 <c>InspectorPanel.tabHeight</c>，页签高 60 + 空隙 8），放得下就正常排；
    ///  · 放不下就压间距到 (可用高 - 页签高) / (枚数 - 1)，但**不低于 <see cref="MinPitch"/>（24，
    ///    约 40% 页签高，保证露出一条能点的窄边）**；
    ///  · 窗口最小高度按"极限重叠刚好摆得下"算（<see cref="RequiredHeight"/>，再加上下内缩
    ///    <see cref="TopInset"/> + <see cref="BottomInset"/>），所以正常操作下
    ///    压到 <see cref="MinPitch"/> 之前窗口就先被夹住了。
    /// </summary>
    internal sealed class PagerTabStrip : MonoBehaviour
    {
        // 固定几何：页签 48×60、标称间距 68，全部对齐原生 inspector 页签（见类型注释）
        internal const float Width = 48f;         // 标签条宽 = 页签宽（原生 tabs 容器就是 48 宽）
        internal const float TabHeight = 60f;     // 原生 inspectorTab 的高
        internal const float PitchExtra = 8f;     // 标称间距 = 60 + 8 = 68 = 原生 tabHeight
        internal const float MinPitch = 24f;      // 极限重叠时的最小间距（约 40% 页签高）
        // §47 标签条**整条在窗口外**、贴左边框线：数值照抄原生 tabs 容器
        // （anchors (0,0)-(0,1)、pivot (1,1)、pos (3,-24)、sizeDelta (48,-44)）
        internal const float BorderOverlap = 3f;  // 右沿压进窗口左沿多少个单位（正好压在边框线上）
        internal const float TopInset = 24f;      // 标签条顶边距窗口顶
        internal const float BottomInset = 20f;   // 标签条底边距窗口底
        private const float LabelSize = 18f;      // 取不到图标的类型页签的首字符：塞进 30×30 的图标槽

        /// <summary>
        /// §47 标签条**探出窗口左沿**的量：夹屏幕边界时左边要按这一段留出余量，
        /// 否则窗口贴到屏幕最左时标签条整条出屏（只在 <c>PagerWindowInteraction.TryGetBounds</c> 一处用）。
        /// 标签条在窗口外 ⇒ 列表不再为它让宽，所以这里不再有 "ListInset"。
        /// </summary>
        internal static float LeftReserve => Width - BorderOverlap;

        /// <summary>
        /// 标签条需要的净高（= 窗口最小高度里属于标签条那一段，**还要另加** <see cref="TopInset"/> +
        /// <see cref="BottomInset"/> 两段内缩）：
        /// n 枚页签**极限重叠**后仍摆得下的高度，所以窗口再矮就会把某枚页签挤到完全看不见。
        /// </summary>
        internal static float RequiredHeight(int tabCount)
        {
            if (tabCount <= 1)
                return TabHeight;
            return TabHeight + (tabCount - 1) * MinPitch;
        }

        // 图标槽取不到原生布局时的兜底（scene dump：anchors (0,0.5)、pos (23,0)、size 30×30）
        private static readonly Vector2 FallbackIconAnchor = new Vector2(0f, 0.5f);
        private static readonly Vector2 FallbackIconPivot = new Vector2(0.5f, 0.5f);
        private static readonly Vector2 FallbackIconPos = new Vector2(23f, 0f);
        private static readonly Vector2 FallbackIconSize = new Vector2(30f, 30f);

        // 原生 SetSelected 的明暗档（InspectorTab.cs :96 底板、:101 图标）
        private const float FrameAlphaSelected = 0.7f;
        private const float FrameAlphaUnselected = 0.45f;
        private const float ContentAlphaSelected = 1f;
        private const float ContentAlphaUnselected = 0.6f;

        /// <summary>
        /// §47 原生页签外观的运行时快照（<see cref="ProbeNative"/>）：页签底板的 sprite / 绘制类型 /
        /// 九宫格倍数 / 材质，与图标槽的布局。取不到就退回兜底样式。
        /// </summary>
        private struct NativeVisual
        {
            internal bool HasFrame;           // 拿到了底板 Image（sprite 可能为 null，那本来就是纯色底）
            internal bool Probed;             // prefab 找到了：不再重探（探不到时留 false，下次重建再试）
            internal bool Logged;             // 缺件的日志只记一次
            internal Sprite Frame;
            internal Image.Type FrameType;
            internal float FramePixelsPerUnit;
            internal Material FrameMaterial;
            // 原生页签可能不止"一圈描边"：prefab 里若另有一块铺满页签的填充底（不是 icon 那一个），
            // 这里记下来，AEE 页签照抄同一层 —— 否则描边内部是透明的，身后的原生页签会透出来。
            internal bool HasFill;
            internal Sprite FillSprite;
            internal Image.Type FillType;
            internal Color FillColor;
            internal float FillPixelsPerUnit;
            internal Material FillMaterial;
            internal bool HasIconLayout;
            internal Vector2 IconAnchorMin;
            internal Vector2 IconAnchorMax;
            internal Vector2 IconPivot;
            internal Vector2 IconPos;
            internal Vector2 IconSize;
        }

        private static NativeVisual native;
        private static bool probeErrorLogged;   // 探测异常只记一次（每次重建都会重试探测本身）

        /// <summary>一枚页签。字段都是建好之后只读，只有颜色/位置会随选中与尺寸变。</summary>
        private sealed class Item
        {
            internal GameObject Go;
            internal RectTransform Rect;
            internal Image Fill;              // 填充底（原生 prefab 里真有这一层时才建；画在描边框之下）
            internal Image Background;
            internal Image Icon;              // Type 页签的事件图标
            internal TMP_Text Label;          // 取不到图标的类型页签的标题首字符
            internal Image[] Bars;            // All / 自定义分组页签的杠条字形
            internal PagerTabKey Key;
            internal bool HasColor;
            internal Color Tint;
        }

        private readonly List<Item> items = new List<Item>();
        private RectTransform root;
        private TMP_Text fontSource;
        private string signature;                 // 页签表内容签名：一致就不重建对象，只重排 + 换高亮
        private bool layingOut;                   // 排版里不要再触发自己

        /// <summary>由 <c>PagerListController.BuildTabStrip</c> 在建好对象后调用一次。</summary>
        internal void Attach(RectTransform stripRoot, TMP_Text source)
        {
            root = stripRoot;
            fontSource = source;
        }

        /// <summary>
        /// 按 <see cref="PagerListController.Tabs"/> 刷新整条：内容变了才重建页签，
        /// 没变（例如只是切了页 / 换了窗口尺寸）就只重排 + 换高亮 —— 这两条路径都很频繁。
        /// </summary>
        internal void Rebuild()
        {
            if (root == null)
                return;
            if (!Main.IsEnabled || !Main.IsPagerListEnabled)
            {
                Clear();                          // 关掉功能：页签全拆，别留一枚点不动的假按钮
                return;
            }

            IReadOnlyList<PagerTabInfo> tabs = PagerListController.Tabs;
            string next = SignatureOf(tabs);
            if (!string.Equals(next, signature, StringComparison.Ordinal))
            {
                signature = next;
                DestroyItems();
                for (int i = 0; i < tabs.Count; i++)
                    CreateItem(tabs[i], i);
            }
            else if (items.Count == 0 && root.childCount > 0)
                DestroyItems();                   // 老版本（漏 Add）泄漏在根下的孤儿子对象：签名没变也顺手清一次
            RepairFrames();                       // 建的时候还没探到原生外观的页签，这里补上（真机"按钮外框看不到"）
            Relayout();
            SyncSelection();
        }

        /// <summary>
        /// 给"建的时候还没拿到原生外观"的页签补上底板贴图与图标槽布局。
        ///
        /// 为什么要补：<see cref="EnsureNative"/> 只在 <see cref="CreateItem"/> 里调一次，而 prefab
        /// （<c>RDConstants.data.prefab_tab</c>）在弹窗第一次建起来时**未必已经就绪**；那时
        /// <c>native.HasFrame</c> 为 false，底板就只剩一块"半透明白平板"（sprite = null），
        /// AEE 页签看上去没有那圈圆角外框 —— v2.9.8 真机实测的就是这个样子。页签表内容没变时
        /// <see cref="Rebuild"/> 走的是"不重建对象"的分支，光靠下一次 <see cref="CreateItem"/> 永远补不上，
        /// 所以这里每趟刷新都拿最新的 <c>native</c> 去对齐一次（已经对了就什么都不做，零开销）。
        /// </summary>
        private void RepairFrames()
        {
            if (root == null || items.Count == 0)
                return;
            EnsureNative();
            if (!native.Probed)
                return;
            for (int i = 0; i < items.Count; i++)
            {
                Item item = items[i];
                if (item == null || item.Background == null)
                    continue;
                if (!ItemFrameMatches(item.Background))
                {
                    ApplyFrame(item.Background);
                    RepairIconSlots(item);
                }
                EnsureFillLayer(item);            // 建的时候还没探到"原生有填充底"的页签，这里补上（并保持它在描边框之下）
            }
        }

        /// <summary>原生有填充底、而这枚页签还没建出那一层时就补上（幂等：已经有了就什么都不做）。
        /// 填充底必须画在描边框**之下**（兄弟下标 0）——它是页签的底色，不是覆盖层。
        /// v2.9.8 的原生页签只有一圈描边、没有填充层（真机诊断），此时什么都不建（与 v3 外观一致）。</summary>
        private static void EnsureFillLayer(Item item)
        {
            if (!native.HasFill || item == null || item.Rect == null)
                return;
            if (item.Fill == null)
            {
                item.Fill = CreateFillLayer(item.Rect);
                ApplyFill(item.Fill);
            }
            item.Fill.rectTransform.SetSiblingIndex(0);
        }

        /// <summary>这块底板是不是已经长得跟当前探到的原生外观一模一样（一样就什么都不做）。</summary>
        private static bool ItemFrameMatches(Image background)
        {
            if (background == null)
                return true;
            if (!native.HasFrame)
                return background.sprite == null;                 // 探不到原生底板：保持"无 sprite 的兜底平板"
            if (background.sprite != native.Frame)
                return false;
            if (background.type != native.FrameType)
                return false;
            if (!Mathf.Approximately(background.pixelsPerUnitMultiplier, native.FramePixelsPerUnit))
                return false;
            if (background.material != Graphic.defaultGraphicMaterial)
                return false;                                     // 描边框必须是 UI 默认材质（见 ApplyFrame）
            return true;
        }

        private static bool frameMaterialLogged;

        private static void LogFrameMaterialOnce(Material source, string detail)
        {
            if (frameMaterialLogged)
                return;
            frameMaterialLogged = true;
            Main.Logger?.Log("分页器标签条：原生页签描边材质=" + (source != null ? source.name : "null")
                + "，shader=" + (source != null && source.shader != null ? source.shader.name : "null") + "；" + detail);
        }

        /// <summary>
        /// 把原生页签底板的外观（贴图 / 绘制类型 / 九宫格倍数）套到一块底板上；取不到就留空 sprite（兜底平板）。
        ///
        /// **材质不照抄**：原生 prefab 的描边材质是 <c>zWriteUI</c>（shader <c>ADOFAI/zWriteUI</c>，
        /// <c>_ZWrite=1</c>，ZTest 硬编码在 shader 里、材质改不了）。它会做深度测试，v2.9.8 真机上
        /// AEE 页签压到原生设置面板时，描边框在面板区域里整段被深度测试吃掉，而同一页签里用默认材质的
        /// 图标照常显示。UI 默认材质走 <c>unity_GUIZTestMode</c>（Overlay 画布 = Always），不吃深度；
        /// 弹窗外框（<c>CopyFrameStyle</c>）本来也是刻意不抄材质的。所以描边框一律用默认材质（null）。
        /// </summary>
        private static void ApplyFrame(Image background)
        {
            if (background == null)
                return;
            if (!native.HasFrame)
            {
                background.sprite = null;
                return;
            }
            background.sprite = native.Frame;
            background.type = native.FrameType;
            background.pixelsPerUnitMultiplier = native.FramePixelsPerUnit;
            background.material = null;                            // 默认 UI 材质：不吃深度测试
            if (native.FrameMaterial != null && native.FrameMaterial != Graphic.defaultGraphicMaterial)
                LogFrameMaterialOnce(native.FrameMaterial, "AEE 页签描边框不沿用该材质，改用 UI 默认材质（避免被原生面板区域的深度测试吃掉）");
        }

        /// <summary>图标槽是按 <c>native.Icon*</c> 落位的：探到原生布局之前建的槽要用新值重排一次（否则图标贴错边）。</summary>
        private void RepairIconSlots(Item item)
        {
            if (item == null || item.Rect == null || !native.HasIconLayout)
                return;
            for (int i = 0; i < item.Rect.childCount; i++)
            {
                RectTransform slot = item.Rect.GetChild(i) as RectTransform;
                if (slot == null || slot.name != "slot")
                    continue;
                ApplyIconSlot(slot);
            }
        }

        /// <summary>按当前 <c>native</c>（或兜底值）把图标槽的锚点 / pivot / 位置 / 尺寸重算一遍，见 <see cref="CreateIconSlot"/>。</summary>
        private static void ApplyIconSlot(RectTransform rect)
        {
            if (rect == null)
                return;
            Vector2 anchorMin = native.HasIconLayout ? native.IconAnchorMin : FallbackIconAnchor;
            Vector2 anchorMax = native.HasIconLayout ? native.IconAnchorMax : FallbackIconAnchor;
            Vector2 pivot = native.HasIconLayout ? native.IconPivot : FallbackIconPivot;
            Vector2 pos = native.HasIconLayout ? native.IconPos : FallbackIconPos;
            Vector2 size = native.HasIconLayout ? native.IconSize : FallbackIconSize;
            rect.anchorMin = new Vector2(1f - anchorMax.x, anchorMin.y);   // 镜像：min/max 对调并翻转 x
            rect.anchorMax = new Vector2(1f - anchorMin.x, anchorMax.y);
            rect.pivot = new Vector2(1f - pivot.x, pivot.y);
            rect.anchoredPosition = new Vector2(-pos.x, pos.y);
            rect.sizeDelta = size;
            rect.localScale = Vector3.one;                        // 根没镜像，这里也不需要转回来
        }

        /// <summary>只换选中高亮与叠放顺序（点页签、以及控制器自己退回 All 页之后都要调）。</summary>
        internal void SyncSelection()
        {
            PagerTabKey active = PagerListController.ActiveTab;
            for (int i = 0; i < items.Count; i++)
            {
                Item item = items[i];
                bool selected = item.Key == active;
                Color background = BackgroundColor(item, selected);
                if (item.Background != null)
                    item.Background.color = background;
                float contentAlpha = selected ? ContentAlphaSelected : ContentAlphaUnselected;   // 原生 SetSelected :101
                if (item.Icon != null)
                    item.Icon.color = new Color(1f, 1f, 1f, contentAlpha);   // 类型图标是彩色的，只压透明度
                if (item.Bars != null)
                {
                    var glyph = new Color(1f, 1f, 1f, contentAlpha);         // All / 自定义分组页签的杠条当原生图标看待
                    for (int b = 0; b < item.Bars.Length; b++)
                        if (item.Bars[b] != null)
                            item.Bars[b].color = glyph;
                }
                if (item.Label != null)
                {
                    // §47 原生页签底板只是一圈**描边**（内部透出弹窗深色底），不是实心填充：
                    // 按底板颜色算对比字色时，选中态那圈更亮的框（白 @0.7）会算出黑字 ⇒ 落在深色内部
                    // 就成了"选中后文字消失"。字色与图标一致走白色 + 原生内容透明度档。
                    // 唯一例外是探不到原生 sprite 的兜底实心浅板（见 CreateItem），那时才按底色取对比字色。
                    bool outlineFrame = native.HasFrame && native.Frame != null;
                    Color text = outlineFrame
                        ? Color.white
                        : DecoGroupState.ContrastTextColor(background, Color.black, Color.white);
                    item.Label.color = new Color(text.r, text.g, text.b, contentAlpha);
                }
            }
            ApplyZOrder(active);
        }

        /// <summary>
        /// 排版（叠放规则见类型注释）。窗口尺寸变化时由 <see cref="OnRectTransformDimensionsChange"/>
        /// 自动再走一次，不需要谁每帧调用。
        /// </summary>
        internal void Relayout()
        {
            if (root == null || layingOut || items.Count == 0)
                return;
            layingOut = true;
            try
            {
                int count = items.Count;
                float available = root.rect.height;
                float pitch = TabHeight + PitchExtra;
                if (count > 1 && TabHeight + (count - 1) * pitch > available)
                    pitch = Mathf.Max((available - TabHeight) / (count - 1), MinPitch);
                for (int i = 0; i < count; i++)
                {
                    RectTransform rect = items[i].Rect;
                    if (rect == null)
                        continue;
                    // 顶端对齐往下叠：anchoredPosition.y 是这一枚的中心（pivot 居中），每枚下移一个 pitch
                    rect.anchoredPosition = new Vector2(Width * 0.5f, -(TabHeight * 0.5f + i * pitch));
                }
            }
            finally
            {
                layingOut = false;
            }
        }

        /// <summary>拆掉全部页签（功能关闭 / 页签表空了）。</summary>
        internal void Clear()
        {
            DestroyItems();
            signature = null;
            PagerListController.HideNoteTooltip();
        }

        /// <summary>
        /// 底板颜色：明暗档一律落在原生 SetSelected 的 0.45 / 0.7 上（:96），
        /// 带色页签（目前只有组色机制在支持，见 <see cref="PagerTabInfo.HasColor"/>）只把 rgb 换成该色、
        /// 并把颜色自身的透明度**乘**进这一档，不另发明亮度。
        /// </summary>
        private static Color BackgroundColor(Item item, bool selected)
        {
            float alpha = selected ? FrameAlphaSelected : FrameAlphaUnselected;
            if (!item.HasColor)
                return new Color(1f, 1f, 1f, alpha);
            Color tint = item.Tint;
            Color rgb = selected ? Color.Lerp(tint, Color.white, 0.22f) : tint;
            float groupAlpha = tint.a > 0.01f ? Mathf.Clamp01(tint.a) : 1f;
            return new Color(rgb.r, rgb.g, rgb.b, alpha * groupAlpha);
        }

        /// <summary>
        /// 叠放顺序：正常顺序下**后建的页签盖住先建的**（uGUI 按 sibling 顺序绘制，命中测试也从最上层开始，
        /// 所以露出的那条窄边归谁点就是谁）。只有当前选中的一枚必须永远画在最上，
        /// 否则它被后面压叠的页签切掉一截、也点不到。
        /// </summary>
        private void ApplyZOrder(PagerTabKey active)
        {
            Item selected = null;
            for (int i = 0; i < items.Count; i++)
            {
                RectTransform rect = items[i].Rect;
                if (rect == null)
                    continue;
                rect.SetSiblingIndex(i);          // 先把顺序复位成逻辑顺序：换过几页之后 sibling 顺序会乱
                if (items[i].Key == active)
                    selected = items[i];
            }
            if (selected != null && selected.Rect != null)
                selected.Rect.SetAsLastSibling();
        }

        private void CreateItem(PagerTabInfo info, int index)
        {
            if (info == null)
                return;
            EnsureNative();

            var go = new GameObject("aee_pagerTab" + index, typeof(RectTransform), typeof(Button));
            var rect = (RectTransform)go.transform;
            rect.SetParent(root, false);
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(Width, TabHeight);
            rect.localScale = Vector3.one;

            var item = new Item
            {
                Go = go,
                Rect = rect,
                Key = info.Key,
                HasColor = info.HasColor,
                Tint = info.Color
            };
            // 底板是**子对象**而不是根自己：镜像只加在它身上（等效于原生对整枚页签 ScaleX(-1)），
            // 根保持正缩放 —— 根上的负缩放会把悬停判定与浮层定位的 x 一起翻掉
            item.Background = CreateFrame(rect);
            // 原生外观还没探到（prefab 未就绪）时这里会把 sprite 置空 = 半透明兜底平板；
            // 之后的每一趟 Rebuild 都由 RepairFrames 拿最新的 native 补上（缺件日志在 ProbeNative 里只记一次）
            ApplyFrame(item.Background);
            item.Background.color = new Color(1f, 1f, 1f, FrameAlphaUnselected);
            item.Background.raycastTarget = true;                 // 页签要有自己的射线，重叠时窄边才点得到
            // 填充底（只有原生 prefab 里真有这一层时才建）：画在描边框之下
            EnsureFillLayer(item);

            var button = go.GetComponent<Button>();
            button.targetGraphic = item.Background;
            // 不用 ColorTint：那套 ColorBlock 会**整体替换** Graphic 的颜色，把自定义组色盖掉；
            // 选中态本来就由 BackgroundColor 统一算，未选中/悬停的差别交给提示浮层表达
            button.transition = Selectable.Transition.None;
            PagerTabKey key = info.Key;
            button.onClick.AddListener(() => PagerListController.OnTabStripClicked(key));

            RectTransform slot = CreateIconSlot(item);
            if (info.Key.Kind == PagerTabKind.All)
                item.Bars = CreateBars(slot);                     // All 页签：三根白杠，不用字体字形
            else if (info.Key.Kind == PagerTabKind.Group)
                item.Bars = CreateIndentedBars(slot);             // 自定义分组页签：缩进列表字形，同样不用字体
            else if (info.Icon != null)
                item.Icon = CreateIconImage(slot, info.Icon);     // Type 页签：原生事件图标
            else
                item.Label = CreateText(slot, "label", ShortLabelOf(info), LabelSize, TextAlignmentOptions.Center);

            // 悬停提示：直接复用备注浮层那一套（位置固定、不吃射线、收窗/滚动会自动收起）
            PagerRowHover hover = go.AddComponent<PagerRowHover>();
            hover.Note = TooltipOf(info);
            hover.NoteArea = rect;

            // 必须入列表：Relayout / SyncSelection / ApplyZOrder 只认 items，漏加时全部页签都停在
            // (0,0) 叠成一坨、且每次重建都泄一套（真机 bug A）
            items.Add(item);
        }

        /// <summary>底板：铺满页签的子 Image，只对它 ScaleX(-1) —— 复刻原生事件面板页签的圆角框朝向。</summary>
        private static Image CreateFrame(RectTransform tab)
        {
            var go = new GameObject("frame", typeof(RectTransform), typeof(Image));
            var rect = (RectTransform)go.transform;
            rect.SetParent(tab, false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            rect.localScale = new Vector3(-1f, 1f, 1f);           // InspectorTab.FlipTab（:64-69）的等效结果
            return go.GetComponent<Image>();
        }

        /// <summary>填充底：铺满页签、不镜像（纯色/纯贴图，镜像与否看不出差别），画在描边框之下。</summary>
        private static Image CreateFillLayer(RectTransform tab)
        {
            var go = new GameObject("fill", typeof(RectTransform), typeof(Image));
            var rect = (RectTransform)go.transform;
            rect.SetParent(tab, false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            rect.localScale = Vector3.one;
            return go.GetComponent<Image>();
        }

        /// <summary>把探到的原生填充底外观（贴图 / 绘制类型 / 颜色 / 九宫格倍数）套到我们那一层上；不吃射线。
        /// 材质同描边框一样不照抄（原生材质可能是会做深度测试的 zWriteUI，见 <see cref="ApplyFrame"/>）。</summary>
        private static void ApplyFill(Image fill)
        {
            if (fill == null)
                return;
            fill.sprite = native.FillSprite;
            fill.type = native.FillType;
            fill.color = native.FillColor;
            fill.pixelsPerUnitMultiplier = native.FillPixelsPerUnit;
            fill.raycastTarget = false;                            // 射线照旧交给页签底板
            fill.material = null;
        }

        /// <summary>
        /// 图标槽：布局照抄原生 inspectorTab 的 icon 子对象（48×60 里 30×30），并按页签中心**水平镜像**
        /// 落位 —— 原生是对整枚页签 ScaleX(-1) 得到这个位置的（anchors (0,0.5)+pos 23 → 镜像后 (1,0.5)+pos -23，
        /// 即图标中心落在靠列表那一侧）。槽内容（图标/文字）不镜像，正立显示，与原版一致。
        /// </summary>
        private static RectTransform CreateIconSlot(Item item)
        {
            var go = new GameObject("slot", typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(item.Rect, false);
            ApplyIconSlot(rect);
            return rect;
        }

        private static Image CreateIconImage(RectTransform slot, Sprite sprite)
        {
            var go = new GameObject("icon", typeof(RectTransform), typeof(Image));
            var rect = (RectTransform)go.transform;
            rect.SetParent(slot, false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            Image image = go.GetComponent<Image>();
            image.sprite = sprite;
            image.preserveAspect = true;                          // 类型图标比例不一，拉扁了就认不出了
            image.raycastTarget = false;                           // 射线交给页签底板
            return image;
        }

        /// <summary>All 页签的"列表"字形：三根等长短杠（不依赖字体，任何语言都画得出来）。</summary>
        private static Image[] CreateBars(RectTransform slot)
        {
            Vector2 size = slot.rect.size;
            if (size.x < 2f || size.y < 2f)
                size = slot.sizeDelta;                 // 布局还没跑过时按 sizeDelta 算（槽是零尺寸锚点，两者等价）
            float barWidth = Mathf.Max(4f, size.x * 0.72f);
            float thickness = Mathf.Clamp(size.y * 0.12f, 2f, 4f);
            float step = thickness * 2.2f;
            var result = new Image[3];
            for (int i = 0; i < result.Length; i++)
            {
                var go = new GameObject("bar" + i, typeof(RectTransform), typeof(Image));
                var rect = (RectTransform)go.transform;
                rect.SetParent(slot, false);
                rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.sizeDelta = new Vector2(barWidth, thickness);
                rect.anchoredPosition = new Vector2(0f, (1f - i) * step);
                Image bar = go.GetComponent<Image>();
                bar.sprite = null;
                bar.raycastTarget = false;
                result[i] = bar;
            }
            return result;
        }

        /// <summary>
        /// 自定义分组页签的"缩进列表"字形：顶上一根全宽杠，下面两根短杠整体右移
        /// （宽度约为顶杠的 70%，右沿与顶杠对齐）—— 同样只用纯色 Image，不依赖字体。
        /// </summary>
        private static Image[] CreateIndentedBars(RectTransform slot)
        {
            Vector2 size = slot.rect.size;
            if (size.x < 2f || size.y < 2f)
                size = slot.sizeDelta;                 // 布局还没跑过时按 sizeDelta 算（槽是零尺寸锚点，两者等价）
            float barWidth = Mathf.Max(4f, size.x * 0.72f);
            float thickness = Mathf.Clamp(size.y * 0.12f, 2f, 4f);
            float step = thickness * 2.2f;
            float subWidth = barWidth * 0.7f;                       // 短杠：右沿与顶杠对齐
            float subX = (barWidth - subWidth) * 0.5f;
            var result = new Image[3];
            for (int i = 0; i < result.Length; i++)
            {
                var go = new GameObject("indent" + i, typeof(RectTransform), typeof(Image));
                var rect = (RectTransform)go.transform;
                rect.SetParent(slot, false);
                rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                bool top = i == 0;
                rect.sizeDelta = new Vector2(top ? barWidth : subWidth, thickness);
                rect.anchoredPosition = new Vector2(top ? 0f : subX, (1f - i) * step);
                Image bar = go.GetComponent<Image>();
                bar.sprite = null;
                bar.raycastTarget = false;
                result[i] = bar;
            }
            return result;
        }

        /// <summary>
        /// 读一次原生页签 prefab 的外观（不实例化：InspectorTab 逻辑、cycleButtons、持久监听都不要）。
        /// prefab 还没加载时**不记 Probed**，下次重建再试；探到了就整条复用，避免每枚页签都去 GetComponent。
        /// </summary>
        private static void EnsureNative()
        {
            if (native.Probed)
                return;
            native = ProbeNative();
        }

        private static NativeVisual ProbeNative()
        {
            var result = new NativeVisual();
            try
            {
                GameObject prefab = RDConstants.data != null ? RDConstants.data.prefab_tab : null;
                if (prefab == null)
                    return result;                             // 资源还没到位：Probed 保持 false，下次再探
                result.Probed = true;
                InspectorTab source = prefab.GetComponent<InspectorTab>();
                Image frame = null;
                if (source != null && source.button != null)
                {
                    frame = source.button.targetGraphic as Image;
                    if (frame == null)
                        frame = source.button.GetComponent<Image>();
                }
                if (frame == null && source != null)
                    frame = source.GetComponent<Image>();
                if (frame != null)
                {
                    result.HasFrame = true;
                    result.Frame = frame.sprite;
                    result.FrameType = frame.type;
                    result.FramePixelsPerUnit = frame.pixelsPerUnitMultiplier;
                    try { result.FrameMaterial = frame.material; } catch { }
                }
                Image icon = source != null ? source.icon : null;
                if (icon != null)
                {
                    RectTransform rect = icon.rectTransform;
                    result.HasIconLayout = true;
                    result.IconAnchorMin = rect.anchorMin;
                    result.IconAnchorMax = rect.anchorMax;
                    result.IconPivot = rect.pivot;
                    result.IconPos = rect.anchoredPosition;
                    result.IconSize = rect.sizeDelta;
                }
                ProbeFill(prefab, frame, icon, ref result);
                if ((!result.HasFrame || result.Frame == null || !result.HasIconLayout) && !result.Logged)
                {
                    result.Logged = true;
                    Main.Logger?.Log("分页器标签条：原生 inspectorTab 外观不完整"
                        + "（底板=" + (result.HasFrame ? (result.Frame != null ? "有贴图" : "无贴图（纯色底）") : "取不到")
                        + "，图标槽=" + (result.HasIconLayout ? "有" : "取不到")
                        + "，填充底=" + (result.HasFill ? "有" : "无")
                        + "），退回半透明平板底 / 兜底图标槽");
                }
            }
            catch (Exception e)
            {
                // 不置 Probed：可能只是资源一时没到位，下次重建再试；日志只记一次，不刷屏
                if (!probeErrorLogged)
                {
                    probeErrorLogged = true;
                    Main.Logger?.Log("读取原生页签外观失败（已吞掉，改用兜底样式）: " + e.Message);
                }
            }
            return result;
        }

        /// <summary>
        /// 找原生页签 prefab 里那块"铺满整枚页签的填充底"（既不是描边框、也不是图标）。
        ///
        /// 判据：不是 <paramref name="frame"/>（button 的 targetGraphic 描边框）也不是 <paramref name="icon"/>，
        /// RectTransform 双向都几乎拉满（anchorMin≈0、anchorMax≈1），且确实会画东西（有贴图 或 alpha &gt; 0.01），
        /// 取其中面积最大的一块（页签里其余 Image 都是箭头一类的小件）。
        /// 找不到就 HasFill = false —— 说明原版页签**只有一圈描边**（内部透明），我们也不该凭空加一层，
        /// 否则外观反而与 v3 不一致。
        /// </summary>
        private static void ProbeFill(GameObject prefab, Image frame, Image icon, ref NativeVisual result)
        {
            if (prefab == null)
                return;
            if (frame == null)
                return;                                        // 认不出描边框时不能猜：那块"够大"的很可能就是描边框本身
            Image[] images = prefab.GetComponentsInChildren<Image>(true);
            float bestArea = -1f;
            for (int i = 0; i < images.Length; i++)
            {
                Image candidate = images[i];
                if (candidate == null || candidate == frame || candidate == icon)
                    continue;
                if (candidate.sprite == null && candidate.color.a <= 0.01f)
                    continue;                                  // 什么都不画的空 Image
                RectTransform rect = candidate.rectTransform;
                if (rect == null)
                    continue;
                if (rect.anchorMin.x > 0.01f || rect.anchorMin.y > 0.01f
                    || rect.anchorMax.x < 0.99f || rect.anchorMax.y < 0.99f)
                    continue;                                  // 只占一角的都是小件（箭头等）
                float area = Mathf.Abs(rect.rect.width * rect.rect.height);
                if (area <= bestArea)
                    continue;                                  // 布局还没跑过时全是 0：取第一个够格的
                bestArea = area;
                result.HasFill = true;
                result.FillSprite = candidate.sprite;
                result.FillType = candidate.type;
                result.FillColor = candidate.color;
                result.FillPixelsPerUnit = candidate.pixelsPerUnitMultiplier;
                try { result.FillMaterial = candidate.material; } catch { }
            }
        }

        private TMP_Text CreateText(RectTransform parent, string name, string text, float size, TextAlignmentOptions alignment)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            var label = go.GetComponent<TextMeshProUGUI>();
            if (fontSource != null)
            {
                label.font = fontSource.font;                       // 弹窗标题的字体（含游戏自己的字体材质）
                label.fontSharedMaterial = fontSource.fontSharedMaterial;
            }
            label.fontSize = size;
            label.alignment = alignment;
            // v2（Unity 2022.3 / 这个 TMP 版本）用 enableWordWrapping，没有 v3 的 textWrappingMode
            label.enableWordWrapping = false;
            label.overflowMode = TextOverflowModes.Overflow;
            label.raycastTarget = false;                            // 点击统一交给页签自己的按钮
            label.richText = false;                                 // 标题可能含用户输入，别当标记解析
            label.text = text;
            return label;
        }

        /// <summary>取不到图标的类型页签：取标题的首个字符（代理对整体算一个字符）放进图标槽。
        /// All / 自定义分组页签走杠条字形路径，不经过这里。</summary>
        private static string ShortLabelOf(PagerTabInfo info)
        {
            string title = info.Title;
            if (string.IsNullOrEmpty(title))
                return "?";
            char first = title[0];
            if (char.IsHighSurrogate(first) && title.Length > 1)
                return title.Substring(0, 2);
            return first.ToString();
        }

        private static string TooltipOf(PagerTabInfo info)
        {
            string title = string.IsNullOrEmpty(info.Title) ? "?" : info.Title;
            return string.Format(Localize("aee.pager.tab.tip"), title, info.Count);
        }

        private static string SignatureOf(IReadOnlyList<PagerTabInfo> tabs)
        {
            if (tabs == null || tabs.Count == 0)
                return string.Empty;
            var sb = new StringBuilder(tabs.Count * 24);
            for (int i = 0; i < tabs.Count; i++)
            {
                PagerTabInfo info = tabs[i];
                sb.Append((int)info.Key.Kind).Append('/')
                    .Append((int)info.Key.EventType).Append('/')
                    .Append(info.Key.GroupIndex).Append('/')
                    .Append(info.Count).Append('/')
                    .Append(info.Title);
                // 组色也要进签名：改了组头色就得重画那一枚（按 0..255 取整，避开 Color.ToString 的区域设置问题）
                if (info.HasColor)
                    sb.Append('#').Append(To255(info.Color.r)).Append(',')
                        .Append(To255(info.Color.g)).Append(',')
                        .Append(To255(info.Color.b)).Append(',')
                        .Append(To255(info.Color.a));
                sb.Append(';');
            }
            return sb.ToString();
        }

        private static int To255(float value)
        {
            return Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(value) * 255f), 0, 255);
        }

        private static string Localize(string key) => Main.Localizations?.GetValue(key) ?? key;

        /// <summary>
        /// 拆掉标签条里的**全部**子对象（不只是 items）：真机 bug A 期间 items 从没被填上，
        /// 于是历史版本在标签条根下留过一堆没人认领的页签，光按 items 销毁会漏掉它们 —— 每次重建
        /// 都先按根扫一遍，泄漏的那几套一并清掉。
        /// </summary>
        private void DestroyItems()
        {
            for (int i = 0; i < items.Count; i++)
                if (items[i] != null && items[i].Go != null)
                    UnityEngine.Object.Destroy(items[i].Go);
            items.Clear();
            if (root != null)
            {
                for (int i = root.childCount - 1; i >= 0; i--)
                {
                    GameObject child = root.GetChild(i).gameObject;
                    if (child != null)
                        UnityEngine.Object.Destroy(child);
                }
            }
        }

        // 弹窗被拖动缩放 / 自动尺寸变化 / 换分辨率时，标签条的矩形跟着变 ⇒ 这里重排一次；
        // 没有尺寸变化就一次都不跑（不做每帧排版）。
        private void OnRectTransformDimensionsChange()
        {
            Relayout();
        }

        private void OnEnable()
        {
            Relayout();
        }

        private void OnDisable()
        {
            PagerListController.HideNoteTooltip();
        }
    }
}
