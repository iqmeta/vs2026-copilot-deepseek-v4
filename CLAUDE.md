# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Test

```bash
# Build
dotnet build

# Run all tests (182 tests, xUnit + WebApplicationFactory)
dotnet test

# Run specific test suite
dotnet test --filter "FullyQualifiedName~ParameterValidationTests"
dotnet test --filter "FullyQualifiedName~EndpointTests"
dotnet test --filter "FullyQualifiedName~ModelSelectionStoreTests"

# Run single test by method name
dotnet test --filter TestMethodName=MySpecificTest

# Verbose output
dotnet test --verbosity detailed

# Run the proxy locally (port 11434 default)
dotnet run
```

Tests live in `tests/ProxyTests/`. The project targets **.NET 10.0** and uses `WebApplication.CreateSlimBuilder()`.

## What This Is

A high-performance ASP.NET Core **minimal API proxy** that bridges GitHub Copilot, Cursor, Continue.dev, Visual Studio BYOM, and Ollama clients to multiple AI providers through two API surfaces:

| API Surface | URL Prefix | Used By |
|---|---|---|
| OpenAI-compatible | `/v1/*` | Copilot, Cursor, Continue.dev, OpenAI SDKs |
| Ollama-compatible | `/api/*` | VS 2026 BYOM, native Ollama clients |

**Supported providers:** DeepSeek, OpenAI, NVIDIA NIM, Groq, OpenRouter, Ollama Cloud, Moonshot/Kimi (Cerebras being added).

## Architecture

### Service Registration (all Singletons)

Every service is registered as a **singleton** in `Program.cs:10-17`. The entire DI graph:

```
ProviderHttpClientFactory  →  Creates/caches per-provider HttpClient with auth headers
ProviderRegistry           →  Resolves model name → ordered list of provider candidates
ModelSelectionStore        →  Loads/parses config/model-selection/*.json
ModelCatalogService        →  Fetches live model catalogs from all providers on startup
ReasoningCacheService      →  Caches DeepSeek reasoning_content for multi-turn conversations
RequestTransformer         →  Injects defaults + filters unsupported params per provider
OllamaResponseBuilder      →  Converts OpenAI JSON response → Ollama NDJSON format
ChatStreamingService       →  Handles SSE streaming + on-the-fly format conversion
ProviderBenchmarkService   →  Background HostedService monitoring provider health
```

### Endpoint Structure

- `Endpoints/OpenAiEndpoints.cs` — Maps `/v1/models`, `/v1/chat/completions`
- `Endpoints/OllamaEndpoints.cs` — Maps `/api/version`, `/api/tags`, `/api/show`, `/api/chat`
- `Endpoints/HealthEndpoints.cs` — Maps `/health`
- `Middleware/` — Empty (auth middleware lives in `Infrastructure/ProxyAuthenticationMiddleware.cs`)

### Request Lifecycle

1. **Request arrives** → endpoint handler parses model name
2. **Model validated** → `ModelCatalogService.AvailableModels` (populated at startup)
3. **Defaults injected** → `RequestTransformer.ApplyExecutionDefaults()` reads config from `ModelSelectionStore` and injects temperature, max_tokens, reasoning_effort, etc. for the requested model
4. **Provider resolved** → `ProviderRegistry.ResolveCandidates(model)` returns ordered list of providers to try
5. **Forward to upstream** → via `ChatStreamingService` (streaming) or direct HTTP (non-streaming)
6. **Response converted** → if Ollama endpoint, `OllamaResponseBuilder` maps OpenAI → Ollama format
7. **Failover** → non-streaming requests retry next candidate on failure; streaming does NOT failover (headers already sent)

### Model Configuration

Model metadata lives in `config/model-selection/{provider}.json`. Each file maps model names to execution defaults:

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
        "temperature": 0.2,
        "max_tokens": 8192,
        "reasoning_effort": "high",
        "timeout_seconds": 180
      }
    }
  ]
}
```

- **Adding a new model:** edit the JSON for its provider + restart (no hot reload)
- **Adding a new provider:** create JSON + add provider to `ProviderRegistry.cs` + add HttpClient factory in `ProviderHttpClientFactory.cs`
- Models with `"enabled": false` are excluded from `/v1/models` and `/api/tags`

### Parameter Filtering Rules (RequestTransformer)

`RequestTransformer` strips unsupported parameters per provider before forwarding:
- `top_k` → removed for DeepSeek, OpenAI, Moonshot/Kimi; kept for NVIDIA, Groq, OpenRouter
- `reasoning_effort` → removed for NVIDIA, Groq, Moonshot/Kimi; kept for DeepSeek and OpenAI o-series
- `top_p` → removed when `reasoning_effort` is set (DeepSeek API rule)
- `tools`/`tool_choice` → removed for Groq
- `function_call` → removed for all (deprecated)

### Configuration Sources (priority order)

1. System environment variables
2. `.env` file (loaded by `Program.cs` if present)
3. `appsettings.json`
4. Hardcoded defaults (port 11434, model `deepseek-v4-pro`)

### Testing Architecture

Tests use `WebApplicationFactory<Program>` with an **in-process stub provider** (no real API calls). The stub simulates OpenAI-compatible endpoints on a random port. Key patterns:

- `ProxyFixture` provides `HttpClient` wired to the in-process proxy
- Tests are class-scoped (`IClassFixture<ProxyFixture>`)
- 182 tests across 12 test files covering endpoints, parameter validation, model selection, transformers, auth, reasoning cache, Ollama response building, JSON defaults, HTTP client factory, and provider registry

## Credential Separation

Per `.github/copilot-instructions.md`: **Never confuse Ollama Cloud API keys with local proxy API keys.** Cloud provider keys are managed via `.env` variables (`PROVIDER_OLLAMACLOUD_API_KEY`). The optional `PROXY_API_KEY` controls access to the proxy itself and is unrelated.

## Key Files Reference

| File | Purpose |
|---|---|
| `Program.cs` | Entry point, DI registration, endpoint mapping |
| `Services/ProviderRegistry.cs` | Model → provider resolution logic |
| `Services/RequestTransformer.cs` | Parameter filtering + default injection |
| `Services/ModelCatalogService.cs` | Live model catalog from all providers |
| `Services/ModelSelectionStore.cs` | JSON config loader for model defaults |
| `Services/ChatStreamingService.cs` | SSE/NDJSON streaming handler |
| `Services/ProviderHttpClientFactory.cs` | HttpClient creation with auth headers |
| `Infrastructure/ProxyAuthenticationMiddleware.cs` | Optional bearer token auth |
| `config/model-selection/` | Per-provider model JSON configs |

Further detail is available in `docs/ARCHITECTURE.md`, `docs/AGENTS.md`, `docs/API.md`, `docs/CONFIGURATION.md`, `docs/TESTING.md`, and `docs/DEPLOYMENT.md`.
