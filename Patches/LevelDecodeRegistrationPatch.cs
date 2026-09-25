using ADOFAI;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace ADOFAIEditorExtension.Patches
{
    /// <summary>
    /// 读档前置确保注册 + 读档后置自检（§43 加固项 1、2）。
    ///
    /// 为什么要挂在 <c>ADOFAI.LevelData.Decode</c> 上：v2.9.8 的 <c>LevelEvent.Decode</c> 只按
    /// <c>info.propertiesInfo</c> 里**注册过**的键重建 <c>data</c>，<c>Encode</c> 也只写注册过的键。
    /// 我们的三个键（始终注册的 <c>aeeNote</c>、写入开关开时注册的 <c>aeeGroupDeco</c> /
    /// <c>aeeGroupEvent</c>）原本只在 <c>scnEditor.Awake</c> Prefix（<c>EditorIntegration.Inject()</c>）
    /// 与 <c>scnEditor.Start</c> Postfix 两处注册。若某次会话里有别的流程提前解码关卡
    /// （<c>LevelData.Decode</c> 跑在注册之前），这三个键会在**读档**时被静默丢弃，
    /// 而玩家下一次保存就会把它们永久抹掉 —— 且日志里什么都看不到。
    ///
    /// 本补丁是第三道防线：Prefix 再确保一次（两个注册方法都是幂等的），
    /// Postfix 把读档结果打一条自检日志，键全丢时醒目报警。
    /// 现有 Awake/Start 两条注册路径**保持不变**，不动 <c>Inject()</c> 的结构。
    /// </summary>
    [HarmonyPatch]
    internal static class LevelDecodeRegistrationPatch
    {
        /// <summary>
        /// 按名字找 <c>ADOFAI.LevelData.Decode</c>。
        /// 同 <see cref="LevelEncodeFinalizerPatch"/>：用反射按名定位而不是 <c>typeof(LevelData)</c>，
        /// 游戏版本一换只会让这一个补丁在启动日志里报“应用失败”，不连带编译期或别的补丁。
        ///
        /// 据查目标形态是 <c>Decode(Dictionary&lt;string, object&gt;, out LoadResult)</c>，但不写死：
        /// 先按这个形态挑，挑不到就退回“唯一一个同名方法”。定位结果打进日志，以实际反射为准。
        /// </summary>
        internal static MethodBase TargetMethod()
        {
            Type levelData = AccessTools.TypeByName("ADOFAI.LevelData");
            if (levelData == null)
            {
                Main.Logger?.Log("读档自检补丁：找不到类型 ADOFAI.LevelData，跳过挂载");
                return null;
            }

            List<MethodInfo> candidates = new List<MethodInfo>();
            foreach (MethodInfo method in levelData.GetMethods(AccessTools.all))
            {
                if (string.Equals(method.Name, "Decode", StringComparison.Ordinal))
                    candidates.Add(method);
            }

            if (candidates.Count == 0)
            {
                Main.Logger?.Log("读档自检补丁：ADOFAI.LevelData 上没有 Decode 方法，跳过挂载");
                return null;
            }

            MethodInfo chosen = null;
            foreach (MethodInfo candidate in candidates)
            {
                ParameterInfo[] parameters = candidate.GetParameters();
                if (!candidate.IsStatic && parameters.Length == 2
                    && LooksLikeStringObjectDictionary(parameters[0].ParameterType)
                    && parameters[1].ParameterType.IsByRef)
                {
                    chosen = candidate;
                    break;
                }
            }
            if (chosen == null && candidates.Count == 1 && !candidates[0].IsStatic)
                chosen = candidates[0];

            if (chosen == null)
            {
                Main.Logger?.Log("读档自检补丁：Decode 共 " + candidates.Count +
                    " 个候选，没有「实例方法 + 字典参数 + out」形态的重载，跳过挂载");
                return null;
            }

            Main.Logger?.Log("读档自检补丁挂载目标: " + Describe(chosen));
            return chosen;
        }

        /// <summary>
        /// 读档之前确保三个键已注册（幂等；<c>Inject()</c> 里是同样这两个调用）。
        /// 整体包 try/catch：这只是保险，绝不能打断游戏自己的解码流程。
        /// </summary>
        internal static void Prefix()
        {
            try
            {
                Features.DecoGrouping.DecoGroupState.EnsureInvisibleProperties();
                Features.Notes.EventNote.EnsureRegistered();
            }
            catch (Exception e)
            {
                Main.Logger?.Log("读档前置属性注册失败: " + e.Message);
            }
        }

        /// <summary>
        /// 读档之后自检：事件/装饰数量 + 三个键各自的出现次数。
        /// <c>__instance</c> 声明成 <c>object</c>，字段按名反射读，避免把 <c>LevelData</c> 的成员类型写死。
        /// </summary>
        internal static void Postfix(object __instance)
        {
            try
            {
                List<LevelEvent> events = ReadEventList(__instance, "levelEvents");
                List<LevelEvent> decorations = ReadEventList(__instance, "decorations");

                int eventCount = events != null ? events.Count : 0;
                int decorationCount = decorations != null ? decorations.Count : 0;
                int noteCount = CountKey(events, Features.Notes.EventNote.KeyNote)
                    + CountKey(decorations, Features.Notes.EventNote.KeyNote);
                int decoGroupCount = CountKey(events, Features.DecoGrouping.DecoGroupState.MemberKeyDeco)
                    + CountKey(decorations, Features.DecoGrouping.DecoGroupState.MemberKeyDeco);
                int eventGroupCount = CountKey(events, Features.DecoGrouping.DecoGroupState.MemberKeyEvent)
                    + CountKey(decorations, Features.DecoGrouping.DecoGroupState.MemberKeyEvent);

                Main.Logger?.Log(string.Format(
                    "读档完成: 事件 {0} 个 / 装饰 {1} 个 / 带 aeeNote 键 {2} 个 / 带 aeeGroupDeco 键 {3} 个 / 带 aeeGroupEvent 键 {4} 个",
                    eventCount, decorationCount, noteCount, decoGroupCount, eventGroupCount));

                if (eventCount + decorationCount > 0 && noteCount == 0 && decoGroupCount == 0 && eventGroupCount == 0)
                {
                    Main.Logger?.Log("【警告】关卡里有事件/装饰，但 aeeNote / aeeGroupDeco / aeeGroupEvent " +
                        "三个键一个都没读回来 —— 疑似属性注册时序失败，键已在解码时被丢弃。" +
                        "**本次保存会把这些数据永久抹掉**，请先另存一份再操作。");
                }
            }
            catch (Exception e)
            {
                Main.Logger?.Log("读档后置自检失败: " + e.Message);
            }
        }

        // ------------------------------------------------------------------ 反射小工具

        private static bool LooksLikeStringObjectDictionary(Type type)
        {
            if (type == null || !type.IsGenericType)
                return false;
            Type[] genericArguments = type.GetGenericArguments();
            if (genericArguments.Length != 2 || genericArguments[0] != typeof(string)
                || genericArguments[1] != typeof(object))
                return false;
            Type definition = type.GetGenericTypeDefinition();
            return definition == typeof(Dictionary<,>) || definition == typeof(IDictionary<,>)
                || definition == typeof(SortedDictionary<,>);
        }

        private static string Describe(MethodInfo method)
        {
            try
            {
                ParameterInfo[] parameters = method.GetParameters();
                string[] rendered = new string[parameters.Length];
                for (int i = 0; i < parameters.Length; i++)
                {
                    Type parameterType = parameters[i].ParameterType;
                    if (parameterType.IsByRef)
                    {
                        parameterType = parameterType.GetElementType();
                        rendered[i] = "ref/out " + (parameterType != null ? Name(parameterType) : "?");
                    }
                    else
                    {
                        rendered[i] = Name(parameterType);
                    }
                }
                return (method.IsStatic ? "static " : "") + Name(method.ReturnType) + " "
                    + method.DeclaringType?.Name + "." + method.Name + "(" + string.Join(", ", rendered) + ")";
            }
            catch
            {
                return method?.Name;
            }
        }

        private static string Name(Type type)
        {
            if (type == null)
                return "void";
            if (type.IsGenericType)
            {
                string name = type.Name;
                int tick = name.IndexOf('`');
                if (tick > 0)
                    name = name.Substring(0, tick);
                Type[] arguments = type.GetGenericArguments();
                string[] rendered = new string[arguments.Length];
                for (int i = 0; i < arguments.Length; i++)
                    rendered[i] = Name(arguments[i]);
                return name + "<" + string.Join(", ", rendered) + ">";
            }
            return type.Name;
        }

        /// <summary>
        /// 按名读取 <c>LevelData</c> 上的事件列表（字段或同名属性，含基类）。
        /// 运行时实际类型可能是 <c>List&lt;LevelEvent&gt;</c> 的派生类（装饰数组）或数组，
        /// 所以拿不到 <c>List&lt;LevelEvent&gt;</c> 时退回按 <c>IEnumerable</c> 收一遍；
        /// 返回 null 表示“这个成员读不出来”（与“读到了但是空的”区分开）。
        /// </summary>
        private static List<LevelEvent> ReadEventList(object target, string memberName)
        {
            if (target == null)
                return null;

            for (Type type = target.GetType(); type != null && type != typeof(object); type = type.BaseType)
            {
                FieldInfo field = type.GetField(memberName, AccessTools.all);
                if (field != null)
                {
                    List<LevelEvent> fromField = AsEventList(field.GetValue(target));
                    if (fromField != null)
                        return fromField;
                }

                System.Reflection.PropertyInfo property = type.GetProperty(memberName, AccessTools.all);
                if (property != null && property.GetGetMethod(true) != null)
                {
                    List<LevelEvent> fromProperty = AsEventList(property.GetValue(target, null));
                    if (fromProperty != null)
                        return fromProperty;
                }
            }
            return null;
        }

        private static List<LevelEvent> AsEventList(object raw)
        {
            if (raw == null)
                return null;
            if (raw is List<LevelEvent> direct)
                return direct;
            if (raw is IEnumerable enumerable)
            {
                List<LevelEvent> collected = new List<LevelEvent>();
                foreach (object item in enumerable)
                    if (item is LevelEvent levelEvent)
                        collected.Add(levelEvent);
                return collected;
            }
            return null;
        }

        /// <summary>统计 data 里带某个键的事件数（null 事件 / null data 都不算）。</summary>
        private static int CountKey(List<LevelEvent> events, string key)
        {
            if (events == null)
                return 0;
            int count = 0;
            for (int i = 0; i < events.Count; i++)
            {
                LevelEvent levelEvent = events[i];
                if (levelEvent != null && levelEvent.data != null && levelEvent.data.ContainsKey(key))
                    count++;
            }
            return count;
        }
    }
}
