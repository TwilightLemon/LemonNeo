# LemonNeo.Desktop 目录迁移方案

日期：2026-10-06。状态：目录迁移已实施；本文保留迁移清单并记录验证结果。

保留 `Controls / ViewModels / Views` 三个顶层目录，在目录内按功能分类，并统一对应模块的名称。原 `Infrastructure` 已按实际职责拆分为 `Services、Utils、Config、Platform`，主题资源类放入已有的 `Resources/Themes`。DesktopHost 和 DesktopContext 各自保持单文件，放在项目根目录。

## 现状与迁移范围

迁移前 `Infrastructure` 有 12 个 C# 文件，`Controls` 有 8 个，`ViewModels` 有 21 个，`Views` 有 16 对 `.axaml / .axaml.cs`。这四个目录中的类型分别集中在各自的单一命名空间中。

迁移前存在以下归属混杂，现已按后文清单拆分：

- `DesktopHost.cs` 曾同时定义启动参数；`DesktopNavigation.cs` 同时定义 `DesktopRoutes`。
- `DesktopPreferences.cs` 集中定义四个配置实体；配置枚举还分散在 `LocalizationService.cs` 和 `ThemePalette.cs` 中。
- `TrackEditorViewModel.cs` 同时定义 `ExternalIdEditor、ProviderOption、RemotePlaylistRow、MetadataMatchRow、PlaybackSourceRow`。其中提供者选项被 Accounts、Online、Tools 使用，远程歌单行实际属于 Online。
- `OnlineViewModel.cs` 内含 `RemotePlaylistCategory`，`PlaybackQueueViewModel.cs` 内含 `QueueTrackViewModel`，`PlaylistDialog.axaml.cs` 内含 `PlaylistEditResult`。

本次迁移以文件归属、必要的类型拆分和引用更新为实施范围。现有程序集与项目依赖边界沿用 [项目与模块结构](module-architecture-plan.md)。功能逻辑、公共类型名称、构造参数、配置格式、资源键和生命周期按现有行为验收。

## 目标目录

```text
LemonNeo.Desktop/
  App.axaml / App.axaml.cs
  Program.cs
  DesktopHost.cs           桌面宿主装配
  DesktopContext.cs        共享运行上下文
  Services/                有生命周期、订阅或运行状态的桌面服务与导航
  Utils/                   图片取色、调色板计算等无订阅工具
    Config/                  桌面存储选项、配置实体、枚举与注册/校验入口
  Platform/                桌面歌词窗口的原生平台行为
  Controls/
    Shared/                跨页面复用控件
    Lyrics/                歌词绘制与布局
    Navigation/            页面切换动画
  ViewModels/
    Shared/                页面基类、曲目列表及提供者选项
    Shell/                 主界面与侧栏状态
    Library/               音乐库、歌单交互
    Search/                曲目搜索
    Online/                在线歌单浏览与导入
    Accounts/              账号认证
    Playback/              播放状态与队列抽屉
    Lyrics/                歌词页与桌面歌词状态
    Metadata/              曲目编辑与歌单元数据匹配
    Tools/                 插件诊断与帮助
    Settings/              设置页与主题选项
    Components/            组件展示页
  Views/
    Shared/                通用确认对话框
    Shell/
    Library/
    Search/
    Online/
    Accounts/
    Playback/
    Lyrics/
    Metadata/
    Tools/
    Settings/
    Components/
  Resources/
    Themes/                现有 AXAML 资源与 ThemeResources.cs
    Languages/             现有语言 JSON
  Properties/
```

服务目录统一使用标准复数名称 `Services`。目前文件数量较少，Services、Utils、Config 暂不再增加子目录；UI 目录按已有功能建立模块。宿主与上下文使用根目录中的独立文件。

## Infrastructure 拆分清单

下表路径均相对于 `src/LemonNeo.Desktop`。

