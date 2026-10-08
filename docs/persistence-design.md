# Hosting 统一配置与 Library 持久化

本文描述已实现的结构。直接采用新配置目录与实体，不读取或迁移旧配置。
这里的播放队列指当前播放会话；用户创建、导入的 Playlists 继续属于 Music Library。

## 持久化边界

| 数据 | 管理者 | 存放位置 | 保存方式 |
| --- | --- | --- | --- |
| 用户歌单、索引和镜像关系 | `LibraryService`、`JsonLibraryRepository` | 独立 Library 目录 | 编辑后增量保存及退出保存 |
| Application、Desktop、Console、插件的普通配置 | Hosting 的 `AppSettingsService` | Configuration 下的包目录 | Apply 内存，固定周期与退出保存 |
| 当前队列、恢复进度、音量 | `PlaybackSessionService` 负责业务，配置服务负责持久化 | Application 包配置目录 | 同上 |
| 插件认证凭据 | `ICredentialVault` | Data/Credentials 下的包目录 | 保留加密与显式提交 |
| 图片、歌词、音频和插件缓存 | 各缓存服务 | Cache 目录 | 保留现有机制 |

Library 的仓储和数据结构保持现状：`library.json`、`playlists/<id>.json` 以及现有镜像元数据。
Library 在内存中记录发生变化和删除的歌单；保存只重写变化的歌单文件，并在 Library 元数据变化时更新索引。
保存期间再次发生的变化会保留为待保存状态，由下一次保存提交。
Desktop 首次启动直接使用平台默认 Library，之后可在设置中打开其他 Library；缓存目录也可独立选择。配置、播放状态、凭据和插件目录使用固定的应用路径。

```text
<LibraryDirectory>/
  library.json
  playlists/<playlist-id>.json

<ConfigurationDirectory>/
  LemonNeo.Application/
    PlaybackQueueState.json
    PlaybackResumeState.json
    PlaybackPreferences.json
  LemonNeo.Desktop/
    DesktopStoragePreferences.json
    AppearancePreferences.json
    LanguagePreferences.json
    LyricsPreferences.json
    DesktopLyricsPreferences.json
  LemonNeo.Console/
    StartupPreferences.json
    CommandHistoryState.json
  LemonNeo.Plugin.Netease/
    AccountSettings.json
  LemonNeo.Plugin.QqMusic/
    AccountSettings.json
```

文件直接存实体的 JSON，字段使用 camelCase。首次加载缺失的文件采用默认值；未修改的默认配置不会创建文件。
同名实体可以存在于不同包中，互不影响。账号 ID、昵称、头像等相关字段组成一个实体，不逐字段分配文件。
包名规则见 [包名与配置实体命名](package-naming.md)。

Configuration、Data、Cache 的平台默认根目录沿用 `AppHost.CreateDefaultPaths()`。
可用 `LEMONNEO_CONFIG_HOME`、`LEMONNEO_DATA_HOME`、`LEMONNEO_CACHE_HOME`、`LEMONNEO_LIBRARY_HOME` 覆盖。
Desktop 不解析命令行路径；Library 和 Cache 均在程序内选择，插件目录保持固定。Console 继续支持 `--library`、`--plugins` 和 `--startup`。

## 依赖与生命周期

`LemonNeo.Hosting.AppHost` 为 Desktop 和 Console 统一注册配置、插件、播放、Library 和缓存服务，管理启动与关闭顺序。
`DesktopHost` 只补充桌面服务与宿主选项。Console 在共享 Hosting 上注册命令服务、启动脚本与宿主播放器。
`StartupCommands` 与 `StartupPreferences` 属于 `LemonNeo.Console`；共享 Hosting 与 Desktop 不登记或读取启动脚本。

`AppSettingsService` 的实现位于 `LemonNeo.Infrastructure.Settings`；`IAppSettingsService`、`ISettingsScope` 位于
`LemonNeo.Abstractions.Settings`，Application 和插件无需依赖 Desktop 或 JSON 实现。
Hosting 持有唯一服务实例与保存循环。包入口只绑定包名，不创建计时器、写盘队列或保存任务。

