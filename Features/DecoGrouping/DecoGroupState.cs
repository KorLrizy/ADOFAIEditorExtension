using ADOFAI;
using ADOFAI.LevelEditor.Controls;
using ADOFAIEditorExtension.Utils;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace ADOFAIEditorExtension.Features.DecoGrouping
{
    /// <summary>装饰栏列表里的一行显示槽位：分组头行或普通装饰行。</summary>
    internal sealed class DecoGroupSlot
    {
        /// <summary>true = 分组头行（非装饰数据），false = 普通装饰行。</summary>
        internal bool IsHeader;

        /// <summary>普通行对应的装饰事件。</summary>
        internal LevelEvent Event;

        /// <summary>头行的分组键（折叠状态以它为 key）。</summary>
        internal string Key;

        /// <summary>头行的显示名（已本地化）。</summary>
        internal string Label;

        /// <summary>头行的组内数量。</summary>
        internal int Count;
    }

    /// <summary>
    /// 分组功能的会话级状态：显示槽位表、折叠集合、自定义分组行。
    /// 折叠状态与自定义分组都只存在内存里（自定义分组的字段值存于标签页的 LevelEvent）。
    /// </summary>
    internal static class DecoGroupState
    {
        internal const int MaxCustomGroups = 20;

        /// <summary>当前面板的显示槽位（头行 + 行），顺序即列表显示顺序。</summary>
        internal static readonly List<DecoGroupSlot> Slots = new List<DecoGroupSlot>();

        /// <summary>折叠的分组键（会话级，不写关卡文件）。</summary>
        internal static readonly HashSet<string> CollapsedGroups = new HashSet<string>();

        /// <summary>装饰事件 → 所属分组键（用于"选中折叠组内的装饰时自动展开"）。</summary>
        internal static readonly Dictionary<LevelEvent, string> GroupKeyOfEvent = new Dictionary<LevelEvent, string>();

        /// <summary>分组键 → 该组的全部装饰事件（含折叠组；组头的可见/锁定/全选要用）。</summary>
        internal static readonly Dictionary<string, List<LevelEvent>> EventsByGroup = new Dictionary<string, List<LevelEvent>>();

        /// <summary>filteredEvents 下标 → 槽位下标（折叠行已被移除，两者顺序一致）。</summary>
        internal static int[] SlotIndexByRowIndex = new int[0];

        /// <summary>当前显示为行的装饰（= 重排后的 filteredEvents 内容，折叠组不在其中）。</summary>
        internal static readonly HashSet<LevelEvent> VisibleEvents = new HashSet<LevelEvent>();

        /// <summary>最近一次渲染的面板（头行点击回调要用它重建列表）。</summary>
        internal static PropertyControl_DecorationsList Panel;

        /// <summary>
        /// 拖动落点：目标分组键 + 插入锚点。
        /// `Anchor == null` 表示落在**组头行**上 ⇒ 按"插入到组尾"处理（见 §15.3）；
        /// 否则 `Anchor` 是落点那一行对应的装饰，`Before` 决定插到它前面还是后面。
        /// </summary>
        internal struct DropTarget
        {
            internal string Key;
            internal LevelEvent Anchor;
            internal bool Before;
        }

        private static int customGroupCount;
        private static int eventCustomGroupCount;

        /// <summary>两套互相独立的自定义分组定义：装饰用一套、事件直选弹窗用一套（见 §17.2）。</summary>
        public enum GroupSet
        {
            Decoration,
            Event
        }

        /// <summary>"手动归属"标记的键名。装饰与事件各用一个键（同一次序里装饰和事件是不同的对象，
        /// 但下标含义不同，分开更不容易混淆，离线排查也直观）。</summary>
        internal const string MemberKeyDeco = "aeeGroupDeco";
        internal const string MemberKeyEvent = "aeeGroupEvent";
        /// <summary>旧版本用的键（只写在装饰上过），读取时兼容、清理时一并剥离。</summary>
        internal const string MemberKeyLegacy = "aeeGroup";

        internal static string MemberKeyOf(GroupSet set) => set == GroupSet.Event ? MemberKeyEvent : MemberKeyDeco;

        /// <summary>
        /// 会话内的手动归属（关闭"写入关卡文件"时唯一的存放处，见 §17.3）。
        /// 按 LevelEvent 对象记：每次读档（<see cref="OnLevelLoaded"/>）与编辑器 Awake（<see cref="ClearSession"/>）清空，
        /// 不会把上一个关卡的对象一直攥在手里。
        /// **已知局限**：原版撤销/重做是把 `levelData` 整体换成 `LevelData.Copy()` 出来的快照对象（IL 已核：
        /// `SaveState` → `levelData.Copy()`，`UndoOrRedo` → `customLevel.levelData = state.data`），
        /// 快照里只有 data 没有这张表 ⇒ 关闭写入开关时，撤销/重做之后会话内的手动归属会"丢失"（对象换了）。
        /// 开着写入开关时归属在 data 里，随快照一起走，不受影响。
        /// </summary>
        private static readonly Dictionary<LevelEvent, int> sessionMembers = new Dictionary<LevelEvent, int>();

        /// <summary>装饰当前被手动指定到的自定义分组下标；-1 = 没有手动归属。</summary>
        internal static int GetManualGroup(LevelEvent e) => GetManualGroup(e, GroupSet.Decoration);

        internal static int GetManualGroup(LevelEvent e, GroupSet set)
        {
            if (e == null)
                return -1;
            // 写入开关开 且 该事件类型注册了这个键 ⇒ data 是唯一来源；否则看会话表
            if (Main.WriteGroupConfig && IsKeyRegistered(e, MemberKeyOf(set)))
                return ReadStoredMember(e, set);
            return sessionMembers.TryGetValue(e, out int session) ? session : -1;
        }

        internal static void SetManualGroup(LevelEvent e, int index) => SetManualGroup(e, GroupSet.Decoration, index);

        internal static void SetManualGroup(LevelEvent e, GroupSet set, int index)
        {
            if (e == null)
                return;
            sessionMembers[e] = index;   // 会话内一直记着：关闭写文件时它就是唯一来源
            if (!Main.WriteGroupConfig)
                return;
            string key = MemberKeyOf(set);
            // 该类型没注册这个键（例如拿事件那一套去写装饰）就只留在会话里：
            // 未注册的键进了 data，原版多选面板（InspectorPanel.ShowPanel 取 propertiesInfo[key]）会直接抛异常
            if (!IsKeyRegistered(e, key))
                return;
            // **必须写字符串**：这两个键在 AddInvisibleProperty 里声明成 String，而 r265 的
            // `LevelEvent.Encode(bool)` 会按声明类型 `castclass System.String`，装箱的 int 直接抛
            // InvalidCastException，被 scnEditor.SaveLevel 自己的 catch 吞成"保存失败！！！"（§42）。
            // 读回侧 ParseInt 本来就吃字符串，所以只有写侧要改。
            try { e[key] = index.ToString(); }
            catch { }
        }

        internal static void ClearManualGroup(LevelEvent e) => ClearManualGroup(e, GroupSet.Decoration);

        internal static void ClearManualGroup(LevelEvent e, GroupSet set)
        {
            if (e == null)
                return;
            sessionMembers.Remove(e);
            if (!Main.WriteGroupConfig)
                return;
            try
            {
                Dictionary<string, object> data = e.data;
                if (data == null)
                    return;
                string key = MemberKeyOf(set);
                // 注册过的键写回默认值 ""（不删键）：原版多选的 fake event 只比较"各事件 data 里有的键"，
                // 有的事件有键、有的没键时，会把第一个事件的归属原样抄给没键的那些（ApplyPropertiesToRealEvents）。
                // 空串在保存时会被剥掉（见存档前的清理步骤），不会进 .adofai。
                if (IsKeyRegistered(e, key))
                    data[key] = "";
                else
                    data.Remove(key);
                if (set == GroupSet.Decoration)
                    data.Remove(MemberKeyLegacy);
            }
            catch { }
        }

        /// <summary>会话表清空（编辑器重新 Awake 时调用；此时旧关卡的 LevelEvent 全部作废）。</summary>
        internal static void ClearSession()
        {
            sessionMembers.Clear();
        }

        /// <summary>
        /// 新关卡读档完成（scnGame.LoadLevel 成功之后）：旧对象全部作废 ⇒ 清空会话表；
        /// 写入开关关着时，把文件里带来的归属搬进会话表再从 data 剥掉（既不丢用户的归属，
        /// 之后保存出来的文件里也不会有我们的键）。
        /// </summary>
        internal static void OnLevelLoaded(LevelData levelData)
        {
            sessionMembers.Clear();
            if (Main.WriteGroupConfig || levelData == null)
                return;
            MoveWrittenKeysToSession(levelData.decorations, levelData.levelEvents);
        }

        /// <summary>写入开关关着时：先把 data 里的归属读进会话表，再剥离 data 里的归属键（顺序不能反）。</summary>
        internal static void MoveWrittenKeysToSession()
        {
            scnEditor editor = scnEditor.instance;
            if (editor == null)
                return;
            MoveWrittenKeysToSession(editor.decorations, editor.events);
        }

        private static void MoveWrittenKeysToSession(List<LevelEvent> decorations, List<LevelEvent> events)
        {
            SeedSessionList(decorations, GroupSet.Decoration);
            SeedSessionList(events, GroupSet.Event);
            PurgeWrittenKeys(decorations, events);
        }

        /// <summary>这个事件的类型有没有注册该属性（写 data 之前必须确认，见上）。</summary>
        private static bool IsKeyRegistered(LevelEvent e, string key)
        {
            try
            {
                LevelEventInfo info = e != null ? e.info : null;
                return info != null && info.propertiesInfo != null && info.propertiesInfo.ContainsKey(key);
            }
            catch
            {
                return false;
            }
        }

        private static int ReadStoredMember(LevelEvent e, GroupSet set)
        {
            try
            {
                int value = ParseInt(e[MemberKeyOf(set)]);
                if (value >= 0)
                    return value;
                if (set == GroupSet.Decoration)
                    return ParseInt(e[MemberKeyLegacy]);   // 兼容旧存档
            }
            catch { }
            return -1;
        }

        private static int ParseInt(object raw)
        {
            if (raw == null)
                return -1;
            if (raw is int i)
                return i;
            if (raw is long l)
                return (int)l;
            if (raw is float f)
                return (int)f;
            if (raw is double d)
                return (int)d;
            return int.TryParse(Convert.ToString(raw), out int parsed) ? parsed : -1;
        }

        // ---------------------------------------------------------------- 写入开关（§17.3）

        /// <summary>
        /// 把分组归属键从关卡数据里剥离干净：关闭"写入关卡文件"时调用（一般走
        /// <see cref="MoveWrittenKeysToSession()"/>：先读进会话表再剥，否则归属就丢了）。装饰与事件两边都剥全部三个键
        /// （aeeGroupDeco / aeeGroupEvent / 旧版 aeeGroup）。存档时另有一道清理兜底（开关关时剥掉全部归属键）。
        /// </summary>
        internal static void PurgeWrittenKeys()
        {
            scnEditor editor = scnEditor.instance;
            if (editor == null)
                return;
            PurgeWrittenKeys(editor.decorations, editor.events);
        }

        private static void PurgeWrittenKeys(List<LevelEvent> decorations, List<LevelEvent> events)
        {
            int removed = 0;
            removed += PurgeList(decorations, MemberKeyDeco);
            removed += PurgeList(decorations, MemberKeyEvent);
            removed += PurgeList(decorations, MemberKeyLegacy);
            removed += PurgeList(events, MemberKeyDeco);
            removed += PurgeList(events, MemberKeyEvent);
            removed += PurgeList(events, MemberKeyLegacy);
            if (removed > 0 && Main.Logger != null)
                Main.Logger.Log(string.Format("分组归属未写入开关=关：已从关卡数据剥离 {0} 个归属键", removed));
        }

        private static int PurgeList(List<LevelEvent> list, string key)
        {
            if (list == null)
                return 0;
            int removed = 0;
            for (int i = 0; i < list.Count; i++)
            {
                LevelEvent e = list[i];
                if (e == null)
                    continue;
                try
                {
                    Dictionary<string, object> data = e.data;
                    // 键一直注册着 ⇒ 每个事件都带着默认值 ""：照删，但只把有内容的算进日志数量
                    if (data != null && data.TryGetValue(key, out object value) && data.Remove(key)
                        && !string.IsNullOrWhiteSpace(Convert.ToString(value)))
                        removed++;
                }
                catch { }
            }
            return removed;
        }

        /// <summary>把 data 里已有的归属读进会话表（剥离前调用，避免当前会话的显示突然变空）。</summary>
        private static void SeedSessionList(List<LevelEvent> list, GroupSet set)
        {
            if (list == null)
                return;
            for (int i = 0; i < list.Count; i++)
            {
                LevelEvent e = list[i];
                if (e == null)
                    continue;
                int value = ReadStoredMember(e, set);
                if (value >= 0)
                    sessionMembers[e] = value;
            }
        }

        /// <summary>开关变化时的行为（§17.3）：关→开把会话里的归属落进 data；开→关搬到会话并剥离 data。</summary>
        internal static void OnWriteModeChanged()
        {
            if (Main.WriteGroupConfig)
            {
                // 从关变开：属性本来就一直注册着（这里只是保险），之后读写走 data（§18.2）。
                EnsureInvisibleProperties();
                // 关着时 data 里的键被剥过：先给当前关卡所有事件补回默认值 ""，保证"有键"这件事处处一致
                // （原因见 ClearManualGroup 里关于原版多选 fake event 的说明）
                scnEditor editor = scnEditor.instance;
                if (editor != null)
                {
                    RestoreDefaultKeys(editor.decorations, GroupSet.Decoration);
                    RestoreDefaultKeys(editor.events, GroupSet.Event);
                }
                var snapshot = new List<KeyValuePair<LevelEvent, int>>(sessionMembers);
                for (int i = 0; i < snapshot.Count; i++)
                {
                    LevelEvent e = snapshot[i].Key;
                    if (e == null)
                        continue;
                    GroupSet set = IsDecorationEvent(e) ? GroupSet.Decoration : GroupSet.Event;
                    SetManualGroup(e, set, snapshot[i].Value);
                }
            }
            else
            {
                MoveWrittenKeysToSession();
            }
        }

        private static void RestoreDefaultKeys(List<LevelEvent> list, GroupSet set)
        {
            if (list == null)
                return;
            string key = MemberKeyOf(set);
            for (int i = 0; i < list.Count; i++)
            {
                LevelEvent e = list[i];
                if (e == null || e.data == null || e.data.ContainsKey(key) || !IsKeyRegistered(e, key))
                    continue;
                try { e.data[key] = ""; }
                catch { }
            }
        }

        /// <summary>这个对象是"装饰"还是"砖上的事件"（决定用哪个键与会话语义）。按事件类型判断，与它是否还在关卡里无关。</summary>
        private static bool IsDecorationEvent(LevelEvent e)
        {
            try
            {
                return e != null && IsDecorationInfo(e.info);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 装饰类型判定：原版 `LevelEvent.IsDecoration` 的 getter 就是 `info.isDecoration`（IL 已核）；
        /// 再补上四种装饰事件类型做保险（万一某个类型的 info 没标这个字段）。
        /// </summary>
        private static bool IsDecorationInfo(LevelEventInfo info)
        {
            if (info == null)
                return false;
            if (info.isDecoration)
                return true;
            switch (info.type)
            {
                case LevelEventType.AddDecoration:
                case LevelEventType.AddText:
                case LevelEventType.AddObject:
                case LevelEventType.AddParticle:
                    return true;
                default:
                    return false;
            }
        }

        // ---------------------------------------------------------------- 归属键的属性注册（§18.2）

        /// <summary>这个属性名是不是我们的"分组归属"键（用于面板跳过渲染、剥离等）。</summary>
        internal static bool IsMembershipKey(string name)
        {
            return name == MemberKeyDeco || name == MemberKeyEvent || name == MemberKeyLegacy;
        }

        /// <summary>
        /// 把归属键注册成事件类型的 invisible 属性：**装饰类型只注册 aeeGroupDeco、砖上事件类型只注册
        /// aeeGroupEvent**。与"写入关卡文件"开关**无关、始终注册**。
        ///
        /// 为什么必须注册：原版 `LevelEvent.Encode` 只写 `propertiesInfo` 里注册过的键、
        /// `Decode` 也只保留注册过的键（IL 已核，§18.2）。以前只在开关开着时注册 ⇒ 重启游戏后
        /// 如果注册晚于读档（或开关当时是关的），文件里的归属在读档时就被静默丢掉。始终注册之后：
        /// 读档一定保留文件里的归属；开关关着时由读档后的 <see cref="OnLevelLoaded"/> 把它们搬进会话表，
        /// 存档前的清理步骤再把归属键（以及所有空值）剥掉，所以开关关着时文件里不会留下我们的键。
        /// 面板侧我们自己的 `RenderControl` 补丁会跳过这些键（不会多出行）。
        ///
        /// 默认值是 ""：原版构造/读档会给每个事件补上默认值，空串在存档前会被剥离。
        /// 幂等：每次都按 ContainsKey 判断（GCS.levelEventsInfo 被重建也能重新补上），可以频繁调用。
        ///
        /// 影响面：注册只发生在**装了本模组**的进程里；没有 mod 的环境里这两个键就是普通未知键，
        /// 原版读档会忽略它们（这正是 §17.3 已验证过的兼容路径）。
        /// </summary>
        internal static void EnsureInvisibleProperties()
        {
            if (GCS.levelEventsInfo == null)
                return;
            try
            {
                foreach (KeyValuePair<string, LevelEventInfo> pair in GCS.levelEventsInfo)
                {
                    LevelEventInfo info = pair.Value;
                    if (info == null || info.propertiesInfo == null)
                        continue;
                    AddInvisibleProperty(info, IsDecorationInfo(info) ? MemberKeyDeco : MemberKeyEvent);
                }
            }
            catch (Exception e)
            {
                Main.Logger?.Log("注册分组归属属性失败: " + e.Message);
            }
        }

        private static void AddInvisibleProperty(LevelEventInfo info, string key)
        {
            if (info.propertiesInfo.ContainsKey(key))
                return;
            // 用我们自己的字段声明构造（与模组注入设置页字段同一套机制），再标成 invisible
            var property = new PropertyCollection.Property_InputField(
                name: key,
                type: PropertyCollection.Property_InputField.InputType.String,
                value_default: "",
                key: "aee.group.members");
            ADOFAI.PropertyInfo propertyInfo = new ADOFAI.PropertyInfo(property.ToData(), info);
            propertyInfo.invisible = true;
            propertyInfo.order = 0;
            info.propertiesInfo[key] = propertyInfo;
        }

        /// <summary>分组键（面板上正在编辑的那一套，供 PrefabProperties 用）。</summary>
        internal static GroupSet EditingSet => Main.EventGroupEditing ? GroupSet.Event : GroupSet.Decoration;

        internal static int CountOf(GroupSet set) => set == GroupSet.Event ? eventCustomGroupCount : customGroupCount;

        /// <summary>设置某一套自定义分组的行数（internal：供设置持久化在读档时恢复行数）。</summary>
        internal static void SetCount(GroupSet set, int value)
        {
            if (set == GroupSet.Event)
                eventCustomGroupCount = value;
            else
                customGroupCount = value;
        }

        /// <summary>
        /// 装饰/事件类型 → "按类型分组"的组键。抽出来共用是因为**拖拽落点判定**也要它：
        /// `type:*` 只有在"目标组就是被拖装饰自己的类型组"时才有意义（= 清除手动归属，见 §22.2）。
        /// 规则与渲染层那五个固定分组一致。
        /// </summary>
        internal static string TypeKeyOf(LevelEventType type)
        {
            switch (type)
            {
                case LevelEventType.AddDecoration:
                    return "type:19";
                case LevelEventType.AddText:
                    return "type:20";
                case LevelEventType.AddObject:
                    return "type:58";
                case LevelEventType.AddParticle:
                    return "type:62";
                default:
                    return "type:other";
            }
        }

        /// <summary>
        /// 兜底组下标 = **第一个** tag 留空的自定义分组（名字与 tag 都空的行 = 用户还没填，跳过）。
        /// -1 表示没有这样的分组。语义见设计文档 §14.1。
        /// </summary>
        internal static int FirstFallbackGroupIndex(List<(string Name, string Tag)> groups)
        {
            if (groups == null)
                return -1;
            for (int i = 0; i < groups.Count; i++)
            {
                if (groups[i].Tag.Length != 0)
                    continue;
                if (string.IsNullOrEmpty(groups[i].Name))
                    continue;
                return i;
            }
            return -1;
        }

        /// <summary>
        /// 删除第 index 行自定义分组后修正所有手动归属：属于该组的装饰回到兜底组（清标记），
        /// 下标大于它的往前挪一位。
        /// </summary>
        internal static void ShiftManualGroups(int removedIndex)
        {
            ShiftManualGroups(GroupSet.Decoration, removedIndex);
        }

        internal static void ShiftManualGroups(GroupSet set, int removedIndex)
        {
            scnEditor editor = scnEditor.instance;
            if (editor == null)
                return;
            List<LevelEvent> list = set == GroupSet.Event ? (List<LevelEvent>)editor.events : editor.decorations;
            if (list == null)
                return;
            for (int i = 0; i < list.Count; i++)
            {
                LevelEvent e = list[i];
                int current = GetManualGroup(e, set);
                if (current < 0)
                    continue;
                if (current == removedIndex)
                    ClearManualGroup(e, set);
                else if (current > removedIndex)
                    SetManualGroup(e, set, current - 1);
            }
        }

        internal static void ResetRenderState()
        {
            Slots.Clear();
            GroupKeyOfEvent.Clear();
            EventsByGroup.Clear();
            VisibleEvents.Clear();
            SlotIndexByRowIndex = new int[0];
            Panel = null;
        }

        internal static string NameKey(int index) => NameKey(GroupSet.Decoration, index);
        internal static string TagKey(int index) => TagKey(GroupSet.Decoration, index);
        internal static string DeleteKey(int index) => DeleteKey(GroupSet.Decoration, index);

        /// <summary>第 index 行（0 基）在指定那一套里的字段名；两套用不同前缀，互不干扰（§17.2）。</summary>
        internal static string NameKey(GroupSet set, int index) => (set == GroupSet.Event ? "eventGroupName" : "groupName") + index;
        internal static string TagKey(GroupSet set, int index) => (set == GroupSet.Event ? "eventGroupTag" : "groupTag") + index;
        internal static string DeleteKey(GroupSet set, int index) => (set == GroupSet.Event ? "eventDeleteGroup" : "deleteGroup") + index;

        /// <summary>当前生效的自定义分组行数（0..MaxCustomGroups）—— 装饰那一套（兼容旧调用）。</summary>
        internal static int CustomGroupCount => customGroupCount;

        /// <summary>
        /// 行数只由“添加/删除分组”按钮维护：设置值本身不写进关卡文件（见设计文档 §6），
        /// 所以从 LevelEvent 数据里反推行数只会在数据异常时误报（实测出现过 20 行全显示），
        /// 这里不再按数据推算。
        /// </summary>
        internal static void ClampCustomGroupCount()
        {
            ClampCustomGroupCount(GroupSet.Decoration);
            ClampCustomGroupCount(GroupSet.Event);
        }

        internal static void ClampCustomGroupCount(GroupSet set)
        {
            int count = CountOf(set);
            if (count < 0)
                count = 0;
            else if (count > MaxCustomGroups)
                count = MaxCustomGroups;
            SetCount(set, count);
        }

        internal static string ReadString(LevelEvent settings, string key)
        {
            if (settings == null)
                return "";
            try
            {
                if (settings.TryGet<string>(key, out string value))
                    return value ?? "";
            }
            catch { }
            return "";
        }

        /// <summary>读取生效的自定义分组（名称 + 匹配 tag，tag 已去空白）—— 装饰那一套（兼容旧调用）。</summary>
        internal static List<(string Name, string Tag)> ReadCustomGroups()
        {
            return ReadCustomGroups(GroupSet.Decoration);
        }

        internal static List<(string Name, string Tag)> ReadCustomGroups(GroupSet set)
        {
            var groups = new List<(string, string)>();
            LevelEvent settings = Main.GetSettingsEvent();
            int count = CountOf(set);
            for (int i = 0; i < count; i++)
            {
                string name = ReadString(settings, NameKey(set, i));
                string tag = ReadString(settings, TagKey(set, i));
                groups.Add((name, tag == null ? "" : tag.Trim()));
            }
            return groups;
        }

        /// <summary>“添加自定义分组”按钮：多显示一行空行（上限 20）。装饰那一套（兼容旧调用）。</summary>
        internal static void AddCustomGroup()
        {
            AddCustomGroup(GroupSet.Decoration);
        }

        internal static void AddCustomGroup(GroupSet set)
        {
            ClampCustomGroupCount(set);
            if (CountOf(set) >= MaxCustomGroups)
            {
                Popup.ShowMessage(L("aee.group.tooMany"));
                return;
            }
            SetCount(set, CountOf(set) + 1);
            ADOFAIEditorExtension.Settings.SettingsStore.Save();   // 行数跨重启持久化
            RebuildSettingsPanel();
        }

        /// <summary>“删除分组 i”按钮：删掉该行并把后面的行上移，保持行连续。装饰那一套（兼容旧调用）。</summary>
        internal static void DeleteCustomGroup(int index)
        {
            DeleteCustomGroup(GroupSet.Decoration, index);
        }

        internal static void DeleteCustomGroup(GroupSet set, int index)
        {
            ClampCustomGroupCount(set);
            LevelEvent settings = Main.GetSettingsEvent();
            int count = CountOf(set);
            if (settings == null || index < 0 || index >= count)
                return;

            for (int i = index; i < count - 1; i++)
            {
                settings[NameKey(set, i)] = ReadString(settings, NameKey(set, i + 1));
                settings[TagKey(set, i)] = ReadString(settings, TagKey(set, i + 1));
            }
            settings[NameKey(set, count - 1)] = "";
            settings[TagKey(set, count - 1)] = "";
            SetCount(set, count - 1);
            ADOFAIEditorExtension.Settings.SettingsStore.Save();   // 行数跨重启持久化

            // 手动归属是按分组下标记的：删掉一行后要跟着修正，否则对象会跳到别的组去。
            // 写入开关开着时这一步改的是关卡 data ⇒ 压一个撤销点并置脏（与其它数据编辑同一写法）；
            // 关着时只改会话表，不碰关卡数据，也就不需要撤销点。
            scnEditor editor = scnEditor.instance;
            if (Main.WriteGroupConfig && editor != null)
            {
                using (new SaveStateScope(editor, false, true, false))
                    ShiftManualGroups(set, index);
            }
            else
            {
                ShiftManualGroups(set, index);
            }

            RebuildSettingsPanel();
        }

        private static void RebuildSettingsPanel()
        {
            Main.GetSettingsEvent()?.UpdatePanel();
            Main.activeChilden();
            DecoGroupRenderer.RefreshList();
        }

        /// <summary>
        /// 组内**最后一个成员之后**的那一行对应的装饰（= 下一个分组的第一行）。
        /// "插到组尾"优先用 <see cref="LastEventOfGroup"/> 当锚点（组成员在装饰数组里不一定连续，
        /// 锚在下一组第一行前面可能落进本组中间）；只有组是空的才退回用它；
        /// 返回 null 表示该组之后没有别的行了（⇒ 追加到装饰数组末尾）。
        /// </summary>
        internal static LevelEvent NextEventAfterGroup(string key)
        {
            return NextEventAfterGroup(key, null);
        }

        /// <summary>
        /// 同上，但跳过 <paramref name="exclude"/> 里的装饰（多选整批拖动时它们正被移走，不能拿来当锚点，见 §44）。
        /// </summary>
        internal static LevelEvent NextEventAfterGroup(string key, ICollection<LevelEvent> exclude)
        {
            if (string.IsNullOrEmpty(key))
                return null;
            List<DecoGroupSlot> slots = Slots;
            int start = -1;
            for (int i = 0; i < slots.Count; i++)
            {
                if (slots[i].IsHeader && slots[i].Key == key)
                {
                    start = i;
                    break;
                }
            }
            if (start < 0)
                return null;

            for (int i = start + 1; i < slots.Count; i++)
            {
                DecoGroupSlot slot = slots[i];
                if (slot.IsHeader || slot.Event == null)
                    continue;   // 别的组的组头：继续往下找它（或再下一组）的第一行
                if (GroupKeyOfEvent.TryGetValue(slot.Event, out string owner) && owner == key)
                    continue;   // 还是本组的成员
                if (exclude != null && exclude.Contains(slot.Event))
                    continue;   // 正被移走的装饰
                return slot.Event;
            }
            return null;
        }

        /// <summary>组内最后一个成员（显示顺序 = 装饰数组顺序）；组空或组头不在槽位表里时返回 null。</summary>
        internal static LevelEvent LastEventOfGroup(string key)
        {
            return LastEventOfGroup(key, null);
        }

        /// <summary>同上，但跳过 <paramref name="exclude"/> 里的装饰；组里只剩被移动的装饰时返回 null（见 §44）。</summary>
        internal static LevelEvent LastEventOfGroup(string key, ICollection<LevelEvent> exclude)
        {
            if (string.IsNullOrEmpty(key))
                return null;
            if (!EventsByGroup.TryGetValue(key, out List<LevelEvent> events) || events == null)
                return null;
            for (int i = events.Count - 1; i >= 0; i--)
            {
                LevelEvent e = events[i];
                if (e != null && (exclude == null || !exclude.Contains(e)))
                    return e;
            }
            return null;
        }

        private static string L(string key) => Main.Localizations?.GetValue(key) ?? key;
    }
}
