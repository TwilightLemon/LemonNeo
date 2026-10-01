# Lemon Neo Prototype

## 1. 项目定位

Lemon Neo 是一个跨平台、插件驱动的音乐客户端。

软件本体不直接实现 QQ 音乐、网易云音乐、YouTube Music 等第三方平台逻辑，而只提供：

- 统一音乐数据模型
- 本地音乐库
- 歌单管理
- 播放队列与播放器抽象
- 插件运行时
- 外部音乐平台能力抽象
- 本地 Library 持久化
- Git 等方式的跨设备同步
- 后续 Avalonia UI 所需的 Application Service

核心理念：

> Host owns experience, plugins own integration.

即：

- Lemon Neo 本体控制统一的数据模型、交互体验和播放行为。
- 插件负责与外部服务通信。
- 外部平台只是 Lemon Neo 的数据源和播放源。
- Lemon Neo Library 才是用户真正拥有的数据。

第一阶段只实现 Console + Class Library 原型，不实现 Avalonia UI。

---

# 2. 原型目标

本阶段重点验证以下架构：

1. Plugin Discovery / Loading
2. Music Provider Capability 模型
3. Track / Album / Artist / Playlist 统一模型
4. Provider-specific ID 与 Canonical Track 的关系
5. Playlist 本地管理
6. Music Provider 搜索
7. Playlist Import
8. PlaybackSource Resolution
9. 播放队列抽象
10. 本地 JSON/YAML 持久化
11. Git Sync Provider 抽象
12. 模拟 Git Pull / Merge / Push 流程
13. 插件与 Host 解耦

暂时不要实现：

- Avalonia UI
- 实际音频解码
- DRM
- 真实 QQ/网易 API
- OAuth/WebView 登录
- 真正的插件 Sandbox
- CRDT
- 实时多设备同步

使用 Mock Plugin 验证整个架构。

---

# 3. Solution 结构

建议建立：

```text
LemonNeo.sln

src/
├── LemonNeo.Console/
├── LemonNeo.Core/
├── LemonNeo.Application/
├── LemonNeo.Playback/
├── LemonNeo.Persistence/
├── LemonNeo.Plugin.Abstractions/
├── LemonNeo.Plugin.Runtime/
└── LemonNeo.Sync.Abstractions/

plugins/
├── LemonNeo.Plugin.MockMusic/
└── LemonNeo.Sync.MockGit/
```

各项目职责如下。

## LemonNeo.Core

只定义与平台、UI、插件加载无关的核心领域模型。

包含：

```text
Track
Album
Artist
Playlist
PlaylistItem
MusicItemId
ExternalMusicId
MusicLibrary
```

不得依赖其他 LemonNeo 项目。

---

## LemonNeo.Plugin.Abstractions

提供第三方 Music Plugin 可以引用的稳定 API。

插件只能依赖：

```text
LemonNeo.Core
LemonNeo.Plugin.Abstractions
```

不得引用：

```text
LemonNeo.Application
LemonNeo.Console
LemonNeo.Persistence
LemonNeo.Plugin.Runtime
```

---

## LemonNeo.Plugin.Runtime

负责：

```text
Plugin Discovery
Plugin Manifest
AssemblyLoadContext
Plugin Lifecycle
Plugin Capability Registration
Provider Registry
```

---

## LemonNeo.Application

负责业务逻辑：

```text
SearchService
PlaylistService
LibraryService
PlaybackResolver
ImportService
SyncService
```

UI 将来只调用这一层。

Application 不应该直接调用任何具体插件类型。

---

## LemonNeo.Playback

定义播放相关抽象：

```text
IPlayer
IPlaybackQueue
PlaybackSource
PlaybackRequest
AudioQuality
```

原型阶段实现：

```text
MockPlayer
```

只打印当前播放内容，不真正输出音频。

---

## LemonNeo.Persistence

负责本地 Library 持久化。

第一版使用：

```text
System.Text.Json
```

存储到：

```text
LemonLibrary/
```

不要使用数据库。

---

