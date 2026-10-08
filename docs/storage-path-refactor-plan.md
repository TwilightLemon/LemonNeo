# Music Library 与缓存路径重构规划

## 1. 本轮范围

本轮只解决两个用户可见问题：

1. Desktop 用户可以在应用内新建、打开和切换 Music Library 目录。
2. Desktop 用户可以在应用内修改缓存目录。

同时完整移除 Git 同步。当前不保留同步接口、占位入口或未来插件扩展点；以后重新设计同步时再按当时需求引入。

Desktop 不再通过命令行参数配置任何路径。Console 是面向高级用户的独立宿主，保留现有
`--plugins`、`--library` 和 `--startup` 参数解析。

## 2. 产品原则

- Music Library 是一个普通目录，包含当前的 `library.json` 和 `playlists/`；不增加专业化的项目概念。
- UI 使用“Music Library”而不是要求普通用户理解 Vault、工作区或仓库。
- 启动时优先打开上次成功使用的 Library；找不到时显示一个简洁的选择页。
- 用户可以“新建 Music Library”或“打开已有 Music Library”。
- 仅维护当前路径和少量最近路径，不实现 Vault ID、marker、锁管理、只读模式或复杂工作区设置。
- DesktopHost、插件、播放器和账号服务在应用生命周期内保持单实例；路径切换只更新宿主持有的存储位置。
- 插件上下文提供当前插件缓存目录；插件应在操作开始时读取路径，不长期缓存旧路径。
- 配置、credentials、Data Protection keys 和 Plugins 继续使用固定目录，不提供路径设置。

## 3. 完整删除 Git 同步

### 3.1 删除源码

- 删除 `src/LemonNeo.Infrastructure/Sync/GitLibrarySyncProvider.cs`。
- 删除 `src/LemonNeo.Abstractions/Sync/SyncContracts.cs`。
- 删除 `src/LemonNeo.Application/Sync/SyncService.cs`。
- 删除 `src/LemonNeo.Application/Sync/ILibraryMergeService.cs`。
- 删除 `src/LemonNeo.Application/Sync/LibraryMergeService.cs`。
- 移除 Infrastructure 和 Application 中对应的 DI 注册与 namespace 引用。

三方合并器当前没有同步之外的使用方，因此一起删除，不保留无调用者的通用化抽象。

### 3.2 删除 Desktop 入口

- 删除 ToolsViewModel 中所有同步状态、命令、冲突列表和 provider 逻辑。
- 删除 ToolsView 中同步位置、连接、立即同步及冲突 UI。
- 删除 ToolsView 的同步文件夹选择事件；code-behind 恢复为纯初始化。
- 删除中英文资源中的 `Sync*` 与 `Conflict*` 文案。
- 删除 Desktop 同步行为测试。

Tools 页中的手动保存/刷新不再作为“同步”页存在。Library 已在编辑及退出时保存；“重新加载 Library”如果仍有
价值，移到 Settings 的 Music Library 卡片中，并在有未保存操作时先执行保存。

### 3.3 删除 Console 入口

- 从 CommandCatalog 删除 `sync`。
- 从 CommandCompletionService 删除同步补全。
- 从 CommandProcessor 删除 SyncService 依赖和 `SyncAsync`。
- 删除帮助和示例中的 `sync status/connect/now`。

这不会改变 Console 的启动参数；Console 仍可用 `--library` 指定 Library。

### 3.4 删除测试与文档

- 删除 `GitLibrarySyncTests.cs`。
- 删除 ArchitectureTests 中默认 Git provider 与 `.git` 初始化断言。
- 删除 `docs/git-library-sync.md`。
- 从 prototype、module architecture、persistence、package naming 文档中删除同步设计和 Git 模块说明。
- 全仓检索 `GitLibrarySync`、`ILibrarySync`、`SyncService`、`SyncConflict`、`sync now` 等残留。

不会删除用户 Library 中已经存在的 `.git` 目录。那是用户磁盘数据，主程序只停止读取和修改它。

## 4. 路径分类

### 4.1 固定目录

由 `IAppPathProvider` 按平台确定，不出现在 Desktop 设置中：

| 内容 | 说明 |
| --- | --- |
| Configuration | 应用和插件的非敏感设置 |
| Credentials | 受保护的插件凭据 |
| Data Protection keys | 凭据加密密钥 |
| Plugins | 固定的插件加载根目录 |
| Data | 其他必须固定的持久数据 |

建议给 `IAppPathProvider` 增加固定的 `PluginDirectory`，由 Desktop 和默认 Host 使用；不再从 DesktopOptions 传入。
如果发布包的插件必须位于程序安装目录，该路径仍由 path provider 统一返回，不能由命令行覆盖。

### 4.2 默认目录

`IAppPathProvider.CacheDirectory` 和 `MusicLibraryDirectory` 在本轮保留，作为 Desktop 未自定义时的实际默认值，
不能继续代表 Desktop 当前实际路径。这样可以降低对 Console 和现有测试的改动范围。

### 4.3 Desktop 当前目录

新增固定配置实体：

