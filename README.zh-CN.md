<div align=center>
<img src="https://github.com/TwilightLemon/LemonNeo/blob/main/src/LemonNeo.Desktop/Resources/Icons/App.ico?raw=true" width="128" height="128"/>

# Lemon Neo (预览版)
一款跨平台音乐播放器，把你喜欢的音乐汇聚在一起。  
✨ *保持简单与精致* ✨

[<img src="https://img.shields.io/badge/license-GPL%203.0-yellow"/>](LICENSE)
![C#](https://img.shields.io/badge/lang-C%23-orange)
![Avalonia UI](https://img.shields.io/badge/UI-Avalonia%20UI-b33bb3)
![GitHub Repo stars](https://img.shields.io/github/stars/TwilightLemon/LemonNeo)

[English](README.md) | 简体中文

</div>

![overview](https://github.com/TwilightLemon/Data/blob/master/lemonneo/overview.webp?raw=true)
![playback](https://github.com/TwilightLemon/Data/blob/master/lemonneo/playback.webp?raw=true)
![lightmode](https://github.com/TwilightLemon/Data/blob/master/lemonneo/lightmode.webp?raw=true)
---

## 简介

Lemon Neo 把你的音乐汇集到一个音乐库中。你可以导入本地文件、登录已经在用的在线服务，并让歌单、播放队列、歌词和封面都保存在自己的电脑上——不会被任何一个服务商锁定。

音乐库之外的一切都由插件提供：音源、解码器、歌词与元数据、账号登录、系统集成。只需把文件夹放进 `plugins/` 即可添加或移除任意插件，因此应用本身保持精简，也没有内置任何音乐服务。

桌面端和终端客户端共用同一套内核、音乐库和设置。Lemon Neo 是遵循 GPL-3.0 的自由软件，目前处于预览阶段——难免有粗糙之处，行为也可能发生变化。

## 环境要求

- [.NET SDK 10.0](https://dotnet.microsoft.com/download) 或更高版本。
- Windows 10 或更高版本、macOS 13 或更高版本。

注：由于 Avalonia UI 在 Linux 上缺乏音频输出和窗口行为支持，目前尚不支持 Linux。控制台客户端或许能在 Linux 上运行，但未经测试。

## 构建与运行

```sh
git clone --recursive https://github.com/TwilightLemon/LemonNeo.git
cd LemonNeo

dotnet build LemonNeo.slnx

# 桌面版
dotnet run --project src/LemonNeo.Desktop

# 终端客户端
dotnet run --project src/LemonNeo.Console
```

插件会自动部署到主程序的 `plugins/` 目录；如需构建不带插件的主程序，请传入 `-p:SkipHostPlugins=true`。控制台主程序支持 `--plugins <dir>`、`--library <dir>` 和 `--startup <file>` 参数。

## 功能特性

### 跨平台

在 Windows 和 macOS 上拥有相同的应用、音乐库和功能：

- **该原生处即原生**——窗口边框、快捷键和侧边栏样式遵循各平台习惯；关闭窗口后，音乐仍会通过托盘图标（Windows）或 Dock 菜单（macOS）继续播放。
- **系统媒体集成**——两个平台都支持硬件媒体键、系统媒体控制和系统"正在播放"面板（Windows 使用 SMTC，macOS 使用 Now Playing）。
- **行为原生的桌面歌词**——浮动歌词窗口在 Windows 上不会抢占焦点，在 macOS 上会跟随每个 Space（包括全屏应用）一起切换。
- **其余部分完全一致**——主题、取自封面的配色、动态网格背景、音频可视化以及全屏歌词页在两个平台上表现相同。

### 可插拔的服务

音乐库之外的一切都是插件：音源、解码器、歌词、元数据、账号和系统集成。你可以只用一个装满文件的文件夹加一个解码器，完全本地运行；也可以在其上叠加在线服务——而卸载任何一项只需删除一个文件夹。不兼容或启动失败的插件会被报告原因并跳过，因此应用的其他部分仍能正常工作。

→ 能力说明与开发指南：[docs/plugins.md](docs/plugins.md)

### 多音源播放

一首曲目并不绑定于某一个服务：它带有自身标识，以及已匹配到的外部 ID（ISRC，或按服务区分的 ID，例如 `netease`、`qqmusic`、`spotify`）。按下播放时：

1. 若存在**本地文件**，则优先使用。
2. 否则使用**缓存副本**。
3. 否则向所有接受该曲目标识的插件发起请求，按**音质从高到低**依次尝试。
4. 若仍无法解析，则**通过元数据匹配进行扩展**后重试——因此即便你只在某一个服务上拥有这首曲目，也能通过另一个服务播放。

播放详情面板会显示实际使用的音源、解码器、音质和缓存状态。

### 跨命名空间的元数据匹配

元数据不绑定于曲目来自哪个服务：

- 匹配结果按**置信度与匹配等级**评分，并以**最低置信度**过滤，同时限定在所选定的**音源命名空间**内。
- 接受匹配会**添加外部 ID**，而不是覆盖你的元数据，因此一首曲目会随时间积累更多身份标识——以及更多播放选择。
- 可以整张歌单一次性匹配，曲目编辑器也允许手动编辑任意字段，包括外部 ID。

### 高度可定制

- **外观**——浅色、深色或跟随系统；使用强调色板或从曲目封面提取的配色；网格背景和可视化可用预设调节或直接关闭；提供减少动效选项。
- **歌词**——字号、加粗、对齐方式、翻译与罗马音开关、单行或双行显示，以及可放置在任意位置的浮动桌面歌词窗口。
- **播放**——首选音质与解码器、播放模式，以及当多个插件都能播放同一曲目时的音源选择。
- **音乐库**——拖放歌单、导入文件夹时自动识别内嵌封面与标签、远程歌单的导入导出，以及无需重启即可把音乐库或缓存迁移到任意位置。

---

## 内置插件

| 插件 | 作用 | 平台 |
| --- | --- | --- |
| `LemonNeo.Plugin.Bass` | 基于 BASS + BASSFLAC 的播放引擎 | 全部 |
| `LemonNeo.Plugin.WindowsSmtc` | 系统媒体传输控制 | Windows |
| `LemonNeo.Plugin.MacNowPlaying` | macOS Now Playing | macOS |
| `LemonNeo.Plugin.Netease` | 网易云音乐用户音乐库 | 全部 |
| `LemonNeo.Plugin.QqMusic` | QQ 音乐用户音乐库 | 全部 |
| `LemonNeo.Plugin.GdStudioNetease` | 备用的网易云音乐播放音源 | 全部 |
| `LemonNeo.Plugin.Lyricify` | 歌词与元数据匹配 | 全部 |
| `LemonNeo.Plugin.MockMusic` | 面向插件开发者的示例实现 | 全部 |


## 配置与存储

Lemon Neo 遵循各平台的目录约定；以下是桌面版的默认值，**设置 → 存储**中会显示当前实际使用的路径。迁移音乐库或缓存会立即生效，无需重启。

| 内容 | Windows | macOS | Linux |
| --- | --- | --- | --- |
| 配置 | `%APPDATA%\LemonNeo` | `~/Library/Application Support/LemonNeo` | `$XDG_CONFIG_HOME/LemonNeo` (`~/.config/LemonNeo`) |
| 音乐库 | `%LOCALAPPDATA%\LemonNeo\Library` | `~/Library/Application Support/LemonNeo/Library` | `$XDG_DATA_HOME/LemonNeo/Library` (`~/.local/share/LemonNeo/Library`) |
| 缓存 | `%LOCALAPPDATA%\LemonNeo\Cache` | `~/Library/Caches/LemonNeo/Cache` | `$XDG_CACHE_HOME/LemonNeo/Cache` (`~/.cache/LemonNeo/Cache`) |
| 数据与凭据 | `%LOCALAPPDATA%\LemonNeo` | `~/Library/Application Support/LemonNeo` | `$XDG_DATA_HOME/LemonNeo` (`~/.local/share/LemonNeo`) |
| 插件 | `<app>\plugins` | `<app>/plugins` | `<app>/plugins` |

环境变量会覆盖解析后的目录；每个变量都按原值直接使用，不会追加子目录：

| 变量 | 覆盖对象 |
| --- | --- |
| `LEMONNEO_CONFIG_HOME` | 配置目录 |
| `LEMONNEO_LIBRARY_HOME` | 音乐库目录 |
| `LEMONNEO_CACHE_HOME` | 缓存目录 |
| `LEMONNEO_DATA_HOME` | 数据目录（凭据与数据保护密钥） |

各目录存放的内容：

- **音乐库**——`library.json`（歌单索引）和 `playlists/<id>.json`。如果配置的音乐库无法打开，应用会报告原因并回退到默认位置。
- **配置**——每个设置实体一个 JSON 文件，按包分组：桌面版偏好设置（包括存储路径覆盖）位于 `LemonNeo.Desktop/`——如 `AppearancePreferences.json`、`LyricsPreferences.json` 等——另外每个插件各有一个文件夹。
- **缓存**——`Artwork/`、`Images/`、`Lyrics/`、`Music/`（缓存的音频），以及各插件的私有文件夹。
- **数据**——按插件保存的登录凭据，以及用于加密它们的密钥。

音乐库和缓存必须可写，并且彼此分离，也要与配置、插件和凭据分开；若某个文件夹中存放了无关文件，会被拒绝使用而不是直接采用。

## 文档

- [插件](docs/plugins.md)——能力说明、清单字段、校验规则与开发指南
- [包命名](docs/package-naming.md)——插件 ID 与配置实体命名
- [桌面版本地化](docs/localization.md)——添加界面语言的约定
- [BASS 格式支持](docs/bass-format-support-todo.md)——解码器覆盖情况说明

## 许可证

基于 [GNU 通用公共许可证 v3.0](LICENSE) 授权。

随附的及第三方组件的许可证各自独立。每个插件都会在 `plugin.json` 中声明其依赖，并随包附带所需的许可声明。
