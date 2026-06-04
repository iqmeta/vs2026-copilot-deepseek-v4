# Multi-Provider AI Proxy

> The fastest way to run DeepSeek, OpenAI, NVIDIA, Groq, OpenRouter, and Ollama models in GitHub Copilot, VS BYOM, and Ollama clients.

**As of May 2026** — Tested with Visual Studio 2026 Insider Edition

A high-performance, ultra-low-overhead HTTP proxy that connects GitHub Copilot and Ollama clients to **DeepSeek, OpenAI, NVIDIA, Groq, OpenRouter, and Ollama Cloud** APIs. Built with .NET 10 and ASP.NET Core minimal APIs for maximum throughput and minimal allocations.

| 🏗️ | Details |
|---|---|
| **Providers** | DeepSeek, OpenAI, NVIDIA NIM, Groq, OpenRouter, Ollama Cloud, Moonshot/Kimi |
| **Models** | Auto-discovered from each provider |
| **Default Port** | `11434` |
| **Framework** | .NET 10 |
| **Tests** | 99 passing ✅ |
| **Deploy** | Docker / bare metal |

## Key Features

- **🧠 Reasoning Content Caching** — Automatically captures DeepSeek's reasoning_content and re-injects it on subsequent messages for true multi-turn reasoning
- **🌐 Multi-Provider Support** — Route requests to DeepSeek, OpenAI, NVIDIA, Groq, OpenRouter, or Ollama Cloud based on model name
- **🔄 Dual API Compatibility**
  - **OpenAI-compatible** (`/v1/chat/completions`) — works with GitHub Copilot, Cursor, Continue.dev, any OpenAI SDK
  - **Ollama-compatible** (`/api/chat`, `/api/tags`, `/api/show`) — works with VS BYOM and Ollama clients
- **⚡ Ultra-Performance** — HTTP/2 connection pooling (256 connections/server), zero-copy streaming, minimal allocations
- **📦 Zero-Copy Streaming** — SSE pass-through without buffering
- **🔧 No External Dependencies** — Uses only built-in ASP.NET Core and System.Text.Json
- **🐳 Docker-Ready** — Multi-stage Dockerfile and docker-compose.yml included
- **🔐 Optional Authentication** — Set `PROXY_API_KEY` to require Bearer token on all endpoints

## Quick Start

### Prerequisites
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) or Docker
- API keys for providers you want to use

### 1. Configure

Copy `.env.example` to `.env` and set your API keys:

```bash
cp .env.example .env
# Edit .env → set PROVIDER_DEEPSEEK_API_KEY=sk-your-key
```

Key environment variables:
```
PROVIDER_DEEPSEEK_API_KEY=sk-...
PROVIDER_OPENAI_API_KEY=sk-proj-...
PROVIDER_NVIDIA_API_KEY=nvapi-...
PROVIDER_GROQ_API_KEY=gsk-...
PROVIDER_OPENROUTER_API_KEY=sk-or-...
PROVIDER_OLLAMACLOUD_API_KEY=...

PROXY_PORT=11434                    # (optional)
DEFAULT_MODEL=deepseek-v4-pro       # (optional)
```

### 2a. Run with Docker (Recommended)

```bash
docker compose up -d
```

### 2b. Run with .NET

```bash
dotnet run
```

You should see startup output with available providers and models.

## API Reference

### OpenAI-Compatible Endpoints

```
GET  /v1/models                          # List models
POST /v1/chat/completions                # Chat (streaming or non-streaming)
GET  /health                             # Health check
```

### Ollama-Compatible Endpoints

```
GET  /api/version                        # Version info
GET  /api/tags                           # List models (Ollama format)
GET  /api/show?model=...                 # Model details
POST /api/show                           # Model details
POST /api/chat                           # Chat (Ollama format)
```

**[→ Full API Documentation](docs/API.md)**

## Configuration

### GitHub Copilot

