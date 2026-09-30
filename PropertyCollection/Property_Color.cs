using System.Collections.Generic;
using UnityEngine;

namespace ADOFAIEditorExtension.PropertyCollection
{
    /// <summary>
    /// 颜色字段声明（照 MultiTrackHelper.PropertyCollection.Property_Color 复制，只换命名空间）。
    ///
    /// 原版 ADOFAI.PropertyInfo 的构造函数看到 data["type"] == "Color" 时（r265 IL 已核）：
    ///  · data["usesAlpha"] 存在就 Convert.ToBoolean 它，否则**默认 true** ⇒ PropertyInfo.color_usesAlpha；
    ///  · data 里**有** "default" 就 `castclass String` 取它（所以必须是 string）⇒ PropertyInfo.value_default；
    ///    没有 "default" 时按 color_usesAlpha 回落到 "ffffff"/"ffffffff"。
    /// PropertyControl_Color.Setup 再把 usesAlpha / defaultValue 交给原版取色器组件（ColorField），
    /// 于是点击控件弹出的就是原版取色器、带 RGBA 四通道（IL：`colorField.colorPickerPopup = editor.colorPickerPopup`）。
    /// 面板回填当前值时走 PropertyControl_Color.set_text ⇒ ColorField.value（纯显示，不触发 onChange，IL 已核）。
    ///
    /// 值在 LevelEvent data 里与装饰的 color 属性同款：**小写 hex 字符串**
    /// （usesAlpha 时 8 位 rrggbbaa，否则 6 位 rrggbb），与 ColorField.Validate 的产出格式一致。
    /// </summary>
    public class Property_Color : Property
    {
        public Property_Color(string name, Color? value_default = null, bool usesAlpha = true, string key = null, bool canBeDisabled = false, bool startEnabled = false, Dictionary<string, string> enableIf = null, Dictionary<string, string> disableIf = null)
            : base(name, key, canBeDisabled, startEnabled, enableIf, disableIf)
        {
            data["type"] = "Color";
            data["default"] = ToHex(value_default.GetValueOrDefault(Color.white), usesAlpha);
            data["usesAlpha"] = usesAlpha;
        }

        /// <summary>Color → hex（小写）。与 ColorField/RDUtils.ToHex 的约定一致：带 alpha 时 8 位，否则 6 位。</summary>
        private static string ToHex(Color c, bool alpha)
        {
            return (alpha
                ? string.Format("{0:X2}{1:X2}{2:X2}{3:X2}", ToByte(c.r), ToByte(c.g), ToByte(c.b), ToByte(c.a))
                : string.Format("{0:X2}{1:X2}{2:X2}", ToByte(c.r), ToByte(c.g), ToByte(c.b))).ToLower();
        }

        /// <summary>0..1 的通道值 → 0..255。默认色 new Color(1,1,1,0) 因此得到 "ffffff00"（全透明纯白）。</summary>
        private static byte ToByte(float f)
        {
            return (byte)(Mathf.Clamp01(f) * 255f);
        }
    }
}
