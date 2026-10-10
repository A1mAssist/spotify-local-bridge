# Spotify Local Bridge：Lyricify 4 本地播放桥接

[English](README.md) | 简体中文

这是一个面向 Windows x64 的非官方集成项目，与 Spotify、Lyricify 均无关联。它让 Lyricify 4 从本机 Spotify 客户端获取当前歌曲、播放时间轴和常用播放控制状态；歌词仍由 Lyricify 的服务器提供，登录也仍走 Spotify。仓库不包含 Spotify 或 Lyricify 程序文件。

目前的补丁位置已针对 Spotify Desktop 1.2.89.539 和 Lyricify 4.4.0 检查。客户端更新后可能需要重新适配。项目源码采用 [MIT 许可证](LICENSE)，打包依赖的版权与许可见 [第三方声明](THIRD_PARTY_NOTICES.md)。

当前 Release：`v0.1.3`。本版本继续转发当前曲目，并在 Spotify XPUI 暴露时补充曲目编号、专辑发行日期和专辑曲目数；安装器显式加载压缩组件，并统一由 `%LOCALAPPDATA%\SpotifyLocalBridge\backups\` 管理原始 XPUI 目录和归档。

## 下载与使用

1. 安装来自 spotify.com 的桌面版 Spotify，并准备原始的 `Apps\xpui.spa`。如果曾使用 Spicetify 修改 Spotify，首次安装补丁时可在面板中指定其备份的原始 `xpui.spa`。安装器会保留原来的 XPUI 目录，并在修改前检查当前版本的启动代码；卸载时若 Spotify 又生成了新的 `xpui.spa`，安装器会停止操作，避免覆盖该文件。
2. 从[最新 Release](https://github.com/A1mAssist/spotify-local-bridge/releases/latest)下载 `LyricifyLauncher.exe` 并运行。在面板里选择 `Install Spotify patch`，然后选择 `Enable Lyricify shortcut`。已安装时，菜单会改为对应的移除或恢复操作。安装和移除 Spotify 补丁会在确认后关闭 Spotify 与 Lyricify；请先保存两款软件中的未保存内容。
3. 之后从已修改的 Lyricify 开始菜单快捷方式启动。快捷方式通过启动器给 Lyricify 加载 Hook，启动器随即退出，不会打开管理面板。桌面和任务栏的其他快捷方式不会自动修改。
4. 播放歌曲后访问 `http://127.0.0.1:48711/state`，正常情况下应返回 JSON。HTTP 204 表示暂时没有新鲜的播放状态。接收服务只在带 Hook 的 Lyricify 进程运行时存在。

TUI 的 `Now Playing` 只是这个本地接收服务的读取端，不会直接读取 Spotify。Spotify 的 XPUI 补丁把状态 POST 到 `/ingest`，而 `127.0.0.1:48711` 由 Lyricify 进程内的 .NET Hook 创建。Spotify 可以在 Lyricify 未启动时继续播放，但没有 Hook 就没有状态接收端，因此 TUI 的 `Now Playing` 没有内容可显示。必须通过插件快捷方式启动 Lyricify，TUI 和 Lyricify 的播放数据才会正常工作。

