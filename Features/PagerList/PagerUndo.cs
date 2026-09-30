using ADOFAI;
using System;
using System.Collections.Generic;

namespace ADOFAIEditorExtension.Features.PagerList
{
    /// <summary>
    /// 弹窗内的撤销 / 重做（§45）：这里放**纯逻辑**与"该不该拦截"的判定，
    /// 真正的编排在 <see cref="PagerListController.UndoRedoInPopup"/>（只有它拿得到弹窗状态）。
    ///
    /// 导火索：本弹窗是 `scnEditor.ShowPopup` 打开的（`showingPopup == true`），而原版
    /// `scnEditor.HandleKeyboardActions` 一进来就是 `if (showingPopup) { 只处理 Esc; return; }`
    /// （r148 IL 已核：`ldfld showingPopup` → `brfalse` → `new EditorKeybind(0, 27, true).IsPressed()`
    /// → `ShowPopup(false,false,false)` → `ret`）⇒ 弹窗期间撤销 / 重做快捷键全部停摆，
    /// 用户必须先关窗再 Ctrl+Z，很不直观。
    ///
    /// 快捷键判定沿用复制粘贴那一套（原版键位表 `EditorKeybindManager.dictionary`，
    /// 用户改过键位也跟得上），撤销 / 重做入口见
    /// <see cref="ADOFAIEditorExtension.Utils.Pacl2Compat.LevelUndoRedo"/>。
    /// </summary>
    internal static class PagerUndo
    {
        /// <summary>
        /// 撤销 / 重做后"按对象重映射选中集与当前下标"（纯逻辑，离线 harness 直接断言；不碰 Unity、不碰静态状态）。
        ///
        /// 为什么必须按对象重映射：撤销会把事件对象**换掉或删掉**（原版撤销把整份 `levelData` 换成快照副本；
        /// PACL2 的增量回滚按引用 `Add`/`Remove`），弹窗记的下标与对象都可能指向已经不在这一堆里的事件 ——
        /// 不重映射就会出现"高亮停在别的事件上 / 当前行指向已删除的事件"。规则：
        ///  · 选中集只保留**还在** <paramref name="after"/> 里的对象，按新下标（升序）去重；
        ///  · 当前事件还在 ⇒ 用它的新下标；没了 ⇒ 取它**撤销前那个下标**处的邻居
        ///    （越界夹到末尾：撤掉最后一个事件后落在新的最后一个上，符合"光标停在原处"的直觉）；
        ///  · <paramref name="after"/> 为空 ⇒ 当前下标 -1（调用方据此关窗）。
        /// </summary>
        internal static void RemapSelection(
            IList<LevelEvent> after,
            IList<LevelEvent> selectedBefore,
            LevelEvent currentBefore,
            int currentIndexBefore,
            ISet<int> selectedIndices,
            out int currentIndex)
        {
            currentIndex = -1;
            if (selectedIndices != null)
                selectedIndices.Clear();
            if (after == null || after.Count == 0)
                return;

            RemapSelection(after, null, selectedBefore, currentBefore, currentIndexBefore, selectedIndices, out currentIndex, null);
        }

        /// <summary>
        /// 同上，但对象被换掉时（§45.2：原版撤销把 `levelData` 整份换成快照副本 ⇒ 撤销后一个旧对象都不在了）
        /// 再按**内容指纹**找回"同一个事件"：<paramref name="fingerprint"/> 相同的事件视为同一批，
        /// 旧对象在 <paramref name="stackBefore"/> 同指纹事件里排第 k 个 ⇒ 对到 <paramref name="after"/> 里同指纹的第 k 个
        /// （不够就取最后一个）。指纹完全相同的两个事件本来就分不出来，这样对最多是"在一模一样的两个里挑错一个"。
        /// 先按引用找，找不到才走指纹（没换对象的撤销，比如只记选择的撤销点，照旧按引用）。
        /// </summary>
        internal static void RemapSelection(
            IList<LevelEvent> after,
            IList<LevelEvent> stackBefore,
            IList<LevelEvent> selectedBefore,
            LevelEvent currentBefore,
            int currentIndexBefore,
            ISet<int> selectedIndices,
            out int currentIndex,
            Func<LevelEvent, string> fingerprint)
        {
            currentIndex = -1;
            if (selectedIndices != null)
                selectedIndices.Clear();
            if (after == null || after.Count == 0)
                return;

            if (selectedIndices != null && selectedBefore != null)
            {
                for (int i = 0; i < selectedBefore.Count; i++)
                {
                    LevelEvent e = selectedBefore[i];
                    if (e == null)
                        continue;
                    int index = MapToAfter(after, stackBefore, e, fingerprint);
                    if (index >= 0)
                        selectedIndices.Add(index);
                }
            }

            if (currentBefore != null)
                currentIndex = MapToAfter(after, stackBefore, currentBefore, fingerprint);
            if (currentIndex < 0)
            {
                int fallback = currentIndexBefore;
                if (fallback < 0)
                    fallback = 0;
                if (fallback > after.Count - 1)
                    fallback = after.Count - 1;
                currentIndex = fallback;
            }
        }

