# Antigravity Quota Bar

Keep an eye on **Gemini quota, reset countdowns, and the remaining context window** while you type in Google Antigravity for Windows. One slim bar spans the chat input and follows you from conversation to conversation.

[Download for Windows x64](https://github.com/jiaotashidi-bi/antigravity-quota-bar/releases/latest/download/antigravity-quota-bar-windows-x64.zip) · [中文安装与使用说明](README.zh-CN.md) · [MIT license](LICENSE)

在 Google Antigravity 输入框上方，一行查看 Gemini、Claude / GPT 的剩余额度、重置倒计时，以及当前对话的上下文余量。

![Antigravity Quota Bar showing Gemini weekly and five-hour limits, Claude and GPT shared quota, reset countdowns, and remaining chat context](preview.png)

*Example values. The Windows interface uses Chinese labels. Read the bar from left to right by model group.*

## Reading the bar

| On the bar | What it tells you |
| --- | --- |
| **周** and **5h** | The percentage left in the weekly and five-hour windows. The clock shows time until reset. For example, `2h43m` means 2 hours and 43 minutes. |
| **Gemini**, **Claude / GPT**, **Argon** | Groups come from your account. Claude and GPT share a row segment when Google pools their quota. A group with only a weekly limit shows just that window. |
| **上下文余 ≈63%** | About 63% of the active chat’s request window was available when the last model request began. A new chat shows `—` until it has a recorded request. |

Quota refreshes every 60 seconds. The clock counts down between checks. Narrowing the window shortens labels first, then wraps the bar if needed. You can click through it, and it gets out of the way when you open the model menu or switch apps.

## Install on Windows

Tested with **Google Antigravity desktop 2.34.0 on Windows x64**. The quota reader uses **Antigravity CLI 1.1.11 or newer**.

1. Install the desktop app and Google’s official CLI on the same Windows account. Run `agy` once to sign in if prompted. [Official CLI installation instructions](https://github.com/google-antigravity/antigravity-cli#installation)
2. Download and extract the [Windows x64 release ZIP](https://github.com/jiaotashidi-bi/antigravity-quota-bar/releases/latest/download/antigravity-quota-bar-windows-x64.zip).
3. Open PowerShell in the extracted folder and run the installer.

```powershell
.\install.ps1
```

Open Antigravity and enter a chat. Antigravity starts the bar through its Sidecar mechanism. Right-click the green tray icon to refresh or select **对话上下文详情** to see the last model, estimated tokens used, remaining tokens, and request limit.

If Windows blocks the downloaded scripts, open the downloaded ZIP’s **Properties**, select **Unblock** if shown, and extract it again. For a shell with a restricted execution policy, you can run the installer in a separate PowerShell process with `powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File .\install.ps1`.

## Where the readings come from

The quota check runs `agy -p "/usage" --output-format json` under your existing Google sign-in. Opening **View Usage** also lets the bar match the percentages on screen. A dash beside a percentage means the service did not supply a reset time.

The context monitor identifies the conversation attached to the visible input box. It opens that conversation’s local SQLite database **read-only** and reads the latest request’s token estimate and window limit. It skips message bodies and tool output. Switching models or compacting a chat changes the displayed estimate when a new model request records those changes.

Antigravity updates can change the desktop UI or database format. For a missing quota reading, run the CLI command above to check that the account reports usage. For a missing context reading, check whether this chat has completed a model request.

## Build or remove

From a source checkout, run `.\build.ps1` to compile `GeminiQuota.exe` with the Windows .NET Framework x64 compiler. Run `.\install.ps1` to install for the current user.

To upgrade, close Antigravity and run `.\install.ps1` from the new release folder. To uninstall, close Antigravity and run `.\install.ps1 -Uninstall` from the release folder or checkout.

This is a community project for Google Antigravity. [Antigravity Sidecars documentation](https://antigravity.google/docs/sidecars/) · [MIT license](LICENSE)
