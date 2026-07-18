# OpenClaw 功能移植记录

泺栋 Chat 采用 C# 原生实现所需能力，不捆绑 OpenClaw、Node.js 或其后台 Gateway。这样可以保持单目录安装、减少常驻进程，并让 Windows 权限和生命周期完全由客户端控制。

## 已完成

- 项目目录内的文件列出、搜索、读取、写入、精确替换、移动和安全删除。
- 前台命令执行、超时取消、输出截断、敏感环境变量清理。
- 请求批准、替我审批、完全访问、自定义四种项目级权限。
- 仅本次、当前会话和项目级永久批准；永久批准只保存精确操作指纹。
- Windows Job Object 进程树约束，客户端退出时终止受管进程。
- 受控启动项目内 EXE、列出运行程序、停止指定程序及子进程。
- 命令和程序批准绑定项目、工作目录、参数、Shell 与超时等上下文。
- 项目脚本和 EXE 内容哈希绑定；文件被替换后原批准自动失效。
- 内联解释器、高风险命令载体禁止加入永久白名单。
- 项目级本地审计，敏感字段遮盖和日志轮换。

## 安全边界

- 工具只能使用用户明确选择的项目目录。
- `.git`、依赖目录、凭据目录、敏感文件和重解析点默认不可访问。
- 启动程序额外允许项目内 `bin/build/dist/out/target` 构建产物目录。
- 提权、关机、磁盘管理、系统账户、服务和关键系统设置始终禁止。
- “完全访问”跳过逐次询问，但不会绕过上述永久阻止规则。
- Job Object 不是完整 AppContainer；当前仍不宣称具备强文件系统和网络隔离。

## 后续候选

1. 使用 Windows AppContainer 或受支持的 Windows 沙箱 API，实现真正的文件和网络边界。
2. 为长时间命令增加后台任务、增量输出、任务恢复和取消面板。
3. 增加可审阅的可执行文件签名、发布者和哈希信息。
4. 为常见只读命令建立严格安全工具集，而不是扩大通用 Shell 白名单。
5. 接入 MCP 时保持工具可见性、权限策略和进程隔离三层独立。

参考：[OpenClaw Exec approvals](https://github.com/openclaw/openclaw/blob/main/docs/tools/exec-approvals.md)、[OpenClaw security model](https://github.com/openclaw/openclaw/blob/main/docs/gateway/security/index.md)。
