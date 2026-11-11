# FOX CODEX HUD · 中文使用说明

当前 Windows 卡片版本 **1.3.0-beta.8.corner.55**。项目独立维护，与 OpenAI 无隶属关系。[返回首页](README.md) · [下载与发行版](https://github.com/cupfox-coud/FOX-CODEX-HUD/releases) · [图文功能图鉴](docs/SHOWCASE.md)

## Windows 角落卡片

400 × 722 逻辑像素的石墨玻璃面板依次分为三组：

1. **全局输出**、当前会话平均响应时间与上下文占用。输出聚合本机可见 Codex 任务，默认 300 tok/s 满刻度；切换会话不会清空全局输出。上下文显示最近请求的输入 / 模型窗口，70% 提醒准备交接，85% 提醒保存交接并开启新对话。
2. **周额度与公共重置雷达**。左侧是账户周额度剩余及官方周期刷新倒计时；右侧显示近期额度消耗速度和今日建议。今日超额显示负数。雷达把全局额度重置和重置卡发放分开，发卡不会提示提前清空额度。
3. **网络环境**。AI 服务与上网节点各自显示真实延迟及最近采样；不切换节点或修改代理规则。

速度颜色和额度虹彩随实际输出平滑变化；明显加速时扫光只出现在全局输出区域。雷达有效信号获得分类高亮和通知；关闭动画或内存紧张时保持静态提示。支持置顶、鼠标穿透及悬停淡化。源码与截图中的演示值均为模拟数据。

周额度每 30 秒通过已登录客户端的官方账户接口核对，本地新额度快照可即时更新。近期消耗速度以最多 5 分钟内余额下降计算，单位 `%/h`；至少观察 1 分钟，采样不足或过期不会伪造 0。今日建议按本地自然日及官方周期重置重建，超额用负数显示；它是建议而非服务限制。详见 [额度说明](docs/QUOTA_RUNWAY.md)。

公共雷达每 3 分钟检查状态；信号没有变化时使用已验证的新鲜度时间，约 12–15 分钟复核；信号变化会立即复核。常态约 25 次请求 / 小时（变化较多时更多），同一出口 IP 与其他客户端共享服务商的每小时 60 次限额。遇到限制会按服务端时间退避，重启不会跳过已知限流等待；不保证共享出口一定有剩余配额。未知、过期或不匹配的来源不产生行动建议。来源：[Did Codex Reset API](https://didcodexreset.com/zh/api.html)。

当前会话跟随、同名会话处理与 70% / 85% 提醒详见 [上下文说明](docs/CONTEXT.md)；速率与动态表现详见 [速度说明](docs/SPEED.md)。主界面和运行数据见 [隐私说明](PRIVACY.md)。

## 随所有兼容客户端启动

Windows 右键勾选 **“随 Codex / CLI / IDE 启动”**，或运行 `Install.ps1`。macOS/Linux 悬浮条菜单使用 **“随受支持客户端启动”**。原来的“开机自动启动”已替换；本副本的旧 Windows 自启任务会迁移。

Windows 常驻卡片使用 resident 模式，登录后即可显示；固定安装目录和入口说明见 [Windows 安装说明](docs/INSTALLATION.md)。其他非 resident 跟随模式中，登录后只运行后台等待器，客户端出现后约 2 秒启动；最后一个客户端关闭后约 6–8 秒退出由等待器启动的状态条。手动打开的窗口保留；手动退出后，本轮客户端全部关闭并再次打开才恢复自动启动。

Windows 数据默认保存到 `%USERPROFILE%\.codex-token-hud`，避免商店版 Codex 的 AppData 重定向使系统任务读不到设置。已有布局、固定对话和区间选择会迁移，原数据保留；旧自启任务同步更新。自定义 `CODEX_TOKEN_HUD_HOME` 应使用客户端和系统任务都能访问的目录。

识别范围按共享 Codex 后端划分，涵盖 Desktop、CLI，以及 VS Code、Cursor、Windsurf 等运行兼容 Codex 后端的 IDE/派生入口；不会只因打开编辑器或终端就启动。Windows 角落卡片跟随最后查看的桌面对话，切换到其他应用时保留该会话；同名对话通过可见内容的本地指纹区分，证据不足时标注待确认。也可按 ID 固定。非 Windows 模式的最近活动不等同于正在查看的窗口。

WSL、SSH 和其他主机须在**日志所在环境**启用跟随；宿主机不能直接识别远端的进程。无图形界面时自动使用无 Qt 依赖的本地统计服务，仍只监听回环地址。源码入口覆盖原有 Python 兼容范围：

```sh
# 有桌面时启用悬浮条；无桌面时使用本地面板和 systemd 用户服务
python3 app_follow.py --enable
# 强制使用浏览器统计服务；SSH 可配合原有端口转发
python3 app_follow.py --enable --interface dashboard
# 改名或额外的兼容客户端：按完整可执行文件路径识别，可重复指定
python3 app_follow.py --enable --client-executable /absolute/path/to/client
# 没有桌面自启或 systemd 用户服务的环境：通过此入口启动任意兼容客户端
python3 app_follow.py --run codex
python3 app_follow.py --interface dashboard --run /absolute/path/to/client arg1 arg2
# 关闭跟随，保留手动工具和已有数据
python3 app_follow.py --disable
```

`--run` 后的程序和参数原样传递，不修改 Codex、编辑器或 shell 配置。它和 `--client-executable` 为新增/改名客户端提供入口，但不会把不兼容的日志变成兼容格式。离线日志、历史导出和纯云端任务没有可跟随的本地客户端进程；离线兼容日志继续手动查看，纯云端记录仍不可测。

**上游基础版本：1.3.0-beta.8；本仓库 Windows 卡片版本：1.3.0-beta.8.corner.55。** 独立开源项目，与 OpenAI 无隶属关系。只读本地日志，不改动 Codex 安装，不上传聊天内容。

![Windows 状态栏](docs/images/hud.png)

## macOS/Linux 原生悬浮条

![跨平台悬浮条](docs/images/overlay.png)

截图来自 macOS ARM64 CI，使用人工统计数据；Linux 使用同一布局及自己的原生窗口后端。

在 [GitHub Releases](https://github.com/drkang7/codex-token-hud/releases/tag/v1.3.0-beta.8) 下载对应的 `overlay-macos-x64`、`overlay-macos-arm64`、`overlay-linux-x64` 或 `overlay-linux-arm64` 安装包并完整解压；开发产物也可在 [GitHub Actions 成功运行](https://github.com/drkang7/codex-token-hud/actions)中获取。macOS 将 **CodexTokenHud.app** 移到“应用程序”后打开；Linux 运行 `./CodexTokenHud/CodexTokenHud`。这些包内置 Python 和 Qt，无需自己安装依赖。macOS 测试包使用临时签名、尚未公证，遇到系统拦截可通过系统设置中的“仍要打开”启动。

悬浮条显示 **tok/s、缓存命中率、模型和统计时间**。拖动空白处移动，拖动边角或右下角调整大小，布局自动保存。“选对话”可搜索标题或完整 ID 并固定；右键解除固定后，优先跟随最近发起的本地任务。没有可识别的任务开始记录时回退到最新活动；后台新任务也可能被选中，因此它不等同于正在查看的窗口。浏览器面板里的固定/解除固定、区间确认和返回最近响应会同步到悬浮条。

也可直接用源码启动（已有 Python 3.10–3.14）：

```sh
sh start-overlay.sh
# macOS 也可以双击 StartOverlay.command
# 使用自己的虚拟环境：
python3 -m venv .venv
. .venv/bin/activate
python -m pip install -r requirements-overlay.txt
python overlay.py
python overlay.py --thread 完整对话ID --language zh
```

脚本首次运行会创建独立虚拟环境并下载 Qt，之后可离线使用；打包版本本身已包含依赖。新悬浮条的数据放在用户数据目录的 `overlay-runtime` 下，与原 Windows 状态栏分开。无需辅助功能权限，可读取兼容的 Desktop/CLI/IDE 本地日志及 `--log` 指定的离线日志。

macOS 使用保持显示的原生浮动面板，并设置跨桌面及全屏辅助窗口行为。Linux 优先使用可用的 XWayland；纯 Wayland 的置顶和窗口位置由桌面合成器决定，可能需要系统窗口规则，程序无法保证所有桌面环境都接受置顶请求。拖动和缩放使用系统提供的操作。具体版本要求和验证范围见 [兼容说明](docs/COMPATIBILITY.md#native-macoslinux-desktop-strip)。

## 新增：区间平均统计

在 Windows 状态栏或托盘图标上右键，选择 **“区间统计：按时间或首尾消息…”**。macOS/Linux 可在新悬浮条点击“区间统计”，也可直接启动浏览器面板。

1. 选择一个本地对话，标题重复时可以按 ID 区分。
2. 选择“按时间段”，填写开始和结束时间；或选择“按首尾消息”，输入消息全文或片段，点击查找，再点击对应的发送时间和消息。
3. 点击“确认范围并统计”，显示该范围的平均 tok/s、平均缓存命中率、响应数量、总输出、有效计时和数据覆盖。对应的桌面悬浮条同时显示该对话的区间平均值。

消息范围包含首尾两条用户消息，以及末条消息后的回答，截止到下一条用户消息之前。时间范围按响应完成时间判断，包含起止边界，使用本机时区。重复的消息不会自动猜测，需明确选择搜索结果。

**平均 tok/s = 有效样本的总输出 token ÷ 总生成秒数。平均缓存命中率 = 总缓存输入 token ÷ 总输入 token。** 两项都采用加权统计。缺少计时的响应只排除速率统计；缺少缓存计数的响应只排除缓存统计，并分别显示覆盖数量。工具等待按日志边界尽量剔除；请求计时包含首 token 等待。包含日志已计入的推理 token，不重复相加。

确认后是固定快照，不随新消息改变。再次确认可更新快照；点击面板“返回最近响应”，或状态栏右键“返回最近响应统计”，即可恢复实时视图。完整历史按需读取，包含续聊日志片段，不受实时采集最近 8 MiB 的限制。

<details>
<summary>界面示例（人工样例，不含真实对话）</summary>

![首尾消息区间统计面板](docs/images/dashboard.jpg)

</details>

## 跨平台面板

需要已有 **Python 3.10+（含 SQLite 标准库）**，无需 pip 依赖。下载 dashboard 源码包或本仓库，完整解压后运行：

```sh
python3 -I -X utf8 dashboard.py
# macOS / Linux 的启动脚本
sh start-dashboard.sh
```

Windows 可双击运行启动脚本，或在 PowerShell 中执行：

```powershell
.\StartDashboard.ps1
# 指定现有 Python
.\StartDashboard.ps1 -PythonExecutable 'C:\path\to\python.exe'
```

面板支持 Windows、macOS、Linux 上有兼容本地日志的 Desktop / CLI / IDE 对话；不依赖当前窗口标题或界面语言。点击“小窗”可打开独立浏览器窗口，使用系统窗口边框拖动和调整大小。macOS/Linux 现已提供独立原生悬浮条，可按准确 ID 固定对话，或明确跟随最新活动日志。

从 Windows 状态栏打开的面板还可点击 **“将此对话固定到 Windows 状态栏”**：按准确 ID 固定并置顶显示，适用于 CLI、IDE，或桌面标题识别暂不可用的情况。固定和自动模式分别保存布局，右键“解除固定，自动跟随桌面对话”可恢复自动模式。

状态栏和托盘的右键菜单始终保留 **“按对话 ID 固定状态栏…”**，点击后打开上述面板选择对话。解除固定后，识别期间状态栏显示 **“正在识别当前对话”**，继续保留右键操作，并清空原固定对话的速率。状态栏窗口独立运行，所跟随的 Codex 窗口关闭或重建不会销毁它。

再次启动 EXE 会通知已有进程重新显示状态栏。也可双击托盘图标，或右键选择 **“显示状态栏”**。启动后会先显示等待状态，并恢复旧快捷方式要求的最小化或隐藏状态，显示过程不抢走其他应用的焦点。自动跟随 Codex 后切到其他应用会隐藏；需要跨应用持续显示时可固定对话。诊断文件记录 Windows 的实际可见、置顶和最小化状态。

```sh
# 自定义 Codex 数据目录
python3 dashboard.py --codex-home /path/to/.codex
# 离线日志、无数据库；可重复指定 --log 读取多个续聊片段
python3 dashboard.py --log /path/to/rollout.jsonl
# WSL / SSH / 无图形界面
python3 dashboard.py --no-browser --port 8765
```

服务只监听 `127.0.0.1`，使用随机本地访问凭据，无外部资源和遥测。SSH 使用相同端口的本地转发，详见 [兼容范围](docs/COMPATIBILITY.md)。纯云端且没有可读本地日志的对话无法统计。其他可运行兼容 Python 的架构具备源码入口，未实机验证的平台不视为已验收。

## Windows 原生状态栏（上游兼容模式）

便携包包含对应 CPU 的私有 Python，无需安装运行时或 pip 包。保留解压后的所有文件，运行 `CodexTokenHud.exe`，将本地 Codex Desktop 对话切到前台。

- x64：Windows 10/11 与 .NET Framework 4.8，本机已验证。
- ARM64：已有匹配 Python 的打包选项；原生运行需兼容 .NET Framework 4.8.1 或仿真环境，未在 ARM64 硬件验证。
- x86：提供 32 位 Python 包，主要用于浏览器/命令行入口；Codex Desktop 自身不一定支持该架构。

程序为 AnyCPU 构建，仍未签名。为读取管理员运行的 Codex，无障碍访问需要匹配权限，管理员账号可能看到 UAC 提示。把状态条中间拖动到需要的位置；拖动边缘或角落改变大小，布局自动保存。

**“立即刷新数据”会重新扫描本地日志、查找新的续聊片段并重读最新文件。** 状态栏显示“正在重新读取日志”“已刷新 · 无新计数”或具体失败状态。刷新不能让 Codex 提前产生尚未写入的 token 计数。采集器不响应时会自动重连。

随应用启动：右键菜单，或以管理员 PowerShell 运行 `Install.ps1`。`Uninstall.ps1` 仅删除本副本创建的等待器注册与快捷方式，保留布局。退出状态栏会同时停止它的采集器和统计服务。

## 上游状态栏与统计工具的数据含义

| 字段 | 含义 |
| --- | --- |
| 模型 | 当前本地对话最近记录的模型；区间混用模型时列出涉及的模型 |
| 速率 tok/s | 最近一次完成响应的输出 token / 生成秒数 |
| 缓存 | 同一响应的 cached_input_tokens / input_tokens |
| 更新时间 | 已完成响应的记录时间；超过 15 分钟、切换模型、新一轮尚无计数时隐藏旧值 |
| 本对话周额度 | 暂无数据；服务没有提供按对话归属的周额度扣减及总额 |

实时视图是最近响应的平均值，响应结束后才有计数，不是逐 token 瞬时速度。采集器每 100 ms 检查新记录，至少每秒发布心跳；状态栏每 150 ms 更新，约每 350 ms 检查当前对话；浏览器每秒更新。续聊文件目录每 2 秒重新发现，手动刷新立即扫描。新对话、无记录、日志不可读或数据库不兼容会明确显示状态；有日志时面板可绕过数据库选择。

## 命令行报告

```sh
python3 stats.py --list
python3 stats.py --thread THREAD_ID --start '2026-10-09T10:00:00+08:00' --end '2026-10-09T11:00:00+08:00'
python3 stats.py --thread THREAD_ID --messages '消息片段'
python3 stats.py --thread THREAD_ID --first FIRST_MESSAGE_ID --last LAST_MESSAGE_ID
```

输出为本地 JSON，适合无桌面环境。`--messages` 会显示用户消息片段供选择，请勿公开该输出。

## 数据与开发

默认读取 `~/.codex`，支持 `CODEX_HOME`、`--codex-home`、归档日志和显式离线日志。Windows 状态栏另支持 `settings.example.json` 中的 Python 与数据路径设置。派生统计和布局保存在 Windows 的 `%LOCALAPPDATA%\CodexTokenHud`、macOS 的 `~/Library/Application Support/CodexTokenHud`、Linux 的 `~/.local/state/codex-token-hud`，可用 `CODEX_TOKEN_HUD_HOME` 覆盖。不要上传这些目录、真实会话、认证文件或含聊天正文的截图。完整说明见 [隐私](PRIVACY.md)。

```powershell
.\Build.ps1
.\Test.ps1
.\Package.ps1 -Architecture x64
# 跨架构产物，不在构建机执行目标架构冒烟测试
.\Package.ps1 -Architecture arm64 -SkipTests -SkipSmokeTest
.\Package.ps1 -Architecture x86 -SkipTests -SkipSmokeTest
```

```sh
python3 -m unittest discover -s tests -p 'test_*.py'
python3 PackageDashboard.py
```

Windows 构建使用已有 Framework 编译器（优先 64 位，回退 32 位），运行中的 EXE 先用 `Stop.ps1` 退出，或构建到 `Build.ps1 -OutputDirectory build\app`。打包按白名单复制，下载固定 Python 并校验下载摘要；`-SkipTests -SkipSmokeTest` 可在已经做过针对性验证时避免重复全量检查。GitHub Actions 配置了 Windows 检查和 macOS/Linux 的采集、区间统计、本地 HTTP 检查；是否实际通过以具体运行结果为准。

[兼容范围](docs/COMPATIBILITY.md) · [验证记录](docs/VALIDATION.md) · [更新日志](CHANGELOG.md) · [MIT 许可](LICENSE)
