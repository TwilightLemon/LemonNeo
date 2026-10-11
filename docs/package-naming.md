# 包名与配置实体命名

解决方案使用与 C# 命名空间相同的点分隔 PascalCase 风格，第一方包名与项目名、程序集名保持一致。
`Directory.Build.props` 将第一方项目的 NuGet `PackageId` 统一设为 `MSBuildProjectName`。

| 所属模块 | 包名 |
| --- | --- |
| Application | `LemonNeo.Application` |
| Desktop | `LemonNeo.Desktop` |
| Console | `LemonNeo.Console` |
| 插件 | `LemonNeo.Plugin.<Name>`，例如 `LemonNeo.Plugin.Netease`、`LemonNeo.Plugin.QqMusic` |
| 基础设施 | `LemonNeo.Infrastructure`；内部实现不作为插件包发布 |

插件 manifest 的 `id` 与 `PluginDescriptor.Id` 必须完全一致，推荐与程序集名一致。
每一段以大写字母开头，后续字符只能使用 ASCII 字母和数字；不使用反向域名、连字符、路径字符或空段。
Hosting 拒绝无效包名、不同大小写的重复配置包与已加载的重复插件包。

```json
{
  "id": "LemonNeo.Plugin.Netease",
  "name": "Netease Cloud Music",
  "version": "1.0.0",
  "minimumHostVersion": "0.1.0",
  "entryAssembly": "LemonNeo.Plugin.Netease.dll",
  "dependencies": [
    {
      "name": "Example.Client",
      "version": "2.0.0",
      "license": "MIT",
      "website": "https://example.com/example-client"
    }
  ]
}
```

`dependencies` 用于声明插件随附或运行时使用的第三方软件包。每一项必须提供名称、版本和许可证；
项目地址可通过 `website` 提供。没有第三方依赖时使用空数组。Desktop 的“关于”页面会直接展示这些声明，
因此发布插件时应同步更新版本和许可信息，并随包附上许可证要求的完整声明或许可证副本。

配置实体文件名使用 PascalCase 标识符，例如 `AccountSettings`，对应 `AccountSettings.json`。
同一包内类型和实体文件一一对应；不同包可以使用相同实体名。文件中的属性名采用 camelCase。

Provider ID 与歌曲的外部标识命名空间继续表达音乐来源，例如 `netease`、`qqmusic`。
这些业务标识与插件包名各有用途，不因包名调整而改变歌曲身份。
