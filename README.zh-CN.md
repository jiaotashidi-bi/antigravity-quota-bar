# Antigravity Quota Bar · 额度与上下文监视器

在 Google Antigravity 桌面版写消息时，直接看见 **Gemini 周额度、5 小时额度、重置倒计时和当前对话上下文余量**。额度条与输入框等宽，会跟着当前聊天切换。

[下载 Windows x64 版](https://github.com/jiaotashidi-bi/antigravity-quota-bar/releases/latest/download/antigravity-quota-bar-windows-x64.zip) · [English](README.md) · [MIT 许可证](LICENSE)

![Antigravity 输入框额度条示例，包含 Gemini、Claude/GPT、Argon 和上下文余量](preview.png)

*图中为演示数据。模型组和额度周期以你的账户返回的结果为准。*

## 怎么读

- **周、5h** 分别表示周额度和 5 小时额度，百分比是剩余量。时钟后的 `5d9h` 表示距重置还有 5 天 9 小时。若只显示某一个周期，说明该组只提供这个周期。
- **Claude / GPT** 表示两个模型系列共享这一组额度。Gemini 4 Argon 有独立额度时会单独显示。
- **上下文余 ≈63%** 表示当前对话上一次模型请求开始时，窗口估计还有 63% 可用。新对话尚无请求记录时显示 `—`。

将鼠标停在「上下文剩余」（窄屏显示为「上下文余」）或旁边的百分比上，即可看到上次请求的预计已用和剩余 token 数，以及窗口上限。记录中有模型名称时也会显示。移开鼠标即可收起。

额度每 60 秒查询一次，倒计时随本机时钟更新。打开模型菜单或切换到其他应用时，额度条自动隐藏。窗口变窄时会先简写，再换行。

## 安装

已在 **Windows x64、Antigravity 桌面版 2.34.0** 上验证。查询账户额度需要 **Google Antigravity CLI 1.1.11 或更新版本**。

1. 在同一个 Windows 用户下安装 Antigravity 桌面版和官方 CLI。首次使用时可运行 `agy`，按提示登录。[Google 官方 CLI 安装说明](https://github.com/google-antigravity/antigravity-cli#installation)
2. 下载并解压 [Windows 安装包](https://github.com/jiaotashidi-bi/antigravity-quota-bar/releases/latest/download/antigravity-quota-bar-windows-x64.zip)。
3. 在解压目录打开 PowerShell，运行下面的命令。

```powershell
.\install.ps1
```

打开 Antigravity 并进入聊天，额度条就会出现在输入框上方。桌面应用通过 Sidecar 自动启停额度条。

如果 Windows 拦截下载的脚本，可以在 ZIP 文件的「属性」中勾选「解除锁定」后重新解压。如果当前终端限制脚本执行，可在解压目录运行 `powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\install.ps1`，策略只对这次 PowerShell 进程生效。

## 数据来源与排查

额度来自官方 CLI 的 `agy -p "/usage" --output-format json`。插件仅在自己的后台查询中关闭 CLI 自更新，并让 Windows 阻止查询启动辅助进程，避免更新器在刷新时弹出终端。你仍可在自己的终端中更新 CLI。

如果未读到额度，可以在终端运行上面的命令检查登录状态。打开 **View Usage** 时，插件会同步原生面板上的百分比。百分比旁的 `—` 表示服务没有提供重置时间。

上下文读数来自当前会话本机 SQLite 中最近一次模型调用的记录。插件以只读方式提取该次请求的已用估算和窗口上限，跳过聊天正文和工具输出。切换模型或压缩上下文后，数值会随下次模型请求更新。

## 编译与卸载

在源码目录运行 `.\build.ps1` 可以使用 Windows 自带的 .NET Framework x64 编译器生成程序。安装位置是 `%LOCALAPPDATA%\GeminiQuota`。

升级时，退出 Antigravity，在新版安装包的解压目录运行 `.\install.ps1`。卸载时，退出 Antigravity，在解压目录或源码目录运行 `.\install.ps1 -Uninstall`。

这是面向 Google Antigravity 的社区项目。[Sidecar 官方说明](https://antigravity.google/docs/sidecars/) · [MIT 许可证](LICENSE)
