# MCP 与 Skills 使用说明

## MCP

在 Windows 客户端侧栏底部点击“…”→“MCP 与 Skills”，打开 MCP 服务器页。

支持两种连接方式：

- `stdio`：启动本机 MCP 进程，例如 `npx`、`uvx`、`python` 或独立 EXE。参数按行填写。子进程不会继承客户端中的 API Key、登录令牌等环境变量，只会获得官方 MCP SDK 提供的最小系统环境变量和用户明确填写的变量。
- `HTTP`：支持 Streamable HTTP，并自动兼容旧版 SSE。可填写自定义请求头。

客户端使用官方 `ModelContextProtocol` C# SDK 完成协议握手、能力发现与调用。目前支持：

- Tools：转换为 Responses API 函数工具；工具名自动加服务器命名空间，避免覆盖本地工具。
- Resources 与 Resource Templates：模型可先列出，再在用户确认后读取。
- Prompts：模型可列出参数，并在用户确认后获取生成的消息。
- 取消：用户停止回答时，取消令牌会传递到 MCP 请求。

服务器配置保存在安装目录的 `data/mcp-servers.dat`。Windows 下配置整体使用当前 Windows 用户的 DPAPI 加密，其他 Windows 账户无法直接解密。配置不会上传到泺栋 Chat 服务器。

安全规则：

- 只添加可信 MCP 服务器。stdio 服务器是本机程序，HTTP 服务器是第三方网络服务。
- 每个第三方 MCP Tool 首次调用都会显示服务器和工具名称，可选择仅本次允许或本次会话允许。
- Resources 与 Prompts 的具体内容在发送给 GPT 前同样需要确认。
- 退出账号会清除本次会话的 MCP 批准记录。

## Skills

Skills 兼容 Codex/Agent Skills 的基本目录结构：

```text
my-skill/
├── SKILL.md
├── references/
├── scripts/
└── assets/
```

`SKILL.md` 必须以 YAML frontmatter 开始，并至少包含：

```markdown
---
name: my-skill
description: Explain what the skill does and when it should be used.
---

# Instructions

Follow these steps...
```

安装后可以全局启用，也可以在消息中显式写 `$my-skill`。客户端采用渐进加载：

1. 只把已安装技能的名称和简短描述提供给模型。
2. 全局启用或显式点名时加载完整 `SKILL.md`。
3. 模型确实需要引用文件时，再调用 `read_skill_file` 读取文本资源。

Skills 保存在安装目录的 `data/skills`。扫描和读取会拒绝目录穿越、绝对路径、符号链接以及二进制资源，并限制单次文本长度。Skills 中的说明不能绕过项目空间权限或 MCP 确认。

当前版本不会自动执行 Skills 自带脚本。脚本执行会引入额外的宿主机权限风险；后续只有在加入独立脚本权限、签名与隔离机制后才会开放。技能仍可组合客户端已有的项目工具和 MCP 工具完成任务。
