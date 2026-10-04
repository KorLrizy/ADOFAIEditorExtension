using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ADOFAIEditorExtension.Features.Metadata
{
    /// <summary>一条事件槽位的元数据快照（纯数据，离线可测）。</summary>
    internal sealed class SlotSnapshot
    {
        /// <summary>当前备注（null / "" = 空）。</summary>
        internal string Note;

        /// <summary>当前手动归属下标，-1 = 没有归属。</summary>
        internal int GroupIndex = -1;
    }

    /// <summary>侧车里的一条记录：只存非空的字段，缺失 = 明确没有该元数据。</summary>
    internal sealed class MetadataRecord
    {
        internal int Index;
        internal string Note;
        internal string Group;

        internal bool IsEmpty => string.IsNullOrEmpty(Note) && string.IsNullOrEmpty(Group);
    }
    /// <summary>一条自定义分组定义的身份证（颜色不参与身份，见设计说明）。</summary>
    internal sealed class GroupDefinition
    {
        internal string Name;
        internal string Tag;
    }

    /// <summary>一套序列（actions 或 decorations）的备份：逐项指纹 + 有元数据的槽位。</summary>
    internal sealed class SequenceBackup
    {
        internal readonly List<string> Fingerprints = new List<string>();

        /// <summary>与 Fingerprints 等长的宽松指纹（不含顶层 floor）；空表 = 这份侧车写于本特性之前，只有精确指纹可用。</summary>
        internal readonly List<string> LooseFingerprints = new List<string>();

        /// <summary>与 Fingerprints 等长的"砖位|事件类型"签名；空表 = 旧侧车，没有第 3 趟对齐。</summary>
        internal readonly List<string> Kinds = new List<string>();

        internal readonly List<MetadataRecord> Records = new List<MetadataRecord>();
    }

    /// <summary>从关卡文本算出的一套序列：Exact = 全部内容；Loose = 再剔掉顶层 floor（加/删砖整体平移后仍能对上）；
    /// Kinds = "砖位|事件类型"（参数被改过、或原版重新编码后改了写法也不变）；Floors = 排序用的砖位（缺省 -1，与运行时同规则）。</summary>
    internal sealed class SequenceFingerprints
    {
        internal List<string> Exact;
        internal List<string> Loose;
        internal List<string> Kinds;
        internal List<int> Floors;
    }

    internal sealed class LevelMetadataBackup
    {
        internal int Version = LevelMetadataStore.CurrentVersion;
        internal string LevelName;
        internal string SavedAtUtc;
        internal readonly SequenceBackup Actions = new SequenceBackup();
        internal readonly SequenceBackup Decorations = new SequenceBackup();
        internal readonly List<GroupDefinition> DecorationGroups = new List<GroupDefinition>();
        internal readonly List<GroupDefinition> EventGroups = new List<GroupDefinition>();
    }

    internal sealed class RestoreAction
    {
        /// <summary>要写到哪个槽位——已经是**当前**序列里的位置（由对齐结果从保存时的下标换过来）。</summary>
        internal int Index;

        /// <summary>这条记录在侧车里的下标（日志用）。</summary>
        internal int StoredIndex;

        internal bool WriteNote;
        internal string Note;
        internal bool WriteGroup;

        /// <summary>要写下的归属下标——已经是**当前**分组表里的位置（由映射表从保存时的下标换过来）。</summary>
        internal int GroupIndex;
    }

    /// <summary>一套序列的对齐结果：侧车每一项对应到当前序列的哪一项（-1 = 对不上）。</summary>
    internal sealed class SequenceAlignment
    {
        /// <summary>长度 = 侧车项数；每项是它在当前序列里的下标，-1 = 没对上。</summary>
        internal int[] StoredToCurrent;

        /// <summary>靠精确指纹对上的项数。</summary>
        internal int ExactMatches;

        /// <summary>靠宽松指纹（剔掉 floor）对上的项数。</summary>
        internal int LooseMatches;

        /// <summary>只靠"砖位|事件类型"对上的项数（参数被改过的同一条事件）。</summary>
        internal int KindMatches;

        /// <summary>对齐工作量超了上限：结果只算了前半截，剩下的都算没对上。</summary>
        internal bool BudgetExceeded;
    }

    internal sealed class RestorePlan
    {
        /// <summary>对齐跑起来了（前置的硬失败都过了）；false ⇒ 连槽位对应关系都定不下来，这个序列什么都不动。</summary>
        internal bool Aligned;

        /// <summary>有记录没对上当前事件，或有归属在当前分组表里找不到落点。
        /// true ⇒ 这个序列按"没完全恢复"算，侧车要被保住，别让下一次保存静默删掉这些元数据。</summary>
        internal bool Incomplete;

        /// <summary>侧车记录对不上当前任何一项事件（被改动或被删除），或对应位置解码后与文件内容不一致的条数。</summary>
        internal int RecordsUnmatched;

        /// <summary>排进计划的备注条数。</summary>
        internal int NotesRestored;

        /// <summary>成功映射到当前分组表、并排进计划的归属条数。</summary>
        internal int GroupsRestored;

        /// <summary>在当前分组表里找不到对应分组（被删/被改名/被改标签，或下标越界、字符串非法）的归属条数。</summary>
        internal int GroupsUnrestorable;

        /// <summary>对齐结果（项数与匹配数，日志用）；Aligned = false 时为 null。</summary>
        internal SequenceAlignment Alignment;

        internal readonly List<RestoreAction> Actions = new List<RestoreAction>();

        /// <summary>给日志用的一句话：整序列否决时是"为什么否决"，对齐后仍有遗漏时是"多少条没恢复"。</summary>
        internal string Reason;
    }

    /// <summary>侧车落盘方式：直接覆盖，或先把磁盘上那份归档再写（归档是复制，发生在替换之前 ⇒ 主文件全程存在）。</summary>
    internal enum SidecarWriteMode
    {
        Write,
        ArchiveThenWrite
    }

    /// <summary>一次侧车落盘的结果（纯数据，离线可测）。Mode = 实际采用的方式；Skipped = 什么都没动；Error 只在失败时非空。</summary>
    internal sealed class SidecarWriteOutcome
    {
        internal SidecarWriteMode Mode;

        internal bool Skipped;

        internal string Error;

        /// <summary>旧侧车被另存到的文件名（没归档就是 null）。</summary>
        internal string ArchivePath;

        /// <summary>为什么归档（调用方给的理由之外的、Store 自己判出来的那种）。</summary>
        internal string ArchiveReason;
    }

    /// <summary>侧车（&lt;完整关卡路径&gt;.aee.json）的纯逻辑：指纹、备份格式、匹配判定、分组身份映射、归档命名、原子写盘；不引用游戏类型，可离线单测。</summary>
    internal static class LevelMetadataStore
    {
        internal const int CurrentVersion = 1;
        internal const string SidecarSuffix = ".aee.json";

        /// <summary>未能完整恢复的旧侧车另存出去的文件名中段。归档名以时间戳（+ 可选序号）加 .json 结尾，
        /// 既不以 SidecarSuffix 结尾（GetSidecarPath 算不出它 ⇒ 读档永远不会把归档当侧车），
        /// 也不以 LevelExtension 结尾（IsLevelPath 也不会当它是关卡）。</summary>
        internal const string UnrestoredInfix = ".aee.unrestored-";

        internal const string LevelExtension = ".adofai";
        internal const string SequenceKeyActions = "actions";
        internal const string SequenceKeyDecorations = "decorations";

        /// <summary>砖位所在事件的顶层键名：宽松指纹把它也剔掉，加/删砖造成的整体平移才不会挡住恢复。</summary>
        internal const string FloorKey = "floor";

        private const string KeyVersion = "version";
        private const string KeyLevel = "level";
        private const string KeySavedAt = "savedAt";
        private const string KeySequence = "sequence";
        private const string KeyLoose = "loose";
        private const string KeyKinds = "kinds";
        private const string KeyEventType = "eventType";
        private const string KeyEntries = "entries";
        private const string KeyIndex = "index";
        private const string KeyNote = "note";
        private const string KeyGroup = "group";
        private const string KeyGroups = "groups";
        private const string KeyDecoGroups = "decoration";
        private const string KeyEventGroups = "event";
        private const string KeyName = "name";
        private const string KeyTag = "tag";

        /// <summary>槽位数上限：手改出来的天文数字一律当坏文件，免得解析把内存吃掉。</summary>
        private const int MaxSlots = 2000000;

        /// <summary>归属下标字符串的长度上限：实际分组远小于此，只为挡手改的天文数字与垃圾值。</summary>
        private const int MaxGroupIndexDigits = 6;

        /// <summary>归档名撞车时的重试上限（同一秒内连续保存）；超了就不写，绝不覆盖已有的归档。</summary>
        private const int MaxArchiveAttempts = 100;

        /// <summary>宽松指纹取 sha1 前多少个十六进制字符（16 = 8 字节）：够两两区分，又能让哈希短到对齐时比对/分桶便宜。</summary>
        private const int LooseHashHexChars = 16;

        /// <summary>一次对齐的总工作量上限（每处理一个待算区间累加两侧长度）。关卡实际远到不了这个量级，
        /// 只为挡住手改出来的巨型侧车把读档主线程拖住；超了就停下，已经对上的照常恢复，其余按没对上计数。</summary>
        internal const int MaxAlignWork = 4_000_000;

        // ---------------------------------------------------------------- 路径与准入

        /// <summary>关卡路径能不能带侧车（非空 + 以 .adofai 结尾，大小写不敏感）。</summary>
        internal static bool IsLevelPath(string levelPath)
        {
            return !string.IsNullOrEmpty(levelPath)
                && levelPath.EndsWith(LevelExtension, StringComparison.OrdinalIgnoreCase);
        }

        internal static string GetSidecarPath(string levelPath) => levelPath + SidecarSuffix;

        /// <summary>旧侧车的归档位置：&lt;关卡路径&gt;.aee.unrestored-yyyyMMdd-HHmmss.json（同目录、本地时间）。
        /// 同一秒内二次保护会撞名，依次追加 -2、-3…；全部被占返回 null（调用方据此不写、保持保护）。</summary>
        internal static string GetArchivePath(string levelPath, DateTime localNow, Func<string, bool> exists)
        {
            if (string.IsNullOrEmpty(levelPath))
                return null;
            // C# 7.3：?? 的右操作数不能是方法组，得显式包成委托
            Func<string, bool> probe = exists ?? new Func<string, bool>(SafeFileExists);
            string stem = levelPath + UnrestoredInfix
                + localNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            for (int attempt = 0; attempt < MaxArchiveAttempts; attempt++)
            {
                string candidate = attempt == 0
                    ? stem + ".json"
                    : stem + "-" + (attempt + 1).ToString(CultureInfo.InvariantCulture) + ".json";
                if (!probe(candidate))
                    return candidate;
            }
            return null;
        }

        // ---------------------------------------------------------------- 指纹

        /// <summary>关卡文本只解析一次，同时算出两套序列的逐项指纹与签名。
        /// false = 文本读不出来、或两套序列里任一处结构不认识（非数组、超上限、数组里出现非对象项）⇒ 调用方整份否决，不猜。</summary>
        internal static bool TryComputeLevelFingerprints(string levelJson, ICollection<string> metadataKeys,
            out SequenceFingerprints actions, out SequenceFingerprints decorations)
        {
            actions = null;
            decorations = null;
            if (string.IsNullOrEmpty(levelJson))
                return false;
            JObject root;
            try
            {
                root = ParseObject(levelJson);
            }
            catch (Exception)
            {
                return false;
            }
            if (root == null)
                return false;

            if (!TryComputeSequence(root, SequenceKeyActions, metadataKeys, out actions))
                return false;
            if (!TryComputeSequence(root, SequenceKeyDecorations, metadataKeys, out decorations))
            {
                actions = null;
                return false;
            }
            return true;
        }

        private static bool TryComputeSequence(JObject root, string sequenceKey, ICollection<string> metadataKeys,
            out SequenceFingerprints result)
        {
            result = new SequenceFingerprints
            {
                Exact = new List<string>(),
                Loose = new List<string>(),
                Kinds = new List<string>(),
                Floors = new List<int>()
            };
            JToken token = root[sequenceKey];
            if (token == null || token.Type == JTokenType.Null)
                return true;            // 这一套序列根本没写进文件 ⇒ 全是空表
            JArray array = token as JArray;
            if (array == null || array.Count > MaxSlots)
            {
                result = null;
                return false;
            }
            ICollection<string> looseKeys = WithFloorSkipped(metadataKeys);
            foreach (JToken item in array)
            {
                JObject obj = item as JObject;
                if (obj == null)
                {
                    result = null;
                    return false;
                }
                result.Exact.Add(HashEvent(obj, metadataKeys));
                result.Loose.Add(LooseHashEvent(obj, looseKeys));
                int floor = ReadFloor(obj[FloorKey]);
                result.Floors.Add(floor);
                JToken type = obj[KeyEventType];
                result.Kinds.Add(EventKind(floor, type != null && type.Type == JTokenType.String ? (string)type : null));
            }
            return true;
        }

        /// <summary>"砖位|事件类型"签名：文件与运行时两侧都只经这一个函数拼，写法才保证一致。
        /// 砖位 -1 与原版 Encode 一样视为没有砖位（不写 floor 键），两侧都留空。</summary>
        internal static string EventKind(int floor, string eventType)
        {
            string floorText = floor == -1 ? "" : floor.ToString(CultureInfo.InvariantCulture);
            return floorText + "|" + (eventType ?? "");
        }

        /// <summary>文件里的 floor：整数取值，缺失或不是 int 范围内的整数都按 -1（运行时 LevelEvent.floor 的缺省值）。
        /// -1 本身原版不会写出来，读到也照收（签名会和运行时的"无砖位"对上，与解码结果一致）。</summary>
        private static int ReadFloor(JToken token)
        {
            if (token == null || token.Type != JTokenType.Integer)
                return -1;
            long value;
            if (!TryGetInt64(token, out value) || value < int.MinValue || value > int.MaxValue)
                return -1;
            return (int)value;
        }

        /// <summary>宽松指纹要剔除的键 = 元数据键 + 顶层 floor（不改动调用方传进来的集合）。</summary>
        private static ICollection<string> WithFloorSkipped(ICollection<string> metadataKeys)
        {
            int count = metadataKeys != null ? metadataKeys.Count : 0;
            var keys = new List<string>(count + 1);
            if (metadataKeys != null)
            {
                foreach (string key in metadataKeys)
                    keys.Add(key);
            }
            if (!ContainsName(keys, FloorKey))
                keys.Add(FloorKey);
            return keys;
        }

        // ---------------------------------------------------------------- 序列对齐

        /// <summary>把侧车那套序列对到当前这套上（保序、一对一、确定性），分三趟，后一趟只在前面留下的空档里跑：
        /// 1) 精确指纹，允许首尾相同段落直接配对（run trimming）；
        /// 2) 宽松指纹（剔掉 floor）：加/删砖之后整体平移的同一条事件；
        /// 3) "砖位|事件类型"签名：参数被改过、或原版重新编码改了写法的同一条事件。
        /// 第 2、3 趟关掉首尾修剪——这两种键在重复摆放的同款事件之间会撞车，修剪会把备注发给错的那一份。
        /// 每趟内部用 patience-diff 那套区间法：区间里"在两侧各只出现一次"的键是锚点，锚点取最长严格递增子序列配对，
        /// 锚点之间的子区间再入栈迭代处理（不递归）。工作量超过 <see cref="MaxAlignWork"/> 就停下，已配对的保留。
        /// 宽松/签名两套任一侧缺失或项数不对（旧侧车），那一趟就跳过。</summary>
        internal static SequenceAlignment AlignSequences(
            IReadOnlyList<string> storedExact, IReadOnlyList<string> currentExact,
            IReadOnlyList<string> storedLoose, IReadOnlyList<string> currentLoose,
            IReadOnlyList<string> storedKinds = null, IReadOnlyList<string> currentKinds = null)
        {
            var alignment = new SequenceAlignment();
            int storedCount = storedExact != null ? storedExact.Count : 0;
            var map = new int[storedCount];
            for (int i = 0; i < storedCount; i++)
                map[i] = -1;
            alignment.StoredToCurrent = map;
            int currentCount = currentExact != null ? currentExact.Count : 0;
            if (storedCount == 0 || currentCount == 0)
                return alignment;

            var aligner = new Aligner(map, storedCount, currentCount);
            aligner.Run(storedExact, currentExact, 0, storedCount, 0, currentCount, true, AlignPass.Exact);
            if (Usable(storedLoose, storedCount) && Usable(currentLoose, currentCount))
                RunInGaps(aligner, map, storedLoose, currentLoose, storedCount, currentCount, AlignPass.Loose);
            if (Usable(storedKinds, storedCount) && Usable(currentKinds, currentCount))
                RunInGaps(aligner, map, storedKinds, currentKinds, storedCount, currentCount, AlignPass.Kind);

            alignment.ExactMatches = aligner.ExactMatches;
            alignment.LooseMatches = aligner.LooseMatches;
            alignment.KindMatches = aligner.KindMatches;
            alignment.BudgetExceeded = aligner.BudgetExceeded;
            return alignment;
        }

        private static bool Usable(IReadOnlyList<string> keys, int count)
        {
            return keys != null && keys.Count == count;
        }

        /// <summary>在已有配对之间的每个空档里跑一趟（不修剪首尾）。已有配对是保序的，按侧车下标升序扫过去两侧都严格递增，
        /// 空档互不重叠；这一趟新配上的都落在各自空档里，所以扫完之后整张表仍然保序。</summary>
        private static void RunInGaps(Aligner aligner, int[] map, IReadOnlyList<string> stored, IReadOnlyList<string> current,
            int storedCount, int currentCount, AlignPass pass)
        {
            int sBase = 0, cBase = 0;
            for (int s = 0; s < storedCount && !aligner.BudgetExceeded; s++)
            {
                int c = map[s];
                if (c < 0)
                    continue;
                if (s > sBase && c > cBase)
                    aligner.Run(stored, current, sBase, s, cBase, c, false, pass);
                sBase = s + 1;
                cBase = c + 1;
            }
            if (!aligner.BudgetExceeded && storedCount > sBase && currentCount > cBase)
                aligner.Run(stored, current, sBase, storedCount, cBase, currentCount, false, pass);
        }

        private enum AlignPass
        {
            Exact,
            Loose,
            Kind
        }

        /// <summary>一次对齐的可变状态：配对表 + 两侧占用标记 + 工作量计数。</summary>
        private sealed class Aligner
        {
            private readonly int[] _map;
            private readonly bool[] _storedTaken;
            private readonly bool[] _currentTaken;
            private long _work;

            internal int ExactMatches;
            internal int LooseMatches;
            internal int KindMatches;
            internal bool BudgetExceeded;

            internal Aligner(int[] map, int storedCount, int currentCount)
            {
                _map = map;
                _storedTaken = new bool[storedCount];
                _currentTaken = new bool[currentCount];
            }

            /// <summary>处理 [sLo,sHi) × [cLo,cHi) 这一块区间；trim = 允许首尾相同段落直接配对；pass 决定配上的计入哪一类。</summary>
            internal void Run(IReadOnlyList<string> stored, IReadOnlyList<string> current,
                int sLo, int sHi, int cLo, int cHi, bool trim, AlignPass pass)
            {
                var pending = new Stack<AlignRange>();
                pending.Push(new AlignRange(sLo, sHi, cLo, cHi));
                while (pending.Count > 0)
                {
                    AlignRange range = pending.Pop();
                    sLo = range.SLo; sHi = range.SHi; cLo = range.CLo; cHi = range.CHi;
                    _work += (sHi - sLo) + (cHi - cLo);
                    if (_work > MaxAlignWork)
                    {
                        BudgetExceeded = true;
                        return;
                    }
                    if (trim)
                    {
                        while (sLo < sHi && cLo < cHi && Equal(stored[sLo], current[cLo]))
                        {
                            Pair(sLo, cLo, pass);
                            sLo++; cLo++;
                        }
                        while (sLo < sHi && cLo < cHi && Equal(stored[sHi - 1], current[cHi - 1]))
                        {
                            sHi--; cHi--;
                            Pair(sHi, cHi, pass);
                        }
                    }
                    if (sLo >= sHi || cLo >= cHi)
                        continue;       // 一侧空了：剩下的没有对象可配

                    // 锚点候选：在这一段两侧都只出现一次的哈希，按侧车顺序取，值为对侧下标
                    var currentOccurrences = new Dictionary<string, int>(StringComparer.Ordinal);
                    var currentPosition = new Dictionary<string, int>(StringComparer.Ordinal);
                    for (int i = cLo; i < cHi; i++)
                    {
                        string key = current[i];
                        if (key == null)
                            continue;
                        int seen;
                        currentOccurrences.TryGetValue(key, out seen);
                        currentOccurrences[key] = seen + 1;
                        currentPosition[key] = i;
                    }
                    var storedOccurrences = new Dictionary<string, int>(StringComparer.Ordinal);
                    for (int i = sLo; i < sHi; i++)
                    {
                        string key = stored[i];
                        if (key == null)
                            continue;
                        int seen;
                        storedOccurrences.TryGetValue(key, out seen);
                        storedOccurrences[key] = seen + 1;
                    }
                    var anchorStored = new List<int>();
                    var anchorCurrent = new List<int>();
                    for (int i = sLo; i < sHi; i++)
                    {
                        string key = stored[i];
                        int occurrences;
                        if (key == null || !storedOccurrences.TryGetValue(key, out occurrences) || occurrences != 1)
                            continue;
                        if (!currentOccurrences.TryGetValue(key, out occurrences) || occurrences != 1)
                            continue;
                        anchorStored.Add(i);
                        anchorCurrent.Add(currentPosition[key]);
                    }
                    if (anchorStored.Count == 0)
                        continue;       // 没有锚点 ⇒ 这一段到此为止，剩下的按没对上算

                    int[] chosen = LongestIncreasingChain(anchorCurrent, anchorCurrent.Count);
                    int sBase = sLo, cBase = cLo;
                    for (int k = 0; k < chosen.Length; k++)
                    {
                        int s = anchorStored[chosen[k]];
                        int c = anchorCurrent[chosen[k]];
                        Pair(s, c, pass);
                        if (s > sBase || c > cBase)
                            pending.Push(new AlignRange(sBase, s, cBase, c));
                        sBase = s + 1;
                        cBase = c + 1;
                    }
                    if (sBase < sHi || cBase < cHi)
                        pending.Push(new AlignRange(sBase, sHi, cBase, cHi));
                }
            }

            private static bool Equal(string left, string right)
            {
                return string.Equals(left, right, StringComparison.Ordinal);
            }

            private void Pair(int storedIndex, int currentIndex, AlignPass pass)
            {
                if (storedIndex < 0 || currentIndex < 0
                    || storedIndex >= _storedTaken.Length || currentIndex >= _currentTaken.Length)
                    return;
                if (_storedTaken[storedIndex] || _currentTaken[currentIndex] || _map[storedIndex] >= 0)
                    return;
                _map[storedIndex] = currentIndex;
                _storedTaken[storedIndex] = true;
                _currentTaken[currentIndex] = true;
                if (pass == AlignPass.Exact)
                    ExactMatches++;
                else if (pass == AlignPass.Loose)
                    LooseMatches++;
                else
                    KindMatches++;
            }

            /// <summary>标准 patience LIS（下界替换 + 前驱链）：返回选中的下标序列（升序，且 values 严格递增）；同一输入永远同一结果。</summary>
            private static int[] LongestIncreasingChain(List<int> values, int count)
            {
                var tails = new List<int>();
                var previous = new int[count];
                for (int i = 0; i < count; i++)
                {
                    previous[i] = -1;
                    int low = 0, high = tails.Count;
                    while (low < high)
                    {
                        int mid = (low + high) / 2;
                        if (values[tails[mid]] < values[i])
                            low = mid + 1;
                        else
                            high = mid;
                    }
                    if (low > 0)
                        previous[i] = tails[low - 1];
                    if (low == tails.Count)
                        tails.Add(i);
                    else
                        tails[low] = i;
                }
                var chain = new int[tails.Count];
                int at = tails.Count - 1;
                for (int i = tails.Count > 0 ? tails[tails.Count - 1] : -1; i >= 0 && at >= 0; i = previous[i])
                    chain[at--] = i;
                return chain;
            }
        }

        private struct AlignRange
        {
            internal readonly int SLo;
            internal readonly int SHi;
            internal readonly int CLo;
            internal readonly int CHi;

            internal AlignRange(int sLo, int sHi, int cLo, int cHi)
            {
                SLo = sLo;
                SHi = sHi;
                CLo = cLo;
                CHi = cHi;
            }
        }

        /// <summary>规范 JSON → sha1：对象键按 ordinal 排序、数字按十进制归一、紧凑无空白；skipKeys 只剔该对象**顶层**的同名键（嵌套的用户 JSON 不剔）。</summary>
        internal static string Canonicalize(JToken token, ICollection<string> skipKeys)
        {
            var builder = new StringBuilder(256);
            AppendCanonical(builder, token, skipKeys, true);
            return builder.ToString();
        }

        private static void AppendCanonical(StringBuilder builder, JToken token, ICollection<string> skipKeys, bool topLevel)
        {
            if (token == null)
            {
                builder.Append("null");
                return;
            }
            switch (token.Type)
            {
                case JTokenType.Object:
                    builder.Append('{');
                    var names = new List<string>();
                    var byName = new Dictionary<string, JToken>(StringComparer.Ordinal);
                    foreach (JProperty property in ((JObject)token).Properties())
                    {
                        if (topLevel && skipKeys != null && ContainsName(skipKeys, property.Name))
                            continue;
                        if (!byName.ContainsKey(property.Name))
                            names.Add(property.Name);
                        byName[property.Name] = property.Value;   // 重复键取最后一个（Newtonsoft 读进来就是这个语义）
                    }
                    names.Sort(StringComparer.Ordinal);
                    for (int i = 0; i < names.Count; i++)
                    {
                        if (i > 0)
                            builder.Append(',');
                        builder.Append(JsonConvert.SerializeObject(names[i]));
                        builder.Append(':');
                        AppendCanonical(builder, byName[names[i]], skipKeys, false);
                    }
                    builder.Append('}');
                    break;
                case JTokenType.Array:
                    builder.Append('[');
                    int n = 0;
                    foreach (JToken child in token)
                    {
                        if (n++ > 0)
                            builder.Append(',');
                        AppendCanonical(builder, child, skipKeys, false);
                    }
                    builder.Append(']');
                    break;
                case JTokenType.Integer:
                case JTokenType.Float:
                    AppendCanonicalNumber(builder, token);
                    break;
                default:
                    // JValue：string 带引号转义、bool/null 都是 JSON 字面量
                    string literal = token.ToString(Formatting.None);
                    builder.Append(string.IsNullOrEmpty(literal) ? "null" : literal);
                    break;
            }
        }

        /// <summary>
        /// 数字一律按十进制定点归一后再比较：文件里的 <c>1</c> / <c>1.0</c> / <c>1e0</c> 与运行时
        /// Encode(false) 给的 decimal(1)、double(1) 必须是同一个字面量（原版用 System.Text.Json 写盘，
        /// LevelEvent.Encode 的 Float 分支又走 Convert.ToDecimal，两边天然不同形）。
        /// 取 token 自己的 JSON 字面量（有效位由写端决定）再按不变文化十进制解析，G29 去掉尾零；
        /// 1.0001 这类值不会因为归一而被折成 1。decimal 装不下的（天文数字、1e300、NaN/Infinity）
        /// 原样保留字面量，不做有损归一，也不抛。
        /// </summary>
        private static void AppendCanonicalNumber(StringBuilder builder, JToken token)
        {
            string literal = token.ToString(Formatting.None);
            if (string.IsNullOrEmpty(literal))
            {
                builder.Append("null");
                return;
            }
            decimal value;
            if (decimal.TryParse(literal, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                builder.Append(value.ToString("G29", CultureInfo.InvariantCulture));
                return;
            }
            builder.Append(literal);
        }

        private static bool ContainsName(ICollection<string> names, string name)
        {
            foreach (string candidate in names)
            {
                if (string.Equals(candidate, name, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private static string HashEvent(JObject encodedEvent, ICollection<string> skipKeys)
        {
            string canonical = Canonicalize(encodedEvent, skipKeys);
            return Sha1Hex(canonical);
        }

        /// <summary>宽松指纹：同样规范化的内容再剔掉顶层 floor，取 sha1 前 16 个十六进制字符。
        /// 只用于把记录对回同一条事件（对上了还要靠精确指纹复核解码内容），所以宁可短、不要长。</summary>
        private static string LooseHashEvent(JObject encodedEvent, ICollection<string> skipKeys)
        {
            string hex = Sha1Hex(Canonicalize(encodedEvent, skipKeys));
            return hex.Length > LooseHashHexChars ? hex.Substring(0, LooseHashHexChars) : hex;
        }

        private static string Sha1Hex(string canonical)
        {
            byte[] bytes = new UTF8Encoding(false).GetBytes(canonical);
            using (var sha1 = SHA1.Create())
            {
                byte[] hash = sha1.ComputeHash(bytes);
                var builder = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++)
                    builder.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                return builder.ToString();
            }
        }

        // ---------------------------------------------------------------- 捕获 / 分组定义

        /// <summary>把槽位快照压成记录表（只留有元数据的槽位，下标就是它在序列里的位置）。</summary>
        internal static List<MetadataRecord> CaptureRecords(IReadOnlyList<SlotSnapshot> slots)
        {
            var records = new List<MetadataRecord>();
            if (slots == null)
                return records;
            for (int i = 0; i < slots.Count; i++)
            {
                MetadataRecord record = CreateRecord(i, slots[i] != null ? slots[i].Note : null,
                    slots[i] != null ? slots[i].GroupIndex : -1);
                if (record != null)
                    records.Add(record);
            }
            return records;
        }

        /// <summary>一条槽位 → 一条记录；备注与归属都没有则 null（保存路径逐条调用，不预先分配整批快照）。</summary>
        internal static MetadataRecord CreateRecord(int index, string note, int groupIndex)
        {
            string normalized = Normalize(note);
            string group = groupIndex >= 0 ? groupIndex.ToString(CultureInfo.InvariantCulture) : null;
            if (normalized == null && group == null)
                return null;
            return new MetadataRecord { Index = index, Note = normalized, Group = group };
        }

        /// <summary>把"保存时的分组表"按下标映射到"当前分组表"：身份 = (名称, 标签) 逐序比较，颜色不参与身份。
        /// 返回长度 = stored.Count 的数组，每项是该保存时下标对应的当前下标，-1 = 当前表里没有这个分组。
        /// 同一身份出现多次：保存侧第 k 个对当前侧第 k 个（确定、可重复），多出来的那些给 -1。
        /// stored 为 null/空 ⇒ 空数组；current 为 null ⇒ 全 -1。</summary>
        internal static int[] BuildGroupIndexMap(IReadOnlyList<GroupDefinition> stored, IReadOnlyList<GroupDefinition> current)
        {
            if (stored == null || stored.Count == 0)
                return new int[0];
            var map = new int[stored.Count];
            for (int i = 0; i < map.Length; i++)
                map[i] = -1;
            if (current == null || current.Count == 0)
                return map;

            // 当前表按身份分桶、桶内保持定义顺序；出队即"第 k 个对上第 k 个"，不必再记两侧的序数
            var buckets = new Dictionary<string, Queue<int>>(StringComparer.Ordinal);
            for (int i = 0; i < current.Count; i++)
            {
                string key = GroupIdentityKey(current[i]);
                Queue<int> bucket;
                if (!buckets.TryGetValue(key, out bucket))
                    buckets[key] = bucket = new Queue<int>();
                bucket.Enqueue(i);
            }
            for (int i = 0; i < stored.Count; i++)
            {
                Queue<int> bucket;
                if (buckets.TryGetValue(GroupIdentityKey(stored[i]), out bucket) && bucket.Count > 0)
                    map[i] = bucket.Dequeue();
            }
            return map;
        }

        /// <summary>分组身份证：名称与标签各按空串兜底后拼接（标签在读取侧已去空白，这里不再加工，保持与建表时同一套比较）。
        /// 分隔符用 \0，免得 ("ab","") 与 ("a","b") 撞成同一个键。</summary>
        private static string GroupIdentityKey(GroupDefinition group)
        {
            string name = group != null ? group.Name : null;
            string tag = group != null ? group.Tag : null;
            return (name ?? "") + "\u0000" + (tag ?? "");
        }

        private static string Normalize(string value)
        {
            return string.IsNullOrEmpty(value) || value.Trim().Length == 0 ? null : value;
        }

        // ---------------------------------------------------------------- 恢复计划

        /// <summary>纯决策：先把侧车那套指纹逐项**对齐**到当前关卡（<see cref="AlignSequences"/>），逐条决定能不能落回某个当前槽位，
        /// 不再要求整套序列逐项一致——加了砖、删了事件只让受影响的那几条算没对上，其余照常恢复。
        /// 只有"对应关系压根定不下来"（没有指纹、关卡文件读不出来、槽位数与序列项数不等）才整条序列否决。
        /// 对齐的是侧车与关卡文件（同一种文本写法）；文件第 j 项落到哪个运行时槽位，按原版解码顺序推出来
        /// （actions 按 floor 稳定排序，decorations 原序），再核实两边"砖位|事件类型"签名一致——
        /// 不拿解码后再编码的内容去比，原版的解码/编码并不保证往返不变（新建事件第一次读档就会变）。
        /// 归属逐条按 <paramref name="groupIndexMap"/>（保存时下标 → 当前下标）映射，映射得到的才写；一条落不回去只让那一条按"没恢复"计数。
        /// 已有非空值的槽位不覆盖（live 优先），不计入未恢复。备注逻辑与分组无关。</summary>
        internal static RestorePlan BuildRestorePlan(
            SequenceBackup stored,
            SequenceFingerprints file,
            IReadOnlyList<string> runtimeKinds,
            IReadOnlyList<SlotSnapshot> currentSlots,
            int[] groupIndexMap,
            bool slotsSortedByFloor)
        {
            var plan = new RestorePlan();
            if (stored == null || stored.Fingerprints.Count == 0)
            {
                plan.Reason = "侧车里没有这套序列的指纹";
                return plan;
            }
            if (file == null || file.Exact == null || file.Kinds == null || file.Floors == null
                || file.Kinds.Count != file.Exact.Count || file.Floors.Count != file.Exact.Count)
            {
                plan.Reason = "关卡文件读不出来或结构不认识";
                return plan;
            }
            if (runtimeKinds == null)
            {
                plan.Reason = "读不到解码后事件的砖位与类型（不能确定槽位对应关系）";
                return plan;
            }
            if (currentSlots == null)
            {
                plan.Reason = "读不到当前事件的槽位值";
                return plan;
            }
            // 解码时丢了或多了事件（越界砖位、旧格式装饰挪表、不认识的类型）⇒ 文件项与槽位的对应关系无从谈起
            if (currentSlots.Count != file.Exact.Count || currentSlots.Count != runtimeKinds.Count)
            {
                plan.Reason = string.Format(CultureInfo.InvariantCulture,
                    "槽位数与序列项数不一致（槽位 {0} / 关卡文件 {1} / 解码事件 {2}）",
                    currentSlots.Count, file.Exact.Count, runtimeKinds.Count);
                return plan;
            }

            SequenceAlignment alignment = AlignSequences(stored.Fingerprints, file.Exact,
                stored.LooseFingerprints, file.Loose, stored.Kinds, file.Kinds);
            plan.Alignment = alignment;
            plan.Aligned = true;
            int[] map = alignment.StoredToCurrent ?? new int[0];
            int[] fileToSlot = FileToSlot(file.Floors, slotsSortedByFloor);

            for (int r = 0; r < stored.Records.Count; r++)
            {
                MetadataRecord record = stored.Records[r];
                if (record == null)
                    continue;
                int fileIndex = record.Index >= 0 && record.Index < map.Length ? map[record.Index] : -1;
                int target = fileIndex >= 0 && fileIndex < fileToSlot.Length ? fileToSlot[fileIndex] : -1;
                // 对不上、或推出来的槽位上不是同一砖位同一类型的事件 ⇒ 只这一条算没恢复
                if (target < 0 || target >= runtimeKinds.Count
                    || !string.Equals(runtimeKinds[target], file.Kinds[fileIndex], StringComparison.Ordinal))
                {
                    plan.RecordsUnmatched++;
                    continue;
                }
                SlotSnapshot current = currentSlots[target];
                if (current == null)
                {
                    plan.RecordsUnmatched++;
                    continue;
                }

                var action = new RestoreAction { Index = target, StoredIndex = record.Index };
                string note = Normalize(record.Note);
                if (note != null && Normalize(current.Note) == null)
                {
                    action.WriteNote = true;
                    action.Note = record.Note;
                    plan.NotesRestored++;
                }
                if (!string.IsNullOrEmpty(record.Group) && current.GroupIndex < 0)
                {
                    // current.GroupIndex >= 0 ⇒ 槽位已有内嵌归属，live 优先、这条本来就不写，不算没恢复
                    int mapped = ResolveGroupIndex(record.Group, groupIndexMap);
                    if (mapped >= 0)
                    {
                        action.WriteGroup = true;
                        action.GroupIndex = mapped;
                        plan.GroupsRestored++;
                    }
                    else
                    {
                        // 只丢掉这一条的话，下一次保存会拿着"已对齐"把它从侧车里静默删掉 ⇒ 计数并标未完全恢复
                        plan.GroupsUnrestorable++;
                    }
                }
                if (action.WriteNote || action.WriteGroup)
                    plan.Actions.Add(action);
            }
            plan.Incomplete = plan.RecordsUnmatched > 0 || plan.GroupsUnrestorable > 0;
            if (plan.Incomplete)
            {
                var parts = new List<string>();
                if (plan.RecordsUnmatched > 0)
                    parts.Add(string.Format(CultureInfo.InvariantCulture, "{0} 条记录对不上当前事件（已改动或删除）",
                        plan.RecordsUnmatched));
                if (plan.GroupsUnrestorable > 0)
                    parts.Add(UnrestorableGroupsMessage(plan.GroupsUnrestorable));
                plan.Reason = string.Join("，", parts);
            }
            return plan;
        }

        /// <summary>文件第 j 项解码后排在第几个槽位：sortedByFloor = 与 OrderBy(e =&gt; e.floor) 同一规则的稳定排序（原版写盘本来就排好了，此时是恒等）；
        /// 否则原序。</summary>
        internal static int[] FileToSlot(IReadOnlyList<int> floors, bool sortedByFloor)
        {
            int count = floors != null ? floors.Count : 0;
            var order = new int[count];
            for (int i = 0; i < count; i++)
                order[i] = i;
            if (sortedByFloor && count > 1)
            {
                // Array.Sort 不稳定：同砖位按原下标决胜，等价于稳定排序
                Array.Sort(order, (a, b) =>
                {
                    int byFloor = floors[a].CompareTo(floors[b]);
                    return byFloor != 0 ? byFloor : a.CompareTo(b);
                });
            }
            var slotOf = new int[count];
            for (int slot = 0; slot < count; slot++)
                slotOf[order[slot]] = slot;
            return slotOf;
        }

        /// <summary>归属落不回当前分组表时的日志文案（计划的 Reason 用的就是这一句，两边不说出两套话）。</summary>
        internal static string UnrestorableGroupsMessage(int unrestorableCount)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "{0} 条归属在当前分组表里找不到对应的分组（名称+标签）", unrestorableCount);
        }

        /// <summary>备份里的归属字符串 → 当前分组下标；解析规则与旧版一致（纯非负数字、不带符号），越界、非法或当前表没有对应分组都返回 -1。</summary>
        private static int ResolveGroupIndex(string group, int[] groupIndexMap)
        {
            if (groupIndexMap == null || groupIndexMap.Length == 0)
                return -1;
            int stored;
            if (!int.TryParse(group, NumberStyles.None, CultureInfo.InvariantCulture, out stored))
                return -1;
            if (stored < 0 || stored >= groupIndexMap.Length)
                return -1;
            return groupIndexMap[stored];
        }

        /// <summary>这套序列里有没有带归属的记录（有才需要算分组映射表）。</summary>
        internal static bool HasGroupRecords(SequenceBackup sequence)
        {
            if (sequence == null)
                return false;
            for (int i = 0; i < sequence.Records.Count; i++)
            {
                if (sequence.Records[i] != null && !string.IsNullOrEmpty(sequence.Records[i].Group))
                    return true;
            }
            return false;
        }

        internal static bool HasAnyRecord(LevelMetadataBackup backup)
        {
            return backup != null
                && (backup.Actions.Records.Count > 0 || backup.Decorations.Records.Count > 0);
        }

        // ---------------------------------------------------------------- 备份文件格式

        internal static string SerializeBackup(LevelMetadataBackup backup)
        {
            var root = new JObject
            {
                [KeyVersion] = CurrentVersion,
                [KeyLevel] = backup.LevelName ?? "",
                [KeySavedAt] = backup.SavedAtUtc ?? ""
            };
            root[SequenceKeyActions] = SerializeSequence(backup.Actions);
            root[SequenceKeyDecorations] = SerializeSequence(backup.Decorations);
            root[KeyGroups] = new JObject
            {
                [KeyDecoGroups] = SerializeGroups(backup.DecorationGroups),
                [KeyEventGroups] = SerializeGroups(backup.EventGroups)
            };
            return root.ToString(Formatting.Indented);
        }

        private static JObject SerializeSequence(SequenceBackup sequence)
        {
            var fingerprints = new JArray();
            var loose = new JArray();
            var kinds = new JArray();
            var entries = new JArray();
            if (sequence != null)
            {
                for (int i = 0; i < sequence.Fingerprints.Count; i++)
                    fingerprints.Add(sequence.Fingerprints[i] ?? "");
                for (int i = 0; i < sequence.LooseFingerprints.Count; i++)
                    loose.Add(sequence.LooseFingerprints[i] ?? "");
                for (int i = 0; i < sequence.Kinds.Count; i++)
                    kinds.Add(sequence.Kinds[i] ?? "");
                for (int i = 0; i < sequence.Records.Count; i++)
                {
                    MetadataRecord record = sequence.Records[i];
                    if (record == null || record.IsEmpty)
                        continue;
                    // 只写非空字段：缺失 = 明确没有这项元数据，不用空串占位
                    var entry = new JObject { [KeyIndex] = record.Index };
                    if (!string.IsNullOrEmpty(record.Note))
                        entry[KeyNote] = record.Note;
                    if (!string.IsNullOrEmpty(record.Group))
                        entry[KeyGroup] = record.Group;
                    entries.Add(entry);
                }
            }
            var node = new JObject { [KeySequence] = fingerprints };
            // 空表 ⇒ 不写（读回来还是空表，对齐时跳过那一趟）
            if (loose.Count > 0)
                node[KeyLoose] = loose;
            if (kinds.Count > 0)
                node[KeyKinds] = kinds;
            node[KeyEntries] = entries;
            return node;
        }

        private static JArray SerializeGroups(IReadOnlyList<GroupDefinition> groups)
        {
            var array = new JArray();
            if (groups == null)
                return array;
            for (int i = 0; i < groups.Count; i++)
            {
                array.Add(new JObject
                {
                    [KeyName] = groups[i] != null ? groups[i].Name ?? "" : "",
                    [KeyTag] = groups[i] != null ? groups[i].Tag ?? "" : ""
                });
            }
            return array;
        }

        /// <summary>解析侧车：坏 JSON / 版本不认识 / 结构越界 / 已知的节出现了却形状不对，一律整份拒绝（"能读多少算多少"正是部分错配的来源）。</summary>
        internal static bool TryParseBackup(string json, out LevelMetadataBackup backup, out string error)
        {
            backup = null;
            error = null;
            if (string.IsNullOrEmpty(json))
            {
                error = "侧车文件是空的";
                return false;
            }
            JObject root;
            try
            {
                root = ParseObject(json);
            }
            catch (Exception e)
            {
                error = "不是合法 JSON（" + e.Message + "）";
                return false;
            }
            if (root == null)
            {
                error = "顶层不是 JSON 对象";
                return false;
            }

            long versionValue;
            if (!TryGetInt64(root[KeyVersion], out versionValue))
            {
                error = "缺合法的 version 整数";
                return false;
            }
            if (versionValue > CurrentVersion)
            {
                error = "版本 " + versionValue + " 比本模组支持的 " + CurrentVersion + " 新";
                return false;
            }
            if (versionValue < 1)
            {
                error = "版本 " + versionValue + " 不合法";
                return false;
            }

            var parsed = new LevelMetadataBackup { Version = (int)versionValue };
            JToken actionsNode = root[SequenceKeyActions];
            if (!IsAbsentOr(actionsNode, JTokenType.Object))
            {
                error = "actions 这一节出现了却不是对象";
                return false;
            }
            SequenceBackup actions;
            if (!TryParseSequence(actionsNode as JObject, out actions, out error))
                return false;
            CopyInto(actions, parsed.Actions);
            JToken decorationsNode = root[SequenceKeyDecorations];
            if (!IsAbsentOr(decorationsNode, JTokenType.Object))
            {
                error = "decorations 这一节出现了却不是对象";
                return false;
            }
            SequenceBackup decorations;
            if (!TryParseSequence(decorationsNode as JObject, out decorations, out error))
                return false;
            CopyInto(decorations, parsed.Decorations);

            JToken groupsNode = root[KeyGroups];
            if (!IsAbsentOr(groupsNode, JTokenType.Object))
            {
                error = "groups 不是对象";
                return false;
            }
            JObject groups = groupsNode as JObject;
            if (groups != null)
            {
                JToken decoGroupsNode = groups[KeyDecoGroups];
                if (!IsAbsentOr(decoGroupsNode, JTokenType.Array))
                {
                    error = "groups.decoration 不是数组";
                    return false;
                }
                List<GroupDefinition> decorationGroups;
                if (!TryParseGroups(decoGroupsNode as JArray, out decorationGroups, out error))
                    return false;
                parsed.DecorationGroups.AddRange(decorationGroups);
                JToken eventGroupsNode = groups[KeyEventGroups];
                if (!IsAbsentOr(eventGroupsNode, JTokenType.Array))
                {
                    error = "groups.event 不是数组";
                    return false;
                }
                List<GroupDefinition> eventGroups;
                if (!TryParseGroups(eventGroupsNode as JArray, out eventGroups, out error))
                    return false;
                parsed.EventGroups.AddRange(eventGroups);
            }
            if (!TryReadString(root[KeyLevel], out parsed.LevelName))
            {
                error = "level 字段不是字符串";
                return false;
            }
            if (!TryReadString(root[KeySavedAt], out parsed.SavedAtUtc))
            {
                error = "savedAt 字段不是字符串";
                return false;
            }
            backup = parsed;
            return true;
        }

        private static void CopyInto(SequenceBackup from, SequenceBackup to)
        {
            to.Fingerprints.AddRange(from.Fingerprints);
            to.LooseFingerprints.AddRange(from.LooseFingerprints);
            to.Kinds.AddRange(from.Kinds);
            to.Records.AddRange(from.Records);
        }

        /// <summary>与 sequence 逐项平行的可选字符串数组（loose / kinds）：缺失 = 旧侧车（空表）；出现了就必须是等长的字符串数组。</summary>
        private static bool TryParseParallel(JObject sequence, string key, int expectedCount, List<string> into, out string error)
        {
            error = null;
            JToken node = sequence[key];
            if (!IsAbsentOr(node, JTokenType.Array))
            {
                error = key + " 不是数组";
                return false;
            }
            JArray array = node as JArray;
            if (array == null)
                return true;
            if (array.Count != expectedCount)
            {
                error = key + " 项数与 sequence 不一致（" + array.Count + " / " + expectedCount + " 项）";
                return false;
            }
            for (int i = 0; i < array.Count; i++)
            {
                if (array[i].Type != JTokenType.String)
                {
                    error = key + " 里第 " + i + " 项不是字符串";
                    return false;
                }
                into.Add((string)array[i] ?? "");
            }
            return true;
        }

        private static bool TryParseSequence(JObject sequence, out SequenceBackup data, out string error)
        {
            data = new SequenceBackup();
            error = null;
            if (sequence == null)
                return true;        // 整节缺失 = 这套序列没有备份（合法）
            JToken sequenceNode = sequence[KeySequence];
            if (!IsAbsentOr(sequenceNode, JTokenType.Array))
            {
                error = "sequence 不是数组";
                return false;
            }
            JArray fingerprints = sequenceNode as JArray;
            if (fingerprints != null)
            {
                if (fingerprints.Count > MaxSlots)
                {
                    error = "指纹项数超出上限";
                    return false;
                }
                for (int i = 0; i < fingerprints.Count; i++)
                {
                    if (fingerprints[i].Type != JTokenType.String)
                    {
                        error = "sequence 里第 " + i + " 项不是字符串";
                        return false;
                    }
                    data.Fingerprints.Add((string)fingerprints[i] ?? "");
                }
            }
            if (!TryParseParallel(sequence, KeyLoose, data.Fingerprints.Count, data.LooseFingerprints, out error))
                return false;
            if (!TryParseParallel(sequence, KeyKinds, data.Fingerprints.Count, data.Kinds, out error))
                return false;
            JToken entriesNode = sequence[KeyEntries];
            if (!IsAbsentOr(entriesNode, JTokenType.Array))
            {
                error = "entries 不是数组";
                return false;
            }
            JArray entries = entriesNode as JArray;
            if (entries == null || entries.Count == 0)
                return true;
            if (data.Fingerprints.Count == 0)
            {
                error = "有 entries 却没有 sequence 指纹（无法确定槽位对应关系）";
                return false;
            }
            var seen = new HashSet<long>();
            for (int i = 0; i < entries.Count; i++)
            {
                JObject entry = entries[i] as JObject;
                if (entry == null)
                {
                    error = "entries 里第 " + i + " 项不是对象";
                    return false;
                }
                long indexValue;
                if (!TryGetInt64(entry[KeyIndex], out indexValue))
                {
                    error = "entries 里第 " + i + " 项缺合法的 index";
                    return false;
                }
                if (indexValue < 0 || indexValue >= data.Fingerprints.Count)
                {
                    error = "entries 里第 " + i + " 项的 index 超出指纹表范围（" + indexValue
                        + " / " + data.Fingerprints.Count + " 项）";
                    return false;
                }
                if (!seen.Add(indexValue))
                {
                    error = "entries 里第 " + i + " 项与前面的 index 重复（" + indexValue + "）";
                    return false;
                }
                string note;
                if (!TryReadString(entry[KeyNote], out note))
                {
                    error = "entries 里第 " + i + " 项的 note 不是字符串";
                    return false;
                }
                string group;
                if (!TryReadString(entry[KeyGroup], out group))
                {
                    error = "entries 里第 " + i + " 项的 group 不是字符串";
                    return false;
                }
                if (string.IsNullOrEmpty(note) && string.IsNullOrEmpty(group))
                    continue;       // 明确空值 = 这条没有元数据
                if (!string.IsNullOrEmpty(group) && !IsGroupIndexString(group))
                {
                    error = "entries 里第 " + i + " 项的 group 不是非负下标（" + group + "）";
                    return false;
                }
                data.Records.Add(new MetadataRecord { Index = (int)indexValue, Note = note, Group = group });
            }
            data.Records.Sort((a, b) => a.Index.CompareTo(b.Index));
            return true;
        }

        private static bool TryParseGroups(JArray groups, out List<GroupDefinition> data, out string error)
        {
            data = new List<GroupDefinition>();
            error = null;
            if (groups == null)
                return true;        // 缺节 = 这套没有记分组定义（映射表为空 ⇒ 归属都会被判成落不回去）
            for (int i = 0; i < groups.Count; i++)
            {
                JObject group = groups[i] as JObject;
                if (group == null)
                {
                    error = "groups 里第 " + i + " 项不是对象";
                    return false;
                }
                string name;
                if (!TryReadString(group[KeyName], out name))
                {
                    error = "groups 里第 " + i + " 项的 name 不是字符串";
                    return false;
                }
                string tag;
                if (!TryReadString(group[KeyTag], out tag))
                {
                    error = "groups 里第 " + i + " 项的 tag 不是字符串";
                    return false;
                }
                data.Add(new GroupDefinition { Name = name, Tag = tag });
            }
            return true;
        }

        /// <summary>已知的节：缺失/null 合法（= 明确没有这一节），出现了就必须是对的形状——把手改错的 `actions: 3`、`entries: {}` 当成"没有备份"，旧备份就会被一次保存覆盖掉。</summary>
        private static bool IsAbsentOr(JToken token, JTokenType expected)
        {
            return token == null || token.Type == JTokenType.Null || token.Type == expected;
        }

        /// <summary>整数 token → Int64；类型不对或超出 Int64（手改的天文数字）都返回 false，不抛。</summary>
        private static bool TryGetInt64(JToken token, out long value)
        {
            value = 0;
            if (token == null || token.Type != JTokenType.Integer)
                return false;
            try
            {
                value = token.Value<long>();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>归属下标的字符串形状：纯数字、不带符号、不超长（越界与非法值在这里就拒掉，旧备份因此不会被覆盖）。</summary>
        private static bool IsGroupIndexString(string group)
        {
            if (group == null || group.Length == 0 || group.Length > MaxGroupIndexDigits)
                return false;
            for (int i = 0; i < group.Length; i++)
            {
                if (group[i] < '0' || group[i] > '9')
                    return false;
            }
            return true;
        }

        /// <summary>可选字符串字段：缺失/null ⇒ null；其它标量、对象、数组 ⇒ false（非法结构不吞成空串悄悄丢记录）。</summary>
        private static bool TryReadString(JToken token, out string value)
        {
            value = null;
            if (token == null || token.Type == JTokenType.Null)
                return true;        // 没有这个字段 = 明确没有这项元数据
            if (token.Type != JTokenType.String)
                return false;
            value = (string)token ?? "";
            return true;
        }

        /// <summary>DateParseHandling.None：Newtonsoft 默认会把像 ISO 日期的字符串变成 DateTime token，格式化跟区域有关，而指纹必须跟区域无关。
        /// FloatParseHandling.Decimal：文件里的浮点先按十进制读，别让 double 把有效位舍掉后再去归一（运行时那套本来就是 decimal，两边同尺度才对得上）。</summary>
        private static JObject ParseObject(string text)
        {
            using (var reader = new JsonTextReader(new StringReader(text)))
            {
                reader.DateParseHandling = DateParseHandling.None;
                reader.FloatParseHandling = FloatParseHandling.Decimal;
                return JObject.Load(reader);
            }
        }

        // ---------------------------------------------------------------- 读写盘

        /// <summary>落盘前最后一道闸（返回 null = 可以写）：关卡由 RDFile.WriteAllText 后置确认真写成、指纹齐、项数与内存事件数一致；原版 SaveLevel 吞异常，不能拿它正常返回当依据。</summary>
        internal static string SidecarWriteGuard(bool levelWritten, bool originalSaveThrew,
            IReadOnlyList<string> actionFingerprints, IReadOnlyList<string> decorationFingerprints,
            int actionSlots, int decorationSlots)
        {
            if (originalSaveThrew)
                return "原版保存抛出异常";
            if (!levelWritten)
                return "原版没有把关卡写到这个路径";
            if (actionFingerprints == null || decorationFingerprints == null)
                return "写出的关卡内容解析不出指纹";
            if (actionFingerprints.Count != actionSlots || decorationFingerprints.Count != decorationSlots)
                return string.Format(CultureInfo.InvariantCulture,
                    "写出的关卡与内存事件数不一致（actions {0}/{1}、decorations {2}/{3}）",
                    actionFingerprints.Count, actionSlots, decorationFingerprints.Count, decorationSlots);
            return null;
        }

        /// <summary>磁盘上已有侧车时怎么写：没有旧侧车、或旧侧车是本进程读过且没被保护的 ⇒ 直接覆盖；
        /// 被保护（没能完整恢复）或本进程压根没读过 ⇒ 先归档再写。
        /// 一律"拦住不写"会让保护永久化：分组归属默认不写进关卡文件，侧车是它们唯一的去处，
        /// 之后的每次保存都跳过 ⇒ 用户新建的分组与备注哪儿都没落，下次读档全丢、并且继续保护。
        /// reason 只在需要归档时给出（日志用）。</summary>
        internal static SidecarWriteMode DecideSidecarWrite(bool existingSidecar, bool knownToThisProcess,
            bool protectedByThisProcess, out string reason)
        {
            reason = null;
            if (!existingSidecar)
                return SidecarWriteMode.Write;
            if (protectedByThisProcess)
            {
                reason = "这份侧车本进程没能完整恢复";
                return SidecarWriteMode.ArchiveThenWrite;
            }
            if (!knownToThisProcess)
            {
                reason = "这份侧车本进程没有读过（可能属于另一份关卡内容）";
                return SidecarWriteMode.ArchiveThenWrite;
            }
            return SidecarWriteMode.Write;
        }

        /// <summary>侧车落盘：要归档的先 File.Copy 归档、再原子写新内容（主文件全程存在）。
        /// false = 归档或写盘失败，磁盘上那份原样不动（调用方保持保护）；Skipped = 没东西可写、什么都没动。
        /// 传进来是 Write、但旧侧车读不回（坏 JSON / 未知版本）⇒ 改判 ArchiveThenWrite：
        /// 归档保旧数据，新内容照样写，不再一字节不动地一直跳过。</summary>
        internal static bool TryWriteBackup(string levelPath, string sidecarPath, LevelMetadataBackup backup,
            bool existingSidecar, SidecarWriteMode mode, SidecarWriteOutcome outcome)
        {
            if (outcome == null)
                outcome = new SidecarWriteOutcome();
            outcome.Mode = mode;
            if (backup == null || string.IsNullOrEmpty(sidecarPath))
            {
                outcome.Skipped = true;
                return true;
            }
            // 没有元数据、磁盘上也没有旧侧车 ⇒ 不生成文件；有旧侧车 ⇒ 照样重写一遍空记录，防旧备注复活（该归档的先归档）。
            if (!HasAnyRecord(backup) && !existingSidecar)
            {
                outcome.Skipped = true;
                return true;
            }
            bool hasOldFile = existingSidecar && SafeFileExists(sidecarPath);
            if (mode == SidecarWriteMode.Write && hasOldFile)
            {
                LevelMetadataBackup previous;
                string readError;
                if (!TryReadBackupFile(sidecarPath, out previous, out readError))
                {
                    outcome.Mode = SidecarWriteMode.ArchiveThenWrite;
                    outcome.ArchiveReason = "旧侧车无法解析（" + readError + "）";
                }
            }
            if (outcome.Mode == SidecarWriteMode.ArchiveThenWrite)
            {
                if (!hasOldFile)
                {
                    // 上下文里"存在"的旧侧车这会儿不见了 ⇒ 没东西可归档，也没东西会被覆盖
                    outcome.Mode = SidecarWriteMode.Write;
                    outcome.ArchiveReason = null;
                }
                else if (!TryArchiveSidecar(levelPath, sidecarPath, outcome))
                    return false;
            }
            string content;
            try
            {
                content = SerializeBackup(backup);
            }
            catch (Exception e)
            {
                outcome.Error = "序列化侧车内容失败: " + e.Message;
                return false;
            }
            try
            {
                WriteAtomically(sidecarPath, content);
                return true;
            }
            catch (Exception e)
            {
                // 替换没成 ⇒ 主文件还是旧的那份，归档只是多一个副本，数据没有丢
                outcome.Error = e.GetType().Name + ": " + e.Message;
                return false;
            }
        }

        private static bool TryArchiveSidecar(string levelPath, string sidecarPath, SidecarWriteOutcome outcome)
        {
            string archivePath = GetArchivePath(levelPath, DateTime.Now, SafeFileExists);
            if (archivePath == null)
            {
                outcome.Error = "旧侧车的归档位置取不出来（同一秒内已用满 " + MaxArchiveAttempts + " 个名字）";
                return false;
            }
            try
            {
                File.Copy(sidecarPath, archivePath, false);     // overwrite:false：撞名/被占用就失败，绝不覆盖已有归档
                outcome.ArchivePath = archivePath;
                return true;
            }
            catch (Exception e)
            {
                outcome.Error = "另存旧侧车失败 " + e.GetType().Name + ": " + e.Message;
                return false;
            }
        }

        /// <summary>同目录唯一临时文件 + 整体替换；替换失败就报错，旧侧车保持原样（不做任何非原子覆盖回退）。
        /// 临时文件用 CreateNew 独占打开、句柄一拿到就认领归本次清理，写一半失败也不会留下没人管的 tmp。</summary>
        private static void WriteAtomically(string path, string content)
        {
            string directory = Path.GetDirectoryName(path) ?? "";
            string temp = Path.Combine(directory, Path.GetFileName(path) + "."
                + Guid.NewGuid().ToString("N") + ".aee-tmp");
            bool tempCreated = false;
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    tempCreated = true;
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                    {
                        writer.Write(content);
                        writer.Flush();
                    }
                }
                if (File.Exists(path))
                    File.Replace(temp, path, null);   // 唯一能覆盖已存在侧车的动作：要么整个换掉，要么原样不动
                else
                    File.Move(temp, path);
                tempCreated = false;
            }
            finally
            {
                // 只清理本次创建的那个临时文件，不碰 path+".tmp" 之类可能属于别人的名字
                if (tempCreated)
                {
                    try { File.Delete(temp); }
                    catch { }
                }
            }
        }

        private static bool SafeFileExists(string path)
        {
            try { return File.Exists(path); }
            catch { return false; }
        }

        internal static bool TryReadBackupFile(string sidecarPath, out LevelMetadataBackup backup, out string error)
        {
            backup = null;
            error = null;
            string text;
            try
            {
                text = File.ReadAllText(sidecarPath, Encoding.UTF8);
            }
            catch (Exception e)
            {
                error = "读取侧车失败 " + e.GetType().Name + ": " + e.Message;
                return false;
            }
            return TryParseBackup(text, out backup, out error);
        }

        internal static string ReadLevelText(string levelPath)
        {
            try
            {
                return File.ReadAllText(levelPath, Encoding.UTF8);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
