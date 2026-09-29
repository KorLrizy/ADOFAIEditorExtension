using ADOFAI;
using ADOFAI.LevelEditor.Controls;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace ADOFAIEditorExtension.Utils
{
    /// <summary>
    /// PACL2 兼容层（r148 / PACL2 v2.5.400 IL 已核，见 §43）。
    ///
    /// 背景：PACL2 的 BetterUndoRedo 功能会**替换掉原版的撤销栈**。它用 JAPatch 把原版
    /// `new SaveStateScope(editor, clearRedo, dataHasChanged, skipSaving)` 整体换成自己的
    /// `CustomSaveStateScope(skipSaving, dataHasChanged)`（PACL2.Features.FixChartLoad.CustomSaveState.Scope）：
    ///   · ctor 里 `skipSaving |= !dataHasChanged` ⇒ **只有 dataHasChanged = true 的 scope 才会压撤销点**
    ///     （原版 `SaveStateScope` 无论 dataHasChanged 都会调 `editor.SaveState`，语义完全不同）；
    ///   · 压进去的不是 `levelData.Copy()` 快照，而是增量式的 `DefaultLevelState`：选区（装饰按
    ///     `allDecorations` 下标记成 `selectedDecorationIndices`）、`LevelEvent.set_Item` 的值改动
    ///     （`changedEventValues`）、以及 `EventsArray.Add` / `DecorationsArray.Add/Insert` /
    ///     `List<LevelEvent>.Remove(object)/RemoveAll` 这些**按引用**的增删（`changedEvents`）；
    ///     ⚠ 这些增删记录**只在 `SaveStatePatch.currentState != null`（= 有人还在用原版 SaveStateScope）时才有**，
    ///     而且 JALib 的补丁参数是按**名字**匹配 + **类型必须完全相同**（`JAMethodPatcher.SetupParameter`
    ///     里 `Parameter type mismatch` 直接抛 `PatchParameterException`）⇒ `Remove(Object item)` 对不上
    ///     `List&lt;LevelEvent&gt;.Remove(LevelEvent item)`（`object` ≠ `LevelEvent`）**这条补丁根本打不上**，
    ///     只有 `RemoveAll(Object __instance, Predicate&lt;LevelEvent&gt;)`（`__instance` 免类型检查）是真的生效的；
    ///   · `List<LevelEvent>.Insert` / `List.RemoveAt` **完全没有被 patch** ⇒ 数组**顺序**改动记录不下来；
    ///   · `SaveStatePatch.UndoOrRedo` 回滚增删时统一走 `editor.events.Add/Remove(ev)`（装饰也一样，
    ///     被删的事件重新 Add 会**追加到末尾**，**不带下标**），然后按记录的**下标**在当前 `decorations` 上
    ///     `SelectDecoration` 重新选中（**不先 Deselect**）。
    ///
    /// 后果：我们自己 new 的原版 SaveStateScope 在 PACL2 下
    ///   (1) 会打日志 "Old SaveState called! This should not be called!"；
    ///   (2) 若 dataHasChanged = false ⇒ 根本不压撤销点；
    ///   (3) 若 dataHasChanged = true ⇒ 压的是 DefaultLevelState，我们做的**重排**不会被回滚，
    ///       而它按"下标"重新选装饰 ⇒ 撤销后选中错位（多选/错选）。
    ///
    /// 所以凡是"重排装饰数组"、"移动 levelData.levelEvents" 或"删除事件"，在 BetterUndoRedo 生效时都必须
    /// 改用 PACL2 自己的 scope（<see cref="TryBeginDecorationDragScope"/> / <see cref="TryBeginEventsRemovalScope"/>），
    /// 或者干脆不改顺序（只改归属）。
    ///
    /// 不引用 PACL2 程序集：全部走反射解析 + 缓存，PACL2 未安装 / 未加载 / 该功能关掉时一律退化成
    /// "和以前完全一样"的行为。
    /// </summary>
    internal static class Pacl2Compat
    {
        private const string Pacl2TypeName = "PACL2.Core.Main";
        private const string BetterUndoRedoFieldName = "BetterUndoRedo";
        private const string BetterUndoRedoTypeName = "PACL2.Features.BetterUndoRedo.BetterUndoRedoFeature";
        private const string ActivePropertyName = "Active";
        private const string DecoDragScopeTypeName =
            "PACL2.Features.FixChartLoad.CustomSaveState.Scope.AdofaiScope.DecoDragScope";
        private const string EventsChangeScopeTypeName =
            "PACL2.Features.FixChartLoad.CustomSaveState.Scope.AdofaiScope.EventsChangeScope";
        private const string EventsChangeModeNestedName = "Mode";
        /// <summary>`EventsChangeScope.Mode.Remove`（IL 已核：None=0 / Add=1 / Remove=2 / AutoAdd=3）。</summary>
        private const int EventsChangeModeRemoveValue = 2;

        /// <summary>缓存的反射成员组合。一次解析成功/失败后都不再重跑反射（0 = 还没试过）。</summary>
        private sealed class BetterUndoRedoSlot
        {
            internal FieldInfo InstanceField;      // PACL2.Core.Main.Instance（static）
            internal FieldInfo FeatureField;       // Main.BetterUndoRedo（实例字段）
            // ADOFAI 也有个叫 PropertyInfo 的类型，这里必须写全名
            internal System.Reflection.PropertyInfo ActiveProperty;  // JALib.Core.Feature.Active
        }

        private static volatile BetterUndoRedoSlot betterUndoRedo;
        private static int betterUndoRedoState;     // 0 = 未试过，1 = 解析成功，-1 = 解析失败（PACL2 不在）

        private static volatile Type decoDragScopeType;
        private static int decoDragScopeTypeState;  // 同上
        private static volatile ConstructorInfo decoDragScopeCtor;
        private static volatile FieldInfo cachedDecorationsField;

        private static volatile Type eventsChangeScopeType;
        private static int eventsChangeScopeTypeState;      // 同上
        private static volatile ConstructorInfo eventsChangeScopeCtor;
        private static volatile object eventsChangeModeRemove;   // 装箱后的 Mode.Remove（枚举类型由 PACL2 定，只能反射拿）

        /// <summary>
        /// 装饰拖动用的那个"重排 scope"能不能用（反射是否解析到了类型与 ctor）。
        /// 与 <see cref="IsBetterUndoRedoActive"/> 一起判断：解析失败就绝不能重排。
        /// </summary>
        internal static Type DecoDragScopeType
        {
            get
            {
                if (decoDragScopeTypeState == 0)
                    ResolveDecoDragScope();
                return decoDragScopeType;
            }
        }

        /// <summary>
        /// 每次调用都重新求值（用户可能在游戏里随时开关这个功能）；反射解析本身只做一次。
        /// 任何异常（PACL2 没装 / 未加载 / JALib 类型初始化失败 / 功能被移除）都当成 "false"。
        /// </summary>
        internal static bool IsBetterUndoRedoActive()
        {
            try
            {
                if (betterUndoRedoState == 0)
                    ResolveBetterUndoRedo();
                if (betterUndoRedoState < 0)
                    return false;

                BetterUndoRedoSlot slot = betterUndoRedo;
                object main = slot.InstanceField.GetValue(null);
                if (main == null)
                    return false;      // PACL2 程序集在、但 mod 还没初始化
                object feature = slot.FeatureField.GetValue(main);
                if (feature == null)
                    return false;
                object active = slot.ActiveProperty.GetValue(feature, null);
                return active is bool b && b;
            }
            catch (Exception e)
            {
                // 只报告第一次：反射解析一旦失败就永久负缓存，避免每帧刷日志
                if (betterUndoRedoState >= 0)
                {
                    betterUndoRedoState = -1;
                    Main.Logger?.Log("读取 PACL2 BetterUndoRedo 状态失败，按未启用处理: " + e.Message);
                }
                return false;
            }
        }

        /// <summary>
        /// 开始一次"装饰重排"的 PACL2 原生撤销记录：构造
        /// `PACL2...AdofaiScope.DecoDragScope(PropertyControl_List)`。
        ///
        /// 为什么可以只用**一个**被拖的装饰喂它（IL 已核）：
        ///   · ctor 只做两件事——`List&lt;scrDecoration&gt; cachedDecorations`（原版 private 字段，PACL2 经
        ///     `PrivateReflectionField.GetCachedDecorations` 读 "cachedDecorations"）里每一项取
        ///     `sourceLevelEvent` 存成 `LevelEvent[] decoration`，然后
        ///     `index = scnEditor.instance.decorations.IndexOf(decoration[0])`；两项都只在 ctor 里写一次，
        ///     之后没人再改 ⇒ 索引捕获发生在**我们动手之前**，正是"原位置"。
        ///   · `Undo()`：把 `decoration` 从 `editor.decorations` 和 `scrDecorationManager.instance.allDecorations`
        ///     里按引用 Remove，再 `InsertRange(index, …)` 插回原下标（两边同一下标一起动，和我们
        ///     `MoveInDecorations` 的约定一致），最后设 `propertyControlDecorationsList.lastSelectedIndex = index`
        ///     并 `OnDecorationUpdate()`；`Redo()` 就是再 `Undo()`（基类 `LevelState.Undo` 是虚方法，
        ///     `SaveStatePatch.UndoOrRedo` 走基类引用调用会派发到这里的重写）。
        ///   · 基类 `CustomSaveStateScope(skipSaving: false, dataHasChanged: true)` ⇒ 在 BetterUndoRedo 生效时
        ///     会**压一个撤销点**；`Dispose()` 只是 `changingState--`（ctor 里 +1），没有别的收尾。
        ///
        /// 用法：`using (Pacl2Compat.TryBeginDecorationDragScope(list, dragged)) { …改动顺序… }`
        /// 返回 null = 不能用（调用方**必须放弃重排**，不要退回原版 SaveStateScope）。
        /// </summary>
        /// <param name="list">原版 `scnEditor.propertyControlDecorationsList`（PACL2 只认它的具体类型）。</param>
        /// <param name="dragged">本次真正被拖动的那个场景装饰对象（单个）。</param>
        internal static IDisposable TryBeginDecorationDragScope(PropertyControl_DecorationsList list, scrDecoration dragged)
        {
            if (list == null || dragged == null)
                return null;
            if (!IsBetterUndoRedoActive())
                return null;
            if (DecoDragScopeType == null || decoDragScopeCtor == null || cachedDecorationsField == null)
                return null;

            // PACL2 的 ctor 直接读这个 private 字段；原版拖动时它由 CacheOnStartDrag 填好，
            // 我们这次不是走原版拖动流程，所以先替它摆好，Dispose 时再还原（内部类负责）。
            object previous = null;
            try
            {
                previous = cachedDecorationsField.GetValue(list);
                var items = new List<scrDecoration>(1) { dragged };
                cachedDecorationsField.SetValue(list, items);
                object scope = decoDragScopeCtor.Invoke(new object[] { list });
                return new DecorationDragScope(cachedDecorationsField, list, previous, scope as IDisposable);
            }
            catch (Exception e)
            {
                // 构造失败（PACL2 改版 / 该装饰不在 decorations 里等）：还原现场，让调用方放弃移动
                try
                {
                    cachedDecorationsField.SetValue(list, previous);
                }
                catch { }
                Main.Logger?.Log("PACL2 装饰重排 scope 创建失败，本次不改变顺序: " + e.Message);
                return null;
            }
        }

        /// <summary>`using` 包装：先 Dispose PACL2 的 scope（= 撤销点收尾），再把 cachedDecorations 还原。</summary>
        private sealed class DecorationDragScope : IDisposable
        {
            private readonly FieldInfo field;
            private readonly object list;
            private readonly object previous;
            private readonly IDisposable scope;

            internal DecorationDragScope(FieldInfo field, object list, object previous, IDisposable scope)
            {
                this.field = field;
                this.list = list;
                this.previous = previous;
                this.scope = scope;
            }

            public void Dispose()
            {
                // using 只会 Dispose 一次；两件事都各自兜异常，保证 cachedDecorations 一定被还原
                try
                {
                    scope?.Dispose();
                }
                catch (Exception e)
                {
                    Main.Logger?.Log("PACL2 装饰重排 scope 收尾失败: " + e.Message);
                }
                try
                {
                    field.SetValue(list, previous);
                }
                catch { }
            }
        }

        // ------------------------------------------------------------------ 事件删除（多选剪切）

        /// <summary>
        /// 开始一次"删除若干事件"的 PACL2 原生撤销记录：反射构造
        /// `PACL2...AdofaiScope.EventsChangeScope(List&lt;LevelEvent&gt;, EventsChangeScope.Mode.Remove)`
        /// （该类型与 ctor 都是 public，Mode 是它的 public 嵌套枚举；IL 已核）。
        ///
        /// **为什么必须这么做**（§43，IL 已核）：
        ///  · `scnEditor.RemoveEvents` 在 PACL2 下是**整体替换**的
        ///    （`PACL2.Features.Timeline.TimelineEnabledInEditorSubfeature.ScnEditorRemoveEventsReplace`，
        ///    `[JAPatch(scnEditor, "RemoveEvents", Replace)]`）。替换体里真正记撤销点的只有
        ///    `new EventsChangeScope(events, Mode.Remove)`（FixChartLoad 生效时）——
        ///    它**只在 `changingState == 0` 时才把自己压进 `SaveStatePatch.undoStates`**
        ///    （`CustomSaveStateScope(skipSaving, dataHasChanged)`：`skipSaving |= !dataHasChanged`，
        ///    然后 `if (!skipSaving && initialized && changingState == 0) undoStates.Add(this)`）。
        ///  · 我们以前在外面套了一个**原版** `SaveStateScope`：它的 ctor 立刻把 `changingState` 抬到 1
        ///    ⇒ PACL2 自己的那个 scope 被静默跳过（不压栈、不记录），而我们这个原版 scope 又只会走
        ///    PACL2 的兜底路径 `SaveStatePatch.SaveState`（日志里那句 "Old SaveState called!"），
        ///    压的是 `DefaultLevelState`。那个兜底状态的 Undo 回滚增删时统一是
        ///    `editor.events.Add(ev)`（**追加到 `levelData.levelEvents` 末尾**，见 `SaveStatePatch.UndoOrRedo`
        ///    的 `changedEvents` 循环）——它**根本没有下标信息**，所以永远回不到原位置；
        ///    而它的记录又只在 `SaveStatePatch.currentState != null` 时生效（Add/Remove/RemoveAll 前缀后缀
        ///    都以它为开关，`SaveStateScopeDispose` 会在第一个原版 scope 收尾时把它搬走并置 null），
        ///    一旦记录没落进我们压的那个状态，Ctrl+Z 就什么也恢复不了（事件丢了）。
        ///
        /// **为什么它能精确还原（IL 已核）**：
        ///  · ctor → `SetEvents(events, mode)` → `ScopeUtil.SetupEventsCache(editor.events, editor.decorations,
        ///    events, out _actions, out _decorations)`：**在删除之前**按引用命中 `editor.events`
        ///    （`scnEditor.get_events()` 就是 `levelData.levelEvents` 本身）里的每一项，记下它的**真实下标**；
        ///    `_actions` 按扫描顺序天然升序。
        ///  · `Undo()`（Mode.Remove）→ `Add()` → `ScopeUtil.AddEventIndexed`：从位置 0 开始往新数组里填，
        ///    下一个待插事件的下标 == 当前位置就插它（并接着插连续的下标），否则从"当前列表里剩下的事件"
        ///    按原顺序取一个填进去 ⇒ 每个被删事件都回到它被删时的下标，多选/非连续下标都成立
        ///    （`len = 当前数 + 缓存数`，两边的相对顺序都不变）。
        ///  · `Redo()`（Mode.Remove）→ `Remove()` → `ScopeUtil.RemoveEventIndexed`：按引用 `RemoveAll`
        ///    重新删掉这批事件（下标无关，所以重做稳定）。
        ///  · 收尾也由它自己做：`AfterUndoOrRedo` 里 33 号类型事件走 `RemakePath(true, true)`、否则
        ///    `ApplyEventsToFloors()`，再选砖 / `SetCacheSelectedEventType` / `ShowTabsForFloor` /
        ///    `ShowEventIndicators`（时间轴模式下则只置刷新标志）。
        ///
        /// 用法：`using (Pacl2Compat.TryBeginEventsRemovalScope(list)) { …删除… }`
        /// 返回 null = 不能用（PACL2 不在 / BetterUndoRedo 关着 / 反射解析失败）⇒
        /// 调用方**必须**保持原来的 "原版 SaveStateScope + 删除" 行为，一个字都不要变。
        ///
        /// **前提**：调用时 `changingState` 必须是 0（别把这个 scope 套进别的 scope 里），
        /// 否则它同样不会压撤销点；这也是原版 `SaveStateScope` 自己的前提。
        /// </summary>
        /// <param name="events">本次要删掉的事件（用它们在构造瞬间的下标做缓存）。</param>
        internal static IDisposable TryBeginEventsRemovalScope(List<LevelEvent> events)
        {
            if (events == null || events.Count == 0)
                return null;
            if (!IsBetterUndoRedoActive())
                return null;
            if (eventsChangeScopeTypeState == 0)
                ResolveEventsChangeScope();
            if (eventsChangeScopeTypeState < 0 || eventsChangeScopeType == null
                || eventsChangeScopeCtor == null || eventsChangeModeRemove == null)
                return null;

            try
            {
                object scope = eventsChangeScopeCtor.Invoke(new[] { events, eventsChangeModeRemove });
                return scope == null ? null : new EventsRemovalScope(scope as IDisposable);
            }
            catch (Exception e)
            {
                // 构造失败（PACL2 改版 / ctor 里 SetEvents 出问题）：退回原版路径，至少行为和以前一样
                Main.Logger?.Log("PACL2 事件删除 scope 创建失败，退回原版 SaveStateScope: " + e.Message);
                return null;
            }
        }

        /// <summary>`using` 包装：收尾异常不往外冒（和 <see cref="DecorationDragScope"/> 同款）。</summary>
        private sealed class EventsRemovalScope : IDisposable
        {
            private readonly IDisposable scope;

            internal EventsRemovalScope(IDisposable scope)
            {
                this.scope = scope;
            }

            public void Dispose()
            {
                try
                {
                    scope?.Dispose();
                }
                catch (Exception e)
                {
                    Main.Logger?.Log("PACL2 事件删除 scope 收尾失败: " + e.Message);
                }
            }
        }

        // ------------------------------------------------------------------ 反射解析（各只跑一次）

        private static void ResolveBetterUndoRedo()
        {
            lock (typeof(Pacl2Compat))
            {
                if (betterUndoRedoState != 0)
                    return;
                try
                {
                    Type main = Reflections.GetType(Pacl2TypeName);
                    if (main == null)
                    {
                        betterUndoRedoState = -1;
                        return;
                    }
                    FieldInfo instance = main.GetField("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    FieldInfo feature = main.GetField(BetterUndoRedoFieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (instance == null || feature == null)
                    {
                        betterUndoRedoState = -1;
                        return;
                    }
                    // Active 定义在 JALib.Core.Feature 上（PACL2 只是继承），从字段类型往上找
                    Type featureType = feature.FieldType;
                    System.Reflection.PropertyInfo active = null;
                    for (Type t = featureType; t != null && active == null; t = t.BaseType)
                        active = t.GetProperty(ActivePropertyName,
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                    if (active == null || !active.CanRead || active.PropertyType != typeof(bool))
                    {
                        betterUndoRedoState = -1;
                        return;
                    }
                    betterUndoRedo = new BetterUndoRedoSlot
                    {
                        InstanceField = instance,
                        FeatureField = feature,
                        ActiveProperty = active,
                    };
                    betterUndoRedoState = 1;
                }
                catch (Exception)
                {
                    betterUndoRedoState = -1;   // PACL2 不在 / JALib 初始化失败：静默退化
                }
            }
        }

        /// <summary>
        /// 解析 `EventsChangeScope` 的类型、`(List&lt;LevelEvent&gt;, Mode)` ctor 与装箱后的 `Mode.Remove`。
        /// 任一项拿不到（PACL2 不在 / 版本换了类型名 / 该功能被移除）⇒ 永久负缓存，调用方退回原版路径。
        /// </summary>
        private static void ResolveEventsChangeScope()
        {
            lock (typeof(Pacl2Compat))
            {
                if (eventsChangeScopeTypeState != 0)
                    return;
                try
                {
                    Type scope = Reflections.GetType(EventsChangeScopeTypeName);
                    if (scope == null)
                    {
                        eventsChangeScopeTypeState = -1;
                        return;
                    }
                    Type mode = scope.GetNestedType(EventsChangeModeNestedName, BindingFlags.Public | BindingFlags.NonPublic);
                    if (mode == null || !mode.IsEnum || !typeof(IDisposable).IsAssignableFrom(scope))
                    {
                        eventsChangeScopeTypeState = -1;
                        return;
                    }
                    ConstructorInfo ctor = scope.GetConstructor(
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                        null, new[] { typeof(List<LevelEvent>), mode }, null);
                    if (ctor == null)
                    {
                        eventsChangeScopeTypeState = -1;
                        return;
                    }
                    eventsChangeScopeType = scope;
                    eventsChangeScopeCtor = ctor;
                    eventsChangeModeRemove = Enum.ToObject(mode, EventsChangeModeRemoveValue);
                    eventsChangeScopeTypeState = 1;
                }
                catch (Exception)
                {
                    eventsChangeScopeTypeState = -1;
                }
            }
        }

        private static void ResolveDecoDragScope()
        {
            lock (typeof(Pacl2Compat))
            {
                if (decoDragScopeTypeState != 0)
                    return;
                try
                {
                    Type scope = Reflections.GetType(DecoDragScopeTypeName);
                    if (scope == null)
                    {
                        decoDragScopeTypeState = -1;
                        return;
                    }
                    ConstructorInfo ctor = scope.GetConstructor(
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                        null, new[] { typeof(PropertyControl_List) }, null);
                    FieldInfo cached = AccessTools.Field(typeof(PropertyControl_DecorationsList), "cachedDecorations");
                    if (ctor == null || cached == null || !typeof(IDisposable).IsAssignableFrom(scope))
                    {
                        decoDragScopeTypeState = -1;
                        return;
                    }
                    decoDragScopeCtor = ctor;
                    cachedDecorationsField = cached;
                    decoDragScopeType = scope;
                    decoDragScopeTypeState = 1;
                }
                catch (Exception)
                {
                    decoDragScopeTypeState = -1;
                }
            }
        }
    }
}
