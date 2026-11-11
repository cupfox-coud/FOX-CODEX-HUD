# FOX CODEX HUD · corner.55

更新公共重置雷达的请求策略：保持每 3 分钟查询状态，信号未变时复用已验证的来源时间，并定期复核；信号变化、来源过期或缺失时重新核验。共享出口的限流仍以服务端规则为准，重启不跳过已知的限流等待。

包含 corner.49–54 的全局多任务输出、会话上下文与 70% / 85% 交接提醒、跟随会话、额度消耗速度与今日建议、原有分组边界以及局部柔和加速扫光。全局重置和发卡继续分开提醒。

![原生界面模拟动画](https://raw.githubusercontent.com/cupfox-coud/FOX-CODEX-HUD/main/docs/images/fox-hud-realtime.gif)

[查看模拟截图与功能说明](https://github.com/cupfox-coud/FOX-CODEX-HUD/blob/main/docs/SHOWCASE.md)

发布后，Windows x64 用户可下载 `FOX-CODEX-HUD-1.3.0-beta.8.corner.55-win-x64.zip` 与 SHA-256 校验文件。完整解压到固定目录后运行 `Install.ps1`，包内已包含 Python。公共重置信号不代表个人额度已经到账。
