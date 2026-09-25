using ADOFAI;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace ADOFAIEditorExtension
{
    /// <summary>
    /// 注入到游戏原生设置面板的标签页描述数据（照 MultiTrackHelper.CustomTab 裁剪）。
    /// </summary>
    internal class CustomTab
    {
        internal Sprite icon;
        internal int type;
        internal string name;
        internal string title;
        internal int index;
        internal List<Dictionary<string, object>> properties;
        internal Action onFocused;
        internal Action onUnFocused;
        internal Func<LevelEvent, string, object, object, bool> onChange;
        internal bool saveSetting;
        internal CustomTab() { }
    }
}
