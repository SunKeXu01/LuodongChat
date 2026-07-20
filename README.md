# 泺栋chat

泺栋chat 是面向 Windows 10/11 x64 与 ARM64 的独立 GPT-5.6 对话客户端。用户只需安装泺栋chat并使用邮箱账号登录，无需安装官方 ChatGPT、配置 API 密钥或修改本机 Codex 文件。Android 版本当前暂停维护。

## 主要功能

- 邮箱注册、密码登录、验证码登录与密码重置
- 已注册邮箱再次注册时明确提醒用户直接登录
- 无效邮箱不会请求发送验证码
- GPT-5.6 流式回复，默认提供 Responses API 联网搜索工具，使用平衡型搜索上下文并展示可点击来源，无需用户配置
- 在普通对话中直接说“生成一张……图片”即可生图，图片直接显示在回复中并保存在本机
- 居中的微信式左右消息气泡，支持 Markdown、代码块、可点击链接、逐条复制和重新生成
- Codex 风格对话附件：支持多选、在整个聊天区域拖拽文件，以及通过 Ctrl+V 直接粘贴剪贴板图片；图片、PDF、Office、文本和代码文件可与文字一起发送给 GPT 分析
- 长对话提供用户问题定位轨道，可通过问题摘要快速跳转，并自动标记当前阅读位置
- Windows 对话界面采用紧凑侧栏和轻量工具栏，常用操作使用统一矢量图标，侧栏账号区显示用户头像
- Windows 窗口支持最大化与高 DPI 缩放，长回答可完整滚动和选择
- Windows 多会话历史仅保存在本机，可从侧栏切换并右键删除
- Windows 支持项目空间：GPT 可读取、搜索和编辑项目文件，可通过 Codex 风格补丁一次精确修改多个文件，也可运行构建、测试、联网下载等命令；长时间命令会返回受控会话，可继续读取输出、写入交互输入或终止进程；还能受控启动项目中的 EXE、查看运行状态并停止由泺栋 Chat 启动的程序。用户可选择“请求批准、替我审批、完全访问、自定义”四种权限模式，并按项目查看仅保存在本机的操作审计记录
- 账号在服务端绑定独立内部凭据，密钥不会展示或下发给用户
- 个人资料支持网名、完整邮箱和头像
- Windows 系统托盘与客户端自动更新
- Windows 支持六套内置皮肤（包含护眼绿），登录前后即时切换并记住选择
- 不读取、不修改、不映射 `.codex/config.toml`、`auth.json` 或 `CODEX_HOME`

## 下载

