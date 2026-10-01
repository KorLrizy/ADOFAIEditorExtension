using System;

namespace ADOFAIEditorExtension.Settings
{
    /// <summary>
    /// 分页器直选弹窗的**布局偏好**（用户手工调的宽高 / 位置 / 吸附边），与"设置页上那三个开关"分开放：
    ///
    ///  · 三个开关（自动调节窗口 / 窗口放大倍率 / 窗口自动吸附）是普通设置项，沿用标签页 LevelEvent +
    ///    <see cref="SettingsStore"/> 的原有字段持久化，读写都走 <see cref="Main.GetBoolSetting"/> /
    ///    <see cref="Main.PagerWindowScale"/>。它们**不进**这里。
    ///  · 这里的 5 个值是纯 UI 偏好（谁也不会去设置页改它们，只在拖拽结束时由
    ///    <c>Features.PagerList.PagerListController</c> 写一次），所以不挂在关卡事件上，
    ///    而是由 <see cref="SettingsStore"/> 序列化成 Settings.json 里的 <c>root.pagerWindow</c> 对象。
    ///
    /// 默认值刻意都是"没有偏好"：
    ///  · <see cref="ManualWidth"/> / <see cref="ManualHeight"/> = 0 ⇒ 用户没手调过尺寸，弹窗照常按行数自动定高；
    ///    只有两者都 &gt; 0 才算"有效手调尺寸"（见 <see cref="HasManualSize"/>）。
    ///  · <see cref="PositionX"/> / <see cref="PositionY"/> = 0.5 ⇒ 可移动范围内的**归一化**中心位置（0..1），
    ///    0.5 = 居中，和弹窗一直以来的落点一致。**两种模式（自动尺寸开/关）都会记住位置**，
    ///    因为拖动标题移动窗口在两种模式下都能用。
    ///  · <see cref="SnapEdges"/> = 0 ⇒ 不贴边。它记的是"上次拖完贴在屏幕哪条边/哪个角"
    ///    （屏幕四边四角，拖动标题时自动吸附，**只改位置、不改窗口大小**，自动尺寸开着时同样有效）。
    ///
    /// 写入一律经过 <see cref="TrySetLayout"/> / <see cref="TrySetManualSize"/> / <see cref="TrySetSnapEdges"/>，
    /// 非法值（NaN / Infinity / 超范围 / 自相矛盾的吸附边）不是被拒就是被规范化，绝不会让坏数字进到
    /// 运行时几何计算里。所有方法都非泛型 <c>Get</c> + <c>is</c> 判断，成员改名也只是拿到 null，不会抛。
    /// </summary>
    internal static class PagerWindowPreferences
    {
        /// <summary>手工宽度下限（UI 单位）：&gt; 0 才算"用户手调过"。</summary>
        internal const float MinManualSize = 1f;

        /// <summary>手工宽高上限（UI 单位）：旧设置文件里被写坏的极大值在这里被夹回去。</summary>
        internal const float MaxManualSize = 10000f;

        /// <summary>吸附边位标志（与 <c>Features.PagerList.PagerWindowGeometry</c> 的 WindowEdges 同值）。</summary>
        internal const int SnapLeft = 1;
        internal const int SnapRight = 2;
        internal const int SnapBottom = 4;
        internal const int SnapTop = 8;
        internal const int SnapAllEdges = SnapLeft | SnapRight | SnapBottom | SnapTop;

        private static float manualWidth;
        private static float manualHeight;
        private static float positionX = 0.5f;
        private static float positionY = 0.5f;
        private static int snapEdges;

        /// <summary>
        /// 用户手调的窗口宽度（UI 单位）；0 = 没手调过。setter 是**严格**的：非有限数 / 负数 / 小于下限一律拒收，
        /// 大于上限夹到上限（"宁可不变也不要写进坏数字"）—— 与 <see cref="TrySetManualSize"/> 的宽松语义不同，
        /// 后者是读盘路径（坏值应当回落成"没手调"，而不是把上一次的好数据也一起废掉）。
        /// </summary>
        internal static float ManualWidth
        {
            get { return manualWidth; }
            set
            {
                if (!IsFinite(value) || value < 0f)
                    return;
                manualWidth = value < MinManualSize ? 0f : (value > MaxManualSize ? MaxManualSize : value);
            }
        }

        /// <summary>用户手调的窗口高度（UI 单位）；0 = 没手调过。setter 语义同 <see cref="ManualWidth"/>。</summary>
        internal static float ManualHeight
        {
            get { return manualHeight; }
            set
            {
                if (!IsFinite(value) || value < 0f)
                    return;
                manualHeight = value < MinManualSize ? 0f : (value > MaxManualSize ? MaxManualSize : value);
            }
        }

        /// <summary>手调宽高是否有效（两者都 &gt; 0）；false ⇒ 控制器应当走自动定高。</summary>
        internal static bool HasManualSize
        {
            get { return manualWidth > 0f && manualHeight > 0f; }
        }

