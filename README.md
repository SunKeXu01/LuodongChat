# 泺栋chat

泺栋chat 是面向 Windows 10/11 x64 与 Android 10+ 的独立 GPT-5.6 对话客户端。用户只需安装泺栋chat并使用邮箱账号登录，无需安装官方 ChatGPT、配置 API 密钥或修改本机 Codex 文件。

## 主要功能

- 邮箱注册、密码登录、验证码登录与密码重置
- 已注册邮箱再次注册时明确提醒用户直接登录
- 无效邮箱不会请求发送验证码
- GPT-5.6 流式回复，按问题自动联网搜索并展示可点击来源，无需用户配置
- 在普通对话中直接说“生成一张……图片”即可生图，图片直接显示在回复中并保存在本机
- 微信式左右消息气泡，消息文字可自由选择和复制
- Windows 窗口支持最大化与高 DPI 缩放，长回答可完整滚动和选择
- Windows 多会话历史仅保存在本机，可从侧栏切换并右键删除
- 账号在服务端绑定独立内部凭据，密钥不会展示或下发给用户
- 个人资料支持网名、完整邮箱和头像
- Windows 系统托盘与客户端自动更新
- Android 新版检测、哈希校验和系统覆盖安装
- 不读取、不修改、不映射 `.codex/config.toml`、`auth.json` 或 `CODEX_HOME`

## 下载

- [OSS 高速下载最新版 Windows 安装程序](https://luodongchat-app.oss-cn-beijing.aliyuncs.com/latest/LuodongChat-Setup.exe)
- [GitHub Releases（含历史版本）](https://github.com/SunKeXu01/LuodongChat/releases)
- Android APK 请从 [GitHub Releases](https://github.com/SunKeXu01/LuodongChat/releases/latest) 下载

GitHub Release 文件均包含版本号，例如 `LuodongChat-1.6-win-x64-setup.exe`、`LuodongChat-1.6-win-x64-portable.zip` 与 `LuodongChat-1.6-android.apk`，并同时提供 SHA-256 校验文件。OSS 同步存储 Windows 与 Android 最新版且不积累旧安装包；Android 对外下载暂由 GitHub Release 提供。

当前 Windows 版尚未进行代码签名。如果系统显示“Windows 已保护你的电脑”，请确认安装包来自本仓库或上述下载地址，然后点击“更多信息”→“仍要运行”。Android 版使用固定发布证书签名，更新时会覆盖旧版本并保留账号数据。

## Windows 使用方式

1. 下载并运行安装程序，可自行选择安装目录，支持中文、空格等 Unicode 路径；默认目录为 `%LOCALAPPDATA%\Programs\LuodongChat`。
2. 首次使用时选择“注册 / 重置密码”，输入邮箱、密码和邮件中的 6 位验证码。
3. 注册成功后可直接使用邮箱和密码登录。
4. 进入“聊天”页面即可与 GPT-5.6 对话；需要最新信息时会自动联网。直接提出“生成一张……图片”一类要求时，生成结果会显示在当前对话中。
5. 消息以左右气泡显示，可选择任意文字复制；窗口可最大化，长回答可持续向下滚动。生成过程中可以点击“停止”，也可以复制当前对话或新建会话。历史对话显示在左侧，右键即可删除。

关闭窗口右上角叉号会隐藏到系统托盘。需要完全退出时，右键托盘图标并选择“退出”。退出泺栋chat不会更改用户电脑上的 Codex 或其他 AI 软件配置。

安装版只在所选的 `LuodongChat` 目录中存放程序和数据，目录内包含 `Uninstall.exe`，并为当前用户创建桌面快捷方式；卸载时会自动删除该快捷方式。ZIP 版解压即用，登录状态、日志、对话历史与更新缓存统一保存在解压目录下的 `data` 子目录。对话正文不会保存在泺栋chat服务器。程序不会把运行数据写入其他用户目录。

## Android 使用方式

Android 版采用 Kotlin 与 Jetpack Compose，登录账号后可直接流式对话并自动使用联网搜索。消息支持长按选择复制，搜索来源可直接点击；顶部同时提供“复制当前对话”按钮。当前阶段不提供历史记录列表。发现新版时，登录页和聊天页都会显示更新提醒，下载并校验 APK 后交由 Android 系统覆盖安装。

## 自动更新

Windows 与 Android 客户端启动时都会自动检查新版本。Windows 在后台限速下载并校验 SHA-256，下载完成后才询问是否更新；选择暂不更新后，下次启动再次询问。安装版通过新版安装程序在原目录覆盖升级，ZIP 便携版原地替换主程序；两者均保留 `data`。Android 由系统安装器覆盖旧版本。

## 服务信息

- 服务地址：`https://520skx.com`（客户端主入口）、`https://luodongchat.com`（同步域名）
- 客服：`2554798585（QQ）`
- 项目主页：<https://github.com/SunKeXu01/LuodongChat>

## 开源参考

聊天交互设计参考了 [NextChat](https://github.com/ChatGPTNextWeb/NextChat)、[DeepChat](https://github.com/ThinkInAIXYZ/deepchat) 与 [Chatbox](https://github.com/chatboxai/chatbox) 的产品思路。NextChat 使用 MIT，DeepChat 使用 Apache-2.0，Chatbox 社区版使用 GPL-3.0。本项目只独立实现所需交互，不复制 Chatbox GPL 代码。
