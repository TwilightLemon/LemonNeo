# BASS 解码格式支持 TODO

## 当前决定

暂时保留 BASS 插件当前声明的保守格式范围：

- `.aiff`
- `.flac`
- `.mp3`
- `.ogg`
- `.wav`

`SupportedExtensions` 同时用于本地文件扫描和解码器信息展示。这里宁可暂时漏掉平台相关格式，也不扩大列表后把实际无法解码的文件导入资料库。

## 已确认情况

- BASS 核心跨平台支持 MPEG Layer 1/2/3、Ogg Vorbis、WAV 和 AIFF。
- LemonNeo 当前随附并加载 BASSFLAC，因此支持 FLAC。
- macOS 和 iOS 可通过 CoreAudio 解码 AAC 与 ALAC。
- Windows 可通过 Media Foundation 解码 AAC 和 WMA；Windows 10 也支持 ALAC。
- Linux 当前只随附 BASS 和 BASSFLAC，没有随附 BASS_AAC 或 BASSALAC，因此不能将 M4A 视为跨平台能力。
- 2026-10-09 使用仓库内 macOS ARM64 BASS 2.4.18.3 实测：AAC 编码与 ALAC 编码的 `.m4a` 均可由 `BASS_StreamCreateFile` 成功打开。

参考资料：

- [BASS_StreamCreateFile](https://www.un4seen.com/doc/bass/BASS_StreamCreateFile.html)
- [BASS add-ons](https://www.un4seen.com/bass.html)
- [BASSALAC](https://www.un4seen.com/doc/bassalac/bassalac.html)
- [BASSFLAC](https://www.un4seen.com/doc/bass/bassflac.html)

## 后续工作

- [ ] 补齐核心格式的常用扩展名，例如 `.mp1`、`.mp2` 和 `.aif`，并为每项准备播放测试文件。
- [ ] 将 BASS 的扩展名声明改为按运行平台生成，而不是所有平台共享一个静态集合。
- [ ] 在 macOS 上评估加入 `.aac`、`.m4a` 和 `.m4b`。
- [ ] 在 Windows 上验证 AAC、ALAC、M4A 和 WMA 的最低系统版本与 Media Foundation 可用性，再确定声明范围。
- [ ] 决定 Linux 是否随附并加载 BASS_AAC/BASSALAC；同时检查二进制体积、分发方式与许可证要求。
- [ ] 评估通过 `BASS_PluginGetInfo` 获取已加载 add-on 的格式信息，避免代码声明与实际部署的 add-on 不一致。
- [ ] 为平台相关格式增加真实解码测试；不能只验证文件扩展名或 TagLib 元数据读取。
- [ ] 单独处理 `.mp4`：它可能包含视频或不受支持的音频编码，不应仅凭扩展名参与文件夹导入。
- [ ] 验证 TagLibSharp 对新增格式的标签和封面读取；读取失败时继续保留文件名回退行为。

## 放宽限制的完成条件

只有同时满足以下条件，才将某个扩展名加入 `SupportedExtensions`：

1. 当前平台随附的 BASS、系统编解码器或已加载 add-on 能稳定打开该格式。
2. 本地文件扫描不会因此大量吸收视频文件或其他误报文件。
3. 至少有一个自动化或可重复的真实文件解码测试。
4. 不支持该格式的平台不会错误声明支持。
