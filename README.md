# C# Multi-Provider AI Proxy for Visual Studio 2026 Ollama Provider 

as of 9. May 2026 use Visual Studio Insider Version

**Why?**

See 
DeepSeek V4 AI Beats Billion Dollar Systems…For (almost) Free

https://www.youtube.com/watch?v=p7K3xfViWCE

A high-performance, ultra-low-overhead HTTP proxy that connects GitHub Copilot and Ollama clients to **DeepSeek, OpenAI, and NVIDIA NIM** APIs. Built with .NET 10 and ASP.NET Core minimal APIs for maximum throughput and minimal allocations.

| 🏷️ | Details |
|---|---|
| **Author** | iqmeta GmbH — Otto Neff |
| **Version** | `2026.06.02` |
| **Providers** | `deepseek`, `openai`, `nvidia` (configurable) |
| **Models** | Auto-discovered from each provider |
| **Default Port** | `11434` |
| **Framework** | .NET 10 |
| **Deploy** | Docker / bare metal |

---

## Screenshots

<p align="center">
  <img src="images/github_copilot_deepseek_v4-0.jpg" alt="DeepSeek Copilot Proxy Screenshot 1" width="45%" />
  <img src="images/github_copilot_deepseek_v4-1.jpg" alt="DeepSeek Copilot Proxy Screenshot 2" width="45%" />
</p>
<p align="center">
  <img src="images/github_copilot_deepseek_v4-2.jpg" alt="DeepSeek Copilot Proxy Screenshot 3" width="45%" />
  <img src="images/github_copilot_deepseek_v4-3.jpg" alt="DeepSeek Copilot Proxy Screenshot 4" width="45%" />
</p>





## Features

- **🧠 Reasoning Content Caching** — Automatically captures DeepSeek's `reasoning_content` from streaming and non-streaming responses, and re-injects it on subsequent assistant messages for true multi-turn reasoning.
- **🌐 Multi-Provider Support** — Configure DeepSeek, OpenAI, and/or NVIDIA NIM. Models are auto-discovered from each provider's API. Requests are automatically routed to the correct backend based on model name.
- **🔄 Dual API Compatibility**
  - **OpenAI-compatible** endpoint (`POST /v1/chat/completions`) — works with GitHub Copilot, Cursor, Continue.dev, and any OpenAI SDK.
  - **Ollama-compatible** endpoints (`GET /api/version`, `GET /api/tags`, `GET/POST /api/show`, `POST /api/chat`) — works with Visual Studio BYOM and Ollama-compatible clients.
- **⚡ Ultra-Performance** — Uses `SocketsHttpHandler` with connection pooling (256 connections/server, HTTP/2 multiplexing), `SlimBuilder`, and pass-through streaming.
- **📦 Zero Allocation Streaming** — SSE (Server-Sent Events) are streamed through without buffering, with minimal allocations during parsing.
- **🔌 No External Dependencies** — Uses only built-in ASP.NET Core and `System.Text.Json`.
- **🐳 Docker-Ready** — Multi-stage Dockerfile (chiseled runtime, ~40 MB overhead) and `docker-compose.yml` included.
- **🔐 Optional Proxy Auth** — Set `PROXY_API_KEY` to require `Bearer` authentication on all endpoints.

---