## LemonNeo.Sync.Abstractions

定义：

```text
ILibrarySyncProvider
SyncResult
SyncStatus
SyncConflict
```

---

# 4. Music ID 模型

不要让 Track 只有一个普通 string ID。

定义：

```csharp
public readonly record struct MusicItemId(
    string ProviderId,
    string RemoteId);
```

例如：

```text
qq:003abc
netease:1983758293
local:43d553...
```

为了跨平台匹配，再定义：

```csharp
public sealed record ExternalMusicId(
    string Namespace,
    string Value,
    string? Kind = null);
```

例如：

```text
isrc:USUG11901452
musicbrainz:xxxx
spotify:track-id:xxxx
qq:song-mid:xxxx
netease:song-id:xxxx
```

Track 可以包含多个 External ID。`Namespace + Kind` 定义 ID 的平台语义；
Provider 的身份不属于 ID，也不参与跨插件匹配。

---

# 5. Track 模型

设计类似：

```csharp
public sealed record Track
{
    public required MusicItemId Id { get; init; }

    public required string Title { get; init; }

    public IReadOnlyList<ArtistRef> Artists { get; init; }
        = [];

    public AlbumRef? Album { get; init; }

    public TimeSpan? Duration { get; init; }

    public Uri? ArtworkUri { get; init; }

    public IReadOnlyList<ExternalMusicId> ExternalIds { get; init; }
        = [];
}
```

注意：

Track 表示音乐实体。

不要把：

```text
Playback URL
Cookie
Token
HTTP Header
```

放进 Track。

Track 本身不声明能否播放。可播放性是一次动态解析的结果，由
`IPlaybackProvider` 根据 Track、用户凭据和当前网络状态决定。

---

# 6. Playlist 模型

Lemon Neo Playlist 是第一等公民。

定义：

```csharp
public sealed class Playlist
{
    public Guid Id { get; init; }

    public string Name { get; set; }

    public string? Description { get; set; }

    public List<PlaylistItem> Items { get; } = [];

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }
}
```

PlaylistItem：

```csharp
public sealed class PlaylistItem
{
    public Guid Id { get; init; }

    public required Track Track { get; init; }

    public string SortKey { get; set; }

    public DateTimeOffset AddedAt { get; init; }
}
```

PlaylistItem.Id 和 Track.Id 必须分离。

同一首歌允许在同一个歌单出现多次。

---

# 7. Playlist 排序

不要单纯依赖数组 Index。

PlaylistItem 使用：

```text
SortKey
```

第一版可以简单实现 fractional ordering。

例如：

```text
1000
2000
3000
```

在 A 与 B 中间插入：

```text
1500
```

如果间隔过小，再执行 Normalize。

将排序逻辑封装：

```text
IPlaylistOrderingService
```

不要散落在业务层。

---

# 8. Plugin 基础模型

不要设计一个巨型：

```text
IMusicPlugin
```

Plugin 本身只提供元信息：

```csharp
public interface ILemonPlugin
{
    PluginDescriptor Descriptor { get; }

    ValueTask InitializeAsync(
        IPluginContext context,
        CancellationToken cancellationToken);

    ValueTask ShutdownAsync(
        CancellationToken cancellationToken);
}
```

Music Provider：

```csharp
public interface IMusicProvider
{
    string Id { get; }

    ProviderDescriptor Descriptor { get; }
}
```

不同能力使用独立 Interface。

---

# 9. Provider Capabilities

至少实现以下接口。

## Search

```csharp
public interface ISearchProvider
{
    Task<SearchResult> SearchAsync(
        SearchRequest request,
        CancellationToken cancellationToken = default);
}
```

Search 只返回临时结果，不承担按 ID 查询。需要根据 Provider 原生 ID
重新取得歌曲时使用独立能力：

```csharp
public interface ITrackLookupProvider
{
    Task<Track?> GetTrackAsync(
        string trackId,
        CancellationToken cancellationToken = default);
}
```

---

## Playlist