| 原文件 | 目标文件/目录 | 拆分说明 |
| --- | --- | --- |
| Infrastructure/DesktopHost.cs | DesktopHost.cs；Config/DesktopStoragePreferences.cs | 宿主装配保持根目录单文件，Desktop 路径由程序内选择并持久化 |
| Infrastructure/DesktopContext.cs | DesktopContext.cs | 根目录独立文件，承载共享服务入口、初始化状态、歌单集合和通知 |
| Infrastructure/DesktopNavigation.cs | Services/DesktopNavigation.cs；Services/DesktopRoutes.cs | 导航状态、页面缓存、释放及路由常量归在同一目录 |
| Infrastructure/DesktopPreferences.cs | Config/DesktopPreferences.cs；下列四个实体文件 | 注册与校验留在 DesktopPreferences，实体按功能分开 |
| Infrastructure/LocalizationService.cs | Services/LocalizationService.cs；Services/LanguageManager.cs | 生成语言管理器的声明独立；LanguagePreference 移入 Config |
| Infrastructure/AppearanceService.cs | Services/AppearanceService.cs | 配置订阅、主题应用与释放 |
| Infrastructure/ThemeColorService.cs | Services/ThemeColorService.cs | 主题切换、计时器与封面色状态 |
| Infrastructure/ArtworkService.cs | Services/ArtworkService.cs | 缓存图片解码与加载 |
| Infrastructure/ArtworkColorExtractor.cs | Utils/ArtworkColorExtractor.cs | 图片取色算法 |
| Infrastructure/ThemePalette.cs | Utils/ThemePalette.cs | 保留 ThemePalette 与其输入值类型 ThemeColorDefinition；ThemeColorPreset 移入 Config |
| Infrastructure/ThemeResources.cs | Resources/Themes/ThemeResources.cs | ResourceDictionary 与现有主题资源放在一起 |
| Infrastructure/DesktopLyricsPlatform.cs | Platform/DesktopLyricsPlatform.cs | Windows/macOS 原生窗口行为；保留 internal 可见性 |

Config 中具体拆分如下：

| 文件 | 类型/职责 |
| --- | --- |
| DesktopStoragePreferences.cs | DesktopStoragePreferences |
| DesktopPreferences.cs | 四个配置实体的注册、默认值与校验 |
| AppearancePreferences.cs | AppearancePreferences、ThemePreference、ThemeColorPreset |
| LanguagePreferences.cs | LanguagePreferences、LanguagePreference |
| LyricsPreferences.cs | LyricsPreferences、LyricAlignment |
| DesktopLyricsPreferences.cs | DesktopLyricsPreferences |

相关枚举与实体同文件组织，避免为简单枚举增加过多文件。Config 只描述选项与实体，主题计算留在 Utils，主题订阅与应用留在 Services。

## Controls 分类清单

| 目标模块 | 当前文件 |
| --- | --- |
| Controls/Shared | Artwork.cs、EmptyState.cs、PageHeader.cs、FrostedGlass.cs |
| Controls/Lyrics | LyricText.cs、LyricLineViewport.cs、LyricsPresenter.cs |
| Controls/Navigation | SlideFadeTransition.cs |

Artwork 用于多个页面与资源模板，归入 Shared。SlideFadeTransition 保留在 Controls 内的 Navigation 模块，与其页面过渡职责对应。

## ViewModels 与 Views 分类清单

下表所有 View 名称均代表 `.axaml / .axaml.cs` 成对迁移，ViewModel 名称代表对应 `.cs` 文件。

| 模块 | ViewModels | Views |
| --- | --- | --- |
| Shared | PageViewModel、TrackListViewModel、TrackRow；新增独立文件 ProviderOption | ConfirmationDialog |
| Shell | MainViewModel、PlaylistMenuItemViewModel | MainWindow |
| Library | LibraryViewModel、PlaylistDialogViewModel | LibraryView、PlaylistDialog、MoveTrackDialog |
| Search | SearchViewModel | SearchView |
| Online | OnlineViewModel；新增独立文件 RemotePlaylistRow、RemotePlaylistCategory | OnlineView |
| Accounts | AccountsViewModel | AccountsView |
| Playback | PlaybackViewModel、PlaybackQueueViewModel；新增独立文件 QueueTrackViewModel | QueueDrawerView |
| Lyrics | LyricsViewModel | LyricsView、DesktopLyricsWindow |
| Metadata | TrackEditorViewModel、TrackEditorPageViewModel、MatchPlaylistPageViewModel、MetadataSourceOption；新增独立文件 ExternalIdEditor、MetadataMatchRow、PlaybackSourceRow | TrackEditorView、MatchPlaylistView |
| Tools | ToolsViewModel | ToolsView |
| Settings | SettingsViewModel、ThemeColorOption | SettingsView |
| Components | ComponentsViewModel | ComponentsView |

另外从 `PlaylistDialog.axaml.cs` 抽出 `Views/Library/PlaylistEditResult.cs`，与对话框返回值放在同一模块。

归属判断：

