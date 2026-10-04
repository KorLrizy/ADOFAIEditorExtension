[English](README_EN.md) | 简体中文

# ADOFAI Editor Extension

一个面向 *A Dance of Fire and Ice* 官方关卡编辑器的 Unity Mod Manager 模组：**装饰栏分组** 与 **事件分页器直选弹窗**。

> An editor extension for ADOFAI: a grouped decoration list, plus a pager jump-to-event popup with multi-select and batch editing.

## 功能

### 装饰栏分组

左侧装饰列表按类型 / `tag` / 自定义规则自动分组，不用再在几百行平铺列表里翻找。

- **自动分组**：按装饰类型或按 `tag` 归类；面板底部工具栏有一个"分组方式"小按钮，循环
  不分组 → 按类型 → 按标签 → 自定义（设置页里保留同样的开关作为默认值）。
- **自定义分组**：在设置页里命名分组、绑定 `tag`，也可以**把装饰行直接拖到某个组上**完成归组，
  组内与跨组的拖动排序同样有效。
- **多选整批拖动**：在多选状态下拖动其中一行（`ctrl` 逐个加选 / `shift` 区间选择），
  整批选中项会**保持相对顺序**一起插到落点；落在组头上插到组尾，跟随方块旁显示 `×N`。
- **组头操作**：组头行分成四块 `[箭头] [组名(数量)] [眼睛] [锁]` ——
  箭头折叠/展开；组名全选该组并在右侧打开原版多选属性面板（改一个属性 = 应用到组内全部装饰）；
  眼睛/锁整组切换可见与锁定（与原版逐行按钮同一套 API，不另造状态）。
- **组头颜色**：设置页每个自定义分组行都带一个原版 RGBA 颜色控件（默认透明 = 不着色），
  组头底色随之着色，组名 / 数量 / 三角箭头 / 眼睛 / 锁按底色自动取对比色（亮底黑字、暗底原色）；
  颜色与分组一起存进 `Settings.json`，装饰与事件两套分组各自独立。