```csharp
public sealed record DesktopStoragePreferences
{
    public string? LibraryDirectory { get; init; }
    public string? CacheDirectory { get; init; }
    public string[] RecentLibraryDirectories { get; init; } = [];
}
```

- `LibraryDirectory == null`：使用旧默认 Library（如果有效），否则显示选择页。
- `CacheDirectory == null`：使用 `IAppPathProvider.CacheDirectory`。
- 最近路径规范化为绝对路径、忽略大小写去重（按平台规则）、最多保留 5 个。
- 只在 Library 成功加载后更新当前路径和最近列表。

该实体保存在固定 Configuration 目录中。DesktopHost 先使用平台默认路径创建，再通过同一个
`IAppSettingsService` 加载该实体，并在运行时数据初始化前设置实际路径，不建立独立配置读写入口。

## 5. 最小运行时结构

不拆出复杂的 Vault 宿主体系。Desktop 使用一个应用生命周期内长期存活的 Host，存储控制器只负责验证和切换目录：

```text
DesktopApplicationService
  ├─ 固定 IAppPathProvider
  ├─ DesktopHost + IAppSettingsService（应用生命周期内唯一实例）
  ├─ MainViewModel（先显示 Loading Shell）
  └─ HostStorageLocations（唯一的当前 Library/Cache 路径状态）
```

建议模型：

```csharp
public sealed class HostStorageLocations
{
    string LibraryDirectory { get; }
    string CacheDirectory { get; }
}
```

Library repository 和各缓存服务在每次操作开始时读取当前位置，并在该次操作中固定路径快照。Plugins 根目录直接来自固定
path provider。Console 继续根据自己的 ConsoleOptions 构造相同的 HostOptions。

## 6. Desktop 启动与命令行

### 6.1 删除配置参数

- 删除 `DesktopOptions.cs`。
- 删除 `Program.Arguments`。
- `App.OnFrameworkInitializationCompleted` 不再调用任何 Desktop 参数解析。
- 不支持 `--library`、`--plugins`、`--cache` 等 Desktop 配置参数。
- Avalonia 接收到的原始 args 不作为设置来源；以后若支持文件关联，应走单独的系统激活逻辑。

### 6.2 启动流程

1. 创建固定 path provider，并以平台默认路径创建唯一 DesktopHost。
2. 注册 Desktop 配置，创建 MainViewModel 并显示不可交互的 Loading Shell。
3. 在 UI 线程通过 Host 的配置服务加载主题、语言和 DesktopStoragePreferences。
4. 解析并验证 Cache 与 Library，在 Host 运行时初始化前写入 HostStorageLocations。
5. 后台并行初始化 Playback 与 Library，随后加载插件。
6. 回到 UI 线程刷新 Library、Playback 和当前页面，再开放交互。
7. 配置的 Library 无效时在同一 Host 中回退默认 Library，不重建 ViewModel 或配置服务。

启动选择窗口只需要：

- “新建 Music Library”
- “打开 Music Library”
- 最近使用列表

不提供 Vault 管理页、标签、描述、重命名或删除磁盘数据。

## 7. Music Library 打开与切换

### 7.1 新建

- 用户选择一个空目录，或在文件夹选择器中创建新目录。
- 验证目录可写且不存在 `library.json`。
- 显式写入一个空的 `library.json` 和 `playlists/`，不能依赖“首次编辑后才出现文件”。
- 成功后才登记为当前 Library。

如果目录非空且没有 `library.json`，默认拒绝初始化，并提示选择空目录，避免把 Library 混入任意文件夹。

### 7.2 打开

- 用户选择包含 `library.json` 的目录。
- 在替换当前内存 Library 前先加载并验证新 repository；失败时当前 Host 与插件保持不变。
- JSON 损坏、格式版本不支持、引用歌单缺失或目录不可读时显示错误，当前 Library 保持不变。
- 打开成功后把路径移到最近列表首位。

### 7.3 切换

1. 暂时禁用当前编辑操作。
2. 使用现有 `LibraryService` 保存旧 Library。
3. 更新宿主持有的 Library 位置，并在同一个 repository/service 实例中加载新 Library。
4. 刷新桌面内存模型并保存路径设置；播放器、插件、账号和窗口 DataContext 均保持不变。
5. 加载失败时恢复旧位置和旧 Library，不改变插件生命周期。

切换入口放在 Settings 的 Music Library 卡片和应用菜单中。窗口标题可以显示当前 Library 文件夹名，但不引入
额外的 Vault 名称字段。

## 8. Cache 路径修改

Settings 的 Storage 卡片增加：

- 当前缓存路径。
- “更改缓存位置”。
- “恢复默认位置”。
- 可选的“打开缓存文件夹”和“清理缓存”。

更改流程：

1. 选择目录。
2. 规范化绝对路径，并用临时探测文件验证可创建和可写。
3. 拒绝 Music Library、Configuration、Credentials、Keys 或 Plugins 本身及其敏感子目录。
4. 更新 `HostStorageLocations` 并保存 DesktopStoragePreferences。
5. 后续缓存操作使用新目录；已在内存中的缓存值和正在进行的操作可自然完成。
6. 不自动搬迁或删除旧缓存。