- `PageViewModel` 是通用页面基类；`TrackListViewModel / TrackRow` 被 Library、Search、Playback 等功能使用，归入 Shared。
- `ProviderOption` 被 Accounts、Online、Tools 共用，抽出到 Shared，消除这些页面通过编辑器文件取得类型的情况。
- `RemotePlaylistRow / RemotePlaylistCategory` 属于在线歌单展示，归入 Online。
- `MetadataSourceOption` 同时服务歌曲编辑和歌单匹配，两个页面统一归入 Metadata。`MetadataMatchRow` 当前供编辑器与公共模板使用，也归入 Metadata。
- `PlaybackSourceRow` 当前用于编辑器发现的播放源，随编辑器放入 Metadata；`QueueTrackViewModel` 属于队列展示，归入 Playback。
- `PlaylistMenuItemViewModel` 当前仅服务主界面侧栏，归入 Shell。
- 当前 Queue 路由通过 `LibraryViewModel` 展示完整队列，抽屉使用 `PlaybackQueueViewModel`。迁移沿用这套映射；播放栏继续由 MainWindow 承载。

## 命名空间与引用同步

命名空间已随目录更新，例如：

```text
LemonNeo.Desktop.Infrastructure.DesktopHost
  → LemonNeo.Desktop.DesktopHost
LemonNeo.Desktop.Infrastructure.DesktopContext
  → LemonNeo.Desktop.DesktopContext
LemonNeo.Desktop.Infrastructure.AppearancePreferences
  → LemonNeo.Desktop.Config.AppearancePreferences
LemonNeo.Desktop.Infrastructure.LocalizationService
  → LemonNeo.Desktop.Services.LocalizationService
LemonNeo.Desktop.Infrastructure.ThemePalette
  → LemonNeo.Desktop.Utils.ThemePalette
LemonNeo.Desktop.Infrastructure.ThemeResources
  → LemonNeo.Desktop.Resources.Themes.ThemeResources
LemonNeo.Desktop.ViewModels.LibraryViewModel
  → LemonNeo.Desktop.ViewModels.Library.LibraryViewModel
LemonNeo.Desktop.Views.MainWindow
  → LemonNeo.Desktop.Views.Shell.MainWindow
LemonNeo.Desktop.Controls.LyricsPresenter
  → LemonNeo.Desktop.Controls.Lyrics.LyricsPresenter
```

每批迁移同步处理以下引用：

1. C# 的 namespace、using、类型引用，以及 `App.axaml.cs` 的启动装配。
2. 每个 View 的 `x:Class` 与对应 partial class，编译绑定的 `x:DataType` 和各层 DataTemplate 类型。
3. XAML 中的 `xmlns`、`x:Type`、`x:Static`、控件标签、ControlTheme TargetType，以及使用类型前缀的样式选择器。
4. `App.axaml` 对 ThemeResources 的引用；所有页面和 `Styles.axaml / Templates.axaml` 对 LanguageManager 及生成 Keys 的引用。
5. `Controls.axaml` 的 Shared 控件命名空间；`Templates.axaml` 的 Shared、Metadata 类型前缀。
6. `MainWindow.axaml` 中 Shell ViewModel、Shared 控件、Playback 队列模板和 QueueDrawerView 的引用，以及各个 code-behind 的跨模块窗口/对话框引用。
7. `DesktopNavigation.CreatePage` 与 `MainViewModel.CreatePage` 中的页面/模型引用。保留现有显式映射与页面缓存机制。
8. 项目外对旧 UI 命名空间的代码引用。

XAML 一个文件引用多个模块时，使用含义清晰的独立前缀，例如 `sharedVm、shellVm、playbackVm、metadataVm、sharedControls、lyricsControls、playbackViews`。`i18n` 对应 `LemonNeo.Desktop.Services`，App 中的 `theme` 对应 `LemonNeo.Desktop.Resources.Themes`。

新增 Lyrics 模块命名空间与领域类型 `LemonNeo.Core.Lyrics.Lyrics` 同名。LyricsViewModel、TrackEditorPageViewModel 和 LyricsPresenter 使用 `CoreLyrics` 类型别名明确指向原领域类型，字段、属性和方法的实际类型及行为保持一致。

项目使用 SDK 默认文件包含方式，当前 csproj 没有逐项列出 C# 或 View 文件。迁移时检查编译资源发现与生成结果；语言 JSON 的 `AdditionalFiles` 路径及 LinguaManager 的输入路径沿用现状。现有 AXAML 主题文件位置保持原路径，避免增加资源路径迁移。

## 实施顺序

每个阶段都完成引用更新并验证可编译，形成可单独审查的变更。

