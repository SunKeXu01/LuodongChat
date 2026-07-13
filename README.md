# ChatGPT 连接器

ChatGPT 连接器目前处于预览验证阶段。本仓库包含面向 Codex／OpenAI Responses API 的网关服务、生产部署组件，以及 Windows 桌面配置客户端。

## 快速入口

- [下载最新版 Windows 客户端](https://github.com/SunKeXu01/ChatGPTConnector/releases)
- [直接下载 ChatGPTConnector.exe](https://520skx.com/client/download/ChatGPTConnector.exe)
- [查看生产运维文档](docs/OPERATIONS.md)
- [查看数据模型](docs/DATA_MODEL.md)

## 已实现功能

- 支持 `POST /responses` 和 `POST /v1/responses` 流式代理
- 使用 SHA-256 哈希校验用户网关密钥
- 基于 PostgreSQL 管理网关密钥、有效期及每个密钥的配额
- 用户通过邮箱验证码自助领取密钥，并可主动轮换密钥
- 使用 Redis 原子实现每日、每分钟及并发限制
- 流式响应开始前，针对可重试的上游故障自动重试一次
- 支持多个上游凭据，并按照负载选择
- 维护降级、断开、半开等凭据健康状态并自动恢复
- 上游返回 401／403 时立即隔离故障凭据并切换
- 记录每次上游尝试的元数据，不记录请求正文或响应正文
- 为身份、凭据、请求、调用尝试、用量和成本提供 PostgreSQL 数据模型
- 支持配置备份、完整性校验、原子替换及三方合并恢复
- 提供请求 ID 和仅包含元数据的日志
- 提供 `GET /healthz` 健康检查及当前部署提交号
- 提供带身份验证的管理后台、审计历史和临时登录封禁
- 提供部署历史、主机隔离的排队回滚、每日备份和健康监控
- 备份、部署、网关、磁盘、证书和上游异常使用中文邮件告警
- GitHub 测试通过后，使用受限 SSH 账户自动部署生产环境
- Windows 客户端支持网关验证、配置备份、冲突检测和安全恢复
- Windows 客户端支持启动时检查版本、SHA-256 校验和一键更新

当前预览版已经包含 PostgreSQL 请求元数据、Redis 分布式限流、上游凭据故障切换、安全管理后台、自动部署与回滚，以及 Windows 客户端。客户端目前尚未进行代码签名，计费对账、多供应商智能路由和更广泛的 Windows 兼容性测试仍在完善中。

## 本地开发

需要 Node.js 22 或更高版本，以及 pnpm。

```bash
pnpm install
cp .env.example .env
pnpm test
pnpm test:integration
pnpm typecheck
pnpm build
```

可以使用以下命令生成开发网关密钥的 SHA-256 哈希，应用日志不会打印原始密钥：

```bash
node -e "const c=require('node:crypto'); console.log(c.createHash('sha256').update(process.argv[1]).digest('hex'))" gw_test_change_me
```

导出 `.env` 中的环境变量后运行 `pnpm dev`。项目有意不自动读取 `.env`，使本地开发与生产环境都通过明确的方式注入敏感配置。

## 安全边界

- 不要提交 `.env`、网关密钥或上游凭据。
- 代理不会记录请求正文或响应正文。
- 只有在尚未向调用方发送任何上游响应内容时才允许重试。
- 内存限流器只用于单进程本地验证；生产环境应使用 Redis。
- 客户端只从 `520skx.com` 的 HTTPS 更新通道下载更新，并强制校验 SHA-256。

## 容器部署

`compose.yaml` 定义了网关、PostgreSQL 17 和 Redis 7.4，并包含健康检查和持久化卷。网关默认只监听本机地址，生产环境应在入口处配置 TLS 终止。

```bash
docker compose up --build
```

设置 `DATABASE_URL` 后，网关会在启动时检查 PostgreSQL 连接并使用持久化请求台账。设置 `REDIS_URL` 后，网关会启用分布式请求及并发限制。未设置这些变量时，本地开发会回退到内存实现。

构建完成后，使用 `pnpm migrate` 在 PostgreSQL 咨询锁保护下执行待处理迁移。每个迁移名称都会写入 `schema_migrations`，不会重复执行。

生产健康检查、每日 PostgreSQL 备份、保留策略和恢复命令详见 [`docs/OPERATIONS.md`](docs/OPERATIONS.md)。

生产镜像通过摘要固定版本。GitHub CI 会依次运行网关单元测试、真实 PostgreSQL／Redis 迁移与限流集成测试、.NET 核心测试和 Windows 独立程序构建；全部通过后才允许自动部署生产环境。

## Windows 客户端

Windows 客户端位于 `client/`，使用 .NET 10 LTS 和 WPF。`ChatGPTConnector.Core` 包含与平台无关的配置规划及安全文件安装逻辑，其测试可在 macOS 或 Windows 上运行。

```bash
dotnet test client/ChatGPTConnector.Core.Tests/ChatGPTConnector.Core.Tests.csproj
dotnet build client/ChatGPTConnector.App/ChatGPTConnector.App.csproj
```

客户端具有以下能力：

- 通过邮箱验证码自助获取网关密钥
- 在修改配置前检查网关是否可用
- 预览受管理的 Codex 配置变更
- 创建带完整性校验的配置备份
- 原子写入配置，并在恢复时检测冲突
- 修改配置前检测正在运行的 ChatGPT／Codex 进程
- 查找并启动已经安装的桌面应用
- 未安装时提供官方软件下载入口
- 清楚标注当前连接属于 API 密钥模式
- 在可执行文件和主窗口中使用多分辨率客户端图标

客户端启动时会检查 `520skx.com` 更新通道。发现新版本后由用户确认更新，客户端通过 HTTPS 下载可执行文件，校验发布文件的 SHA-256，退出后替换旧程序并自动启动新版本。

## 下载 Windows 预览版

推荐前往 [Releases](https://github.com/SunKeXu01/ChatGPTConnector/releases) 下载最新预览版：

1. 打开最新的预览版 Release。
2. 直接下载 `ChatGPTConnector.exe`，或下载 Windows x64 ZIP 压缩包。
3. 如需手动验证文件完整性，可同时下载对应的 `.sha256` 文件。
4. 在 Windows 10／11 x64 上运行 `ChatGPTConnector.exe`。

也可以从 [生产更新通道直接下载客户端](https://520skx.com/client/download/ChatGPTConnector.exe)。

当前预览版尚未进行代码签名，Windows 可能提示“无法识别的应用”。请只运行从本仓库 Releases、Actions 或上述生产更新通道下载的文件。
