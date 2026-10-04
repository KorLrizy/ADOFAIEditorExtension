using ADOFAI;
using ADOFAIEditorExtension.Features.DecoGrouping;
using ADOFAIEditorExtension.Features.Metadata;
using ADOFAIEditorExtension.Features.Notes;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace ADOFAIEditorExtension.Patches
{
    /// <summary>侧车（&lt;关卡路径&gt;.aee.json）与游戏的对接层：读写 LevelEvent.data、接入 SaveLevel / WriteAllText / LoadLevel。纯逻辑都在 LevelMetadataStore。</summary>
    internal static class LevelMetadataBridge
    {
        /// <summary>算指纹时剔除的键：备注 + 两套手动归属，只剔事件对象顶层（开关开不开都不影响指纹）。</summary>
        private static readonly string[] MetadataKeys =
        {
            EventNote.KeyNote, DecoGroupState.MemberKeyDeco, DecoGroupState.MemberKeyEvent
        };

        /// <summary>当前最内层的保存上下文（嵌套时外层挂在 Parent 上），只由 Prefix/Finalizer 配对推进。</summary>
        private static SaveContext _current;

        /// <summary>本进程读过并成功恢复过、或自己写过的侧车：只有它们允许被后续保存直接覆盖，其余先归档再写。</summary>
        private static readonly HashSet<string> _verifiedSidecars = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>本进程没能完整恢复的侧车：后续同路径保存先把旧文件另存归档，再按当前内容写新备份（旧数据不丢，新数据也不再被静默丢掉）。</summary>
        private static readonly HashSet<string> _protectedSidecars = new HashSet<string>(StringComparer.Ordinal);

        private static string _lastLog;

        /// <summary>同一句话只记一次：读档失败之类会随每次加载重复出现。</summary>
        internal static void Log(string message)
        {
            try
            {
                if (string.Equals(message, _lastLog, StringComparison.Ordinal))
                    return;
                _lastLog = message;
                Main.Logger?.Log(message);
            }
            catch { }
        }

        // ---------------------------------------------------------------- 保存

        /// <summary>SaveLevel 前置：返回这次调用自己的上下文（不准入或出错就是 null）；不准入时一律不碰 _current，否则外层上下文会被内层这次调用顶掉。</summary>
        internal static SaveContext BeginSave()
        {
            SaveContext parent = _current;
            try
            {
                string levelPath = ADOBase.levelPath;
                LevelData data = scnEditor.instance != null ? scnEditor.instance.levelData : null;
                if (data == null || !LevelMetadataStore.IsLevelPath(levelPath))
                    return null;
                string sidecarPath = LevelMetadataStore.GetSidecarPath(levelPath);
                var context = new SaveContext
                {
                    Parent = parent,
                    LevelPath = levelPath,
                    SidecarPath = sidecarPath,
                    ExistingSidecar = SafeExists(sidecarPath),
                    ActionEvents = OrderedEvents(data.levelEvents, true),
                    DecorationEvents = OrderedEvents(data.decorations, false)
                };
                context.DecorationGroups.AddRange(CurrentGroupDefinitions(DecoGroupState.GroupSet.Decoration));
                context.EventGroups.AddRange(CurrentGroupDefinitions(DecoGroupState.GroupSet.Event));
                context.ActionRecords.AddRange(CollectRecords(context.ActionEvents, DecoGroupState.GroupSet.Event));
                context.DecorationRecords.AddRange(CollectRecords(context.DecorationEvents, DecoGroupState.GroupSet.Decoration));
                _current = context;
                return context;
            }
            catch (Exception e)
            {
                _current = parent;                 // 半截的上下文不上链
                Log("建立元数据备份上下文失败: " + e.Message);
                return null;
            }
        }

        /// <summary>RDFile.WriteAllText 后置：只认领最内层上下文、且路径完全一致的那一次写盘；算不出指纹 = 写出的不是我们认识的结构 ⇒ 放弃这次备份，不猜。</summary>
        internal static void MarkLevelWritten(string path, string text)
        {
            SaveContext context = _current;
            if (context == null || context.Written)
                return;
            if (!string.Equals(path, context.LevelPath, StringComparison.Ordinal))
                return;
            try
            {
                SequenceFingerprints actions, decorations;
                if (!LevelMetadataStore.TryComputeLevelFingerprints(text, MetadataKeys, out actions, out decorations))
                {
                    Log("关卡内容解析不出指纹，本次不更新元数据备份");
                    return;
                }
                context.ActionFingerprints = actions;
                context.DecorationFingerprints = decorations;
                context.Written = true;
            }
            catch (Exception e)
            {
                Log("计算关卡指纹失败: " + e.Message);
            }
        }

        /// <summary>SaveLevel 收尾：只有拿着自己那次调用的上下文才解链/写侧车（null = 前置没建，什么都不做）。</summary>
        internal static void CompleteSave(Exception error, SaveContext context)
        {
            if (context == null)
                return;
            if (ReferenceEquals(_current, context))
                _current = context.Parent;         // 按 identity 解链，不盲弹栈
            try
            {
                string blocked = LevelMetadataStore.SidecarWriteGuard(context.Written, error != null,
                    context.ActionFingerprints != null ? context.ActionFingerprints.Exact : null,
                    context.DecorationFingerprints != null ? context.DecorationFingerprints.Exact : null,
                    context.ActionEvents.Count, context.DecorationEvents.Count);
                if (blocked != null)
                {
                    // 关卡没落成 ⇒ 磁盘上什么都不动（这道闸仍然只拦不写），但要说出来。
                    Log(blocked + "，本次不更新元数据备份（未恢复备份保留，未覆盖）");
                    return;
                }

                string archiveReason;
                SidecarWriteMode mode = LevelMetadataStore.DecideSidecarWrite(context.ExistingSidecar,
                    _verifiedSidecars.Contains(context.SidecarPath), _protectedSidecars.Contains(context.SidecarPath),
                    out archiveReason);

                var backup = new LevelMetadataBackup
                {
                    LevelName = Path.GetFileName(context.LevelPath),
                    SavedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
                };
                backup.Actions.Fingerprints.AddRange(context.ActionFingerprints.Exact);
                backup.Actions.LooseFingerprints.AddRange(context.ActionFingerprints.Loose);
                backup.Actions.Kinds.AddRange(context.ActionFingerprints.Kinds);
                backup.Decorations.Fingerprints.AddRange(context.DecorationFingerprints.Exact);
                backup.Decorations.LooseFingerprints.AddRange(context.DecorationFingerprints.Loose);
                backup.Decorations.Kinds.AddRange(context.DecorationFingerprints.Kinds);
                backup.Actions.Records.AddRange(context.ActionRecords);
                backup.Decorations.Records.AddRange(context.DecorationRecords);
                backup.DecorationGroups.AddRange(context.DecorationGroups);
                backup.EventGroups.AddRange(context.EventGroups);

                var outcome = new SidecarWriteOutcome();
                if (!LevelMetadataStore.TryWriteBackup(context.LevelPath, context.SidecarPath, backup,
                    context.ExistingSidecar, mode, outcome))
                {
                    // 没写成 ⇒ 旧侧车一个字节没动（归档失败时连副本都没有）；走归档这条路的继续按"未恢复"保护
                    if (outcome.Mode == SidecarWriteMode.ArchiveThenWrite)
                        ProtectSidecar(context.SidecarPath);
                    Log("写元数据备份失败 " + Path.GetFileName(context.SidecarPath) + ": " + outcome.Error);
                    return;
                }
                if (outcome.ArchivePath != null)
                {
                    string why = outcome.ArchiveReason ?? archiveReason ?? "这份侧车本进程没能完整恢复";
                    Log(why + "：已把未能完整恢复的旧元数据备份另存为 "
                        + Path.GetFileName(outcome.ArchivePath) + "，并按当前内容写入新备份");
                }
                // 写成了（含空记录那份）⇒ 这个路径归本进程所有，下次保存走正常覆盖，归档一次保护只做一次
                VerifySidecar(context.SidecarPath);
            }
            catch (Exception e)
            {
                Log("写元数据备份异常: " + e.GetType().Name + ": " + e.Message);
            }
        }

        // ---------------------------------------------------------------- 读取

        /// <summary>LoadLevel 后置（原版返回 true 之后才恢复）：没能安全恢复的侧车记进"保护"名单，后续同路径保存会先把它另存归档再写新备份，换到新路径则照常写；中途抛异常同样按"没恢复"保护这条路径。</summary>
        internal static void AfterLevelLoaded(LevelData data, bool loaded, string path)
        {
            try
            {
                if (!loaded || data == null || !LevelMetadataStore.IsLevelPath(path))
                    return;
                string sidecarPath = LevelMetadataStore.GetSidecarPath(path);
                if (!SafeExists(sidecarPath))
                {
                    ForgetSidecar(sidecarPath);                     // 磁盘上没有侧车了 ⇒ 别再拦后续写盘
                    return;
                }
                scnEditor editor = scnEditor.instance;
                if (editor == null || !ReferenceEquals(data, editor.levelData))
                    return;                                         // 官方关卡 / 试玩 / 别的场景：不动也不认领

                LevelMetadataNotice.Cancel();                       // 上一个关卡还没弹出的提示到这里就过时了

                EnsureMetadataKeysRegistered();

                LevelMetadataBackup backup;
                string parseError;
                if (!LevelMetadataStore.TryReadBackupFile(sidecarPath, out backup, out parseError))
                {
                    ProtectSidecar(sidecarPath);
                    Log("忽略元数据备份 " + Path.GetFileName(sidecarPath) + ": " + parseError + "（未恢复备份保留，未覆盖）");
                    QueueUnreadableNotice(sidecarPath);
                    return;
                }

                string levelText = LevelMetadataStore.ReadLevelText(path);
                if (levelText == null)
                {
                    ProtectSidecar(sidecarPath);
                    Log("读取关卡文件失败，跳过元数据备份恢复: " + Path.GetFileName(path) + "（未恢复备份保留，未覆盖）");
                    QueueUnreadableNotice(sidecarPath);
                    return;
                }
                // 关卡文本只解析一次，两套序列各出"精确 + 宽松"两份指纹；宽松那份用来在加/删砖之后仍能把记录对回原事件
                SequenceFingerprints actionHashes, decorationHashes;
                if (!LevelMetadataStore.TryComputeLevelFingerprints(levelText, MetadataKeys,
                    out actionHashes, out decorationHashes))
                {
                    ProtectSidecar(sidecarPath);
                    Log("关卡文件与备份无法按同一规则解析，跳过元数据恢复（未恢复备份保留，未覆盖）");
                    QueueUnreadableNotice(sidecarPath);
                    return;
                }

                SequenceRestoreResult actions = RestoreSequence(data, backup.Actions, actionHashes,
                    DecoGroupState.GroupSet.Event, backup.EventGroups);
                SequenceRestoreResult decorations = RestoreSequence(data, backup.Decorations, decorationHashes,
                    DecoGroupState.GroupSet.Decoration, backup.DecorationGroups);
                if (actions.Clean && decorations.Clean)
                {
                    VerifySidecar(sidecarPath);
                    return;
                }
                ProtectSidecar(sidecarPath);
                Log("元数据备份没有完全恢复（" + Path.GetFileName(sidecarPath) + "），未恢复备份保留，未覆盖");
                QueuePartialNotice(actions.Unrestored + decorations.Unrestored,
                    actions.Restored + decorations.Restored);
            }
            catch (Exception e)
            {
                Log("恢复元数据备份异常: " + e.GetType().Name + ": " + e.Message);
                // 这次读档到底恢复成什么样已经说不清了 ⇒ 按"没恢复"处理，别沿用这条路径上一次的 verified 让后续保存覆盖掉侧车
                if (LevelMetadataStore.IsLevelPath(path))
                {
                    string sidecarPath = LevelMetadataStore.GetSidecarPath(path);
                    ProtectSidecar(sidecarPath);
                    QueueUnreadableNotice(sidecarPath);
                }
            }
        }

        /// <summary>一套序列的恢复：先按指纹把侧车每一项对到当前事件（对不上的只算它自己没恢复），再逐条落回当前槽位。
        /// Clean = 这套没有记录，或对齐跑通、没有任何遗漏、且计划里的每一次写入都核实过（键已注册、写后读回一致）；
        /// 归属按保存时与当前的分组身份证（名称+标签）逐条映射，对不上的只跳过自己。
        /// Restored/Unrestored 是核实过的条数，调用方据此给用户一句话。</summary>
        private static SequenceRestoreResult RestoreSequence(LevelData data, SequenceBackup stored,
            SequenceFingerprints fileHashes, DecoGroupState.GroupSet set, List<GroupDefinition> storedGroups)
        {
            bool actions = set == DecoGroupState.GroupSet.Event;
            string label = actions ? "事件" : "装饰";
            try
            {
                if (stored == null || stored.Records.Count == 0)
                    return new SequenceRestoreResult { Clean = true };
                IEnumerable<LevelEvent> source = actions
                    ? (IEnumerable<LevelEvent>)data.levelEvents
                    : (IEnumerable<LevelEvent>)data.decorations;
                List<LevelEvent> events = OrderedEvents(source, actions);

                var slots = new List<SlotSnapshot>(events.Count);
                for (int i = 0; i < events.Count; i++)
                {
                    slots.Add(new SlotSnapshot
                    {
                        Note = EventNote.GetNote(events[i]),
                        GroupIndex = DecoGroupState.GetManualGroup(events[i], set)
                    });
                }

                List<string> runtimeKinds = new List<string>(events.Count);
                for (int i = 0; i < events.Count; i++)
                    runtimeKinds.Add(events[i] != null
                        ? LevelMetadataStore.EventKind(events[i].floor, events[i].eventType.ToString())
                        : null);
                List<GroupDefinition> currentGroups = CurrentGroupDefinitions(set);
                RestorePlan plan = LevelMetadataStore.BuildRestorePlan(stored, fileHashes, runtimeKinds, slots,
                    LevelMetadataStore.BuildGroupIndexMap(storedGroups, currentGroups), actions);
                if (!plan.Aligned)
                {
                    Log(string.Format(CultureInfo.InvariantCulture,
                        "元数据备份与当前关卡对不上（{0}），跳过恢复{1}", plan.Reason, label));
                    return PartialResult(stored.Records.Count, 0);
                }
                int written = 0;
                for (int i = 0; i < plan.Actions.Count; i++)
                {
                    RestoreAction action = plan.Actions[i];
                    if (action.Index < 0 || action.Index >= events.Count)
                    {
                        Log(string.Format(CultureInfo.InvariantCulture,
                            "第 {0} 项（侧车第 {1} 项）已经不在当前{2}里，元数据没有完全恢复（未恢复备份保留，未覆盖）",
                            action.Index, action.StoredIndex, label));
                        return PartialResult(stored.Records.Count, written);
                    }
                    LevelEvent e = events[action.Index];
                    string item = string.Format(CultureInfo.InvariantCulture, "{0}第 {1} 项", label, action.Index);
                    if (e == null)
                    {
                        Log(item + "读不到事件对象，元数据没有完全恢复（未恢复备份保留，未覆盖）");
                        return PartialResult(stored.Records.Count, written);
                    }
                    if (action.WriteNote)
                    {
                        // 未注册的键写进去也会被原版 Encode/Decode 丢掉 ⇒ 先核实注册，再核实写后的值，否则这次"恢复成功"会让后续保存覆盖掉原侧车
                        if (!IsKeyRegistered(e, EventNote.KeyNote))
                        {
                            Log(item + "的备注键没有注册，元数据没有完全恢复（未恢复备份保留，未覆盖）");
                            return PartialResult(stored.Records.Count, written);
                        }
                        e[EventNote.KeyNote] = action.Note;
                        if (!string.Equals(EventNote.GetNote(e), action.Note, StringComparison.Ordinal))
                        {
                            Log(item + "的备注写进去读不回来，元数据没有完全恢复（未恢复备份保留，未覆盖）");
                            return PartialResult(stored.Records.Count, written);
                        }
                        written++;
                    }
                    if (action.WriteGroup)
                    {
                        // SetManualGroup 对未注册键 / 写失败是吞异常的，只能靠写后再读一次确认下标真的落上了
                        DecoGroupState.SetManualGroup(e, set, action.GroupIndex);
                        if (DecoGroupState.GetManualGroup(e, set) != action.GroupIndex)
                        {
                            Log(item + "的归属没有写成，元数据没有完全恢复（未恢复备份保留，未覆盖）");
                            return PartialResult(stored.Records.Count, written);
                        }
                        written++;
                    }
                }
                int unrestored = plan.RecordsUnmatched + plan.GroupsUnrestorable;
                // 放在逐条写完之后：这里的"已恢复"是核实过落上的条数，不是计划条数
                if (plan.Incomplete)
                {
                    Log(string.Format(CultureInfo.InvariantCulture,
                        "{0}元数据部分恢复：精确对上 {1} 项 / 平移对上 {2} 项 / 砖位+类型对上 {3} 项（侧车 {4} 项、当前 {5} 项），已恢复 {6} 条、未恢复 {7} 条；{8}",
                        label, plan.Alignment != null ? plan.Alignment.ExactMatches : 0,
                        plan.Alignment != null ? plan.Alignment.LooseMatches : 0,
                        plan.Alignment != null ? plan.Alignment.KindMatches : 0,
                        stored.Fingerprints.Count, events.Count, written, unrestored, plan.Reason));
                    if (plan.Alignment != null && plan.Alignment.BudgetExceeded)
                        Log(label + "指纹对齐超出工作量上限，之后的项目按没对上处理（未恢复备份保留，未覆盖）");
                }
                return new SequenceRestoreResult
                {
                    Clean = !plan.Incomplete,
                    Restored = written,
                    Unrestored = unrestored
                };
            }
            catch (Exception e)
            {
                Log("恢复" + label + "元数据失败: " + e.Message);
                return PartialResult(stored != null ? stored.Records.Count : 0, 0);
            }
        }

        /// <summary>这套序列没完全恢复时的计数：记录条数减去已经核实落上的条数；
        /// 走到这里就是至少有东西没落上（一条记录可能同时写备注和归属，逐字段计数会大于记录数），所以最少算 1 条。</summary>
        private static SequenceRestoreResult PartialResult(int recordCount, int written)
        {
            int unrestored = recordCount > written ? recordCount - written : (recordCount > 0 ? 1 : 0);
            return new SequenceRestoreResult
            {
                Restored = written,
                Unrestored = unrestored
            };
        }

        // ---------------------------------------------------------------- 游戏侧小工具

        /// <summary>与 LevelData.EncodeToDictionary 同一条规则（actions 按 floor 稳定排序、两套都只留 isActive），侧车下标才与文件位置一一对应。</summary>
        private static List<LevelEvent> OrderedEvents(IEnumerable<LevelEvent> source, bool byFloor)
        {
            var result = new List<LevelEvent>();
            if (source == null)
                return result;
            IEnumerable<LevelEvent> sequence = byFloor ? source.OrderBy(e => e.floor) : source;
            foreach (LevelEvent e in sequence)
            {
                LevelEventInfo info = e != null ? e.info : null;
                if (info != null && info.isActive)
                    result.Add(e);
            }
            return result;
        }

        /// <summary>归属取自 event.data，与"把分组归属写进关卡文件"开关无关（开关只决定原版保存输出写不写这两个键）。</summary>
        private static List<MetadataRecord> CollectRecords(List<LevelEvent> events, DecoGroupState.GroupSet set)
        {
            var records = new List<MetadataRecord>();
            for (int i = 0; i < events.Count; i++)
            {
                MetadataRecord record = LevelMetadataStore.CreateRecord(i, EventNote.GetNote(events[i]),
                    DecoGroupState.GetManualGroup(events[i], set));
                if (record != null)
                    records.Add(record);
            }
            return records;
        }

        /// <summary>当前两套自定义分组的身份（名称 + tag，颜色不参与）；读取方式与分组功能本身一致。</summary>
        private static List<GroupDefinition> CurrentGroupDefinitions(DecoGroupState.GroupSet set)
        {
            var definitions = new List<GroupDefinition>();
            foreach (var group in DecoGroupState.ReadCustomGroups(set))
                definitions.Add(new GroupDefinition { Name = group.Name, Tag = group.Tag });
            return definitions;
        }

        /// <summary>这个键在该事件的类型上注册过才能写进 data（未注册键会被原版 Encode/Decode 丢掉）。</summary>
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

        /// <summary>恢复前保证备注/归属键在各事件类型上注册过（原版 Encode/Decode 只认注册过的键），两个调用都幂等。</summary>
        private static void EnsureMetadataKeysRegistered()
        {
            try { DecoGroupState.EnsureInvisibleProperties(); }
            catch (Exception e) { Log("恢复前补注册分组归属键失败: " + e.Message); }
            try { EventNote.EnsureRegistered(); }
            catch (Exception e) { Log("恢复前补注册事件备注键失败: " + e.Message); }
        }

        private static void VerifySidecar(string sidecarPath)
        {
            _verifiedSidecars.Add(sidecarPath);
            _protectedSidecars.Remove(sidecarPath);
        }

        private static void ProtectSidecar(string sidecarPath)
        {
            _protectedSidecars.Add(sidecarPath);
            _verifiedSidecars.Remove(sidecarPath);
        }

        private static void ForgetSidecar(string sidecarPath)
        {
            _verifiedSidecars.Remove(sidecarPath);
            _protectedSidecars.Remove(sidecarPath);
        }

        private static bool SafeExists(string path)
        {
            try { return File.Exists(path); }
            catch { return false; }
        }

        /// <summary>备份压根用不了（读不出/解析不了/关卡文件读不出/算不出指纹/异常）：只在这条路径上确有侧车时提示一次。</summary>
        private static void QueueUnreadableNotice(string sidecarPath)
        {
            if (SafeExists(sidecarPath))
                LevelMetadataNotice.Queue(NoticeText("aee.metadata.unreadable",
                    "The metadata backup {0} could not be used, so no event notes or group assignments were restored "
                    + "(see the mod log for details).\nThe old backup will not be overwritten: it is kept as a "
                    + ".aee.unrestored-<time>.json file the next time you save.",
                    Path.GetFileName(sidecarPath)));
        }

        /// <summary>备份部分恢复：告诉用户多少条没落回去、多少条落回去了。</summary>
        private static void QueuePartialNotice(int unrestored, int restored)
        {
            if (unrestored <= 0)
                return;
            LevelMetadataNotice.Queue(NoticeText("aee.metadata.partial",
                "{0} event notes or group assignments could not be restored ({1} restored). The matching events were "
                + "probably changed or deleted without this mod, or the groups were renamed or removed."
                + "\nThe old backup will not be overwritten: it is kept as a .aee.unrestored-<time>.json file the next time you save.",
                unrestored, restored));
        }

        /// <summary>本地化取句 + 套参数；本地化表没有这一项（或读取失败）时退回英文。</summary>
        private static string NoticeText(string key, string english, params object[] args)
        {
            string text = Main.Localizations != null ? Main.Localizations.GetValue(key) : null;
            if (string.IsNullOrEmpty(text))
                text = english;
            try
            {
                return string.Format(CultureInfo.InvariantCulture, text, args);
            }
            catch (Exception e)
            {
                Log("格式化元数据提示失败: " + e.Message);
                return text;
            }
        }

        /// <summary>一次保存的上下文：只持有这次要用的快照，同时充当 Prefix/Finalizer 的配对令牌。</summary>
        internal sealed class SaveContext
        {
            internal string LevelPath;
            internal string SidecarPath;
            internal bool ExistingSidecar;
            internal SaveContext Parent;

            internal List<LevelEvent> ActionEvents = new List<LevelEvent>();
            internal List<LevelEvent> DecorationEvents = new List<LevelEvent>();
            internal readonly List<MetadataRecord> ActionRecords = new List<MetadataRecord>();
            internal readonly List<MetadataRecord> DecorationRecords = new List<MetadataRecord>();
            internal readonly List<GroupDefinition> DecorationGroups = new List<GroupDefinition>();
            internal readonly List<GroupDefinition> EventGroups = new List<GroupDefinition>();

            internal bool Written;
            internal SequenceFingerprints ActionFingerprints;
            internal SequenceFingerprints DecorationFingerprints;
        }

        /// <summary>一套序列的恢复结果：Clean 决定侧车是被认领（后续保存直接覆盖）还是被保住（后续保存先归档）；
        /// Restored/Unrestored 是核实过的条数，给用户的提示说的就是这两个数。</summary>
        internal sealed class SequenceRestoreResult
        {
            internal bool Clean;
            internal int Restored;
            internal int Unrestored;
        }
    }

    /// <summary>读档后给用户的可见提示：备份没能（完整）恢复这种事只在读档这一刻有机会说一次，
    /// 所以要排队到编辑器空闲、并且同一次读档只留最新的一条。</summary>
    internal static class LevelMetadataNotice
    {
        /// <summary>最近一次排队请求的编号：读一次档一个编号，旧编号的协程看到编号变了就自己退出。</summary>
        private static int _requestId;

        internal static void Cancel()
        {
            _requestId++;
        }

        internal static void Queue(string text)
        {
            if (string.IsNullOrEmpty(text))
                return;
            int requestId = ++_requestId;
            try
            {
                scnEditor editor = scnEditor.instance;
                if (editor == null)
                    return;
                editor.StartCoroutine(ShowWhenIdle(editor, requestId, text));
            }
            catch (Exception e)
            {
                LevelMetadataBridge.Log("排队元数据提示失败: " + e.Message);
            }
        }

        /// <summary>等两帧（让原版的读档流程、"已保存"之类的提示先走完），再等弹窗空闲，然后弹一句。</summary>
        private static System.Collections.IEnumerator ShowWhenIdle(scnEditor editor, int requestId, string text)
        {
            yield return null;
            yield return null;
            while (editor != null && requestId == _requestId
                && (Main.IsShowingPopup(editor) || Main.IsPopupAnimating(editor)))
                yield return null;
            if (editor == null || requestId != _requestId)
                yield break;      // 又读了一次档：这句已经过时，由那一次的排队来说
            try
            {
                if (ADOFAIEditorExtension.Utils.Popup.popup != null)
                    ADOFAIEditorExtension.Utils.Popup.ShowMessage(text);
                else
                    editor.ShowNotification(text, null, 8f);
            }
            catch (Exception e)
            {
                LevelMetadataBridge.Log("显示元数据提示失败: " + e.Message);
            }
        }
    }

    /// <summary>scnEditor.SaveLevel 的前置/收尾：每次调用各自的上下文用 Harmony 的 __state 配对（object 形态对各版本签名校验都安全）。</summary>
    [HarmonyPatch(typeof(scnEditor), "SaveLevel")]
    internal static class LevelMetadataSaveLevelPatch
    {
        internal static void Prefix(out object __state)
        {
            __state = LevelMetadataBridge.BeginSave();
        }

        internal static Exception Finalizer(Exception __exception, object __state)
        {
            LevelMetadataBridge.CompleteSave(__exception, __state as LevelMetadataBridge.SaveContext);
            return __exception;
        }
    }

    /// <summary>只用来认领"上下文里那一次关卡写盘"：RDFile.WriteAllText 是全程序集共用的，第一步先查有没有上下文。</summary>
    [HarmonyPatch(typeof(RDFile), "WriteAllText", new[] { typeof(string), typeof(string), typeof(Encoding) })]
    internal static class LevelMetadataWriteAllTextPatch
    {
        internal static void Postfix(string __0, string __1)
        {
            LevelMetadataBridge.MarkLevelWritten(__0, __1);
        }
    }

    /// <summary>LevelData.LoadLevel(string, out LoadResult) 成功后按侧车恢复元数据；签名有 byref 参数，所以用 TargetMethod 精确指定。</summary>
    [HarmonyPatch]
    internal static class LevelMetadataLoadLevelPatch
    {
        internal static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(LevelData), "LoadLevel",
                new[] { typeof(string), typeof(LoadResult).MakeByRefType() });
        }

        internal static void Postfix(LevelData __instance, bool __result, string __0)
        {
            LevelMetadataBridge.AfterLevelLoaded(__instance, __result, __0);
        }
    }
}