- **可选写入关卡**：手动归属可以只活在本次会话里，也可以写进关卡文件（装饰键名 `aeeGroupDeco`、
  事件键名 `aeeGroupEvent`），默认不写入，见[注意事项](#注意事项与关卡兼容性)。

### 事件分页器直选弹窗

同类型、同位置的事件在原版里堆叠成 `◀ 1/3 ▶` 一个分页器，只能一格一格翻。**直接点击这段分页器文本**，
会弹出一个可滚动的直选列表（原版前后箭头翻页不受影响）。

- **点一行直接跳**：跳到该事件（走 `tab.eventIndex` + `ShowPanel`，不硬改 `selectedEvent`），弹窗保持打开，
  连点连切——替代原版的前后箭头。
- **按 `tag` / `eventTag` 自动分组**：组头可折叠展开，可点组名**整组选中**、也可以把行拖到组上改归属。
- **多选**：`ctrl` 逐个加/减选，`shift` 以锚点做区间选择（同一段范围反复 shift 可加可减），点组名全选该组。
- **批量编辑**：多选后右侧属性面板进入批量模式，任一改值都会写回全部选中事件；
  各选中事件**取值不同的字段**标红 `(Mixed)`，且**仍可输入**——填一个新值即对全体生效。
  关窗后批量视图常驻，回到弹窗继续改仍然生效。
- **弹窗内复制 / 剪切 / 粘贴**：`ctrl-shift-c/x`、`ctrl-alt-c/x`、`ctrl-shift-v` 按原版语义操作事件
  （单选 = 当前事件，多选 = 全部选中事件），并在左上角提示"已复制 N / 已剪切 N / 已粘贴 N"。
  原版在弹窗期间会停掉所有快捷键，这几条由本模组接手。
- **弹窗内撤销 / 重做**：弹窗开着时 `ctrl-z` 撤销、`ctrl-shift-z` 重做，一次按键只走一步
  （文本输入框有焦点时不拦截，保留文本框自己的撤销）。撤销后的选中集与当前事件按内容重新对应，
  列表、右侧属性面板与装饰栏一起刷新；开启 PACL2 的 BetterUndoRedo 时走它的撤销栈，
  分组归属与事件顺序的重排也能一步撤销 / 重做。
- **事件备注**：见下节，备注显示在事件行的右侧（超长截断），悬停看完整内容。

### 事件备注

给每个**事件类型**注册一个可见的 String 属性 `aeeNote`，于是**原版**事件面板自动多出一行"事件备注"输入框
（输入、保存、读档全部走原版 `LevelEvent.Encode` / `Decode`，本模组没有为它写任何输入控件）。
备注会写进 `.adofai`，并显示在分页器直选列表事件行的**右侧**（超长截断），鼠标**悬停**该行弹出完整备注。
空备注不会落盘（保存前把空的 `aeeNote` 摘掉），所以文件里不会凭空多出一堆空串。

### 模组设置页

模组自带一个 UI 标签页（放在编辑器的原版标签页序列里），用于开关上述功能、选择默认分组方式、
管理装饰 / 事件两套互相独立的自定义分组。实现上把本模组伪装成一个"设置型关卡事件"注入游戏原生设置面板，
字段值存于该标签页自己的 `LevelEvent`，因此**同一进程内切换关卡设置不丢**；
同时持久化到 mod 目录下的 `Settings.json`，**重启游戏后仍然保留**。
设置项包括：装饰分组总开关、分组方式、组头是否显示数量、分页器直选列表开关、编辑目标（装饰分组 / 事件分组）、
以及"把分组归属写进关卡文件"开关（默认关闭；只决定保存关卡时是否写入分组归属）。

## 环境要求

- **游戏**：A Dance of Fire and Ice **v3.3.1**（r148，Unity 6000.3）（PC / Steam，64 位）；v2.9.8 请使用 [`v2` 分支](https://github.com/KorLrizy/ADOFAIEditorExtension/tree/v2)的版本
- **模组管理器**：[Unity Mod Manager](https://github.com/sinai-dev/UnityModManager) **0.32.5+**
- 运行时依赖由游戏与 UMM 提供：Harmony（`Mods/UnityModManager/0Harmony.dll`）、Newtonsoft.Json——**无需额外安装**
- 编译期依赖 .NET Framework 4.8 参考程序集，仅[从源码构建](#从源码构建)时需要

## 安装

**方式一：Release 压缩包（推荐）**

1. 从本仓库的 **Releases** 页面下载 `ADOFAIEditorExtension_v1.2.0_game-v3.3.1.zip`
   （同一个 Release 下的 `..._game-v2.9.8.zip` 是给游戏 v2.9.8 的包，**不要下错**）
2. 打开游戏里的 Unity Mod Manager → **Mod** → **Install mod** → 选择该 zip
3. 启动游戏，在 UMM 的 Mod 列表可见 "ADOFAI Editor Extension"，勾选启用

**方式二：手动放置**

把 zip 解压到游戏目录下的 `Mods/ADOFAIEditorExtension/`，最终形如：

```
<游戏目录>\Mods\ADOFAIEditorExtension\
├── ADOFAIEditorExtension.dll
├── Info.json
└── Localizations.json
```

启动游戏后在 UMM 中启用即可。编辑器界面在**进入关卡编辑器后**才会出现本模组的分组与弹窗入口。

## 从源码构建

工程是老式（非 SDK 风格）MSBuild 工程，`ToolsVersion 15.0`，目标 **.NET Framework 4.8 / C# 8.0**，
引用游戏 `Managed` 目录下的 dll。在 **Developer Command Prompt for VS**（或任意能找到 `MSBuild.exe` 的 shell）里：

```
MSBuild ADOFAIEditorExtension.csproj -p:Configuration=Release -p:GameDir="D:\steam\steamapps\common\A Dance of Fire and Ice"
```

可选参数：

- `-p:GameDir="..."` — 游戏安装目录（**必须**指定为你本机路径，工程里默认值是作者本机路径）；
  编译所需的 `Assembly-CSharp.dll` 等均从 `<GameDir>\A Dance of Fire and Ice_Data\Managed` 读取。
- `-p:DeployToGame=false` — 关闭构建后自动部署，只产出 `bin\Release\ADOFAIEditorExtension.dll`。
  默认 `true` 时会把 dll + `Info.json` + `Localizations.json` 复制到 `<GameDir>\Mods\ADOFAIEditorExtension\`。
- `-p:FrameworkPathOverride="C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8"`
  — 已装 4.8 targeting pack 的机器可指定真正的参考程序集目录。
  未安装的机器无需额外操作：工程已保留 `FrameworkPathOverride` 回退（指向 `C:\Windows\Microsoft.NET\Framework64\v4.0.30319`，
  游戏侧 dll 以 `Private=false` 引用，API 一致）。

## 注意事项与关卡兼容性

- **只有开了"把分组归属写进关卡文件"才会有额外键**。此时关卡会带上
  `aeeGroupDeco`（装饰分组归属）、`aeeGroupEvent`（事件分组归属），事件备注则始终会写入 `aeeNote`。
- **原版能正常读**带这些键的关卡（未知键被忽略），但**用无 mod 的原版保存一次，这些键就会丢失**（分组归属、备注都没了）。
  协作交付关卡时，请保证收发双方都装了本模组，或始终用本模组的编辑器保存。
- 建议：**分享关卡前先在带 mod 的环境下存一份**；这些键属于扩展数据，不承担玩法逻辑，丢了只影响分组与备注显示。
- **批量编辑本身不写入任何东西**——它只是把改动落到已有事件字段上。分组归属与备注的落盘统一由上面那个开关（默认关闭）
  与"备注非空"这两条控制，因此**默认配置下关卡文件与原版完全一致**。
- 分页器直选列表是自建弹窗（克隆原版 `okPopupContainer`（缺失时退回 `largeOkPopupContainer`）+ 自建 `ScrollRect`，
  行克隆装饰列表自己的行 prefab `listItemPool.itemPrefab`），因为原版 `ShowNotificationPopup` 的滚动高度按正文文本定死、且点选项不会关窗。
  如果游戏更新改动了这些预制体，弹窗可能失效——遇到时请在 Issues 里附游戏版本号与复现步骤。
- 设置项存于标签页自己的 `LevelEvent`，同一进程内跨关卡保留；并持久化到 mod 目录下的 `Settings.json`
  （用户数据，不随 mod 发布），**重启游戏后仍然保留**。删掉该文件即恢复默认值。
- **与 PACL2 共存**：检测到 PACL2 的 BetterUndoRedo 通过纯反射接入其撤销栈（未安装 PACL2 时完全无影响）。
  开启后分页器弹窗内的排序、跨组拖动会走 PACL2 的作用域，保证一步撤销 / 重做不会复制出重复事件。

## 目录结构

```
ADOFAI Editor extension\
├── ADOFAIEditorExtension.csproj   工程文件（老式非 SDK 工程，ToolsVersion 15.0，net48 / C# 8.0）
├── Info.json                      UMM 识别模组的清单（Id / DisplayName / EntryMethod 等）
├── Localizations.json             本地化文本，Dictionary<key, Dictionary<语言, 文本>> 格式，缺失时回退英文
├── Startup.cs                     UMM 入口，转发到 Main.Setup
├── Main.cs                        生命周期 + 标签页声明 + 设置值读取 + 编辑器重启
├── CustomTab.cs                   标签页描述数据类
├── PrefabProperties.cs            面板字段声明（含自定义分组的动态行，装饰/事件各一套）
├── Localization.cs                读取 Localizations.json（Newtonsoft.Json）
├── EnumCollection\
│   ├── AutoGroupMode.cs           自动分组方式枚举
│   └── GroupEditTarget.cs         设置页"编辑目标"两态枚举（装饰分组 / 事件分组）
├── PropertyCollection\            面板字段类型（Property 基类 + Bool/Enum/InputField/Button/Color）
├── Settings\SettingsStore.cs       模组设置持久化（mod 目录下的 Settings.json）
├── Utils\                         Reflections（私有成员访问）、LevelEventEX（UpdatePanel）、Popup、Pacl2Compat（PACL2 撤销兼容，纯反射）
├── Patches\
│   ├── EditorIntegrationPatches.cs   GCS 注入 / ShowPanel 接管 / 本地化与枚举键改写
│   └── PropertyPanelPatches.cs       Export 按钮渲染 + 字段值变化回调
├── Features\DecoGrouping\
│   ├── DecoGroupState.cs          槽位模型 + 折叠集合 + 组→装饰表 + 自定义分组行 + 手动归属
│   ├── DecoGroupRenderer.cs       分组构建与 ApplyUpdateList 接管渲染 + 头行对象池 + 落点指示线
│   ├── DecoGroupActions.cs        组头四个动作 + 拖动落点（改归属 + 组内/跨组排序）
│   ├── DecoGroupDrag.cs           装饰行拖到分组 = 改归属 + 插到落点位置
│   ├── DecoGroupModeButton.cs     面板底部工具栏里的"分组方式"循环按钮
│   └── DecoGroupingPatches.cs     Harmony 补丁集
├── Features\PagerList\
│   ├── PagerListController.cs     分页器点击入口 + 直选列表弹窗（分组/折叠/拖拽排序/多选/批量写回/备注列）
│   ├── PagerClipboard.cs          弹窗内的复制/剪切/粘贴事件（复用原版剪贴板格式与快捷键表）
│   ├── PagerUndo.cs               弹窗内撤销 / 重做（原版 SaveStateScope 与 PACL2 两条路径）
│   └── PagerListPatches.cs        Harmony 补丁集（InspectorTab.Init / 选区与面板切换时关闭 / 弹窗快捷键）
├── Features\Notes\
│   └── EventNote.cs               事件备注（aeeNote 属性注册 + 空备注不落盘的 Encode 后置补丁）
├── Properties\AssemblyInfo.cs     程序集信息与版本（1.2.0.0）
├── LICENSE                        MIT
└── README.md
```

## 本地化

界面文本全部走 `Localizations.json`，随游戏语言自动切换，目前覆盖简体中文、繁体中文、英文、韩文；
缺词条时回退英文。新增文案请在该文件里补齐各语言，不要在代码里硬编码。

## License

MIT License — 详见 [LICENSE](LICENSE)。
