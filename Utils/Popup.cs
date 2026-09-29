using ADOFAI;
using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ADOFAIEditorExtension.Utils
{
    /// <summary>
    /// 复用游戏原生弹窗显示一行提示（照 MultiTrackHelper.Utils.Popup 复制）。
    /// 弹窗模板由 scnEditor.Start 后置补丁克隆 okPopupContainer 得到（缺失时退回 largeOkPopupContainer）。
    /// </summary>
    internal static class Popup
    {
        public static GameObject popup;

        // 模板里正文 / 确定按钮相对弹窗根的路径：克隆时按原版字段（okPopupText / popupOkOk）算出来，
        // 克隆体结构与源完全一致，所以同一路径就能在克隆体里找到对应物体，不依赖子物体命名。
        private static string textPath;
        private static string buttonPath;

        /// <summary>编辑器 Start 时克隆一份游戏自带的确认弹窗作为消息模板。</summary>
        public static void EnsureCaptured()
        {
            scnEditor editor = scnEditor.instance;
            if (editor == null || popup != null || editor.popupWindow == null)
                return;

            GameObject source = editor.okPopupContainer;
            Component sourceText = editor.okPopupText;
            Component sourceButton = editor.popupOkOk;
            if (source == null)
            {
                source = editor.largeOkPopupContainer;
                sourceText = editor.largeOkPopupText;
                sourceButton = editor.popupLargeOkOk;
            }
            if (source == null)
                return;

            textPath = RelativePath(source.transform, sourceText);
            buttonPath = RelativePath(source.transform, sourceButton);
            popup = UnityEngine.Object.Instantiate(source, editor.popupWindow.transform);
            foreach (scrTextChanger changer in popup.GetComponentsInChildren<scrTextChanger>())
                UnityEngine.Object.Destroy(changer);
        }

        /// <summary>target 相对 root 的层级路径（Transform.Find 可用）；不在 root 之下返回 null。</summary>
        private static string RelativePath(Transform root, Component target)
        {
            if (root == null || target == null)
                return null;
            Transform t = target.transform;
            if (t == root)
                return "";
            List<string> names = new List<string>();
            while (t != null && t != root)
            {
                names.Insert(0, t.name);
                t = t.parent;
            }
            return t == root ? string.Join("/", names) : null;
        }

        /// <summary>先按克隆时记下的路径找，再按旧命名找，最后退回 GetComponentInChildren（不依赖命名）。</summary>
        private static T Resolve<T>(string path, string legacyName) where T : Component
        {
            Transform t = null;
            if (!string.IsNullOrEmpty(path))
                t = popup.transform.Find(path);
            if (t == null)
                t = popup.transform.Find(legacyName);
            T component = t != null ? t.GetComponent<T>() : null;
            return component != null ? component : popup.GetComponentInChildren<T>(true);
        }

        /// <summary>
        /// 显示一行提示。
        ///
        /// 文本/按钮**在 `ShowPopup(true,…)` 之前**就取好：原先是 `ShowPopup(true,…)` 之后才
        /// `transform.Find("popupText")` / `Find("buttonOk")`，游戏换 prefab 结构就 NRE ——
        /// 原版的 `showingPopup` 就此卡在 true，`HandleKeyboardActions` 只处理 Esc，表现为"ctrl+S 全废"。
        /// 现在取不到就不弹；整段兜异常，异常时把弹窗标志收回去。
        /// </summary>
        public static void ShowMessage(string message)
        {
            if (popup == null || scnEditor.instance == null)
                return;
            bool shown = false;
            try
            {
                TMP_Text text = Resolve<TMP_Text>(textPath, "popupText");
                Button button = Resolve<Button>(buttonPath, "buttonOk");
                if (text == null || button == null)
                {
                    Main.Logger?.Log("弹提示失败: 弹窗模板里找不到正文或确定按钮");
                    return;
                }

                popup.transform.SetParent(null, false);
                popup.SetActive(true);
                shown = true;
                scnEditor.instance.ShowPopup(true, (scnEditor.PopupType)233, false);
                popup.transform.SetParent(scnEditor.instance.popupWindow.transform, false);
                text.text = message;
                button.onClick.RemoveAllListeners();
                button.onClick.AddListener(() => scnEditor.instance.ShowPopup(false, (scnEditor.PopupType)233, false));
            }
            catch (Exception e)
            {
                Main.Logger?.Log("弹提示失败: " + e);
                // 标志已经置位就必须收回去，否则原版所有编辑器快捷键都停摆
                if (shown)
                {
                    try { scnEditor.instance?.ShowPopup(false, (scnEditor.PopupType)233, false); }
                    catch { }
                }
            }
        }
    }
}
