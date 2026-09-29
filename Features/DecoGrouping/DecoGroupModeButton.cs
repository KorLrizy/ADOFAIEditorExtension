using ADOFAI;
using ADOFAI.LevelEditor.Controls;
using ADOFAIEditorExtension.Utils;
using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ADOFAIEditorExtension.Features.DecoGrouping
{
    /// <summary>
    /// 装饰列表面板里的“分组方式”小按钮：克隆面板自带的按钮 prefab（底部工具栏里那排类型筛选按钮
    /// 就是用它 instantiate 出来的），点一下循环 不分组 → 按类型 → 按标签 → 自定义 → 不分组，
    /// 状态与模组设置页共用同一批设置字段，改完立即重建列表。
    /// </summary>
    internal static class DecoGroupModeButton
    {
        private const float ButtonWidth = 104f;
        private const string ButtonName = "aee_groupModeButton";
        // 硬编浅色：不再从占位符/其它控件取色（取到暗色就会变成深底黑字）
        private static readonly Color TextColor = new Color(0.92f, 0.92f, 0.92f, 1f);

        private static GameObject buttonRoot;
        private static TMP_Text label;

        /// <summary>面板创建后挂一次（DecorationsList.Start 后置补丁调用）。</summary>
        internal static void Attach(PropertyControl_DecorationsList panel)
        {
            if (panel == null || panel.buttonsContainer == null || panel.buttonTemplate == null)
                return;

            if (buttonRoot != null && buttonRoot.transform != null && buttonRoot.transform.parent == panel.buttonsContainer)
            {
                Refresh();
                return;
            }

            // buttonTemplate 是 RectTransform（原版就是这么 instantiate 的）
            RectTransform cloneRect = UnityEngine.Object.Instantiate(panel.buttonTemplate, panel.buttonsContainer);
            GameObject go = cloneRect != null ? cloneRect.gameObject : null;
            if (go == null)
                return;
            go.name = ButtonName;
            go.SetActive(true);
            buttonRoot = go;

            // 模板可能自带 CanvasGroup（透明/不可交互），先归一化，否则连文字一起看不见
            CanvasGroup group = go.GetComponent<CanvasGroup>();
            if (group != null)
            {
                group.alpha = 1f;
                group.interactable = true;
                group.blocksRaycasts = true;
            }

            // 图标留空、只当底板，信息全部由文字承担（图标语义在 4 个状态间无法自解释）
            Image icon = go.GetComponent<Image>();
            if (icon != null)
            {
                icon.sprite = null;
                icon.color = new Color(1f, 1f, 1f, 0.12f);
            }

            RectTransform rect = go.GetComponent<RectTransform>();
            float height = rect != null && rect.sizeDelta.y > 4f ? rect.sizeDelta.y : 28f;
            if (rect != null)
                rect.sizeDelta = new Vector2(ButtonWidth, height);
            // 布局组把按钮压成 0 宽时兜底给回可读宽度（否则文字矩形同样为 0）
            if (rect != null && rect.rect.width < 40f)
                rect.sizeDelta = new Vector2(ButtonWidth, rect.sizeDelta.y > 4f ? rect.sizeDelta.y : 28f);
            if (rect != null)
                rect.localScale = Vector3.one;

            LayoutElement element = go.GetComponent<LayoutElement>();
            if (element == null)
                element = go.AddComponent<LayoutElement>();
            element.minWidth = ButtonWidth;
            element.preferredWidth = ButtonWidth;
            element.flexibleWidth = 0f;
            element.minHeight = height;
            element.preferredHeight = height;

            label = CreateLabel(go, panel.searchFieldPlaceholder);

            Button button = go.GetComponent<Button>();
            if (button == null)
                button = go.AddComponent<Button>();
            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(Cycle);

            Refresh();
        }

        /// <summary>模式变化后刷新按钮文字。</summary>
        internal static void Refresh()
        {
            if (label == null)
                return;
            string text = ModeLabel(Main.CurrentGroupingMode);
            if (string.IsNullOrEmpty(text))
                text = Main.CurrentGroupingMode.ToString();
            label.text = text;
            label.color = TextColor;
            label.enableAutoSizing = true;
            label.overflowMode = TextOverflowModes.Overflow;
            label.ForceMeshUpdate();   // 立即排版，避免创建当帧还看不到字
            if (string.IsNullOrEmpty(label.text) && Main.Logger != null)
                Main.Logger.Log("分组方式按钮：文字为空");
        }

        internal static void Reset()
        {
            buttonRoot = null;
            label = null;
        }

        /// <summary>模组被关掉而编辑器还活着（没能重启）：把按钮从工具栏里拿掉。</summary>
        internal static void Detach()
        {
            if (buttonRoot != null)
            {
                buttonRoot.SetActive(false);
                UnityEngine.Object.Destroy(buttonRoot);
            }
            Reset();
        }

        private static void Cycle()
        {
            if (!Main.IsEnabled)
                return;   // 模组已关闭（补丁已卸）：残留按钮不再改设置
            switch (Main.CurrentGroupingMode)
            {
                case Main.GroupingMode.ByType:
                    Main.SetGroupingMode(Main.GroupingMode.ByTag);
                    break;
                case Main.GroupingMode.ByTag:
                    Main.SetGroupingMode(Main.GroupingMode.Custom);
                    break;
                case Main.GroupingMode.Custom:
                    Main.SetGroupingMode(Main.GroupingMode.Off);
                    break;
                default:
                    Main.SetGroupingMode(Main.GroupingMode.ByType);
                    break;
            }
        }

        private static TMP_Text CreateLabel(GameObject parent, TMP_Text fontSource)
        {
            var go = new GameObject("aee_groupModeText", typeof(RectTransform), typeof(TextMeshProUGUI));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent.transform, false);
            // 铺满按钮（留 2px 边距），保证文字矩形不会被裁成 0
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(2f, 0f);
            rect.offsetMax = new Vector2(-2f, 0f);
            rect.localScale = Vector3.one;
            go.transform.SetAsLastSibling();   // 画在按钮底板之上

            TextMeshProUGUI text = go.GetComponent<TextMeshProUGUI>();
            TMP_FontAsset font = fontSource != null ? fontSource.font : null;
            if (font == null)
                font = TMP_Settings.defaultFontAsset;
            if (font == null)
            {
                Main.Logger?.Log("分组方式按钮：找不到可用字体，按钮文字会不可见");
                return text;
            }
            text.font = font;
            // 关键：用字体自带的默认材质。字体源是原版的“搜索占位符”文本，它的 fontSharedMaterial
            // 往往是暗色/半透明的一套，继承过来后无论 color 设多亮都显示不出来。
            text.fontSharedMaterial = font.material;

            text.color = TextColor;
            text.fontSize = 18f;
            text.enableAutoSizing = true;
            text.fontSizeMin = 9f;
            text.fontSizeMax = 20f;
            text.alignment = TextAlignmentOptions.Center;
            text.enableWordWrapping = false;
            text.overflowMode = TextOverflowModes.Overflow;   // 不裁切
            text.raycastTarget = false;
            return text;
        }

        private static string ModeLabel(Main.GroupingMode mode)
        {
            string key = "aee.group.mode." + mode.ToString().ToLowerInvariant();
            string text = Main.Localizations?.GetValue(key);
            return string.IsNullOrEmpty(text) ? mode.ToString() : text;
        }
    }
}