```csharp
public interface IPlaylistProvider
{
    Task<RemotePlaylist?> GetPlaylistAsync(
        string playlistId,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<RemotePlaylistSummary>
        GetUserPlaylistsAsync(
            CancellationToken cancellationToken = default);
}
```

---

## Playback

```csharp
public interface IPlaybackProvider
{
    Task<PlaybackSource?> ResolvePlaybackAsync(
        Track track,
        PlaybackRequest request,
        CancellationToken cancellationToken = default);
}
```

---

## Lyrics

```csharp
public interface ILyricsProvider
{
    Task<Lyrics?> GetLyricsAsync(
        Track track,
        CancellationToken cancellationToken = default);
}
```

---

## Authentication

定义接口，但原型可以不实现完整登录：

```csharp
public interface IAuthenticationProvider
{
    AuthenticationState State { get; }

    Task<IReadOnlyList<AuthMethod>>
        GetAvailableMethodsAsync(
            CancellationToken cancellationToken = default);
}
```

---

# 10. Capability 模型

一个 Provider 不一定实现所有功能。

例如：

```text
QQMusicProvider

ISearchProvider
IPlaylistProvider
IPlaybackProvider
ILyricsProvider
IAuthenticationProvider
```

Lyrics 插件可能只实现：

```text
ILyricsProvider
```

不要规定：

```text
一个 Plugin = 一个音乐平台
```

应允许：

```text
一个 Plugin = 一组 Capability
```

---

# 11. Plugin Context

Plugin 不得直接访问应用 ServiceProvider。

定义受控：

```csharp
public interface IPluginContext
{
    ILoggerFactory LoggerFactory { get; }

    IHttpClientFactory HttpClientFactory { get; }

    IPluginStorage Storage { get; }

    ICredentialVault Credentials { get; }
}
```

第一版：

```text
IPluginStorage
ICredentialVault
```

可以只做内存 Mock。

但是 API 边界必须建立。

---

# 12. Plugin Manifest

每个插件目录：

```text
plugins/
└── LemonNeo.Plugin.MockMusic/
    ├── plugin.json
    └── LemonNeo.Plugin.MockMusic.dll
```

plugin.json 示例：

```json
{
  "id": "org.lemonneo.mockmusic",
  "name": "Mock Music",
  "version": "1.0.0",
  "entryAssembly": "LemonNeo.Plugin.MockMusic.dll",
  "minimumHostVersion": "0.1.0",
  "permissions": [
    "network"
  ]
}
```

Plugin Runtime 使用：

```text
AssemblyLoadContext
```

动态加载。

不要在 Console 工程中直接项目引用 MockMusic。

目的是验证插件确实可以动态加载。

---

# 13. PlaybackSource

Playback Plugin 不应该只返回 URL。

定义：

```csharp
public abstract record PlaybackSource;
```

至少实现：

```csharp
public sealed record HttpPlaybackSource : PlaybackSource
{
    public required Uri Uri { get; init; }

    public IReadOnlyDictionary<string, string> Headers { get; init; }
        = new Dictionary<string, string>();

    public DateTimeOffset? ExpiresAt { get; init; }
}
```

以及：

```csharp
public sealed record FilePlaybackSource(
    string Path) : PlaybackSource;
```

预留：

```text
HlsPlaybackSource
DashPlaybackSource
StreamPlaybackSource
```

暂时不需要实现。

---

# 14. 播放器抽象

定义：

```csharp
public interface IPlayer
{
    PlayerState State { get; }

    Track? CurrentTrack { get; }

    Task PlayAsync(
        Track track,
        PlaybackSource source,
        CancellationToken cancellationToken = default);

    Task PauseAsync();

    Task ResumeAsync();

    Task StopAsync();
}
```

原型：

```text
ConsolePlayer
```

播放时输出：

```text
Now Playing:
Track: Mock Song
Provider: mock
Playback Source:
https://example.com/test.mp3
```

不实际播放。

---

# 15. Playback Resolver

Application 层实现：

```text
PlaybackResolver
```

流程：