## Quick Start

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) or [Docker](https://www.docker.com/)
- A DeepSeek API key

### 1. Configure

Copy `.env.example` to `.env` and set your API key:

```bash
cp .env.example .env
# Edit .env → set DEEPSEEK_API_KEY=sk-your-real-key
```

All configuration lives in environment variables:

| Variable | Default | Description |
|---|---|---|
| `PROVIDER_DEEPSEEK_API_KEY` | — | DeepSeek API key |
| `PROVIDER_DEEPSEEK_BASE_URL` | `https://api.deepseek.com` | DeepSeek API base URL |
| `PROVIDER_OPENAI_API_KEY` | — | OpenAI API key (optional) |
| `PROVIDER_OPENAI_BASE_URL` | `https://api.openai.com` | OpenAI API base URL |
| `PROVIDER_NVIDIA_API_KEY` | — | NVIDIA NIM API key (optional) |
| `PROVIDER_NVIDIA_BASE_URL` | `https://integrate.api.nvidia.com` | NVIDIA NIM API base URL |
| `DEEPSEEK_API_KEY` | — | Legacy: works if no `PROVIDER_*` vars are set |
| `DEEPSEEK_MODEL` | `deepseek-v4-pro` | Fallback model if client request does not include `model` |
| `PROXY_PORT` | `11434` | Port the proxy listens on |
| `PROXY_API_KEY` | *optional* | If set, clients must send `Authorization: Bearer <key>` |

> **Backward compatible**: If only `DEEPSEEK_API_KEY` is set (without `PROVIDER_*` vars), the proxy works as before with DeepSeek only.

### Provider Model Selection (JSON files)

Model selection is modular and provider-specific via JSON files:

- `config/model-selection/deepseek.json`
- `config/model-selection/openai.json`
- `config/model-selection/nvidia.json`

Format:
- `provider`: provider key (`deepseek`, `openai`, `nvidia`)
- `models`: ordered list of preferred model IDs/patterns

The proxy filters discovered models using the provider's file and preserves the declared order as priority.

### Supported Models & Capabilities

Models are auto-discovered from each configured provider. Typical defaults:

| Provider | Example Models | Context | Max Output | Tools | Vision |
|---|---|---|---|---|---|
| DeepSeek | `deepseek-v4-pro`, `deepseek-v4-flash` | 1M | 384K | ✅ | ❌ |
| OpenAI | `gpt-4o`, `gpt-4o-mini` | 128K | 16K | ✅ | ✅ |
| NVIDIA NIM | `meta/llama-3.3-70b`, `nvidia/nemotron-4-340b` | 128K | 4K | ✅ | ❌ |

### 2a. Run with Docker (recommended)

```bash
docker compose up -d
```

### 2b. Run bare metal

```bash
dotnet run
```

You should see:

```
╔══════════════════════════════════════════════════════════════════╗
║   DeepSeek / Multi-Provider Copilot Proxy (Ultra)               ║
╠══════════════════════════════════════════════════════════════════╣
║  Default: deepseek-v4-pro                                        ║
║  Providers: deepseek                                              ║
║  Models:   deepseek-v4-flash, deepseek-v4-pro                    ║
║  URL:     http://localhost:11434/v1                              ║
║  Auth:    open (no key set)                                      ║
╚══════════════════════════════════════════════════════════════════╝
```

---

## Endpoints

### Health Check

```http
GET /health
```

Response:
```json
{ "status": "ok", "model": "deepseek-v4-pro", "available_models": ["deepseek-v4-flash", "deepseek-v4-pro"] }
```

### List Models (OpenAI-style)

```http
GET /v1/models
```

### Chat Completions (OpenAI-style)

```http
POST /v1/chat/completions
```

Full OpenAI chat completions API — supports both streaming (`stream: true`) and non-streaming modes. Handles tool calls, multi-turn reasoning, and vision (image) inputs.

### Ollama API

```http
GET /api/version
GET /api/tags
GET /api/show
POST /api/show
POST /api/chat
```

Converts Ollama's message format to OpenAI format transparently and proxies to DeepSeek. Supports `messages`, `tools`, and `stream`. `/api/tags` and `/api/show` include model capabilities and token limits for Visual Studio BYOM compatibility.

---

## Configuration Guide

### GitHub Copilot

Configure your Copilot client to use the proxy:

```json
{
  "github.copilot.advanced": {
    "debug.chatOverride": {
      "provider": "openai",
      "endpoint": "http://localhost:11434/v1/chat/completions",
      "model": "deepseek-v4-flash"
    }
  }
}
```

If `PROXY_API_KEY` is set, add the auth header:

```json
{
  "github.copilot.advanced": {
    "debug.chatOverride": {
      "provider": "openai",
      "endpoint": "http://localhost:11434/v1/chat/completions",
      "model": "deepseek-v4-flash",
      "apiKey": "your-proxy-key"
    }
  }
}
```

### Ollama

Point any Ollama client to the proxy:

```bash
ollama run deepseek-v4-flash --api http://localhost:11434/api/chat
```

Or use the OpenAI compatibility mode with Ollama clients:

```bash
OLLAMA_HOST=http://localhost:11434 ollama serve
```

### Continue.dev / Cursor

Configure the OpenAI-compatible endpoint:

```json
{
  "models": [{
    "title": "DeepSeek V4",
    "provider": "openai",
    "model": "deepseek-v4-flash",
    "apiBase": "http://localhost:11434/v1"
  }]
}
```

---

## How Reasoning Caching Works

DeepSeek responses include a `reasoning_content` field containing the model's chain-of-thought. This proxy:

1. **Captures** the reasoning from each assistant response (both streaming and non-streaming).
2. **Keys** the reasoning by assistant message index or tool call IDs.
3. **Re-injects** cached reasoning into subsequent assistant messages in the same conversation.
4. **Preserves existing** `reasoning_content` — if a message already has it, it won't be overwritten.

This enables coherent multi-turn reasoning conversations without losing context between turns.

---

## Performance Tuning

The `SocketsHttpHandler` is configured for maximum throughput:

| Setting | Value | Purpose |
|---|---|---|
| `MaxConnectionsPerServer` | 256 | High concurrency |
| `PooledConnectionLifetime` | 5 min | Connection reuse |
| `KeepAlivePingDelay` | 30 sec | Keep connections alive |
| `PooledConnectionIdleTimeout` | 30 sec | Free idle connections |
| `EnableMultipleHttp2Connections` | `true` | HTTP/2 multiplexing |

Adjust these in `Program.cs` based on your workload.

---

## Architecture

```
┌──────────────┐     ┌─────────────────────────────────┐     ┌───────────────┐
│  Copilot /   │────▶│  DeepSeek Copilot Proxy         │────▶│  api.deepseek │
│  Ollama CLI  │     │  (localhost:11434)               │     │  .com         │
│              │◀────│  - Reasoning caching             │◀────│               │
│              │     │  - Format translation            │     │               │
│              │     │  - Streaming proxy               │     │               │
└──────────────┘     └─────────────────────────────────┘     └───────────────┘
```

<p align="center">
  <img src="images/github_copilot_deepseek_v4-0.jpg" alt="Architecture overview" width="80%" />
</p>

The proxy is a single-file .NET application using:
- `Microsoft.AspNetCore` — Minimal API hosting
- `System.Text.Json` — JSON parsing with snake_case policy
- `System.Net.Http.SocketsHttpHandler` — High-performance HTTP transport

---

## Testing Artifacts

Generated validation outputs are stored in `docs/testing/`:
- `docs/testing/top10-optimal-params-send-test.json` — raw request/response validation for the current top-10 list.
- `docs/testing/top10-parameter-matrix.json` — recommended per-model runtime parameters and availability status.
- `docs/testing/nvidia-nim-models-config.json` — full sweep output for the broader NVIDIA NIM model set.

---

## License

WTFPL (Do What The Fuck You Want To Public License)

---

## Disclaimer

This is a proxy tool intended for development use. Ensure compliance with DeepSeek's terms of service and your API usage policies.
