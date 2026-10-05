# 持久化重构后续问题

记录日期：2026-10-06。本文记录本次检查发现、尚未实现的后续处理，不包含已通过验收的配置合并保存与通知隔离。
当前结构及保存流程见 [持久化设计](persistence-design.md)。

## 1. 同一配置目录的多进程写入

状态：代码审查发现，尚未实现跨进程协调，也未进行双进程验收。

Desktop 与 Console 默认使用相同配置根目录。`AppSettingsService` 的保存锁只保护当前进程，
`JsonSettingsEntityStore` 使用固定的 `<entity>.json.tmp`。两个宿主同时运行时，可能争用临时文件，
或将各自内存里的旧值覆盖到对方刚保存的实体。

当前实现按一个配置目录由一个进程管理的边界设计，但启动时尚未强制这一约束。
本次验收运行独立临时目录中的单个 Runtime，没有验证跨进程写入。

下一次处理先明确产品选择：同目录只允许一个宿主，或允许 Desktop/Console 共享目录并定义冲突策略。
前者需要在 Runtime 启动时取得目录所有权；后者需要跨进程锁与实体更新冲突处理。
只将临时文件名改为唯一值，无法解决旧内存快照覆盖新设置的问题。

验收要求：两个独立进程使用相同目录修改同一实体及不同实体；验证目录所有权或冲突策略，
避免 JSON 损坏、临时文件争用和未报告的覆盖；进程退出后能够重新获取所有权。

## 2. 插件热卸载后的即时程序集替换

状态：配置注册与订阅的释放已实现，延迟回收测试通过；运行中热替换插件文件的流程尚未实现。

本次验收发现，刚完成卸载就强制 GC 时，加载上下文仍可能存活。
隔离 JSON 存储后可以回收，进一步定位到 System.Text.Json 的短期反射访问器缓存：
缓存保留类型与成员，过期清理由后续元数据访问触发。
机制见 [访问器缓存](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Text.Json/src/System/Text/Json/Serialization/Metadata/ReflectionEmitCachingMemberAccessor.cs)
和 [缓存清理](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Text.Json/src/System/Text/Json/Serialization/Metadata/ReflectionEmitCachingMemberAccessor.Cache.cs)。

当前配置存储使用每次操作独立的 JSON 选项；卸载时清除包注册，加载器清除报告中的插件引用。
回收验收等待框架短期缓存过期，并通过宿主实体的元数据访问触发清理后验证上下文可回收。
这证明应用自身没有持续持有插件类型，但不能保证 `Unload()` 返回后立刻回收，
或宿主空闲时立刻释放插件 DLL 文件。正常进程退出仍会结束这些引用。

下一次实现热更新时，需要明确卸载完成与文件替换的协议，并检查播放器、平台事件及其他插件自己的引用。
不要依赖固定 sleep 或框架私有反射接口作为卸载完成条件。

验收要求：在目标平台反复加载、保存配置、卸载与替换 DLL；确认配置可重新注册、
旧上下文最终回收，并验证文件占用期间的更新处理。当前 macOS 验收不代表 Windows 的即时文件替换保证。
