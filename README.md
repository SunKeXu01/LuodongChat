# ChatGPT 连接器

面向 Windows 10/11 x64 与 Android 10+ 的 GPT-5.6 客户端。用户只需通过邮箱验证码注册或登录，无需领取、查看或填写网关密钥。

## 主要功能

- 邮箱验证码自动注册和登录
- 每个账号在服务端自动绑定唯一的内部网关凭据，凭据不会下发给用户
- 未登录时无法使用连接功能
- 登录凭证使用 Windows DPAPI 在本机加密保存
- 首页显示本地连接状态和 GPT-5.6
- 个人页面支持完整邮箱、网名和头像
- 消费页面预留余额与流水功能；正式启用前不会扣费
- 客户端运行并保持登录时，本地代理才会工作
- 退出登录、退出软件或异常终止后自动恢复原始 Codex 环境
- Windows 使用受管配置目录和目录联接，不覆盖用户原来的 `.codex/config.toml` 和 `auth.json`
- Windows 与 Android 使用同一账号同步聊天会话和消息
- 系统托盘、自动更新、版本校验和中文客服入口

## 下载

- [GitHub Releases](https://github.com/SunKeXu01/ChatGPTConnector/releases)
- [直接下载最新 EXE](https://520skx.com/client/download/ChatGPTConnector.exe)

Release 中的 EXE、ZIP 和校验文件均包含版本号，例如 `ChatGPTConnector-0.1.0-preview.11-win-x64.exe`。

当前预览版尚未进行代码签名。如果 Windows 显示“Windows 已保护你的电脑”，请确认文件来自本仓库或上述下载地址，然后点击“更多信息”→“仍要运行”。

## 使用方式

1. 完全退出正在运行的 ChatGPT 或 Codex。
2. 打开连接器，输入邮箱并获取 6 位验证码。
3. 输入验证码并点击“登录 / 注册”。
4. 登录成功后，连接器自动创建独立的托管 Codex 环境、将默认 `.codex` 目录联接到该环境并启动本地代理。
5. 若 ChatGPT 或 Codex 已经运行，请重新启动一次。
6. 使用期间保持连接器运行并处于登录状态，可以最小化到系统托盘。

关闭窗口右上角叉号只会隐藏到系统托盘。需要完全退出时，右键托盘图标并选择“退出”。完全退出或退出登录后，连接器会拆除受管目录联接、恢复原来的 `.codex` 目录和此前的 `CODEX_HOME`；如果用户原来没有 `.codex` 目录，退出后仍保持不存在。

## Android 预览版

Android 客户端采用 Kotlin 与 Jetpack Compose，支持 Android 10 及以上版本。首版包括邮箱登录、GPT-5.6 流式聊天、安全保存登录会话和跨端聊天同步。Android 版是独立聊天客户端，不会修改其他 Android 应用的配置。

## 个人资料

“个人”页面可以修改 2～20 个字符的网名，并上传不超过 512 KB 的 JPG、PNG 或 WebP 头像。邮箱完整显示，不做脱敏。

## 自动更新

客户端启动时自动检查新版本。更新文件仅从 `https://520skx.com` 下载，并在替换程序前校验 SHA-256。

## 服务信息

- 网关：`https://520skx.com`
- 客服：`2554798585（QQ）`
- 项目主页：<https://github.com/SunKeXu01/ChatGPTConnector>
