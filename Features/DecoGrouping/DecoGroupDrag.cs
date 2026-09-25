using ADOFAI;
using UnityEngine;
using UnityEngine.EventSystems;

namespace ADOFAIEditorExtension.Features.DecoGrouping
{
    /// <summary>
    /// 装饰行的"拖到分组上"拖拽源。分组开启时（`itemsReorderable == false`，原版的排序拖拽被禁用）
    /// 接管拖动：拖到某个组上松手 = 把该装饰归入该组，并插到落点那一行之前/之后。
    ///
    /// 反馈照原版：组头用原版"选中行"样式高亮、悬停行用原版 `ListItem.ShowHighlight`、
    /// 落点用原版规格的白横线 + 跟随鼠标的小方块（见 `DecoGroupRenderer.UpdateDropFeedback`）。
    /// 被拖动的那一行**保持原位、不做半透明/隐藏**（原版重排拖拽也是这样：原版只高亮悬停行，
    /// 数据改动在松手时才发生）。
    ///
    /// 为什么自己实现而不是复用原版拖拽：原版 `PropertyControl_List.BeginDrag` 第一行就是
    /// `if (!itemsReorderable) return;`（IL 已核），分组开启时它根本不会启动。
    /// 分组关闭时本组件**完全不碰事件**（不调用 `PointerEventData.Use()`），保持原版排序拖拽不变。
    /// </summary>
    internal sealed class DecoRowDragTarget : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        private LevelEvent dragging;

        public void OnBeginDrag(PointerEventData eventData)
        {
            dragging = null;
            if (!Main.IsDecoGroupingEnabled)
                return;   // 分组关闭：交给原版的排序拖拽

            ListItem item = GetComponent<ListItem>();
            if (item == null || item.sourceLevelEvent == null)
                return;

            eventData?.Use();
            dragging = item.sourceLevelEvent;
            DecoGroupRenderer.UpdateDropFeedback(eventData != null ? eventData.position : Vector2.zero, dragging);
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (dragging == null)
                return;
            eventData?.Use();
            DecoGroupRenderer.UpdateDropFeedback(eventData != null ? eventData.position : Vector2.zero, dragging);
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            // dragging == null ⇒ 这次拖动不是我们接管的（分组关闭 / 没抓到行），一点都不要碰事件
            if (dragging == null)
                return;
            eventData?.Use();

            LevelEvent ev = dragging;
            dragging = null;
            Vector2 position = eventData != null ? eventData.position : Vector2.zero;

            // 落点：以松手时的指针位置为准（最后一帧的反馈也是按它显示的）
            bool hasTarget = DecoGroupRenderer.TryFindDropTarget(position, out DecoGroupState.DropTarget target, out _);
            DecoGroupRenderer.ClearDropFeedback();

            if (ev == null || !hasTarget || string.IsNullOrEmpty(target.Key))
                return;   // 松手时不在任何组上：什么都不做
            DecoGroupActions.DropDecoration(ev, target.Key, target.Anchor, target.Before);
        }

        /// <summary>行被回收/面板重建（拖到一半行没了）：清干净，别把状态带到下一次拖动。</summary>
        private void OnDisable()
        {
            if (dragging == null)
                return;
            dragging = null;
            DecoGroupRenderer.ClearDropFeedback();
        }
    }
}