Desktop 创建唯一的 `DesktopHost` 后，先通过该 Host 的配置服务加载包含
`DesktopStoragePreferences` 在内的 Desktop 实体，再于运行时初始化开始前设置实际 Library 与 Cache。
Desktop 不再为存储路径维护独立 JSON Store。

启动顺序：

1. Desktop 在 UI 线程加载其预登记实体，应用主题、语言并确定 Library/Cache；Shell 此时已经显示但不可交互。
2. 后台并行加载 Library，并登记 Application 播放实体、恢复队列、进度和音量。
3. 两个分支完成后加载插件，由插件主动登记自己的实体。
4. 启动配置服务的固定周期保存，并在 UI 线程发布 Library 与 Playback 快照。

Console 随后加载自己的启动脚本并执行命令，再进入交互；Desktop 直接打开图形界面。

正常关闭顺序：

1. 宿主停止交互并等待已接受操作，记录桌面歌词窗口尺寸。
2. Hosting 停止配置保存循环，等待进行中的周期保存退出。
3. 停止播放监控，等待播放命令，捕获最终状态，再停止播放器。
4. 执行插件关闭回调，允许回调更新其配置。
5. 保存 Library，统一保存有变化的配置。
6. 释放服务、插件配置注册、订阅与程序集。

关闭保存不使用交互操作的取消令牌。Hosting 汇总关闭错误，并继续完成其他清理步骤。
Desktop 将关闭错误写入诊断日志；Console 报告错误并返回失败退出码。

## 配置入口

实体使用不可变 record，集合使用不可变数据或独立且不再修改的快照。
业务代码通过 Apply 替换实体，不直接修改 Get 返回的对象。
每个包内，一个类型对应一个显式实体文件名；重复文件名或将同一类型登记到不同文件会报错。
类型重命名时可以保留原实体文件名。

```csharp
var settings = host.Settings.ForPackage(PackageNames.Desktop);

await settings.RegisterAsync(
    "DesktopLyricsPreferences",
    new DesktopLyricsPreferences(),
    value => value with { FontSize = Math.Clamp(value.FontSize, 16, 48) });

using var subscription = settings.Subscribe<DesktopLyricsPreferences>(ApplyDesktopLyrics);
ApplyDesktopLyrics(settings.Get<DesktopLyricsPreferences>());

settings.Apply<DesktopLyricsPreferences>(current => current with { FontSize = 32 });
```

`RegisterAsync<T>` 登记默认值与可选校验函数，并读取实体一次。`Get<T>` 随后只访问内存。
缺失文件采用默认值；无效 JSON 或读取错误明确报错，实体不能 Apply，也不会用默认值覆盖原文件。

需要在 UI 初始化期间绑定默认值时，可以先同步 `Register<T>`，之后由 Hosting 的 `LoadAsync` 加载。
加载完成前可 Get 和 Subscribe，不能 Apply。加载值与默认值不同时通知该实体的订阅者，但不标记用户修改。

Apply 同步校验、比较、替换内存值并标记变化，然后在内存锁外通知订阅者。
相同值直接返回；不读写文件、不创建保存 Task。通知仅覆盖同一包的同一实体。
通知在调用线程同步执行；Desktop 的配置控件和启动加载使用 UI 线程。

主题效果由 `AppearanceService` 订阅 `AppearancePreferences`；本地化服务只订阅 `LanguagePreferences`。
歌词页面分别订阅 `LyricsPreferences`、`DesktopLyricsPreferences`。
调整桌面歌词字号不会触发本地化或 Library 通知，因此不会清空歌单行、重新加载封面。

## 统一保存

服务只有一个后台循环，默认每 **10 秒**保存有变化的实体。

- 连续修改同一实体只提交最新快照；同一次保存中每个有变化的实体至多写一次。
- 无变化的实体不写文件；音量和进度的变化不重写队列。
- 保存使用一个串行锁。短暂的内存锁只用于捕获快照与修改序号，序列化和 I/O 在锁外执行。
- 写入期间仍可 Apply。提交成功只确认该快照的序号，之后产生的修改继续待保存。
- 单个实体失败时，其状态继续待保存；其他实体仍可提交。后台记录错误，下个周期重试。
- 取消保存不确认未提交的快照；随后可以继续保存。
- JSON 先写临时文件，再替换目标文件。成功或失败都不发布配置业务通知。

