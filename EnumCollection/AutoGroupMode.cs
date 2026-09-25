namespace ADOFAIEditorExtension
{
    /// <summary>
    /// 装饰栏自动分组方式。ByType = 按装饰类型（图片/文本/对象/粒子），ByTag = 按装饰 tag，
    /// Custom = 只用自定义分组（未命中自定义 tag 的装饰归入「未分组」）。
    /// 「不分组」由 decoGroupingEnabled=false 表示，不在本枚举里。
    /// </summary>
    public enum AutoGroupMode
    {
        ByType,
        ByTag,
        Custom
    }
}
