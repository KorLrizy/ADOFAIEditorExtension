using HarmonyLib;
using System;
using System.Reflection;

namespace ADOFAIEditorExtension.Patches
{
    /// <summary>
    /// 让"关卡编码失败"这件事在日志里可见（§42 bug 1）。
    ///
    /// 现象：点保存只弹一句"保存失败！！！"，UMM 日志里什么都没有。原因是
    /// `scnEditor.SaveLevel` 自己把整段序列化包在 try/catch 里，异常在它内部就被吞掉换成弹框了
    /// —— 所以**挂在 `SaveLevel` 上的 Finalizer 永远拿不到异常**（它不是逃出来的，是被吃掉的）。
    /// 真正抛的地方是它调用的关卡编码：r265 的 `LevelEvent.Encode(bool)` 按 data 键遍历、
    /// 按 `PropertyInfo.type` 硬转（String/LongString/File/Color 走 `castclass System.String`，
    /// Int/Rating 走 `unbox.any Int32`，Float/Bool 同理），任何一处类型不符就抛 `InvalidCastException`，
    /// 一路冒到编码入口。因此 Finalizer 要挂在**编码入口**上，而不是 `SaveLevel` 上。
    ///
    /// 本补丁**只记日志、原样把异常抛回去**，不改变任何行为；纠正值类型是
    /// `Features/Notes/EventNote.cs` 里那个保存前置补丁的事。
    /// </summary>
    [HarmonyPatch]
    internal static class LevelEncodeFinalizerPatch
    {
        /// <summary>
        /// 按名字找 `ADOFAI.LevelData.Encode()`（无参）。
        /// 用反射按名定位而不是 `typeof(LevelData)`：游戏版本一换，类型/方法改名只让这一个补丁
        /// 在启动日志里报"应用失败"（`Main.StartMod` 是逐类打补丁、每类单独 catch 的），
        /// 不会连带编译期或别的补丁出问题。
        /// </summary>
        internal static MethodBase TargetMethod()
        {
            Type levelData = AccessTools.TypeByName("ADOFAI.LevelData");
            if (levelData == null)
                return null;
            return AccessTools.Method(levelData, "Encode", new Type[0]);
        }

        internal static Exception Finalizer(Exception __exception)
        {
            if (__exception != null)
                Main.Logger?.Log("关卡编码抛异常（保存会失败）: " + __exception);
            return __exception;
        }
    }
}
