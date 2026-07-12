# 阶段 0：技术验证记录

## 已确认

- 本机 Codex CLI 为 `0.144.0-alpha.4`。
- 用户配置位于 `~/.codex/config.toml`。
- Codex CLI 支持通过 `--config key=value` 覆盖配置，并可用 `--strict-config` 检查未知字段。
- 当前仓库尚无既有代码，首个原型采用模块化 TypeScript/Node.js，先验证网关协议。
- 上游公开地址 `https://code.heihuzi.ai` 可通过 HTTPS 访问，并由 nginx 提供服务。
- 上游教程指定 `wire_api = "responses"`、`requires_openai_auth = true`，凭证由 `auth.json` 的 `OPENAI_API_KEY` 提供。
- 仓库内测试夹具只使用无效占位密钥，真实密钥不得进入 Git。
- 使用隔离 `CODEX_HOME` 和 `--strict-config` 验证后，教程配置可被 Codex `0.144.0-alpha.4` 正确加载；诊断结果识别出模型 `gpt-5.5`、provider `ConnectorTest`、Responses 协议和 API Key 认证。
- Codex 对该教程中的根 `base_url` 探测 `/models`，因此连接器网关同时接受 `/responses` 和常见的 `/v1/responses`。
- 使用真实上游凭证验证 `POST https://code.heihuzi.ai/responses`：`gpt-5.5` 返回标准 JSON、完成状态和用量对象。
- 通过本地连接器网关完成流式端到端验证：HTTP 200、`text/event-stream`、9 组 SSE 事件，并收到 `response.completed`。
- `GET https://code.heihuzi.ai/models` 当前返回 HTML 控制台页面而非模型目录 JSON；客户端和网关不能把该探测结果当作模型能力证明。

## 第一条端到端路径

```text
Codex -> POST /v1/responses -> 用户网关密钥鉴权 -> 限流 -> 单一上游 -> 流式响应
```

原型只支持 Responses API。这是有意缩小范围：先验证 Codex 主链路，再决定是否兼容 Chat Completions。

## 尚待实测

1. 当前目标 Windows Codex 版本与教程字段的兼容矩阵。
2. 自定义 provider 使用 Responses API 时的错误处理。
3. 工具调用的完整流式事件序列。
4. 客户端取消后，上游是否立即终止计费。
5. 上游对 Codex 所需模型和参数的兼容程度。

在以上字段经过官方文档和真实客户端共同验证前，Windows 客户端不得写入用户配置。

## 下一阶段

- 加入可替换的上游适配器接口。
- 使用 PostgreSQL 保存用户、密钥 ID、请求和尝试记录。
- 使用 Redis 实现分布式限流、并发和短期粘性。
- 实现双凭证健康状态与熔断。
- 建立 Windows 配置修改器的备份、差异和原子替换测试夹具。

## 网关原型进度

- 已实现多凭证池，优先选择当前负载较低的健康凭证。
- 已实现 `healthy`、`degraded`、`open`、`half_open` 状态及冷却后试探恢复。
- 上游 401/403 会立即永久隔离对应凭证，首个输出前切换下一凭证。
- 网络错误、429 和 5xx 会累计失败并触发熔断。
- 每次上游尝试记录请求 ID、尝试序号、脱敏凭证 ID、状态和耗时，不记录正文或密钥。
- 请求账本接口已接入网关完整生命周期，并通过测试确认不保存用户输入。
- PostgreSQL 初始迁移已覆盖用户、网关密钥、上游、加密凭证、用户请求和逐次上游尝试。
- PostgreSQL 与 Redis 正式驱动已接入启动流程；配置对应 URL 时启动前检查连接，未配置时使用内存实现。
- 网关已验证可在新增运行时装配后正常构建、启动并通过 `/healthz` 检查。
- 配置写入基线已实现备份元数据、原文件 SHA-256、互斥锁、临时文件同步及同目录原子替换。
- 三方恢复算法已验证：保留非受管字段的用户修改，安全恢复未冲突字段，并单独报告受管字段冲突。
