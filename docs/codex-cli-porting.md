# OpenAI Codex CLI 本地执行能力移植记录

本次研究基于 OpenAI 官方 `openai/codex` 仓库提交 `0fb559f0f6e2`。泺栋 Chat 没有把 Rust CLI、Node.js 或 Codex 登录体系打包进客户端，而是依据其公开的工具边界，在现有 C# 项目空间安全模型上独立实现必要能力。

## 借鉴的架构

Codex 的 unified exec 将命令执行拆成两个工具：

- `exec_command`：运行命令；短命令直接返回，长命令等待一段时间后返回会话 ID。
- `write_stdin`：使用会话 ID 继续写入标准输入，或用空输入轮询新增输出。

它还把命令审批、运行环境、进程生命周期和输出截断分离处理。泺栋 Chat 延续这一边界，但复用自己的项目权限、Windows Job Object 和本地审计实现。

## 已实现

- 新增 `apply_patch`：支持 Codex 补丁格式中的新增、更新、移动和删除文本文件。
- 补丁会先完整解析并在内存中验证全部上下文，任一文件不匹配时不会修改任何文件。
- 补丁批准绑定补丁内容、源文件 SHA-256 和目标路径状态；用户确认后文件发生变化会要求重新批准。
- 多文件提交前创建事务备份，写入使用同目录临时文件替换；中途失败会恢复原文件。
- `run_command` 增加 `yield_time_ms`：短命令保持原行为，长命令返回 `session_id` 和 `running` 状态。
- 新增 `write_stdin`：支持轮询增量输出、写入交互输入、终止进程树。
- 每次 AI 回复使用独立会话管理器；会话 ID 不能跨回复、跨项目复用。
- 每个进程有 5～3600 秒的硬超时，回复结束或用户停止生成时会清理尚未结束的命令。
- 标准输出和错误输出分开收集，每次最多保留 96,000 个字符；超限时明确返回 `output_truncated`。
- Windows 命令继续加入 Job Object，客户端清理会话时同时终止子进程。
- 命令启动仍需经过原有权限判断和审批；`write_stdin` 只操作已经审批并由当前管理器持有的进程，不会启动新命令。
- 命令仍只在用户选择的项目空间内启动，并清除 API Key、访问令牌、密码等敏感环境变量。

## 没有照搬的部分

- Codex 的平台级沙箱、网络代理、PTY、远程执行环境和登录 Shell 没有直接移植。
- Responses API 当前通过结构化函数参数传递 `apply_patch` 文本，而不是 Codex 的 Lark freeform custom tool；补丁正文格式保持兼容。
- 泺栋 Chat 当前使用标准输入/输出管道，不伪装成完整终端；全屏 TUI、复杂 ANSI 交互程序可能无法正常工作。
- “完全访问”仍不是 AppContainer。永久禁止的提权、关机、磁盘、系统账户和关键系统设置命令不会放行。

## 验证重点

- 普通构建命令在等待时间内完成并返回退出码。
- 多文件补丁可新增、更新、移动、删除；上下文失败、路径穿越和敏感文件修改均被拒绝。
- 长命令先返回会话 ID，后续轮询取得剩余输出与最终退出码。
- 交互命令可接收输入；未知会话 ID 被拒绝。
- 回复结束时未完成进程被终止，避免后台遗留。
- 原有路径穿越、敏感文件、命令风险分类和审批哈希测试继续通过。

参考源码：[OpenAI Codex](https://github.com/openai/codex)、[`exec_command` 工具定义](https://github.com/openai/codex/blob/main/codex-rs/core/src/tools/handlers/shell_spec.rs)、[`write_stdin` 处理器](https://github.com/openai/codex/blob/main/codex-rs/core/src/tools/handlers/unified_exec/write_stdin.rs)。仓库采用 Apache-2.0 许可证；本项目此次为独立 C# 实现，没有复制 Rust 源码。
