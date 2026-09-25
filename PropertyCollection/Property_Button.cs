using System.Collections.Generic;
using UnityEngine.Events;

namespace ADOFAIEditorExtension.PropertyCollection
{
    /// <summary>Export 型字段：面板上渲染成一个按钮（游戏原生不支持，需替换 PropertiesPanel.RenderControl）。</summary>
    public class Property_Button : Property
    {
        public Property_Button(string name, UnityAction action, string key = null, bool canBeDisabled = false, bool startEnabled = false, Dictionary<string, string> enableIf = null, Dictionary<string, string> disableIf = null)
            : base(name, key, canBeDisabled, startEnabled, enableIf, disableIf)
        {
            data["type"] = "Export";
            data["default"] = action;
        }
    }
}