        /// <summary>先按引用；找不到且给了指纹 ⇒ 按"同指纹第 k 个"对过去（见上面的重载说明）。</summary>
        internal static int MapToAfter(IList<LevelEvent> after, IList<LevelEvent> stackBefore, LevelEvent target,
            Func<LevelEvent, string> fingerprint)
        {
            int index = IndexOfReference(after, target);
            if (index >= 0 || fingerprint == null || stackBefore == null || target == null)
                return index;
            string key = SafeFingerprint(fingerprint, target);
            if (key == null)
                return -1;

            int rank = 0;
            for (int i = 0; i < stackBefore.Count; i++)
            {
                if (ReferenceEquals(stackBefore[i], target))
                    break;
                if (stackBefore[i] != null && SafeFingerprint(fingerprint, stackBefore[i]) == key)
                    rank++;
            }

            int seen = 0, last = -1;
            for (int i = 0; i < after.Count; i++)
            {
                if (after[i] == null || SafeFingerprint(fingerprint, after[i]) != key)
                    continue;
                if (seen == rank)
                    return i;
                seen++;
                last = i;
            }
            return last;
        }

        private static string SafeFingerprint(Func<LevelEvent, string> fingerprint, LevelEvent e)
        {
            try { return fingerprint(e); }
            catch { return null; }
        }

        /// <summary>
        /// 事件内容指纹（§45.2）：类型 + 所在砖 + data 里除 <paramref name="ignoreKey"/> 以外的全部键值（按键名排序）。
        /// 忽略的是分组用的标签键 —— 撤销撤的往往正是"拖进别的组"，标签值前后不同，不能拿它当身份。
        /// </summary>
        internal static string Fingerprint(LevelEvent e, string ignoreKey)
        {
            if (e == null)
                return null;
            var sb = new System.Text.StringBuilder();
            sb.Append((int)e.eventType).Append('@').Append(e.floor);
            // r148 的 `LevelEvent.data` 是 protected，公开读口是 `GetData()`（返回同一张字典）
            Dictionary<string, object> data = e.GetData();
            if (data != null)
            {
                var keys = new List<string>(data.Keys);
                keys.Sort(StringComparer.Ordinal);
                for (int i = 0; i < keys.Count; i++)
                {
                    if (keys[i] == ignoreKey)
                        continue;
                    sb.Append('|').Append(keys[i]).Append('=');
                    AppendValue(sb, data[keys[i]]);
                }
            }
            return sb.ToString();
        }

        private static void AppendValue(System.Text.StringBuilder sb, object value)
        {
            if (value == null)
            {
                sb.Append("null");
                return;
            }
            if (value is string s)
            {
                sb.Append('"').Append(s).Append('"');
                return;
            }
            if (value is System.Collections.IEnumerable list)
            {
                sb.Append('[');
                foreach (object item in list)
                {
                    AppendValue(sb, item);
                    sb.Append(',');
                }
                sb.Append(']');
                return;
            }
            sb.Append(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// 按**引用**查找（不用 `List&lt;T&gt;.IndexOf`：它走 `EqualityComparer&lt;T&gt;`，
        /// 事件类型万一重写了 `Equals` 就换了语义；这里要的始终是"同一个对象"）。
        /// </summary>
        internal static int IndexOfReference(IList<LevelEvent> list, LevelEvent target)
        {
            if (list == null || target == null)
                return -1;
            for (int i = 0; i < list.Count; i++)
                if (ReferenceEquals(list[i], target))
                    return i;
            return -1;
        }

        /// <summary>
        /// 弹窗内按撤销 / 重做时，**文本输入框有焦点就不拦截**（按原版习惯：输入框里 Ctrl+Z 是文本撤销）。
        ///
        /// 原版 `HandleKeyboardActions` 在执行键位表之前就有这道门（r265 IL 已核：
        /// `if (showingPopup) {…return;}` 之后的 `call get_userIsEditingAnInputField` → `brtrue` 分支
        /// 连同 prefsContainer / particleEditorContainer 的判断一起 `ret`），而它的实现就是
        /// "`EventSystem.currentSelectedGameObject` 上取到 `TMP_InputField` 且 `isFocused`"。
        /// 我们的前缀跑在原版方法体**之前**，所以这道门得自己补。
        /// 读不到（属性改名等）时按"正在输入"处理：宁可少拦截一次，也不抢走输入框自己的文本撤销。
        /// 弹窗自身没有输入框（标题 / 事件行 / 备注浮层都是只读文本），这道门是给
        /// "打开弹窗前就聚焦着的属性输入框"兜底。
        /// </summary>
        internal static bool SkipForInputField(scnEditor editor)
        {
            if (editor == null)
                return true;
            try
            {
                return editor.userIsEditingAnInputField;
            }
            catch (Exception e)
            {
                Main.Logger?.Log("读取 userIsEditingAnInputField 失败（按正在输入处理，不拦截撤销/重做）: " + e.Message);
                return true;
            }
        }
    }
}