```text
Track
  ↓
查看原始 Provider
  ↓
查找 IPlaybackProvider
  ↓
ResolvePlaybackAsync
  ↓
PlaybackSource
  ↓
IPlayer
```

后续预留 fallback：

```text
Track
 ↓
Canonical IDs
 ↓
多个 PlaybackProvider
 ↓
Candidate Sources
 ↓
优先级选择
```

但第一版只实现直接 Provider Resolution。

---

# 16. Lemon Library

本地存储结构：

```text
LemonLibrary/
├── library.json
└── playlists/
    ├── favorites.json
    └── coding.json
```

`MusicLibrary` 不维护独立的全局 Tracks 集合。每个 PlaylistItem 直接保存
完整 Track 快照（包括 ArtworkUri 和 ExternalIds），搜索结果则保持临时状态，
避免搜索导致 Library 无界增长。

不要把以下内容存进 Git Library：

```text
Cookie
Token
Credentials
Audio Cache
Image Cache
Temporary Playback URL
Temporary Application State
```

---

# 17. Library Repository

定义：

```csharp
public interface ILibraryRepository
{
    Task<MusicLibrary> LoadAsync(
        CancellationToken cancellationToken = default);

    Task SaveAsync(
        MusicLibrary library,
        CancellationToken cancellationToken = default);
}
```

实现：

```text
JsonLibraryRepository
```

优先保证：

- 文件内容可读
- Git diff 友好
- 字段顺序稳定
- 使用缩进 JSON
- 不产生无意义随机变化

---

# 18. 外部 Playlist 与 Lemon Playlist

必须区分：

```text
RemotePlaylist
```

和：

```text
Playlist
```

RemotePlaylist 是 Provider 的数据。

Playlist 是 Lemon Neo 自己的数据。

支持三个操作语义。

## Import

```text
Remote → Lemon
```

一次性导入。

## Export

```text
Lemon → Remote
```

一次性输出。

当前原型不实现远端写入，只预留接口。

## Mirror

```text
Lemon ↔ Remote
```

持续绑定同步。

当前原型定义：

```text
PlaylistMirror
```

数据模型即可，不实现真实双向同步。

---

# 19. Sync Provider

Git 不应该写死在 Library Core 中。

定义：

```csharp
public interface ILibrarySyncProvider
{
    string Id { get; }

    Task<SyncResult> PullAsync(
        CancellationToken cancellationToken = default);

    Task<SyncResult> PushAsync(
        CancellationToken cancellationToken = default);

    Task<SyncStatus> GetStatusAsync(
        CancellationToken cancellationToken = default);
}
```

以后允许实现：

```text
Git
WebDAV
OneDrive
iCloud
LocalFolder
```

---

# 20. Git Sync 原型

创建：

```text
LemonNeo.Sync.MockGit
```

第一版可以：

- 模拟 Repository
- 模拟 Pull
- 模拟 Push
- 打印提交过程
- 不真正调用 Git

但同时提供：

```csharp
public interface IGitBackend
{
    Task FetchAsync(...);

    Task PullAsync(...);

    Task CommitAsync(
        string message,
        ...);

    Task PushAsync(...);
}
```

后续真实实现优先使用系统：

```text
git
```

而不是立即绑定 LibGit2Sharp。

---

# 21. Git Sync 流程

未来真实流程设计为：

```text
Local changes
     ↓
Save Library
     ↓
git fetch
     ↓
Compare revisions
     ↓
Semantic Merge
     ↓
Save merged library
     ↓
git add
     ↓
git commit
     ↓
git push
```

不要直接依赖 Git textual merge 来解决歌单冲突。

---

# 22. Semantic Merge

定义：

```text
ILibraryMergeService
```

输入：

```text
Base
Local
Remote
```

输出：

```text
Merged
Conflicts
```

第一版实现简单策略。

Playlist 根据：

```text
Playlist.Id
```

匹配。

PlaylistItem 根据：

```text
PlaylistItem.Id
```

匹配。

例如：

