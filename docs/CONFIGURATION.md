# Configuration Guide

Complete configuration documentation for the multi-provider proxy.

## Table of Contents

- [Environment Setup](#environment-setup)
- [Provider Configuration](#provider-configuration)
- [Model Selection & Defaults](#model-selection--defaults)
- [Parameter Mapping](#parameter-mapping)
- [Context Window Specifications](#context-window-specifications)
- [Advanced Configuration](#advanced-configuration)

---

## Environment Setup

### Required Environment Variables

Provider API keys must be set as environment variables. The proxy reads from:

```bash
# DeepSeek (https://platform.deepseek.com)
PROVIDER_DEEPSEEK_API_KEY=sk-xxxxx

# OpenAI (https://platform.openai.com)
PROVIDER_OPENAI_API_KEY=sk-proj-xxxxx

# NVIDIA (NIM on-prem or cloud)
PROVIDER_NVIDIA_API_KEY=nvapi-xxxxx

# OpenRouter (https://openrouter.ai)
PROVIDER_OPENROUTER_API_KEY=sk-or-xxxxx

# Groq (https://console.groq.com)
PROVIDER_GROQ_API_KEY=gsk-xxxxx

# Ollama Cloud (https://ollama.ai)
PROVIDER_OLLAMACLOUD_API_KEY=xxxxx
```

### Optional Configuration Variables

```bash
# Listen port (default: 11434)
PROXY_PORT=11434

# Log level (default: Information)
LOG_LEVEL=Debug

# Request timeout in seconds (default: 300)
REQUEST_TIMEOUT=300

# Max streaming chunk size in bytes (default: 4096)
STREAM_CHUNK_SIZE=8192

# Concurrent request limit (default: no limit)
MAX_CONCURRENT_REQUESTS=1000

# Enable metrics logging (default: true)
ENABLE_METRICS=true
```

### .env File Example

Create a `.env` file at the repository root:

```bash
# API Keys
PROVIDER_DEEPSEEK_API_KEY=sk-xxxxx
PROVIDER_OPENAI_API_KEY=sk-proj-xxxxx
PROVIDER_NVIDIA_API_KEY=nvapi-xxxxx

# Proxy Settings
PROXY_PORT=11434
LOG_LEVEL=Information
REQUEST_TIMEOUT=300

# Optional: Model overrides
DEFAULT_MODEL=deepseek-v4-pro
```

The proxy uses `IConfiguration` and will read from:
1. Environment variables
2. `.env` file (via `DotEnv` loading in `Program.cs`)
3. `appsettings.json` (project defaults)

---

## Provider Configuration

### Provider Registry

Configured providers are defined in `Services/ProviderRegistry.cs` and instantiated with their upstream service URLs:

| Provider | Service URL | Status |
|----------|-------------|--------|
| **DeepSeek** | `https://api.deepseek.com/v1` | ✅ Fully supported |
| **OpenAI** | `https://api.openai.com/v1` | ✅ Fully supported |
| **NVIDIA NIM** | `https://api.nvcf.nvidia.com/v2` | ✅ Fully supported |
| **OpenRouter** | `https://openrouter.ai/api/v1` | ✅ Fully supported |
| **Groq** | `https://api.groq.com/openai/v1` | ✅ Fully supported |
| **Ollama Cloud** | `https://api.ollama.cloud/api` | ✅ Fully supported |
| **Moonshot/Kimi** | `https://api.moonshot.ai/v1` | ✅ Fully supported |

### Provider Selection Logic

The proxy uses this priority when resolving a model request:

1. **Direct Model-to-Provider Mapping**
   - `deepseek-*` → DeepSeek provider
   - `gpt-*` → OpenAI provider
   - `llama-*`, `mixtral-*` (NVIDIA catalog) → NVIDIA NIM
   - etc.

2. **NVIDIA NIM Fallback**
   - If model not matched directly, try NVIDIA (hosts many models)

3. **Default Provider**
   - If no match, use `DEFAULT_MODEL` provider

### Adding a New Provider

1. Create HTTP client factory in `Services/HttpClientFactory.cs`:
```csharp
services.AddHttpClient("NewProvider", client =>
{
    client.BaseAddress = new Uri("https://api.newprovider.com/v1");
    client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
});
```

2. Update `ProviderRegistry.ResolveCandidates()`:
```csharp
case "newmodel-123":
    candidates.Add("newprovider");
    break;
```

3. Create model selection JSON in `config/model-selection/newprovider.json`:
```json
{
  "provider": "newprovider",
  "models": [
    {
      "match": "newmodel-123",
      "priority": 1,
      "enabled": true,
      "execution": {
        "context_length": 200000,
        "max_output_tokens": 32000,
        "temperature": 0.7,
        "max_tokens": 4096,
        "timeout_seconds": 120
      }
    }
  ]
}
```

---

## Model Selection & Defaults

### Default Model

The default model is determined by (in priority order):

1. **Request-specified model** (e.g., `"model": "gpt-5"`)
2. **Environment variable** `DEFAULT_MODEL` (if set)
3. **Configuration** `DefaultModel` in `appsettings.json`
4. **Hardcoded fallback** `"deepseek-v4-pro"` in `ModelSelectionStore`

### Model Selection JSON Files

Model metadata and defaults are loaded from `config/model-selection/*.json`:

```
config/model-selection/
├── deepseek.json      # DeepSeek models (v4-pro, v4-flash, coder, etc.)
├── openai.json        # OpenAI models (gpt-5, gpt-4o, etc.)
├── nvidia.json        # NVIDIA/NIM models (llama, mixtral, etc.)
├── groq.json          # Groq models (mixtral, llama3, etc.)
├── openrouter.json    # OpenRouter catalog
└── ollamacloud.json   # Ollama Cloud models
```

### Example: DeepSeek Configuration

From `config/model-selection/deepseek.json`:

```json
{
  "provider": "deepseek",
  "models": [
    {
      "match": "deepseek-v4-pro",
      "priority": 1,
      "enabled": true,
      "execution": {
        "context_length": 1048576,
        "max_output_tokens": 384000,
        "family": "deepseek",
        "temperature": 0.2,
        "max_tokens": 8192,
        "reasoning_effort": "high",
        "timeout_seconds": 180
      }
    },
    {
      "match": "deepseek-v4-flash",
      "priority": 2,
      "enabled": true,
      "execution": {
        "context_length": 1048576,
        "max_output_tokens": 131072,
        "family": "deepseek",
        "temperature": 0.2,
        "max_tokens": 4096,
        "reasoning_effort": "medium",
        "timeout_seconds": 90
      }
    }
  ]
}
```

### Modifying Defaults

To change default parameters for a model, edit its JSON file:

```bash
# Edit deepseek.json
vi config/model-selection/deepseek.json

# Change temperature, max_tokens, reasoning_effort, etc.
# Restart the proxy to reload configuration
```

**Note:** The proxy does NOT reload configuration on-the-fly; restart required.

---

## Parameter Mapping

The proxy automatically filters and adapts parameters based on the upstream provider's API requirements.

### Parameter Filtering Rules

| Parameter | DeepSeek | OpenAI | NVIDIA | Groq | OpenRouter | Support |
|-----------|----------|--------|--------|------|------------|---------|
| `temperature` | ✅ | ✅ | ✅ | ✅ | ✅ | All |
| `top_p` | ✅ | ✅ | ✅ | ✅ | ✅ | All |
| `top_k` | ✅ | ❌ | ✅ | ✅ | ✅ | Most |
| `max_tokens` | ✅ | ✅ | ✅ | ✅ | ✅ | All |
| `reasoning_effort` | ✅ | ✅ (o-series) | ❌ | ❌ | ❌ | Limited |
| `tools` | ✅ | ✅ | ✅ | ❌ | ✅ | Most |
| `tool_choice` | ✅ | ✅ | ✅ | ❌ | ✅ | Most |
| `function_call` | ❌ (deprecated) | ❌ (deprecated) | ❌ | ❌ | ❌ | None |

### Parameter Normalization Examples

**Request with `top_k`:**
```json
{
  "model": "gpt-5",
  "messages": [...],
  "top_k": 100,
  "top_p": 0.9
}
```

**Normalized for OpenAI (no `top_k` support):**
```json
{
  "model": "gpt-5",
  "messages": [...],
  "top_p": 0.9
}
```

**Request with `reasoning_effort`:**
```json
{
  "model": "llama-3.3-70b-versatile",
  "messages": [...],
  "reasoning_effort": "high"
}
```

**Normalized for Groq (no reasoning support):**
```json
{
  "model": "llama-3.3-70b-versatile",
  "messages": [...],
  "temperature": 0.7,
  "top_p": 0.9
}
```

### RequestTransformer Implementation

The `RequestTransformer` class handles parameter normalization:

```csharp
public class RequestTransformer
{
    public string TransformRequest(string provider, string requestJson)
    {
        // 1. Parse incoming request
        // 2. Filter parameters based on provider capabilities
        // 3. Apply default parameter values
        // 4. Validate parameter ranges
        // 5. Return transformed request
    }
}
```

---

## Context Window Specifications

All models and their context window limits are documented here:

### DeepSeek Models

| Model | Context | Max Output | Input Cost | Output Cost | Reasoning |
|-------|---------|-----------|-----------|----------|-----------|
| deepseek-v4-pro | 1M tokens | 384k | $0.27/M | $1.08/M | ✅ Yes |
| deepseek-v4-flash | 1M tokens | 131k | $0.05/M | $0.15/M | ✅ Yes |
| deepseek-coder-6.7b | 128k tokens | 8k | Free-tier | Free-tier | ❌ No |

### OpenAI Models

| Model | Context | Max Output | Input Cost | Output Cost |
|-------|---------|-----------|-----------|----------|
| gpt-5 | 128k tokens | 8k | $15/M | $60/M |
| gpt-4o | 128k tokens | 4k | $5/M | $15/M |
| gpt-4-turbo | 128k tokens | 4k | $10/M | $30/M |

### NVIDIA NIM Models

| Model | Context | Max Output | Notes |
|-------|---------|-----------|-------|
| llama-3.3-70b-versatile | 128k | 8k | Tool-capable |
| mixtral-8x22b-instruct | 65k | 8k | MoE architecture |
| nemotron-4-340b-instruct | 128k | 8k | Long-context |

### Groq Models

| Model | Context | Max Output | Speed | Notes |
|-------|---------|-----------|-------|-------|
| mixtral-8x7b-32768 | 32k | 8k | Ultra-fast | Speed-optimized |
| llama3-70b-8192 | 8k | 8k | Fast | Quantized |

### OpenRouter Models

See https://openrouter.ai/models for the complete catalog.

### Moonshot/Kimi Models

| Model | Context | Max Output | Input Cost | Output Cost | Vision |
|-------|---------|-----------|-----------|----------|--------|
| kimi-k2.6 | 256k tokens | 128k | Competitive | Competitive | ✅ |
| kimi-k2.5 | 256k tokens | 64k | Competitive | Competitive | ❌ |
| moonshot-v1-128k | 128k tokens | 32k | Competitive | Competitive | ✅ |
| moonshot-v1-auto | 128k tokens | 32k | Competitive | Competitive | ❌ |
| moonshot-v1-32k | 32k tokens | 8k | Competitive | Competitive | ✅ |
| moonshot-v1-8k | 8k tokens | 4k | Competitive | Competitive | ✅ |

---

## Advanced Configuration

### Disable Streaming for Testing

Set in `appsettings.json` or via environment variable:

```json
{
  "Proxy": {
    "DisableStreaming": true
  }
}
```

### Custom HTTP Headers

To inject custom headers (e.g., for authentication with internal proxies):

```csharp
// Modify HttpClientFactory.cs
client.DefaultRequestHeaders.Add("X-Custom-Header", "value");
```

### Connection Pool Settings

The proxy uses `SocketsHttpHandler` with:
- **Max connections per server:** 256
- **HTTP/2 multiplexing:** Enabled
- **Connection lifetime:** Infinite (let OS manage)

To adjust, modify `HttpClientFactory.cs`:

```csharp
var handler = new SocketsHttpHandler
{
    MaxConnectionsPerServer = 512,  // Increase if needed
    AutomaticDecompression = DecompressionMethods.All
};
```

### Request Timeout Settings

Per-provider timeout overrides in `config/model-selection/*.json`:

```json
{
  "execution": {
    "timeout_seconds": 60
  }
}
```

Global timeout: `REQUEST_TIMEOUT` environment variable (default 300s).

---

## Related Documentation

- [API.md](API.md) — Endpoint reference and request/response formats
- [ARCHITECTURE.md](ARCHITECTURE.md) — System design and component overview
- [TESTING.md](TESTING.md) — Test coverage and validation procedures