设置控件只 Apply。ViewModel 不维护保存队列、保存任务列表、配置防抖或保存完成回填。
音量操作中的串行逻辑只协调异步播放器调用，配置变化本身立即进入内存。

显式保存也统一调用 `host.Settings.SaveAsync()`。Console 的 `save` 同时保存 Library、捕获播放状态、刷新配置；
Console 的启动脚本保存命令也通过此入口。默认启动脚本存于 Console 包的 `StartupPreferences`；
Console 的 `--startup <file>` 显式选择外部脚本时，继续直接读写该文件。Desktop 不提供启动脚本选项或编辑界面。
Console 历史存于 `CommandHistoryState`，记录命令只更新内存，仍保留去重、1000 条上限与凭据命令过滤。

固定周期允许合并高频变化；强制结束进程可能丢失最近一个周期尚未提交的修改。正常退出立即保存最终状态。
共享目录下的服务采用单进程所有者，不提供多进程配置合并协议。

## Application 播放实体

| 实体 | 内容 | 变化来源 |
| --- | --- | --- |
| `PlaybackQueueState` | 队列项、当前项 ID、播放模式、随机顺序与游标、播放请求 | 队列编辑、切歌、模式变化 |
| `PlaybackResumeState` | 队列项 ID、进度、上次播放状态 | 时间线、seek、暂停等 |
| `PlaybackPreferences` | 音量 | 音量修改 |

`PlaybackSessionService` 操作播放器与队列，再更新相应配置实体。播放监控仍按 250ms 刷新时间线，
只捕获小型恢复实体与播放偏好；结构变化时才捕获完整队列。配置保存由统一 10 秒周期负责。
`CaptureStateAsync` 只将当前会话捕获到内存，不进行文件写入。

恢复时先加载队列，恢复实体的队列项 ID 与当前项相同时才使用保存进度；不匹配则从零开始。
这样即使两个实体在中断前只提交了其中一个，也不会将另一首歌的进度用于当前项。
启动恢复队列和音量，不自动播放。用户开始播放当前项时，再应用恢复进度。
退出捕获进度发生在播放器 Stop 之前，避免 Stop 清零时间线后保存零进度。

已删除 Library 下单独播放状态仓储与旧即时保存路径。

## 插件配置

`IPluginContext.Settings` 已绑定 manifest ID。插件不需要构造路径或再次指定包名。
网易云、QQ 音乐插件各自登记 `AccountSettings`，账号显示信息通过一次 Apply 更新；Cookie 等秘密继续使用 CredentialVault。

插件关闭回调先运行，随后保存其包并释放类型注册与订阅。
`ReleasePackageAsync` 保存期间拒绝该包的新 Apply；保存失败保留包以便重试，成功后原入口失效。
重新加载插件可以重新登记类型并读取原实体。重复插件包名会被拒绝，避免两个插件共用配置入口。
JSON 序列化元数据使用每次操作独立的选项，配置存储不长期缓存可卸载插件的类型。
程序集采用 CLR 协作式卸载。回收验收允许 System.Text.Json 的短期反射访问器缓存过期，
再验证加载上下文可回收；框架缓存的过期机制见 [dotnet/runtime 源码](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Text.Json/src/System/Text/Json/Serialization/Metadata/ReflectionEmitCachingMemberAccessor.Cache.cs)。

## 验收

项目尚未发布，本次重构不保留历史数据迁移或兼容入口，并移除了原有测试项目。验收以完整构建、静态残留检查和 Desktop 实机操作为准。

```sh
dotnet build LemonNeo.slnx -p:SkipHostPlugins=true --disable-build-servers -m:1
```

Desktop 实机检查首次启动使用默认 Library、打开已有 Library、切换 Library、切换 Cache、取消选择、无效目录提示，以及重启后恢复所选路径。
