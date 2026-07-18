# 开源模型兼容性评估

本文用于指导泺栋 Chat 网关接入可自托管模型。模型权重、推理引擎、代理框架和模型服务是四个不同层次；“模型支持工具调用”不代表它天然拥有文件、命令或联网权限，所有本地能力仍必须经过客户端工具策略、用户批准和进程隔离。

## 第一批候选

| 模型 | 适合场景 | 已确认能力 | 接入注意事项 |
| --- | --- | --- | --- |
| Qwen3 / Qwen3-Coder | 中文、编码、本地代理 | Qwen-Agent 提供函数调用、并行工具调用、MCP、RAG 与代码解释器；可通过 vLLM 等部署 | Qwen3-Coder 需要匹配其工具调用解析器；不要把代码解释器直接暴露在宿主机 |
| Kimi K2.5 | 图片理解、长任务、编码代理 | 原生视觉语言、图片/视频输入、多步工具调用，官方接口兼容 OpenAI/Anthropic | 模型规模较大；视频在第三方自托管接口上的支持与官方接口并不完全相同 |
| MiMo-V2-Flash | 中文推理、编码、低激活参数代理 | 309B 总参数、15B 激活参数、最长 256K 上下文、专用工具调用解析器 | 推荐部署仍需要多卡服务器；多轮工具调用必须保留 reasoning_content |
| GLM-4.7-Flash / GLM-4.7 | 编码、工具调用、较轻量自托管 | 工具调用、交错思考；Flash 为 30B-A3B，官方说明可用单张 H100 运行基础配置 | vLLM/SGLang 需要指定 GLM 工具与推理解析器 |
| MiniMax-M2.5 / M2.1 | 编码和长链路代理 | 官方提供工具调用说明和开放权重 | 先通过兼容测试再进入生产池，不依据宣传榜单直接替换主模型 |
| DeepSeek V3 系列 | 中文、推理、编码备选 | 开放模型权重并支持自部署 | 不同版本和服务商的工具调用格式差异较大，应单独验证 reasoning_content 与流式工具事件 |

## 网关接入原则

1. 桌面端继续使用稳定的统一请求合同，不感知服务商模型名。
2. 每个上游端点显式配置 `model`、`supportsWebSearch` 和 `supportsImageGeneration`，不再仅凭密钥位置推断能力。
3. `model` 只在网关转发时覆盖，密钥和真实路由不下发客户端。
4. 当前直连端点必须兼容 OpenAI Responses 请求与流式事件。只提供 Chat Completions 的模型，应先接入独立协议适配器，不能简单改 URL。
5. 联网搜索是服务端工具能力，不是模型参数。自托管模型需要泺栋 Chat 自己提供搜索工具，或使用明确支持搜索工具的模型服务。
6. 文件读写和命令执行始终在用户电脑本地完成；模型只能发出结构化工具请求，不能直接取得宿主机权限。

## 上线前兼容测试

- 基础中文对话与流式输出。
- 多轮上下文，尤其是 reasoning_content 是否必须回传。
- 单工具、连续多工具、并行工具调用及无效参数恢复。
- 图片输入、PDF/Office 提取文本后的理解质量。
- 联网搜索调用、来源引用和上游不支持时的降级。
- 取消请求、超时、429、401、5xx 与故障切换。
- 工具调用是否会伪造路径、绕过工作区或重复执行。

只有全部通过的端点才可声明对应能力并进入生产池。

## 参考资料

- [Qwen-Agent](https://github.com/QwenLM/Qwen-Agent)
- [Kimi K2.5](https://github.com/MoonshotAI/Kimi-K2.5)
- [MiMo-V2-Flash](https://github.com/XiaomiMiMo/MiMo-V2-Flash)
- [GLM-4.5 / 4.6 / 4.7](https://github.com/zai-org/GLM-4.5)
- [MiniMax-M2.5](https://github.com/MiniMax-AI/MiniMax-M2.5)
- [DeepSeek-V3](https://github.com/deepseek-ai/DeepSeek-V3)
- [OpenClaw exec approvals](https://github.com/openclaw/openclaw/blob/main/docs/tools/exec-approvals.md)
- [OpenClaw security model](https://github.com/openclaw/openclaw/blob/main/docs/gateway/security/index.md)
