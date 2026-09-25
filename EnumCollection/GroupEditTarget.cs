namespace ADOFAIEditorExtension
{
    /// <summary>
    /// 设置页「编辑目标」的两态：装饰分组 / 事件分组（§17.2）。
    /// 只有两个成员 ⇒ 原版 PropertyControl_Toggle 用**按钮**而不是下拉框渲染（与 MultiTrackHelper
    /// 的 AffectAt 同款：三个成员以上才退回下拉框）。
    /// 成员名同时是本地化键 enum.ADOFAIEditorExtension.GroupEditTarget.&lt;成员名&gt;。
    /// </summary>
    public enum GroupEditTarget
    {
        Decoration,
        Event
    }
}