EXE 已包含 .NET 8 运行时、Hook 及其依赖、Spotify XPUI 脚本和安装/卸载脚本，不需要另装 Node 或 Spicetify 运行环境。首次运行会把这些文件释放到 `%LOCALAPPDATA%\SpotifyLocalBridge\runtime\<构建标识>\`，供 Lyricify 和安装器加载。选定的 Lyricify 路径与 Hook 日志保存在 `%LOCALAPPDATA%\SpotifyLocalBridge\`。如果本机还有占用 48711 端口的旧版 Node 桥接服务，需先关闭它。

原始 Lyricify 快捷方式备份、选定的程序路径、日志及释放出的运行文件统一放在 `%LOCALAPPDATA%\SpotifyLocalBridge\`，与启动器 EXE 的位置无关。新安装的 Spotify XPUI 备份和卸载后保留的补丁目录放在其中的 `backups\` 子目录；旧版留在 Spotify `Apps\.spotify-local-bridge-backups\` 的备份仍可由卸载器恢复。用于识别当前补丁的小型安装清单仍随 Spotify XPUI 保存。移动启动器不会丢失这些数据，但需要在面板中选择 `Repair Lyricify shortcut`，把开始菜单快捷方式更新到新位置。

### 管理面板

面板左侧集中管理 Spotify 补丁、Lyricify 快捷方式、Lyricify 程序路径与 Hook 日志；右侧显示客户端连接、当前播放和快捷方式状态。状态约每 250 毫秒读取一次，只重绘变化的行。窄终端会切换为紧凑布局。

| 按键或操作 | 作用 |
| --- | --- |
| `↑` / `↓`、`Enter` | 选择并执行左侧操作 |
| `Q` / `Esc` | 退出面板，不关闭 Spotify 或 Lyricify |
| `View hook log` | 在当前终端查看最近 10000 条原始日志 |
| 日志页 `F` | 在过滤重复成功记录的 `Events` 与完整 `All` 视图间切换 |
| 日志页 `↑` / `↓`、`PgUp` / `PgDn`、`Home` / `End` | 滚动日志 |
| 日志页 `R`、`Q` / `Esc` / `Enter` | 重新读取日志、返回面板 |

日志会缩短时间戳并折行显示长消息。安装、移除等需要确认的操作在面板内按 `Y` 确认，其他按键取消。程序路径与缺失的 XPUI 归档路径也在面板内输入。管理操作会先请求客户端正常关闭，两秒后仍未退出才强制结束进程。

`Enable Lyricify shortcut` 会先将 `%APPDATA%\Microsoft\Windows\Start Menu\Programs\Lyricify\Lyricify 4.lnk` 备份到 `%LOCALAPPDATA%\SpotifyLocalBridge\Lyricify 4.original.lnk`，再把该快捷方式指向启动器的 `--launch-only` 模式。`Restore Lyricify shortcut` 会在快捷方式仍由本项目管理时恢复原件。移动 EXE 后，从新位置运行它，再在面板中选择 `Repair Lyricify shortcut` 更新开始菜单链接。如果 Lyricify 已经从未加载 Hook 的入口启动，请先从托盘彻底退出，再使用修改后的快捷方式。

也可以向 EXE 传入一个 Lyricify 程序路径：它会先启动该程序，再打开面板。开发者还可使用 `.\lyricify-hook\run.ps1 -LyricifyExe 'C:\path\to\Lyricify for Spotify.exe'` 单独启动 Hook；运行日志位置由脚本输出。日志中的 `PATCHED playback reads, modes, volume, and library controls` 与 `PATCHED MediaSession timeline enhancement bypass (including Plus)` 表示相应补丁已安装。

### 开机启动

Lyricify 必须经插件快捷方式启动，Hook 才会加载；管理面板不需要常驻。

1. 先把 `LyricifyLauncher.exe` 放在固定位置，在面板中选择 `Enable Lyricify shortcut`。如果此前移动过 EXE，选择 `Repair Lyricify shortcut`。
2. 关闭 Lyricify 自带的开机启动设置，或在 Windows「启动应用」中禁用直接启动 Lyricify 的条目，避免它先以未加载 Hook 的方式运行。
3. 按 `Win + R`，输入 `shell:startup`。把开始菜单中已启用插件的 **Lyricify 4** 快捷方式复制到打开的文件夹。下次登录时，它会通过 `--launch-only` 启动 Lyricify，不打开管理面板；Spotify 未运行时，启动器也会尝试启动它。

以后移动 EXE 时，先从新位置运行并修复开始菜单快捷方式，再替换 `shell:startup` 中的副本。要取消开机启动，删除启动文件夹中的副本即可；只恢复开始菜单快捷方式不会删除这个副本。

## 桥接范围

Spotify XPUI 脚本读取客户端自身的 `PlayerAPI`、`PlaybackAPI` 和 `LibraryAPI`，把播放状态发往 `127.0.0.1:48711`，并接收本地控制命令。Lyricify 进程内的 .NET Startup Hook 提供该本地接收服务，用内存状态替换 `SpotifyAPI.Web.PlayerClient.GetCurrentPlayback` 的两个重载，并将暂停、继续、切歌、拖动进度、随机/循环、音量和当前歌曲的收藏操作转给 Spotify 客户端。当前歌曲、曲目编号、专辑发行日期、专辑曲目数、时间轴、播放模式、音量、红心状态以及队列预加载所需的已收藏查询均在 XPUI 暴露时转发。TUI 读取的是同一个接收服务，不是独立的 Spotify 读取器。无需浏览器包装器或 Windows 媒体会话参与播放状态采集。

带 Hook 的进程会跳过 Lyricify 媒体会话增强及其定时器入口，因此保存的“媒体会话增强”“时间轴修正”和“时间轴修正 Plus”设置不会控制该进程的播放或时间轴。Plus 可能为刷新 Windows 时间轴而主动暂停、继续播放，所以桥接状态统一来自 Spotify XPUI。未带 Hook 启动的 Lyricify 仍按自身保存的设置运行。

音量通过 Spotify 的 `PlaybackAPI.getVolume()` / `setVolume()` 读取和设置，原生 0–1 数值会转换成 Lyricify 的 0–100%。有效读数会启用 Lyricify 的滑块和静音按钮，读数不可用时则禁用。音量事件会立即刷新桥接状态。控制命令异步返回；等待超过五秒的请求会过期，排队中的过期请求也会被丢弃。Spotify 的音量设置函数可能先于实际操作完成而返回，最终音量应以 `/state.volume_percent` 的回读为准。

### ISRC

Spotify 的 [Get Playback State 文档](https://developer.spotify.com/documentation/web-api/reference/get-information-about-the-users-current-playback)在可用时通过 `item.external_ids.isrc` 返回歌曲 ISRC。脚本优先读取当前曲目或元数据中的 ISRC；缺失时立即把 22 位 base62 曲目 ID 转成 32 位十六进制 GID，再通过 Spotify 自身的 `RequestBuilder` 请求 `https://spclient.wg.spotify.com/metadata/4/track/{gid}`。响应中的 `external_id` 记录若包含 `type: "isrc"`，Hook 会把它映射到 Lyricify 的 `FullTrack.ExternalIds`。

