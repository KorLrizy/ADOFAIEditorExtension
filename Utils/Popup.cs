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
    /// 弹窗模板由 scnEditor.Start 后置补丁克隆 okPopupContainer 得到。
    /// </summary>
    internal static class Popup
    {
        public static GameObject popup;

        /// <summary>编辑器 Start 时克隆一份游戏自带的确认弹窗作为消息模板。</summary>
        public static void EnsureCaptured()
        {
            if (scnEditor.instance == null || scnEditor.instance.okPopupContainer == null || popup != null)
                return;
            popup = UnityEngine.Object.Instantiate(scnEditor.instance.okPopupContainer, scnEditor.instance.popupWindow.transform);
            foreach (scrTextChanger changer in popup.GetComponentsInChildren<scrTextChanger>())
                UnityEngine.Object.Destroy(changer);
        }

        /// <summary>
        /// 显示一行提示。
        ///
        /// 取文本/按钮**不按子物体名找**（原先是 `transform.Find("popupText")` / `Find("buttonOk")`，
        /// 游戏换 prefab 结构就 NRE，而 NRE 发生在 `ShowPopup(true,…)` 之后 ——
        /// 原版的 `showingPopup` 就此卡在 true，`HandleKeyboardActions` 只处理 Esc，表现为"ctrl+S 全废"，§42 bug 2）。
        /// 改成 `GetComponentInChildren`：不依赖命名。整段兜异常，异常时把弹窗标志收回去。
        /// </summary>
        public static void ShowMessage(string message)
        {
            if (popup == null || scnEditor.instance == null)
                return;
            try
            {
                popup.transform.SetParent(null, false);
                popup.SetActive(true);
                scnEditor.instance.ShowPopup(true, (scnEditor.PopupType)233, false);
                popup.transform.SetParent(scnEditor.instance.popupWindow.transform, false);

                TMP_Text text = popup.GetComponentInChildren<TMP_Text>(true);
                if (text != null)
                    text.text = message;

                Button button = popup.GetComponentInChildren<Button>(true);
                if (button != null)
                {
                    button.onClick.RemoveAllListeners();
                    button.onClick.AddListener(() => scnEditor.instance.ShowPopup(false, (scnEditor.PopupType)233, false));
                }
            }
            catch (Exception e)
            {
                Main.Logger?.Log("弹提示失败: " + e);
                // 标志已经置位就必须收回去，否则原版所有编辑器快捷键都停摆
                try { scnEditor.instance?.ShowPopup(false, (scnEditor.PopupType)233, false); }
                catch { }
            }
        }
    }
}