- [OSS 高速下载最新版 Windows x64 安装程序](https://oss.520skx.com/latest/LuodongChat-Setup.exe)
- [OSS 高速下载最新版 Windows ARM64 安装程序](https://oss.520skx.com/latest/LuodongChat-ARM64-Setup.exe)（Apple 芯片虚拟机、Windows ARM 设备）
- [GitHub Releases（含历史版本）](https://github.com/SunKeXu01/LuodongChat/releases)
- [Android 最后一个稳定版](https://oss.520skx.com/latest/LuodongChat.apk)（暂停维护）

GitHub Release 和 OSS 文件均包含版本号，例如 `LuodongChat-2.0.6-win-x64-setup.exe`、`LuodongChat-2.0.6-win-arm64-setup.exe` 及各自的便携 ZIP，并同时提供 SHA-256 校验文件。OSS 还保留不带版本号的固定地址用于自动更新，并且只存储最新版，不积累历史安装包。客户端会根据自身架构自动选择后续更新。Android 当前暂停维护，保留最后一个稳定版的固定下载地址。

当前 Windows 版尚未进行代码签名。如果系统显示“Windows 已保护你的电脑”，请确认安装包来自本仓库或上述下载地址，然后点击“更多信息”→“仍要运行”。Android 版使用固定发布证书签名，更新时会覆盖旧版本并保留账号数据。

## Windows 使用方式

1. 普通 Intel/AMD 电脑下载 x64 安装包；Apple 芯片虚拟机或 Windows ARM 设备下载 ARM64 安装包。运行后可自行选择安装目录，支持中文、空格等 Unicode 路径；默认目录为 `%LOCALAPPDATA%\Programs\LuodongChat`。
2. 首次使用时选择“注册 / 重置密码”，输入邮箱、密码和邮件中的 6 位验证码。
3. 注册成功后可直接使用邮箱和密码登录。
4. 进入“聊天”页面即可与 GPT-5.6 对话；需要最新信息时会自动联网。直接提出“生成一张……图片”一类要求时，生成结果会显示在当前对话中。
5. 消息以左右气泡显示，可选择任意文字复制；按 Enter 发送、Shift+Enter 换行。窗口可最大化，长回答可持续向下滚动。生成过程中可以点击“停止”，也可以复制当前对话或新建会话。历史对话显示在左侧，右键即可删除。
6. 点击“新建对话”可以选择普通对话或项目空间。普通对话仍可正常聊天，但不会获得本地文件、命令或程序启动能力；输入框内会持续提示用户先将当前对话加入项目空间。选择本地项目目录后，该会话会永久归入对应项目。输入框旁会显示当前访问权限：请求批准会逐次确认修改和命令；替我审批自动执行常规项目文件操作，但运行命令、启动程序、删除或发布前仍会询问；完全访问不逐次询问；自定义可分别设置读取、写入、联网和危险操作为允许、询问或禁止。权限按项目目录独立保存，切换到未授权的新目录会恢复默认“请求批准”；命令确认窗口支持“仅本次允许”“本次会话允许”和“始终允许此命令”。始终允许只保存不可逆的 SHA-256 指纹，并严格绑定项目、工作目录、完整命令或程序参数、Shell 类型和超时时间；任一内容变化都会重新询问，也可以从权限菜单清空。GPT 只能启动项目目录内的 Windows EXE，不能借此启动项目外程序；它可以列出和停止由泺栋 Chat 在当前项目中启动的程序，客户端完全退出时也会统一停止这些程序。文件工具默认限制在所选项目目录，并排除密钥文件、版本库元数据、依赖目录、构建产物、符号链接和目录联接。命令不会继承 API 密钥环境变量，也不会自动获得管理员权限；提权、关机、磁盘、系统账户和关键系统设置在任何模式下都禁止。Windows 命令及其子进程会加入 Job Object 进程容器，限制同时活动的进程数量和桌面、剪贴板、显示设置、系统参数等交互，并在停止、超时或容器关闭时统一终止；这仍不是具备文件和网络边界的完整 AppContainer 沙箱，开启完全访问前应确认任务可信。
7. 点击输入框左下角回形针可一次选择多个附件，也可把文件拖到聊天页面任意位置，或在输入框聚焦时按 Ctrl+V 直接粘贴截图。单个文档最大 40 MB，图片及其他附件最大 20 MB，每条消息最多 10 个且合计不超过 48 MB；服务器临时附件 30 分钟后自动删除，已发送附件仅随本地对话保存在当前安装目录。
8. 同一会话达到 2 个用户问题后，消息区左侧会显示问题定位轨道；悬停可查看问题摘要，点击即可跳转到对应用户消息。

关闭窗口右上角叉号会隐藏到系统托盘。需要完全退出时，右键托盘图标并选择“退出”。退出泺栋chat不会更改用户电脑上的 Codex 或其他 AI 软件配置。

安装版只在所选的 `LuodongChat` 目录中存放程序和数据，目录内包含 `Uninstall.exe`，并为当前用户创建桌面快捷方式；卸载时会自动删除该快捷方式。ZIP 版解压即用，登录状态、日志、对话历史与更新缓存统一保存在解压目录下的 `data` 子目录。对话正文不会保存在泺栋chat服务器。程序不会把运行数据写入其他用户目录。

## Android 历史稳定版

Android 历史稳定版采用 Kotlin 与 Jetpack Compose，登录账号后可直接流式对话并自动使用联网搜索。该版本当前暂停维护，后续恢复开发时再继续发布新版 APK。

## 自动更新

Windows 客户端启动时会自动检查新版本，在后台限速下载并校验 SHA-256，下载完成后才询问是否更新；选择暂不更新后，下次启动再次询问。安装版通过新版安装程序在原目录覆盖升级，ZIP 便携版原地替换主程序；两者均保留 `data`。

## 服务信息

- 服务地址：`https://520skx.com`（客户端主入口）、`https://luodongchat.com`（同步域名）
- 客服：`2554798585（QQ）`
- 项目主页：<https://github.com/SunKeXu01/LuodongChat>

## 开源参考

聊天交互设计参考了 [NextChat](https://github.com/ChatGPTNextWeb/NextChat)、[DeepChat](https://github.com/ThinkInAIXYZ/deepchat) 与 [Chatbox](https://github.com/chatboxai/chatbox) 的产品思路；皮肤状态管理参考了 MIT 许可的 [Codex Dream Skin](https://github.com/Fei-Away/Codex-Dream-Skin)；项目文件工具的工作区隔离、工具策略和审批设计参考了 [OpenClaw](https://github.com/openclaw/openclaw)，权限模式、审批交互和长命令会话参考了 [OpenAI Codex CLI](https://github.com/openai/codex)，并参考 [Qwen Code](https://github.com/QwenLM/qwen-code)、[Gemini CLI](https://github.com/google-gemini/gemini-cli) 与 [Kimi CLI](https://github.com/MoonshotAI/kimi-cli)。泺栋 Chat 使用 WPF 与 C# 独立实现这些能力，不捆绑上述项目的运行时，也不复制外部项目的受限素材。参见 [OpenClaw 功能移植记录](docs/openclaw-porting.md)、[Codex CLI 本地执行能力移植记录](docs/codex-cli-porting.md)和[开源模型接入评估](docs/open-model-compatibility.md)。