Spotify 若正在请求相同记录，脚本只对重复请求错误在三秒内重试。成功的 ISRC 按曲目缓存在内存中；空结果和 HTTP 429 五分钟后重试，临时请求错误 15 秒后重试。不再扫描 Spotify 磁盘缓存，也不读取登录凭据。本地文件、播客、特殊版本或元数据请求失败时仍可能没有 ISRC。

切歌后的首个播放状态会等待 ISRC 查询，尽量让 Lyricify 在第一次歌词请求中拿到它。查询失败或达到三秒上限时，状态会先在没有 ISRC 的情况下发出；后续不会自动重试已经发出的歌词请求。Lyricify 4.4.0 的歌曲信息弹窗只在打开时复制一次 ISRC，因此元数据稍后到达时，Hook 会在 UI 线程检查弹窗仍显示同一曲目，再更新该字段。`tests/isrc` 使用真实 WPF 控件检查了弹窗更新、旧曲目拒绝和 Dispatcher 调度。

## Lyricify 的 Spotify 接口

[Lyricify 自定义 API Client 文档](https://docs.lyricify.app/lyricify-4/custom-api-client/)允许填写 Spotify OAuth Client ID 和 Secret，但没有 API 基础地址设置。Lyricify 4.4.0 随附的 `SpotifyAPI.Web.dll` 将基础地址设为 `https://api.spotify.com/v1/`，两个 `GetCurrentPlayback` 重载均对应 `me/player`。恢复出的 Lyricify 方法体显示，启动流程和播放辅助逻辑会直接调用 `SpotifyAPIClient.Player.GetCurrentPlayback`。通过其 SDK 构造的测试调用已得到本地播放状态；这项测试本身不等于完整的登录后端到端验证。

| Lyricify 4.4.0 调用 | 接口 | 使用时机 | 本地覆盖 |
| --- | --- | --- | --- |
| `UserProfile.Current` | `GET /v1/me` | 主窗口完成加载前获取用户 ID、邮箱、名称、国家、订阅类型和图片 | 未覆盖 |
| `Player.GetCurrentPlayback` | `GET /v1/me/player` | 启动及歌曲变化轮询；歌词依赖其歌曲和设备字段 | 两个 SDK 重载均已 Hook |
| `Player.GetQueue` | `GET /v1/me/player/queue` | Premium 账户默认队列预加载时自动调用 | 未覆盖 |
| `Library.CheckTracks` | `GET /v1/me/tracks/contains` | 非本地歌曲的后台收藏状态查询 | 转给 `LibraryAPI.contains` |
| `Tracks.GetSeveral` | `GET /v1/tracks?ids=...` | 出现重新关联的歌曲时 | 未覆盖 |
| `Player.GetAvailableDevices` | `GET /v1/me/player/devices` | 无播放状态且启用内置 WebView2 播放器时 | 未覆盖 |
| `Player.GetRecentlyPlayed` | `GET /v1/me/player/recently-played` | 首页；其他首页模块还会调用个性化、浏览、艺人和关注接口 | 未覆盖 |
| `Library.SaveTracks` / `RemoveTracks` | `PUT/DELETE /v1/me/tracks` | 红心按钮 | 转给 `LibraryAPI.add` / `remove` |
| 播放控制方法 | `PUT/POST /v1/me/player/*` | 暂停、继续、拖动、切歌、随机、循环、音量、队列 | 除队列外的列出操作转给 Spotify XPUI；队列仍走远端 |
| Spotify OAuth | `GET https://accounts.spotify.com/authorize`、`POST https://accounts.spotify.com/api/token` | 首次登录与令牌续期 | 未覆盖 |

恢复出的授权流程会交换 Spotify 刷新令牌、读取 `/v1/me`，再把刷新令牌及自定义 Client 凭据交给 `RegisterLyricify`。本 Hook 不拦截登录或用户资料查询，因此 Lyricify 仍需要 Spotify 授权，也可能为其他功能连接 Spotify；它不能完全替代 Lyricify 的 Spotify 连接。对于已有有效 Lyricify 账户的歌词显示，启动时的关键依赖是 `/v1/me` 和 `/v1/me/player`。若要消除**所有** Spotify Web API 请求，还需处理队列、收藏及条件调用，或关闭对应功能；首次登录则需要另行解决身份初始化。歌词请求本身发往 Lyricify 的 `/lyrics/get` 服务，并携带歌曲元数据及 Spotify 用户字段，本项目不替代它。

上表来自随附 SDK、[SpotifyAPI-NET 的 PlayerClient](https://github.com/JohnnyCrazy/SpotifyAPI-NET/blob/master/SpotifyAPI.Web/Clients/PlayerClient.cs)及恢复出的 Lyricify 调用点，不代表运行时请求次数的完整记录。辅助预加载方法的反编译仍有少量错误；本地恢复的程序集仅用于静态分析，解包器报告过一个资源错误及可能的代码虚拟化，该程序集未在仓库分发。目前也没有捕获完整的登录后运行请求轨迹。其他设备上的 Spotify Connect 播放尚未实测。Lyricify 更新可能改变 SDK 方法或数据要求；找不到预期的两个重载时，Hook 会记录补丁错误并停止该补丁。

## 构建与验证

在 Windows PowerShell 中运行 `.\package-single.ps1` 构建单文件版本；需要适配另一份 Lyricify 时，传入 `-SpotifyApiDll 'C:\path\to\SpotifyAPI.Web.dll'`。构建输出是 `dist\LyricifyLauncher.exe`，它作为 GitHub Release 附件发布，不纳入 Git 跟踪。Lyricify 加载 Hook 时仍需要磁盘上的 DLL，所以 EXE 会在运行时释放内嵌文件。

可运行以下检查。打包脚本把最新 Hook 编译到独立暂存目录，因此检查命令显式使用该 DLL：

```powershell
.\package-single.ps1 -SpotifyApiDll 'C:\path\to\SpotifyAPI.Web.dll'
$hook = (Resolve-Path '.\launcher\obj\single-exe-payload\StartupHook.dll').Path
node .\test.mjs
.\lyricify-hook\check-server.ps1 -HookPath $hook -SpotifyApiDll 'C:\path\to\SpotifyAPI.Web.dll'
.\lyricify-hook\check-map.ps1 -HookPath $hook -SpotifyApiDll 'C:\path\to\SpotifyAPI.Web.dll'
dotnet run --project tests/launcher/LauncherCheck.csproj -c Release
dotnet run --project tests/isrc/IsrcCheck.csproj -c Release '-p:SpotifyApiDll=C:\path\to\SpotifyAPI.Web.dll' "-p:HookPath=$hook"
dotnet run --project tests/media-session/MediaSessionCheck.csproj -c Release '-p:SpotifyApiDll=C:\path\to\SpotifyAPI.Web.dll' "-p:HookPath=$hook"
.\install.ps1 -XpuiArchive 'C:\path\to\xpui.spa' -ValidateOnly
.\tests\storage\check.ps1
```

测试覆盖音量 0、37、100%、无效值、不可用设备、RPC 失败和过期命令；接收器检查音量边界并映射 `Device.SupportsVolume` / `VolumePercent`。测试还通过 Hook 前缀发送真实的 SDK `PlayerVolumeRequest`，检查结果传递。.NET 6 Harmony 测试验证媒体会话初始化和轮询被跳过，以及暂停、继续、切歌、拖动进度的回退路径。该测试使用恢复出的公开辅助方法形态；受保护的 Lyricify 程序集需在部署时另行验证。Lyricify 当前使用的 .NET 6 目标框架已停止支持，构建时可能出现 SDK 的生命周期警告。

XPUI 脚本测试不依赖 Spicetify 全局对象，覆盖 `PlayerAPI` 更新事件、原型 getter 元数据、首个状态的 ISRC 查询、重复请求重试、切歌隔离、超时回退、播放控制、随机/循环及 `LibraryAPI` 收藏操作。接收器在独立端口上通过了 Origin、参数校验、命令队列、时间轴和 Hook 映射检查。安装器的两个启动代码插入点已用原始 Spotify 1.2.89.539 归档验证。

开发机实测中，重启两款客户端后，曲目 `spotify:track:1lbNgoJ5iMrMluCyhI4OQP` 首次采样的 `/state` 已含 ISRC `GBAHS2500558`。一次切到下一首的测试中，媒体下一曲按键发出 1.48 秒后，首次变化的 `/state` 已包含曲目 `spotify:track:2oyu6OED3JSxgogTuTZRab` 的 ISRC `DGA082501213`。尚未捕获使用这条新首状态路径的 Lyricify 歌词请求日志。

## 已知限制

- 接收器只监听本机回环地址，并检查 Spotify XPUI 的浏览器 Origin；但其他本地进程可以伪造 HTTP 标头，因此它面向可信任的本机环境。Hook 不保存 Spotify 令牌。
- 接收器会在两次更新之间推算播放进度；Spotify 意外退出后，旧状态最多可能保留 90 秒。
- Spotify 更新可能替换 XPUI。安装器会检查已知的启动代码位置，版本变化后可能需要适配。
- Lyricify 的受保护初始化器曾在 Hook 立即打补丁时崩溃，因此目前延迟两秒安装补丁。Lyricify 更新后需要重新做运行验证。
