using System;
using ADOFAIEditorExtension.Settings;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ADOFAIEditorExtension.Features.PagerList
{
    /// <summary>
    /// UI-only window geometry. No event selection, level writes or undo scopes.
    /// The root keeps its existing centered anchors; all drag math uses its parent's coordinates.
    /// </summary>
    internal sealed class PagerWindowInteraction : MonoBehaviour
    {
        private const float DefaultWidth = 760f;
        private const float MinWidth = 360f;
        private const int MinAutoRows = 6;
        private const int MaxAutoRows = 14;
        private const float ResizeBand = 6f;
        private const float LeftResizeInner = 12f;      // §47 左缩放手柄的内边界（< 列表左内边距 16，不抢行点击）
        private const float CornerSize = 14f;
        private const float SafeScreenPixels = 6f;
        private const float SnapEnterPixels = 16f;
        private const float SnapReleasePixels = 28f;
        private RectTransform host;
        private ScrollRect scroll;
        private float rowHeight, headerHeight, footerHeight, paddingY, spacing;
        private int rowCount;
        private readonly GameObject[] resizeHandles = new GameObject[8];
        private PagerNativeResizeFeedback nativeFeedback;
        private RectTransform activeHighlight;
        private PagerWindowHandle activeHandle;
        private bool dragging, changed, positioned;
        private int pointerId;
        private WindowEdges dragEdges, snappedEdges;
        private WindowRect startRect, lastBounds;
        private Vector2 startPointer;
        private Vector2 lastPointerScreen;
        private RectTransform dragParent;
        private Camera dragCamera;
        private float unitsPerPixel = 1f;
        internal void Initialise(RectTransform root, ScrollRect list, float itemHeight, float header, float footer, float listPaddingY, float rowSpacing)
        {
            host = root;
            scroll = list;
            rowHeight = itemHeight;
            headerHeight = header;
            footerHeight = footer;
            paddingY = listPaddingY;
            spacing = rowSpacing;
            BuildHandles();
            nativeFeedback = PagerNativeResizeFeedback.Create(root);
        }

        /// <summary>
        /// §47 左侧标签条要占的净高（页签按最小间距极限重叠后仍摆得下的高度，**不含**上下内缩那两段）。
        /// 由 <see cref="PagerListController"/> 在标签页表变化时写进来，0 = 没有标签条。
        /// </summary>
        internal float MinStripHeight { get; set; }

        /// <summary>
        /// §47 交互层记住的行数（Configure 的入参，也是 StripHeightChanged / 拖完 / 失焦后重算用的那个）。
        /// 控制器在"自动尺寸"下必须按 All 页行数喂它，切换设置开关时也可以反过来把它换算成新口径再重算。
        /// </summary>
        internal int TrackedRows
        {
            get { return rowCount; }
            set { rowCount = Math.Max(1, value); }
        }

        /// <summary>
        /// 最小高度：除原来的"标题 + 页脚 + 两行列表"之外，还要装得下标签条。
        /// §47 标签条整条在窗口**外面**、纵向只跨 (窗口高 − TopInset − BottomInset)，所以标签条那一项是
        /// <c>TopInset + BottomInset + MinStripHeight</c>；两条取大 ⇒ 页签变多时窗口不许再往矮里缩，
        /// 而且拖动 / Configure / LateUpdate 的夹取都走这一个属性。
        /// </summary>
        private float MinimumHeight => Mathf.Max(
            headerHeight + footerHeight + 2f * paddingY + 2f * rowHeight + spacing + 6f,
            PagerTabStrip.TopInset + PagerTabStrip.BottomInset + MinStripHeight);

        /// <summary>
        /// §47 标签条探出窗口左沿的量：夹屏幕边界时**左边**要按它留出余量，
        /// 否则窗口贴到屏幕最左时整条标签条出屏（只在 <see cref="TryGetBounds"/> 一处加，
        /// Configure / 拖动 / 缩放 / 贴边 / LateUpdate 全都从那份 bounds 出发，自然一起生效）。
        /// 标签条已经不在窗口里 ⇒ 不再吃列表宽度，最小/默认宽就是普通值。
        /// </summary>
        private static float StripLeftReserve => PagerTabStrip.LeftReserve;

        /// <summary>
        /// 标签条变高之后重跑一次几何：**只在当前高度已经不够**时才动窗口（一次性调用，不每帧）。
        /// 没摆过位置不动（第一次 Configure 自然带上新的最小值）；拖拽中不动（EndDrag 会再 Configure 一次）。
        /// </summary>
        internal void StripHeightChanged()
        {
            if (host == null || !positioned || dragging)
                return;
            if (ReadRect().Height >= MinimumHeight - 0.5f)
                return;
            Configure(rowCount);
        }

        private void BuildHandles()
        {
            // The title surface is BELOW resize handles and ABOVE title text (which never raycasts).
            CreateHandle("aee_pagerWindowMove", WindowEdges.None, new Vector2(0f, 1f), Vector2.one, new Vector2(CornerSize, -headerHeight), new Vector2(-CornerSize, -ResizeBand));
            WindowEdges[] edges =
            {
                WindowEdges.Left,
                WindowEdges.Right,
                WindowEdges.Bottom,
                WindowEdges.Top,
                WindowEdges.Left | WindowEdges.Bottom,
                WindowEdges.Left | WindowEdges.Top,
                WindowEdges.Right | WindowEdges.Bottom,
                WindowEdges.Right | WindowEdges.Top
            };
            // §47 左边那一条整段搬进窗口内：标签条现在贴在窗口左沿**外侧**（右沿只压进来 BorderOverlap），
            // 而把手是标签条之后建的（同级靠后 = 画在上、也先命中），留在外面就会把页签的点击抢走。
            // 内边界取 LeftResizeInner = 12 < 列表左内边距 16 ⇒ 既不抢页签，也不抢事件行。
            resizeHandles[0] = CreateHandle("aee_pagerResizeLeft", edges[0], Vector2.zero, new Vector2(0f, 1f), new Vector2(PagerTabStrip.BorderOverlap, CornerSize), new Vector2(LeftResizeInner, -CornerSize));
            resizeHandles[1] = CreateHandle("aee_pagerResizeRight", edges[1], new Vector2(1f, 0f), Vector2.one, new Vector2(-ResizeBand, CornerSize), new Vector2(ResizeBand, -CornerSize));
            resizeHandles[2] = CreateHandle("aee_pagerResizeBottom", edges[2], Vector2.zero, new Vector2(1f, 0f), new Vector2(CornerSize, -ResizeBand), new Vector2(-CornerSize, ResizeBand));
            resizeHandles[3] = CreateHandle("aee_pagerResizeTop", edges[3], new Vector2(0f, 1f), Vector2.one, new Vector2(CornerSize, -ResizeBand), new Vector2(-CornerSize, ResizeBand));
            Vector2[] corners =
            {
                Vector2.zero,
                new Vector2(0f, 1f),
                new Vector2(1f, 0f),
                Vector2.one
            };
            for (int i = 0; i < corners.Length; i++)
            {
                Vector2 c = corners[i];
                Vector2 lo = new Vector2(c.x == 0 ? -ResizeBand : -CornerSize, c.y == 0 ? -ResizeBand : -CornerSize);
                Vector2 hi = new Vector2(c.x == 0 ? CornerSize : ResizeBand, c.y == 0 ? CornerSize : ResizeBand);
                resizeHandles[i + 4] = CreateHandle("aee_pagerResizeCorner" + i, edges[i + 4], c, c, lo, hi);
            }
        // Keep all resize hit areas invisible at rest. Rotated grip bars placed on the
        // bottom-right hit area intersected the original rounded frame, appearing as
        // stray pixels only in manual mode. Cursor and transient hover feedback remain.
        }

        private GameObject CreateHandle(string name, WindowEdges edge, Vector2 min, Vector2 max, Vector2 offsetMin, Vector2 offsetMax)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(PagerWindowHandle));
            var rt = (RectTransform)go.transform;
            rt.SetParent(host, false);
            rt.anchorMin = min;
            rt.anchorMax = max;
            rt.offsetMin = offsetMin;
            rt.offsetMax = offsetMax;
            Image hit = go.GetComponent<Image>();
            hit.color = Color.clear;
            hit.raycastTarget = true;
            var handle = go.GetComponent<PagerWindowHandle>();
            handle.Owner = this;
            handle.Edges = edge;
            if (edge != WindowEdges.None)
            {
                var glow = new GameObject("hover", typeof(RectTransform), typeof(Image));
                var glowRT = (RectTransform)glow.transform;
                glowRT.SetParent(rt, false);
                glowRT.anchorMin = Vector2.zero;
                glowRT.anchorMax = Vector2.one;
                glowRT.offsetMin = glowRT.offsetMax = Vector2.zero;
                Image image = glow.GetComponent<Image>();
                image.color = new Color(1f, 1f, 1f, 0.12f);
                image.raycastTarget = false;
                glow.SetActive(false);
                handle.Highlight = glowRT;
            }

            return go;
        }

        internal void Configure(int visibleRows)
        {
            rowCount = Math.Max(1, visibleRows);
            bool auto = Main.PagerAutoWindowSize;
            if (dragging && dragEdges != WindowEdges.None && auto)
                FinishDrag(); // a mode change must immediately end a now-forbidden resize
            for (int i = 0; i < resizeHandles.Length; i++)
                if (resizeHandles[i] != null && resizeHandles[i].activeSelf == auto)
                    resizeHandles[i].SetActive(!auto);
            if (!TryGetBounds(out WindowRect bounds, out float pixelUnits))
                return;
            unitsPerPixel = pixelUnits;
            if (dragging)
                return; // list/keyboard refresh may happen during a drag; never overwrite its geometry
            WindowRect current = ReadRect();
            float width, height;
            if (auto)
            {
                float factor = Main.PagerWindowScale;
                int count = Math.Min(MaxAutoRows, Math.Max(MinAutoRows, rowCount));
                width = DefaultWidth * factor;
                height = (headerHeight + footerHeight + 2f * paddingY + 6f + count * rowHeight + (count - 1) * spacing) * factor;
            }
            else
            {
                width = PagerWindowPreferences.ManualWidth > 0f ? PagerWindowPreferences.ManualWidth : (positioned ? current.Width : DefaultWidth);
                int count = Math.Min(MaxAutoRows, Math.Max(MinAutoRows, rowCount));
                height = PagerWindowPreferences.ManualHeight > 0f ? PagerWindowPreferences.ManualHeight : (positioned ? current.Height : headerHeight + footerHeight + 2f * paddingY + 6f + count * rowHeight + (count - 1) * spacing);
            }

            var desired = PagerWindowGeometry.Constrain(new WindowRect(bounds.X, bounds.Y, width, height), bounds, MinWidth, MinimumHeight);
            float x = Mathf.Clamp01(PagerWindowPreferences.PositionX);
            float y = Mathf.Clamp01(PagerWindowPreferences.PositionY);
            desired.X = bounds.X + (bounds.Width - desired.Width) * x;
            desired.Y = bounds.Y + (bounds.Height - desired.Height) * y;
            // Position fractions naturally preserve a docked side across auto-size/data changes.
            WindowEdges dock = (WindowEdges)PagerWindowPreferences.SnapEdges;
            if (Main.PagerWindowSnap)
            {
                if ((dock & WindowEdges.Left) != 0)
                    desired.X = bounds.Left;
                else if ((dock & WindowEdges.Right) != 0)
                    desired.X = bounds.Right - desired.Width;
                if ((dock & WindowEdges.Bottom) != 0)
                    desired.Y = bounds.Bottom;
                else if ((dock & WindowEdges.Top) != 0)
                    desired.Y = bounds.Top - desired.Height;
            }

            ApplyRect(desired);
            lastBounds = bounds;
            positioned = true;
        }

        internal void SettingsChanged()
        {
            if (dragging)
                FinishDrag();
            Configure(rowCount);
        }

        /// <summary>
        /// 自动尺寸开关切换 ⇒ 位置重新初始化到屏幕中心（记住的宽高不动）。
        /// 顺序要紧：FinishDrag 会把在拖的几何写回位置偏好，中心必须写在它之后、Configure 之前。
        /// </summary>
        internal void Recenter()
        {
            FinishDrag();
            WriteCenteredPreferences();
            Configure(rowCount);
        }

        /// <summary>把位置偏好落回中心、清掉贴边（弹窗没开时也用它，下次 Configure 自然居中）。</summary>
        internal static void WriteCenteredPreferences()
        {
            PagerWindowPreferences.TrySetPosition(0.5f, 0.5f);
            PagerWindowPreferences.TrySetSnapEdges(0);
        }

        private WindowRect ReadRect()
        {
            RectTransform parent = host.parent as RectTransform;
            Vector3 center = parent.InverseTransformPoint(host.TransformPoint(host.rect.center));
            return new WindowRect(center.x - host.rect.width * 0.5f, center.y - host.rect.height * 0.5f, host.rect.width, host.rect.height);
        }

        private void ApplyRect(WindowRect rect)
        {
            if (host == null || !(host.parent is RectTransform))
                return;
            WindowRect previous = ReadRect();
            host.sizeDelta = new Vector2(rect.Width, rect.Height);
            host.anchoredPosition += new Vector2(rect.CenterX - previous.CenterX, rect.CenterY - previous.CenterY);
            // Preserve the user's scroll offset, rather than calling LayoutList (which centers selection).
            if (scroll != null && scroll.content != null && scroll.viewport != null)
            {
                Vector2 offset = scroll.content.anchoredPosition;
                offset.y = Mathf.Clamp(offset.y, 0f, Mathf.Max(0f, scroll.content.rect.height - scroll.viewport.rect.height));
                scroll.content.anchoredPosition = offset;
            }
        }

        private Camera ResolveCamera()
        {
            Canvas canvas = host != null ? host.GetComponentInParent<Canvas>() : null;
            return canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
        }

        private bool TryGetBounds(out WindowRect bounds, out float pixelUnits)
        {
            bounds = default(WindowRect);
            pixelUnits = 1f;
            RectTransform parent = host != null ? host.parent as RectTransform : null;
            if (parent == null)
                return false;
            Camera camera = ResolveCamera();
            Rect screen = camera != null ? camera.pixelRect : new Rect(0f, 0f, Screen.width, Screen.height);
            if (screen.width < 1f || screen.height < 1f)
                return false;
            Vector2 lo, hi;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, screen.min, camera, out lo) || !RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, screen.max, camera, out hi))
                return false;
            float width = Mathf.Abs(hi.x - lo.x), height = Mathf.Abs(hi.y - lo.y);
            pixelUnits = Mathf.Max(width / screen.width, height / screen.height);
            if (float.IsNaN(pixelUnits) || float.IsInfinity(pixelUnits) || pixelUnits <= 0f)
                return false;
            float margin = SafeScreenPixels * pixelUnits;
            Rect parentArea = parent.rect;
            // §47 左边多让出标签条探出窗口的那一段（48 − 3 = 45 个单位）：bounds 是 Configure / 拖动 /
            // 缩放 / 贴边 / LateUpdate 唯一的出发点，所以只在这里加一次就全都生效
            float left = Mathf.Max(Mathf.Min(lo.x, hi.x), parentArea.xMin) + margin + StripLeftReserve;
            float right = Mathf.Min(Mathf.Max(lo.x, hi.x), parentArea.xMax) - margin;
            float bottom = Mathf.Max(Mathf.Min(lo.y, hi.y), parentArea.yMin) + margin;
            float top = Mathf.Min(Mathf.Max(lo.y, hi.y), parentArea.yMax) - margin;
            if (right <= left || top <= bottom)
                return false;
            bounds = new WindowRect(left, bottom, right - left, top - bottom);
            return true;
        }

        internal void BeginDrag(PagerWindowHandle handle, PointerEventData data)
        {
            if (!Main.IsEnabled || !PagerListController.IsPopupOpen || data == null || data.button != PointerEventData.InputButton.Left || dragging || (handle.Edges != WindowEdges.None && Main.PagerAutoWindowSize))
                return;
            WindowRect bounds;
            float pixelUnits;
            if (!TryGetBounds(out bounds, out pixelUnits))
                return;
            dragParent = host.parent as RectTransform;
            dragCamera = data.pressEventCamera != null ? data.pressEventCamera : ResolveCamera();
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(dragParent, data.position, dragCamera, out startPointer))
                return;
            startRect = PagerWindowGeometry.Constrain(ReadRect(), bounds, MinWidth, MinimumHeight);
            ApplyRect(startRect);
            lastBounds = bounds;
            unitsPerPixel = pixelUnits;
            activeHandle = handle;
            dragEdges = handle.Edges;
            pointerId = data.pointerId;
            changed = false;
            dragging = true;
            snappedEdges = WindowEdges.None;
            data.eligibleForClick = false;
            PagerListController.HideNoteTooltip();
            lastPointerScreen = data.position;
            SetFeedback(handle);
        }

        internal void Drag(PagerWindowHandle handle, PointerEventData data)
        {
            if (!dragging || activeHandle != handle || data == null || data.pointerId != pointerId)
                return;
            if (!Main.IsEnabled || !PagerListController.IsPopupOpen || dragParent != host.parent || (dragEdges != WindowEdges.None && Main.PagerAutoWindowSize))
            {
                FinishDrag();
                return;
            }

            Vector2 pointer;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(dragParent, data.position, dragCamera, out pointer))
                return;
            lastPointerScreen = data.position;
            Vector2 delta = pointer - startPointer;
            WindowRect next = dragEdges == WindowEdges.None ? PagerWindowGeometry.Move(startRect, delta.x, delta.y, lastBounds, Main.PagerWindowSnap, SnapEnterPixels * unitsPerPixel, SnapReleasePixels * unitsPerPixel, ref snappedEdges) : PagerWindowGeometry.Resize(startRect, dragEdges, delta.x, delta.y, lastBounds, MinWidth, MinimumHeight);
            WindowRect previous = ReadRect();
            changed |= Different(previous, next);
            ApplyRect(next);
            if (dragEdges != WindowEdges.None)
                SetFeedback(handle);
            if (data != null)
                data.eligibleForClick = false;
        }

        internal void EndDrag(PagerWindowHandle handle, PointerEventData data)
        {
            if (!dragging || activeHandle != handle || (data != null && data.pointerId != pointerId))
                return;
            if (data != null)
                Drag(handle, data); // include the release-frame pointer position
            FinishDrag();
            if (Main.IsEnabled && PagerListController.IsPopupOpen && gameObject.activeInHierarchy)
                Configure(rowCount); // apply content changes deferred during dragging
        }

        private void FinishDrag()
        {
            if (!dragging)
                return;
            bool save = changed && host != null && host.parent is RectTransform;
            WindowEdges edges = dragEdges;
            dragging = false;
            changed = false;
            activeHandle = null;
            ClearFeedback();
            if (!save)
                return;
            WindowRect rect = ReadRect();
            if (edges != WindowEdges.None)
            {
                PagerWindowPreferences.ManualWidth = rect.Width;
                PagerWindowPreferences.ManualHeight = rect.Height;
            }

            PagerWindowPreferences.PositionX = lastBounds.Width > rect.Width + 0.01f ? Mathf.Clamp01((rect.X - lastBounds.X) / (lastBounds.Width - rect.Width)) : 0.5f;
            PagerWindowPreferences.PositionY = lastBounds.Height > rect.Height + 0.01f ? Mathf.Clamp01((rect.Y - lastBounds.Y) / (lastBounds.Height - rect.Height)) : 0.5f;
            PagerWindowPreferences.SnapEdges = Main.PagerWindowSnap && edges == WindowEdges.None ? (int)snappedEdges : 0;
            SettingsStore.Save(); // one write per completed/cancelled interaction, never per frame
        }

        private static bool Different(WindowRect a, WindowRect b)
        {
            return Mathf.Abs(a.X - b.X) > 0.01f || Mathf.Abs(a.Y - b.Y) > 0.01f || Mathf.Abs(a.Width - b.Width) > 0.01f || Mathf.Abs(a.Height - b.Height) > 0.01f;
        }

        private void LateUpdate()
        {
            if (host == null || !Main.IsEnabled || !PagerListController.IsPopupOpen)
                return;
            WindowRect bounds;
            float pixelUnits;
            if (!TryGetBounds(out bounds, out pixelUnits))
            {
                FinishDrag();
                return;
            }

            if (!positioned || Different(bounds, lastBounds))
            {
                FinishDrag(); // do not use a stale pointer/size snapshot after resolution/Canvas changes
                Configure(rowCount);
                return;
            }

            // §47 最小高度里"标签条那一段"是随页签数变的：只有真的矮到夹不住时才重排一次。
            // 屏幕本身不够高时按能达到的值比（Constrain 会把最小值夹到 bounds 以内），
            // 否则这条判断每帧都成立、变成隐形重排。
            float achievable = Mathf.Min(MinimumHeight, lastBounds.Height);
            if (!dragging && ReadRect().Height < achievable - 0.5f)
                Configure(rowCount);
        }

        internal void Hover(PagerWindowHandle handle, bool inside, PointerEventData data)
        {
            if (dragging || !Main.IsEnabled || !PagerListController.IsPopupOpen)
                return;
            if (inside && handle.Edges != WindowEdges.None && !Main.PagerAutoWindowSize)
            {
                if (data != null)
                    lastPointerScreen = data.position;
                SetFeedback(handle);
            }
            else if (!inside || handle.Edges == WindowEdges.None)
                ClearFeedback();
        }

        internal void HandleDisabled(PagerWindowHandle handle)
        {
            if (dragging && activeHandle == handle)
                FinishDrag();
            if (activeHighlight == handle.Highlight)
                ClearFeedback();
        }

        private void SetFeedback(PagerWindowHandle handle)
        {
            if (handle.Edges == WindowEdges.None)
                ClearFeedback();
            if (activeHighlight != handle.Highlight)
            {
                if (activeHighlight != null)
                    activeHighlight.gameObject.SetActive(false);
                activeHighlight = handle.Highlight;
                if (activeHighlight != null)
                    activeHighlight.gameObject.SetActive(true);
            }

            if (handle.Edges != WindowEdges.None)
                nativeFeedback?.Show(handle.Edges, lastPointerScreen, ResolveCamera());
        }

        private void ClearFeedback()
        {
            if (activeHighlight != null)
                activeHighlight.gameObject.SetActive(false);
            activeHighlight = null;
            nativeFeedback?.Hide();
        }

        private void OnDisable()
        {
            FinishDrag();
            ClearFeedback();
        }

        private void OnApplicationFocus(bool focused)
        {
            if (focused && Main.IsEnabled && PagerListController.IsPopupOpen && gameObject.activeInHierarchy)
                Configure(rowCount);
            if (!focused)
            {
                FinishDrag();
                ClearFeedback();
            }
        }

        private void OnDestroy()
        {
            ClearFeedback();
            if (nativeFeedback != null)
                UnityEngine.Object.Destroy(nativeFeedback);
        }
    }

    internal sealed class PagerWindowHandle : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IInitializePotentialDragHandler, IBeginDragHandler, IDragHandler, IEndDragHandler, IPointerEnterHandler, IPointerExitHandler, IPointerMoveHandler
    {
        internal PagerWindowInteraction Owner;
        internal WindowEdges Edges;
        internal RectTransform Highlight;
        public void OnPointerDown(PointerEventData data)
        {
            Owner?.BeginDrag(this, data);
        }

        public void OnInitializePotentialDrag(PointerEventData data)
        {
            if (data != null)
                data.useDragThreshold = false;
        }

        public void OnBeginDrag(PointerEventData data)
        {
            if (data != null)
                data.eligibleForClick = false;
        }

        public void OnDrag(PointerEventData data)
        {
            Owner?.Drag(this, data);
        }

        public void OnEndDrag(PointerEventData data)
        {
            Owner?.EndDrag(this, data);
        }

        public void OnPointerUp(PointerEventData data)
        {
            Owner?.EndDrag(this, data);
        }

        public void OnPointerEnter(PointerEventData data)
        {
            Owner?.Hover(this, true, data);
        }

        public void OnPointerMove(PointerEventData data)
        {
            Owner?.Hover(this, true, data);
        }

        public void OnPointerExit(PointerEventData data)
        {
            Owner?.Hover(this, false, data);
        }

        private void OnDisable()
        {
            Owner?.HandleDisabled(this);
        }
    }
}