In VS Code settings:

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

### VS 2026 BYOM

Point the Ollama BYOM at:
```
http://localhost:11434/api/chat
```

### Continue.dev / Cursor

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

## Documentation

- **[API.md](docs/API.md)** — Complete endpoint reference with examples
- **[CONFIGURATION.md](docs/CONFIGURATION.md)** — Setup, providers, parameter mapping, context windows
- **[ARCHITECTURE.md](docs/ARCHITECTURE.md)** — System design, components, request lifecycle
- **[TESTING.md](docs/TESTING.md)** — Test architecture, running tests, adding new tests
- **[DEPLOYMENT.md](docs/DEPLOYMENT.md)** — Docker, Kubernetes, monitoring, troubleshooting
- **[AGENTS.md](AGENTS.md)** — Quick reference for AI assistants (Copilot, Claude, etc.)

## Performance

- **Connection pooling:** 256 per provider with HTTP/2 multiplexing
- **Streaming:** Zero-copy pass-through (minimal memory overhead)
- **Model metadata:** Loaded once on startup, cached in RAM
- **Typical latency:** <10ms proxy overhead
- **Test coverage:** 99 tests covering endpoints, parameters, model selection, transformations

## Testing

```bash
# Run all tests
dotnet test

# Run specific suite
dotnet test --filter ClassName=EndpointTests

# Verbose output
dotnet test --verbosity detailed
```

**[→ Testing Guide](docs/TESTING.md)**

## How Reasoning Caching Works

DeepSeek models return `reasoning_content` with their responses. The proxy:

1. Captures reasoning from each assistant response
2. Stores it in `ReasoningCacheService`
3. Re-injects cached reasoning into subsequent assistant messages in the same conversation
4. Enables coherent multi-turn reasoning without losing context

## Provider Support

| Provider | Models | Context | Max Output | Tools | Vision |
|----------|--------|---------|-----------|-------|--------|
| **DeepSeek** | v4-pro, v4-flash, coder | 1M tokens | 384K | ✅ | ❌ |
| **OpenAI** | gpt-5, gpt-4o, gpt-4-turbo | 128K | 16K | ✅ | ✅ |
| **NVIDIA NIM** | llama-3.3-70b, mixtral, nemotron | 128K | 8K | ✅ | ❌ |
| **Groq** | mixtral-8x7b, llama3-70b | 32K | 8K | ⚠️ | ❌ |
| **OpenRouter** | All available (100+) | Varies | Varies | Varies | Varies |
| **Ollama Cloud** | All available | Varies | Varies | Varies | Varies |
| **Moonshot/Kimi** | kimi-k2.6, kimi-k2.5, moonshot-v1 | 256K | 128K | ✅ | ✅ (vision) |

**[→ Configuration Guide](docs/CONFIGURATION.md#context-window-specifications)**

## Architecture Overview

```
Clients (Copilot, VS BYOM, Ollama)
    ↓
Proxy (localhost:11434)
  ├─ Parameter filtering (RequestTransformer)
  ├─ Model routing (ProviderRegistry)
  ├─ Reasoning caching (ReasoningCacheService)
  ├─ Format conversion (OpenAI ↔ Ollama)
  └─ Streaming handler (ChatStreamingService)
    ↓
Upstream Providers
  ├─ DeepSeek API
  ├─ OpenAI API
  ├─ NVIDIA NIM
  ├─ Groq API
  ├─ OpenRouter API
  └─ Ollama Cloud API
```

**[→ Full Architecture](docs/ARCHITECTURE.md)**

## License

WTFPL (Do What The Fuck You Want To Public License)

## Support

For issues, questions, or contributions:
- Check **[AGENTS.md](AGENTS.md)** for quick reference
- Review **[TESTING.md](docs/TESTING.md)** for test architecture
- See **[ARCHITECTURE.md](docs/ARCHITECTURE.md)** for design details
