# Codex API Bridge

[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

Codex API Bridge is a cross-platform .NET 10 local proxy that translates OpenAI/Codex-style Responses API requests into Chat Completions requests for third-party model providers. It includes a browser-based admin console, local file logs, runtime statistics, Responses tool compatibility, and optional HTTP/MCP tool forwarding.

Codex API Bridge 是一个跨平台 .NET 10 本地代理，用于把 Codex 使用的 Responses API 请求转换为第三方模型常见的 Chat Completions API。项目内置浏览器管理台、本地日志、运行统计、Responses 工具兼容转换，以及可选的 HTTP/MCP 工具转发执行。

> This project is community software and is not affiliated with OpenAI, Codex, DeepSeek, or any third-party API provider.
>
> 本项目是社区软件，与 OpenAI、Codex、DeepSeek 或任何第三方 API 服务商均无官方关联。

## 中文说明

### 功能特性

- 将 `/v1/responses` 和 `/responses` 转换为上游 `/v1/chat/completions`。
- 支持 `/v1/chat/completions` 与 `/v1/models` 原样透传。
- 内置中文 HTML 管理台：配置、统计、日志、工具转发规则都可在浏览器中查看和修改。
- 支持本地日志文件：默认写入 `logs/bridge-yyyyMMdd.log`。
- 支持 Responses 工具兼容：`custom`、`image_generation`、`namespace`、可配置的 `web_search` 等工具会降级为 Chat Completions function。
- 支持工具自动执行：模型发起 function call 后，桥接器可调用 HTTP 网关、MCP stdio 或 MCP SSE，再把结果作为 `tool` 消息回灌给上游模型。
- 支持 DeepSeek 等模型常见的 `reasoning_content` 兼容与兜底。
- 支持流式响应转换，并在工具调用后继续下一轮上游请求。
- 支持 Windows、macOS、Linux。

### 快速开始

从 Release 下载适合系统的压缩包，解压后复制配置文件：

```bash
cp bridge-config.example.json bridge-config.json
```

修改 `bridge-config.json`：

```json
{
  "listen_url": "http://127.0.0.1:11434",
  "upstream_base_url": "https://api.example.com",
  "upstream_api_key": "sk-your-third-party-key",
  "model_override": "",
  "forward_incoming_authorization": true,
  "enable_reasoning_content_compatibility": true,
  "enable_tool_forwarding_execution": true
}
```

启动：

```bash
./CodexApiBridge --config bridge-config.json
```

Windows：

```powershell
.\CodexApiBridge.exe --config bridge-config.json
```

macOS 如果提示 `operation not permitted` 或被 Gatekeeper 阻止，可在解压目录执行：

```bash
xattr -dr com.apple.quarantine .
chmod +x CodexApiBridge
```

启动后打开管理台：

```text
http://127.0.0.1:11434/admin
```

把 Codex 或兼容客户端的 API Base URL 指向：

```text
http://127.0.0.1:11434/v1
```

### 配置项

| 字段 | 说明 |
| --- | --- |
| `listen_url` | 本地监听地址，默认 `http://127.0.0.1:11434`。 |
| `upstream_base_url` | 第三方模型 API Base URL，例如 `https://api.example.com`。 |
| `upstream_api_key` | 上游 API Key。为空时可通过 `forward_incoming_authorization` 转发客户端 Authorization。 |
| `model_override` | 可选。强制替换请求中的模型名称。 |
| `request_timeout_seconds` | 请求超时时间，默认 600 秒。 |
| `log_directory` | 日志目录，默认 `logs`。 |
| `enable_verbose_body_logging` | 是否记录完整请求体。可能包含敏感信息，默认关闭。 |
| `enable_reasoning_content_compatibility` | 是否启用 `reasoning_content` 兼容。 |
| `enable_missing_reasoning_content_fallback` | 当历史 thinking 消息缺失 `reasoning_content` 时是否自动补兜底文本。 |
| `enable_tool_forwarding_execution` | 是否自动执行已配置网关地址的工具调用。 |
| `max_tool_forwarding_iterations` | 工具调用后继续请求上游模型的最大轮数。 |
| `tool_forwarding_rules` | Responses 工具到 Chat Completions function/外部网关的映射规则。 |

### 工具转发

当规则满足 `forward_mode != "function"` 且 `forward_endpoint` 不为空时，桥接器会自动执行模型发起的 function call。

支持的 `forward_mode`：

| 模式 | 说明 |
| --- | --- |
| `function` | 只把 Responses 工具声明降级为 Chat Completions function，不由桥接器执行。 |
| `http` | POST 到一个普通 HTTP 网关。 |
| `mcp_stdio` | 启动 MCP stdio server，通过 JSON-RPC 调用 `tools/call`。 |
| `mcp_sse` | 连接 MCP SSE server，读取 `endpoint` 事件后通过 JSON-RPC 调用 `tools/call`。 |

HTTP 网关收到的请求格式：

```json
{
  "call_id": "call_xxx",
  "name": "web_search",
  "responses_tool_type": "web_search",
  "arguments": { "query": "..." },
  "raw_arguments": "{\"query\":\"...\"}",
  "forward_mode": "http"
}
```

HTTP 网关可以返回纯文本，也可以返回 JSON。JSON 中优先读取 `output`、`result`、`content`、`text` 字段；否则整个 JSON 会作为工具结果传回模型。

MCP stdio 示例：

```json
{
  "enabled": true,
  "responses_tool_type": "web_search",
  "function_name": "web_search",
  "description": "Search the web with an MCP server.",
  "parameters_json": "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\"}},\"required\":[\"query\"],\"additionalProperties\":true}",
  "forward_mode": "mcp_stdio",
  "forward_endpoint": "npx -y your-mcp-search-server"
}
```

MCP SSE 示例：

```json
{
  "enabled": true,
  "responses_tool_type": "web_search",
  "function_name": "web_search",
  "description": "Search the web with an MCP SSE server.",
  "parameters_json": "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\"}},\"required\":[\"query\"],\"additionalProperties\":true}",
  "forward_mode": "mcp_sse",
  "forward_endpoint": "http://127.0.0.1:8000/sse"
}
```

### 开发

需要 .NET 10 SDK。

```bash
dotnet restore
dotnet build -c Release
dotnet run -- --config bridge-config.json
```

发布本机运行包：

```bash
dotnet publish ./CodexApiBridge.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true
dotnet publish ./CodexApiBridge.csproj -c Release -r osx-arm64 --self-contained true -p:PublishSingleFile=true
dotnet publish ./CodexApiBridge.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

### GitHub Actions 发布

仓库已包含 `.github/workflows/build-and-release.yml`：

- Push/PR 到 `main` 或 `master`：执行 restore 和 build。
- 手动运行 workflow：生成 Windows、Linux、macOS x64、macOS arm64 构建产物。
- 推送 `v*` 标签：自动创建 GitHub Release 并上传二进制压缩包。

示例：

```bash
git tag v0.1.0
git push origin v0.1.0
```

### 安全提醒

- 管理台当前没有鉴权，建议只监听 `127.0.0.1`，不要直接暴露到公网。
- 开启 `enable_verbose_body_logging` 会记录完整请求体，可能包含 API Key、提示词、文件内容或隐私信息。
- `mcp_stdio` 会执行本地命令，必须只配置你信任的 MCP server。
- `mcp_sse` 和 HTTP 网关可能把工具参数发送给外部服务，请确认数据合规。
- 本项目是协议桥接工具，不保证任何第三方模型能够 100% 等价实现 Responses API 的全部行为。

### 许可证

MIT License，见 [LICENSE](LICENSE)。

## English

### Features

- Converts `/v1/responses` and `/responses` requests to upstream `/v1/chat/completions`.
- Passes through `/v1/chat/completions` and `/v1/models`.
- Provides a built-in browser admin console for settings, logs, statistics, and tool forwarding rules.
- Writes local log files to `logs/bridge-yyyyMMdd.log` by default.
- Downgrades Responses tools such as `custom`, `image_generation`, `namespace`, and configurable tools like `web_search` to Chat Completions functions.
- Can automatically execute model-initiated function calls through HTTP gateways, MCP stdio servers, or MCP SSE servers, then feed the result back as a `tool` message.
- Includes compatibility handling for `reasoning_content`, useful for providers such as DeepSeek.
- Supports streaming response conversion and multi-turn continuation after tool calls.
- Runs on Windows, macOS, and Linux.

### Quick Start

Download a release archive for your platform, extract it, and create a local config:

```bash
cp bridge-config.example.json bridge-config.json
```

Edit `bridge-config.json`:

```json
{
  "listen_url": "http://127.0.0.1:11434",
  "upstream_base_url": "https://api.example.com",
  "upstream_api_key": "sk-your-third-party-key",
  "model_override": "",
  "forward_incoming_authorization": true,
  "enable_reasoning_content_compatibility": true,
  "enable_tool_forwarding_execution": true
}
```

Run:

```bash
./CodexApiBridge --config bridge-config.json
```

Windows:

```powershell
.\CodexApiBridge.exe --config bridge-config.json
```

If macOS blocks the binary with `operation not permitted` or Gatekeeper quarantine, run this in the extracted directory:

```bash
xattr -dr com.apple.quarantine .
chmod +x CodexApiBridge
```

Open the admin console:

```text
http://127.0.0.1:11434/admin
```

Point Codex or another compatible client to:

```text
http://127.0.0.1:11434/v1
```

### Configuration

| Field | Description |
| --- | --- |
| `listen_url` | Local listen URL. Defaults to `http://127.0.0.1:11434`. |
| `upstream_base_url` | Third-party model API base URL, for example `https://api.example.com`. |
| `upstream_api_key` | Upstream API key. If empty, incoming Authorization can be forwarded. |
| `model_override` | Optional model name override. |
| `request_timeout_seconds` | Request timeout in seconds. Defaults to 600. |
| `log_directory` | Log directory. Defaults to `logs`. |
| `enable_verbose_body_logging` | Logs full request bodies. Disabled by default because it may contain sensitive data. |
| `enable_reasoning_content_compatibility` | Enables `reasoning_content` compatibility handling. |
| `enable_missing_reasoning_content_fallback` | Adds fallback reasoning text when a previous thinking message lacks `reasoning_content`. |
| `enable_tool_forwarding_execution` | Automatically executes tool calls that have a forwarding endpoint. |
| `max_tool_forwarding_iterations` | Maximum continuation rounds after forwarded tool calls. |
| `tool_forwarding_rules` | Mapping rules from Responses tools to Chat Completions functions and optional external gateways. |

### Tool Forwarding

When a rule has `forward_mode != "function"` and a non-empty `forward_endpoint`, the bridge executes matching function calls automatically.

Supported `forward_mode` values:

| Mode | Description |
| --- | --- |
| `function` | Only downgrade the Responses tool declaration to a Chat Completions function. The bridge does not execute it. |
| `http` | POST the tool call to a plain HTTP gateway. |
| `mcp_stdio` | Start an MCP stdio server and call `tools/call` via JSON-RPC. |
| `mcp_sse` | Connect to an MCP SSE server, read the `endpoint` event, and call `tools/call` via JSON-RPC. |

HTTP gateway request body:

```json
{
  "call_id": "call_xxx",
  "name": "web_search",
  "responses_tool_type": "web_search",
  "arguments": { "query": "..." },
  "raw_arguments": "{\"query\":\"...\"}",
  "forward_mode": "http"
}
```

The HTTP gateway may return plain text or JSON. For JSON, `output`, `result`, `content`, and `text` are preferred in that order; otherwise the whole JSON document is returned as the tool result.

MCP stdio example:

```json
{
  "enabled": true,
  "responses_tool_type": "web_search",
  "function_name": "web_search",
  "description": "Search the web with an MCP server.",
  "parameters_json": "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\"}},\"required\":[\"query\"],\"additionalProperties\":true}",
  "forward_mode": "mcp_stdio",
  "forward_endpoint": "npx -y your-mcp-search-server"
}
```

MCP SSE example:

```json
{
  "enabled": true,
  "responses_tool_type": "web_search",
  "function_name": "web_search",
  "description": "Search the web with an MCP SSE server.",
  "parameters_json": "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\"}},\"required\":[\"query\"],\"additionalProperties\":true}",
  "forward_mode": "mcp_sse",
  "forward_endpoint": "http://127.0.0.1:8000/sse"
}
```

### Development

Install the .NET 10 SDK.

```bash
dotnet restore
dotnet build -c Release
dotnet run -- --config bridge-config.json
```

Publish self-contained binaries:

```bash
dotnet publish ./CodexApiBridge.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true
dotnet publish ./CodexApiBridge.csproj -c Release -r osx-arm64 --self-contained true -p:PublishSingleFile=true
dotnet publish ./CodexApiBridge.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

### GitHub Actions Releases

This repository includes `.github/workflows/build-and-release.yml`:

- Push/PR to `main` or `master`: restore and build.
- Manual workflow dispatch: publish Windows, Linux, macOS x64, and macOS arm64 artifacts.
- Push a `v*` tag: create a GitHub Release and upload binary archives.

Example:

```bash
git tag v0.1.0
git push origin v0.1.0
```

### Security Notes

- The admin console has no authentication. Listen on `127.0.0.1` unless you put it behind your own trusted access control.
- `enable_verbose_body_logging` records full request bodies and may capture API keys, prompts, files, or private data.
- `mcp_stdio` executes local commands. Only configure MCP servers you trust.
- `mcp_sse` and HTTP gateways may send tool arguments to external services. Check your data policy first.
- This project is a protocol bridge. It cannot guarantee that every third-party model provider will behave exactly like the Responses API.

### License

MIT License. See [LICENSE](LICENSE).