- Local 新增 item，Remote 未修改 → 保留
- Remote 新增 item，Local 未修改 → 保留
- 双方新增不同 item → 都保留
- 一边删除、一边修改 → 产生 Conflict
- 同一个 PlaylistName 双方修改 → Conflict

不要依赖 JSON 文本 Diff。

---

# 23. Sync Conflict

定义：

```csharp
public sealed record SyncConflict
{
    public required string Path { get; init; }

    public required SyncConflictType Type { get; init; }

    public object? BaseValue { get; init; }

    public object? LocalValue { get; init; }

    public object? RemoteValue { get; init; }
}
```

例如：

```text
PlaylistNameConflict
PlaylistItemConflict
DeleteModifyConflict
OrderConflict
```

Console 中打印冲突即可。

暂时不用交互解决 UI。

---

# 24. Mock Music Plugin

实现一个：

```text
MockMusicPlugin
```

包含固定数据：

```text
Artists
Albums
Tracks
Playlists
```

例如：

```text
Track 1
Neo Sunrise
Lemon Band

Track 2
Glass Horizon
Lemon Band

Track 3
Midnight Compile
Digital Cat
```

支持：

```text
Search
GetPlaylist
GetUserPlaylists
ResolvePlayback
GetLyrics
```

PlaybackSource 返回：

```text
https://example.invalid/audio/track1.flac
```

只用于演示。

---

# 25. Console Prototype

启动时：

```text
Lemon Neo Prototype
===================

Loading plugins...

[Plugin]
Mock Music 1.0.0
Provider: mock

Capabilities:
Search
Playlist
Playback
Lyrics

Library loaded.
3 playlists.
42 tracks.
```

提供简单命令：

```text
help
plugins
providers
search <keyword>
remote-playlists <provider>
import <provider> <playlist-id>
playlists
show-playlist <id>
create-playlist <name>
add <playlist-id> <provider> <track-id>
remove <playlist-id> <item-id>
play <provider> <track-id>
queue
sync status
sync pull
sync push
save
exit
```

---

# 26. 示例工作流

目标验证以下流程。

## 搜索

```text
> search neo

Provider: Mock Music

1. Neo Sunrise
   Lemon Band
   mock:track-001
```

---

## 导入外部歌单

```text
> remote-playlists mock

1. Mock Favorites
2. Coding Mix
```

```text
> import mock coding
```

内部：

```text
IPlaylistProvider
      ↓
RemotePlaylist
      ↓
ImportService
      ↓
Lemon Playlist
      ↓
LibraryRepository
```

输出：

```text
Imported "Coding Mix"
12 tracks
Local Playlist ID:
xxxxxxxx
```

---

## 播放

```text
> play mock track-001
```

内部：

```text
Track
      ↓
PlaybackResolver
      ↓
IPlaybackProvider
      ↓
PlaybackSource
      ↓
ConsolePlayer
```

输出：

```text
Now Playing
Neo Sunrise
Lemon Band

Source:
HTTPS
https://example.invalid/audio/track-001.flac
```

---

## Git Sync

```text
> sync pull
```

输出：

```text
Fetching remote library...
Remote revision: mock-13
Local revision: mock-12

Merging...

+ 2 playlist items
~ 1 playlist
0 conflicts

Library updated.
```

---

# 27. Dependency Injection

使用：

```text
Microsoft.Extensions.DependencyInjection
Microsoft.Extensions.Logging
Microsoft.Extensions.Http
```

Application Layer 全部通过构造函数依赖注入。

避免：

```text
ServiceLocator
static global service provider
App.Current
```

---

# 28. Cancellation

所有涉及：

```text
Network
IO
Plugin
Sync
Playback resolve
```

的 async API 必须支持：

```csharp
CancellationToken
```

不要省略。

---

# 29. Error 模型

不要大量使用：

```csharp
catch (Exception)
```

然后吞异常。

可以定义：

```text
ProviderException
PluginLoadException
LibraryException
PlaybackResolutionException
SyncException
```

Console 统一捕获后输出。

插件异常不得直接导致整个 Host 崩溃。

---