以下服务必须统一使用 Host 当前的 CacheDirectory：

- `ImageCache`
- `LyricsCache`
- `PlaybackAudioCache`
- `IPluginContext.CacheDirectory`

`ApplicationRegistration` 和 `PluginDataStoreFactory` 读取 Host 当前缓存位置。插件可以使用上下文提供的私有缓存目录
访问文件系统，但配置和 credentials 仍通过宿主接口持久化，且不受缓存目录切换影响。

## 9. HostOptions 调整

目标结构保持简单：

```csharp
public sealed record HostOptions(
    string PluginDirectory,
    string LibraryDirectory,
    string CacheDirectory);
```

- Desktop：PluginDirectory 来自固定 path provider；Library/Cache 来自 DesktopStoragePreferences 和选择 UI。
- Console：Plugin/Library 继续来自 ConsoleOptions；Cache 暂时使用平台默认值。
- tests：显式构造三个临时目录。

后续如果固定 PluginDirectory 已完全收敛到 path provider，再单独从 HostOptions 移除；本轮不为纯粹性扩大改造范围。

## 10. 兼容迁移

- Preferences 不存在且旧默认 `MusicLibraryDirectory/library.json` 存在：直接登记并打开，不搬数据。
- 默认 Library 不存在或为空：直接创建并打开。
- Cache 未设置：继续使用原 CacheDirectory，已有缓存立即可用。
- 用户选择新 Cache：不搬旧文件，不删除旧文件。
- 已有 Library 内的 `.git` 保持原样，但程序不再访问。
- 现有 Configuration、credentials、keys 和 Plugins 路径保持不变。

## 11. 实施顺序

### 阶段 A：删除 Git 同步

删除同步源码、DI、Desktop/Console 入口、资源、测试和文档，并清理所有引用。

验收：全仓没有 GitLibrarySync、ILibrarySync、SyncService 或用户可见同步命令；Library 功能测试继续通过。

### 阶段 B：显式 Host 路径

给 HostOptions 增加初始 CacheDirectory；Host 创建 `HostStorageLocations`，Library、三个内置缓存和插件 cache
在每次操作开始时读取当前路径。保持 Console 参数行为不变。

验收：同一测试进程可以用不同 HostOptions 创建完全隔离的 Library 与 Cache。

### 阶段 C：DesktopStoragePreferences 与无参数启动

把 storage preferences 纳入 Host 的统一配置服务；删除 DesktopOptions、Program.Arguments 和独立 JSON Store；
接入平台默认路径兼容逻辑。

验收：Desktop args 不能改变任何目录；Console args 仍能改变其 Plugin/Library 路径。

### 阶段 D：轻量 Library 选择

在设置中实现打开 Library 和错误反馈；增加空 Library 显式初始化 API。首次启动不弹选择器。

验收：首次启动直接使用默认 Library；无效 Library 不覆盖数据。

### 阶段 E：应用内切换与 Cache 设置

实现 DesktopApplicationService，在 Settings 接入 Library 与 Cache 的原位切换。

验收：切换前数据已保存；新路径立即生效；失败可恢复原 Library；插件和播放器实例不变；所有缓存写入选定目录。

### 阶段 F：文档和清理

更新持久化、路径和用户帮助文档；移除过期架构描述；执行全仓残留扫描。

验收：Release 全量构建零 warning，完整测试通过，文档和 UI 对目录所有权的描述一致。

## 12. 测试矩阵

- Git 删除：DI 无同步服务、Tools 无同步控件、Console 无 sync 命令、用户 `.git` 不被删除。
- Desktop 参数：`--library`/`--plugins` 不被解析；启动不依赖 Program.Arguments。
- Console 参数：`--plugins`、`--library`、`--startup` 行为保持原样。
- 新建 Library：空目录初始化、非空普通目录拒绝、不可写目录报错。
- 打开 Library：正常、损坏 index、缺失 playlist、格式不支持、路径不存在。
- 最近记录：去重、排序、上限、丢失路径移除。
- 切换：旧 Library 保存、新 Library 加载、播放不中断、插件不重载、失败回滚。
- Cache：默认路径、自定义路径、恢复默认、不可写路径、敏感目录拒绝。
- 隔离：Library A/B 数据不串用；Cache A/B 文件不串用；credentials 路径始终不变。
- 持久化：现有增量写入、原子 index 更新和删除歌单测试全部保留。

## 13. 完成定义

1. 所有 Git 同步代码、入口、测试和文档已删除，且不会删除用户磁盘上的 `.git`。
2. Desktop 不解析配置命令行参数，Console 现有参数保持兼容。
3. 首次启动直接创建或打开默认 Music Library，用户可以在设置中改用其他路径。
4. 用户可以从 Settings 切换 Music Library 和 Cache 路径。
5. Library/Cache 在现有 Host 内切换，插件、播放器、账号和系统媒体会话不重建，失败不丢失当前 Library。
6. 配置、credentials、keys 与 Plugins 始终位于固定目录。
7. 全量构建、测试和残留扫描通过。
