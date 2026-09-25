using ADOFAI;
using System.Collections.Generic;
using System.Linq;

namespace ADOFAIEditorExtension.Utils
{
    /// <summary>
    /// 标签页 LevelEvent 的刷新辅助（照 MultiTrackHelper.Utils.LevelEventUtils 复制）。
    /// </summary>
    public static class LevelEventUtils
    {
        /// <summary>重建该设置型事件对应的属性面板（可见行/控件状态随之刷新）。</summary>
        public static void UpdatePanel(this LevelEvent e)
        {
            if (e == null || scnEditor.instance == null || scnEditor.instance.settingsPanel == null)
                return;
            List<PropertiesPanel> panels = scnEditor.instance.settingsPanel.panelsList;
            if (panels == null)
                return;
            PropertiesPanel panel = panels.Find(p => p.levelEventType == e.eventType);
            if (panel != null)
                panel.SetProperties(e);
        }
    }
}