| 阶段 | 工作内容 | 实施结果 |
| --- | --- | --- |
| 0. 建立基线 | 确认工作区状态，记录 Desktop 构建结果 | 完成 |
| 1. 拆分 Infrastructure | 将 Host、Context 迁入根目录，按清单整理 Services、Utils、Config、Platform、ThemeResources，并拆出相关类型 | 完成；Desktop 构建通过 |
| 2. 整理 ViewModels | 按模块迁移全部模型，拆出混放的展示类型 | 完成；调用点、编译绑定和资源模板已更新，Desktop 构建通过 |
| 3. 整理 Controls / Views | 按模块迁移全部控件与 View 文件对，拆出 PlaylistEditResult | 完成；XAML、控件主题、导航和跨模块引用已更新，构建通过 |
| 4. 清理与验收 | 检查旧目录/命名空间残留、资源加载、页面与对话框行为，补齐文档 | 完成；Infrastructure 目录已移除，静态核对、构建、发布及无界面资源加载检查通过 |

阶段 1 中为保持 Config 的独立性，ThemeColorPreset 和 LanguagePreference 同步迁入对应实体文件。阶段 2 中即使 View 尚未迁移，也要同步更新其引用的新 ViewModel 命名空间。

## 验证与验收标准

实施阶段使用以下命令，执行后记录实际结果：

```powershell
dotnet build src/LemonNeo.Desktop/LemonNeo.Desktop.csproj -p:SkipHostPlugins=true --disable-build-servers -m:1

# 最终验收：完整构建与 Desktop 发布
dotnet build LemonNeo.slnx --disable-build-servers -m:1
dotnet publish src/LemonNeo.Desktop -c Release -o artifacts/publish/desktop --disable-build-servers -m:1
```

最终还需检查：

- 启动应用，依次打开 Library、Search、Online、Accounts、Lyrics、Metadata 编辑/匹配、Tools、Settings、Components，检查页面加载、导航返回及前进。
- 打开完整 Queue 页面、队列抽屉、桌面歌词窗口及 Playlist、MoveTrack、Confirmation 对话框，检查样式与数据模板。
- 切换语言、明暗主题、预设主题、封面取色和减少动画设置，确认主题资源及生成语言管理器正常工作。
- 使用既有配置启动，确认四个 Desktop 配置实体的文件名、字段、枚举值、默认值与校验行为一致。注册继续使用原有实体名，PackageNames.Desktop 沿用现状；当前 JSON 存储按实体名定位文件，因此命名空间迁移本身无需配置文件迁移。
- 搜索生产代码中的 `LemonNeo.Desktop.Infrastructure`，以及旧的精确根命名空间声明/导入和 XAML 命名空间；确认旧文件已移除且没有重复类或资源。
- Windows 验证原生桌面歌词行为；macOS 的 Spaces、全屏共存行为在 macOS 环境验证。以实际完成的验证范围记录结果。

本次实际验证结果：

| 检查 | 结果 |
| --- | --- |
| 分阶段 Desktop 构建 | 零警告、零错误 |
| 完整解决方案构建 | 零警告、零错误 |
| Desktop Release 发布 | 成功；输出为 artifacts/publish/desktop，包含 8 个 Windows 默认插件 |
| 类型与 XAML 内容核对 | 79 个顶层类型的实现主体一致（归一化 CoreLyrics 别名后）；23 个 XAML 文档在归一化类型引用与命名空间后内容等价 |
| 旧结构检查 | Infrastructure 已移除；Controls、ViewModels、Views 下无遗留根目录文件；生产代码无旧精确命名空间引用 |
| Avalonia Headless 检查 | 16 个 View 构造与布局、全部 11 条路由、前进/返回、队列抽屉、语言和主题切换，以及四类已有格式配置 JSON 加载通过 |

迁移辅助脚本、快照与一次性 Headless 检查项目位于已忽略的 `artifacts/desktop-migration`，不纳入产品代码。Headless 检查覆盖资源加载和布局，不代表实机视觉或原生窗口交互验收；Windows 原生焦点/窗口行为及 macOS Spaces、全屏共存仍需在相应实机验证。

## 后续文件归属规则

新增页面及其 ViewModel 使用相同模块名；仅该模块使用的展示类型留在模块内。跨模块复用的页面基类、曲目展示和通用选项放入 Shared。有订阅、计时器、外部服务协调或运行状态的对象放入 Services；纯计算放入 Utils；配置实体与校验放入 Config；原生 API 调用放入 Platform；主题资源放入 Resources/Themes。DesktopHost 和 DesktopContext 保持根目录单文件。

导航和 Shell 是现有跨模块装配入口，允许引用各功能模块。模块间通过 Shared 展示类型及现有服务交互，避免为了取得通用选项而引用其他页面的私有展示类型。
