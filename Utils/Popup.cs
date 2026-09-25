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

        public static void ShowMessage(string message)
        {
            if (popup == null || scnEditor.instance == null)
                return;
            popup.transform.SetParent(null, false);
            popup.SetActive(true);
            scnEditor.instance.ShowPopup(true, (scnEditor.PopupType)233, false);
            popup.transform.SetParent(scnEditor.instance.popupWindow.transform, false);
            popup.transform.Find("popupText").GetComponent<TMP_Text>().text = message;
            Button button = popup.transform.Find("buttonOk").GetComponent<Button>();
            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(() => scnEditor.instance.ShowPopup(false, (scnEditor.PopupType)233, false));
        }
    }
}
