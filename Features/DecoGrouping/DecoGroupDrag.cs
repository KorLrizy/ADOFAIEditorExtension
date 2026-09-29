using ADOFAI;
using System.Collections.Generic;
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
        /// <summary>
        /// 这次要搬的全部装饰（拖动开始时定下，规则同原版 CacheOnStartDrag：被拖行在多选集合里 ⇒ 整批，
        /// 否则只有它自己）。按装饰数组顺序，至少包含 <see cref="dragging"/>。
        /// </summary>
        private List<LevelEvent> draggingSet;

        public void OnBeginDrag(PointerEventData eventData)
        {
            dragging = null;
            draggingSet = null;
            // 模组已在 UMM 里关掉（补丁已卸、编辑器没能重启）：组件还挂在池化的行上，但不能再接管
            if (!Main.IsEnabled || !Main.IsDecoGroupingEnabled)
                return;   // 分组关闭：交给原版的排序拖拽

            ListItem item = GetComponent<ListItem>();
            if (item == null || item.sourceLevelEvent == null)
                return;

            eventData?.Use();
            dragging = item.sourceLevelEvent;
            draggingSet = DecoGroupActions.CollectDraggingSet(dragging);
            if (draggingSet.Count >= 2)
                Main.Logger?.Log(string.Format("装饰拖动开始：多选整批 {0} 个", draggingSet.Count));
            DecoGroupRenderer.UpdateDropFeedback(eventData != null ? eventData.position : Vector2.zero, dragging, draggingSet);
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (dragging == null)
                return;
            if (!Main.IsEnabled)
            {
                // 拖到一半模组被关掉：放弃这次拖动
                dragging = null;
                draggingSet = null;
                DecoGroupRenderer.ClearDropFeedback();
                return;
            }
            eventData?.Use();
            DecoGroupRenderer.UpdateDropFeedback(eventData != null ? eventData.position : Vector2.zero, dragging, draggingSet);
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            // dragging == null ⇒ 这次拖动不是我们接管的（分组关闭 / 没抓到行），一点都不要碰事件
            if (dragging == null)
                return;
            if (!Main.IsEnabled)
            {
                dragging = null;
                draggingSet = null;
                DecoGroupRenderer.ClearDropFeedback();
                return;
            }
            eventData?.Use();

            LevelEvent ev = dragging;
            List<LevelEvent> set = draggingSet;
            dragging = null;
            draggingSet = null;
            Vector2 position = eventData != null ? eventData.position : Vector2.zero;

            // 落点：以松手时的指针位置为准（最后一帧的反馈也是按它显示的）
            bool hasTarget = DecoGroupRenderer.TryFindDropTarget(position, out DecoGroupState.DropTarget target, out _);
            DecoGroupRenderer.ClearDropFeedback();

            if (ev == null || !hasTarget || string.IsNullOrEmpty(target.Key))
                return;   // 松手时不在任何组上：什么都不做
            if (set != null && set.Count >= 2)
                DecoGroupActions.DropDecorations(set, target.Key, target.Anchor, target.Before);
            else
                DecoGroupActions.DropDecoration(ev, target.Key, target.Anchor, target.Before);
        }

        /// <summary>行被回收/面板重建（拖到一半行没了）：清干净，别把状态带到下一次拖动。</summary>
        private void OnDisable()
        {
            if (dragging == null)
                return;
            dragging = null;
            draggingSet = null;
            DecoGroupRenderer.ClearDropFeedback();
        }
    }
}