        /// <summary>窗口中心在可移动范围内的归一化 X（0..1，0.5 = 居中）。setter 会夹到 0..1（NaN/Infinity 拒收）。</summary>
        internal static float PositionX
        {
            get { return positionX; }
            set
            {
                if (!IsFinite(value))
                    return;
                positionX = Clamp01(value);
            }
        }

        /// <summary>窗口中心在可移动范围内的归一化 Y（0..1，0.5 = 居中）。setter 会夹到 0..1（NaN/Infinity 拒收）。</summary>
        internal static float PositionY
        {
            get { return positionY; }
            set
            {
                if (!IsFinite(value))
                    return;
                positionY = Clamp01(value);
            }
        }

        /// <summary>
        /// 吸附边（位标志，见 SnapLeft/SnapRight/SnapBottom/SnapTop）；0 = 不吸附。
        /// 记录的是**拖动标题移动窗口**结束后贴住了屏幕的哪条边 / 哪个角（两种模式都能拖标题，所以都可能更新）；
        /// 吸附只改位置、不改窗口大小。
        /// setter 直接走 <see cref="SanitizeSnapEdges"/>（未知位丢掉、左右/上下只留一条），永远不会写入矛盾的组合。
        /// </summary>
        internal static int SnapEdges
        {
            get { return snapEdges; }
            set { snapEdges = SanitizeSnapEdges(value); }
        }

        /// <summary>
        /// 一次性写入宽高 + 位置 + 吸附边（<c>PagerListController</c> 在拖拽/缩放结束时调用）。
        /// 任一值非法时该字段保持原值不动（不写入半套坏数据），其余字段照常写入。
        /// </summary>
        internal static void TrySetLayout(float width, float height, float x, float y, int edges)
        {
            if (!TrySetManualSize(width, height))
                return;
            TrySetPosition(x, y);
            TrySetSnapEdges(edges);
        }

        /// <summary>写入手调宽高；两者必须都是有限数且落在 0 或 [<see cref="MinManualSize"/>, <see cref="MaxManualSize"/>] 内。</summary>
        internal static bool TrySetManualSize(float width, float height)
        {
            if (!TrySanitizeManualSize(width, out float w) || !TrySanitizeManualSize(height, out float h))
                return false;
            manualWidth = w;
            manualHeight = h;
            return true;
        }

        /// <summary>写入归一化中心位置；两个轴都会夹到 0..1（NaN/Infinity ⇒ 保持原值）。</summary>
        internal static bool TrySetPosition(float x, float y)
        {
            if (!IsFinite(x) || !IsFinite(y))
                return false;
            positionX = Clamp01(x);
            positionY = Clamp01(y);
            return true;
        }

        /// <summary>写入吸附边：先规范化（去未知位、解除左右/上下同贴的矛盾），永远成功。</summary>
        internal static void TrySetSnapEdges(int edges)
        {
            snapEdges = SanitizeSnapEdges(edges);
        }

        /// <summary>回到"没有偏好"的初始状态（清空手工尺寸 + 居中 + 不贴边）。</summary>
        internal static void Reset()
        {
            manualWidth = 0f;
            manualHeight = 0f;
            positionX = 0.5f;
            positionY = 0.5f;
            snapEdges = 0;
        }

        // ------------------------------------------------------------------ 规范化（SettingsStore 与应用几何共用）

        /// <summary>
        /// 手调尺寸的宽松规范化：非有限数 → false；&lt; <see cref="MinManualSize"/> → 0（当作没手调）；
        /// 其余夹到 <see cref="MaxManualSize"/>。返回 false 表示这个值不可用（调用方保持原值）。
        /// </summary>
        internal static bool TrySanitizeManualSize(float value, out float sanitized)
        {
            sanitized = 0f;
            if (float.IsNaN(value) || float.IsInfinity(value))
                return false;
            if (value < MinManualSize)
            {
                sanitized = 0f;                      // 0（或负数）就是"没有手调尺寸"这个合法状态
                return true;
            }
            sanitized = value > MaxManualSize ? MaxManualSize : value;
            return true;
        }

        /// <summary>
        /// 吸附边规范化：未知位直接丢掉；同方向的两条边同时被置位是自相矛盾的，按"保留先出现的那条"
        /// 处理（Left 优先于 Right、Bottom 优先于 Top）。这样 0..15 的任意整数进来都能得到一个可用结果。
        /// </summary>
        internal static int SanitizeSnapEdges(int edges)
        {
            int result = edges & SnapAllEdges;
            if ((result & SnapLeft) != 0)
                result &= ~SnapRight;
            if ((result & SnapBottom) != 0)
                result &= ~SnapTop;
            return result;
        }

        internal static float Clamp01(float value)
        {
            if (float.IsNaN(value) || value <= 0f)
                return 0f;
            return value >= 1f ? 1f : value;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
