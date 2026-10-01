using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace ADOFAIEditorExtension.Features.PagerList
{
    /// <summary>
    /// Borrows the original panel divider's Image sprite/material, rather than painting a texture
    /// or replacing the OS cursor. Only presentation is reused: PanelTransformGizmoHolder's
    /// controller is tied to InspectorPanel and scnEditor's global (non-modal) mouse actions.
    /// Never changes a native holder, its gizmos, lastHoveredGizmo or draggingGizmo.
    /// </summary>
    internal sealed class PagerNativeResizeFeedback : MonoBehaviour
    {
        private const float CornerIndicatorInset = 8f;
        private RectTransform host;
        private RectTransform indicator;
        private Image image;
        private WindowEdges edges;
        private Vector2 screenPosition;
        private Camera eventCamera;
        private float horizontalSpriteAngle;
        private Vector2 targetSize;
        private float animationTime = 0.075f;
        private float elapsed;
        private float nextSourceAttempt;
        private bool missingSourceLogged;
        private static readonly BindingFlags Constants = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        internal bool IsVisible => image != null && image.gameObject.activeSelf;

        internal static PagerNativeResizeFeedback Create(RectTransform root)
        {
            if (root == null)
                return null;
            var component = root.gameObject.AddComponent<PagerNativeResizeFeedback>();
            component.host = root;
            return component;
        }

        internal void Show(WindowEdges edge, Vector2 pointer, Camera camera)
        {
            if (edge == WindowEdges.None || !Main.IsEnabled || Main.PagerAutoWindowSize || !PagerListController.IsPopupOpen || host == null || !host.gameObject.activeInHierarchy)
            {
                Hide();
                return;
            }

            edges = edge;
            screenPosition = pointer;
            eventCamera = camera;
            if (image == null && !TryResolveNativeStyle())
                return; // edge highlight remains available; no invented cursor fallback
            if (!image.gameObject.activeSelf)
            {
                indicator.sizeDelta = Vector2.zero;
                elapsed = 0f;
                image.gameObject.SetActive(true);
            }

            indicator.localRotation = Quaternion.Euler(0f, 0f, horizontalSpriteAngle + DirectionAngle(edge));
            indicator.SetAsLastSibling(); // above content/tooltip, but never a raycast target
            UpdatePosition();
        }

        internal void Hide()
        {
            edges = WindowEdges.None;
            if (image != null)
                image.gameObject.SetActive(false);
        }

        private bool TryResolveNativeStyle()
        {
            if (host == null || Time.unscaledTime < nextSourceAttempt)
                return false;
            nextSourceAttempt = Time.unscaledTime + 1f;
            try
            {
                // Include inactive scene holders: the native side panel may be collapsed.
                // Assets/prefabs are intentionally excluded so world rotation is well-defined.
                PanelTransformGizmoHolder[] holders = Resources.FindObjectsOfTypeAll<PanelTransformGizmoHolder>();
                for (int pass = 0; pass < 2; pass++)
                {
                    foreach (PanelTransformGizmoHolder holder in holders)
                    {
                        if (holder == null || !holder.gameObject.scene.IsValid() || holder.handles == null)
                            continue;
                        // The actual Image proves UI presentation; prefer forUI, but do not reject a valid native Image when that serialized flag differs.
                        if (holder.forUI != (pass == 0))
                            continue;
                        foreach (TransformGizmoHolder.Handle handle in holder.handles)
                        {
                            if (handle == null || handle.imageRect == null || handle.transformGizmo == null)
                                continue;
                            int placement = (int)handle.transformGizmo.gizmoPlacement;
                            if (placement != 0 && placement != 4)
                                continue; // select an original horizontal panel-divider handle
                            Image source = handle.imageRect.GetComponent<Image>();
                            if (source == null || source.sprite == null)
                                continue;
                            var go = new GameObject("aee_pagerNativeResizeIndicator", typeof(RectTransform), typeof(Image));
                            go.SetActive(false); // never expose a partially configured Image
                            indicator = (RectTransform)go.transform;
                            indicator.SetParent(host, false);
                            indicator.anchorMin = indicator.anchorMax = host.pivot;
                            indicator.pivot = new Vector2(0.5f, 0.5f);
                            image = go.GetComponent<Image>();
                            image.sprite = source.sprite;
                            image.material = source.material;
                            image.color = source.color;
                            image.type = source.type;
                            image.preserveAspect = source.preserveAspect;
                            image.pixelsPerUnitMultiplier = source.pixelsPerUnitMultiplier;
                            image.raycastTarget = false;
                            image.maskable = false;
                            // The template's image may be rotated by its gizmo parent, not the Image itself.
                            // World-relative angle retains that rotation, independent of the source panel's pivot.
                            horizontalSpriteAngle = handle.imageRect.eulerAngles.z - holder.transform.eulerAngles.z;
                            float size = NativeConstant("GizmoSpriteSize", 0.15f);
                            float expansion = NativeConstant("GizmoOpenedHeightMultiplier", 2.25f);
                            float heightScale = NativeConstant("HandleSizeMult", 0.883f);
                            // Native animation is75 ms; do not confuse it with0.75 seconds.
                            animationTime = NativeConstant("EnlargeAnimTime", 0.075f);
                            targetSize = new Vector2(size, size * expansion * heightScale) * 100f;
                            indicator.sizeDelta = Vector2.zero;
                            go.SetActive(false);
                            return true;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                if (indicator != null)
                    UnityEngine.Object.Destroy(indicator.gameObject);
                indicator = null;
                image = null;
                if (!missingSourceLogged)
                    Main.Logger?.Log("原版窗口缩放手柄资源读取失败，保留边缘高亮: " + e.Message);
                missingSourceLogged = true;
                return false;
            }

            if (!missingSourceLogged)
            {
                Main.Logger?.Log("未找到原版面板缩放手柄 Image，窗口缩放仍可用（保留边缘高亮）");
                missingSourceLogged = true;
            }

            return false;
        }

        private static float NativeConstant(string name, float fallback)
        {
            FieldInfo field = typeof(TransformGizmoHolder).GetField(name, Constants);
            if (field == null || !field.IsLiteral)
                return fallback;
            float value = Convert.ToSingle(field.GetRawConstantValue());
            return !float.IsNaN(value) && !float.IsInfinity(value) && value > 0f ? value : fallback;
        }

        internal static float DirectionAngle(WindowEdges edge)
        {
            bool horizontal = (edge & (WindowEdges.Left | WindowEdges.Right)) != 0;
            bool vertical = (edge & (WindowEdges.Top | WindowEdges.Bottom)) != 0;
            if (!horizontal)
                return (90f);
            if (!vertical)
                return (0f);
            bool left = (edge & WindowEdges.Left) != 0;
            bool top = (edge & WindowEdges.Top) != 0;
            return left == top ? -45f : 45f;
        }

        private void UpdatePosition()
        {
            if (host == null || indicator == null)
                return;
            Vector2 local;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(host, screenPosition, eventCamera, out local))
                return;
            Rect rect = host.rect;
            float x = Mathf.Clamp(local.x, rect.xMin, rect.xMax);
            float y = Mathf.Clamp(local.y, rect.yMin, rect.yMax);
            if ((edges & WindowEdges.Left) != 0)
                x = rect.xMin;
            else if ((edges & WindowEdges.Right) != 0)
                x = rect.xMax;
            if ((edges & WindowEdges.Bottom) != 0)
                y = rect.yMin;
            else if ((edges & WindowEdges.Top) != 0)
                y = rect.yMax;
            // Matches PanelTransformGizmoHolder: snap the normal axis to the border and follow
            // the pointer along its tangent axis; corners are fixed at the selected corner.
            bool corner = (edges & (WindowEdges.Left | WindowEdges.Right)) != 0 && (edges & (WindowEdges.Top | WindowEdges.Bottom)) != 0;
            if (corner)
            {
                // The visible rounded arc lies inside the bounding-box corner. Inset only the
                // arrow center; hit areas, pointer deltas and the actual resize edges stay unchanged.
                float insetX = Mathf.Min(CornerIndicatorInset, Mathf.Max(0f, rect.width) * 0.5f);
                float insetY = Mathf.Min(CornerIndicatorInset, Mathf.Max(0f, rect.height) * 0.5f);
                x += (edges & WindowEdges.Left) != 0 ? insetX : -insetX;
                y += (edges & WindowEdges.Bottom) != 0 ? insetY : -insetY;
            }

            // Straight-edge indicators still track the exact edge and follow its tangent axis.
            indicator.anchoredPosition = new Vector2(x, y);
        }

        private void LateUpdate()
        {
            if (!IsVisible)
                return;
            if (!Main.IsEnabled || Main.PagerAutoWindowSize || !PagerListController.IsPopupOpen)
            {
                Hide();
                return;
            }

            UpdatePosition();
            elapsed = Mathf.Min(animationTime, elapsed + Time.unscaledDeltaTime);
            float t = animationTime > 0f ? elapsed / animationTime : 1f;
            // The original holder uses DOTween's default OutQuad expansion over EnlargeAnimTime.
            float eased = 1f - (1f - t) * (1f - t);
            indicator.sizeDelta = targetSize * eased;
        }

        private void OnDisable()
        {
            Hide();
        }

        private void OnApplicationFocus(bool focused)
        {
            if (!focused)
                Hide();
        }

        private void OnDestroy()
        {
            // Only our Image object is ours. The borrowed sprite/material belong to the game.
            if (indicator != null)
                UnityEngine.Object.Destroy(indicator.gameObject);
        }
    }
}