# 30. Plugin 隔离

第一版 In-Process。

使用独立：

```text
AssemblyLoadContext
```

加载插件。

设计时假设未来可能改为：

```text
Host
  ↓ IPC
PluginHost
```

因此 Plugin API 不要传递：

```text
UI object
Window
Control
Dispatcher
ServiceProvider
```

主要传：

```text
record
DTO
primitive type
Stream abstraction
```

---

# 31. 插件原则

插件原则上是 Headless。

插件不能提供：

```text
Avalonia Control
Window
Page
```

插件应该提供：

```text
Data
Capabilities
Actions
Schemas
```

未来 UI 由 Lemon Neo Host 统一渲染。

---

# 32. Architecture Rules

务必保持以下依赖原则：

```text
                Plugin
                   │
                   ↓
        Plugin.Abstractions
                   │
                   ↓
                  Core
```

Plugin 不得依赖 Host。

主程序：

```text
Console
  ↓
Application
 ├──────────────┐
 ↓              ↓
Core        Plugin.Runtime
                ↓
       Plugin.Abstractions
```

Persistence：

```text
Persistence
   ↓
Core
```

Playback：

```text
Playback
   ↓
Core
```

Sync：

```text
Sync Provider
      ↓
Sync.Abstractions
      ↓
Core
```

避免循环引用。

---

# 33. 第一阶段完成标准

原型完成后必须能够执行：

```text
1. 启动 LemonNeo.Console

2. 从 plugins 目录动态发现 MockMusicPlugin

3. 输出插件和 Capability

4. 搜索歌曲

5. 获取远端 Mock Playlist

6. 将 RemotePlaylist 导入 Lemon Library

7. 创建本地 Playlist

8. 添加/删除 PlaylistItem

9. 保存 Library JSON

10. 重启程序后恢复 Library

11. Resolve PlaybackSource

12. 使用 ConsolePlayer 模拟播放

13. 调用 MockGit Pull

14. 执行 Semantic Merge

15. 输出 Merge Result

16. MockGit Push
```

---

# 34. 当前阶段不要过度设计

暂时不要实现：

```text
真正 QQ Music API
网易云 API
Spotify API
实际 GitHub OAuth
真实 Git Credential
Avalonia UI
数据库
EF Core
SQLite
Audio Decoder
FFmpeg
BASS
WebView
插件市场
插件签名
完整 Sandbox
IPC
CRDT
实时同步
云服务器
账号系统
```

先验证领域模型和插件边界。

---

# 35. 后续阶段

原型稳定后再进入：

## Phase 2

真实 Git Backend：

```text
SystemGitBackend
```

支持：

```text
clone
fetch
pull
commit
push
```

---

## Phase 3

真实 Music Provider：

```text
LocalMusicProvider
```

优先实现本地音乐。

---

## Phase 4

Avalonia UI：

```text
LemonNeo.Desktop
```

UI 只调用 Application Layer。

---

## Phase 5

第三方 Music Plugins。

---

# 36. 最重要的设计原则

整个原型始终遵守：

```text
Lemon Neo Library
       │
       ├── Music Providers
       │      ├── Search
       │      ├── Playlist
       │      ├── Metadata
       │      ├── Playback
       │      └── Lyrics
       │
       ├── Player
       │
       └── Sync Providers
              ├── Git
              ├── WebDAV
              └── Others
```

其中：

```text
Music Provider != Library

Remote Playlist != Lemon Playlist

Track != PlaybackSource

Music Plugin != UI Plugin

Git != Library Storage

Sync Provider != Music Provider
```

Lemon Neo 自己的 Library 始终处于整个系统中心。

最终目标是让：

```text
QQ Music
NetEase
Spotify
YouTube
Local Music
WebDAV Music
Subsonic
```

都只是 Lemon Neo 的 Provider。

而：

```text
Git
WebDAV
Cloud Drive
```

只是 Lemon Neo Library 的同步方式。

Lemon Neo 本身则负责提供统一、漂亮、一致的音乐管理与播放体验。
