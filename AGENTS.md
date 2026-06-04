# AGENTS.md - AI Assistant Quick Reference

Optimized documentation for GitHub Copilot, Claude, and other AI code assistants working with this codebase.

## Project Essence

**Multi-Provider AI Proxy** — Single HTTP gateway to DeepSeek, OpenAI, NVIDIA, Groq, OpenRouter, Ollama Cloud, and Moonshot/Kimi.

- **Dual API Support:** OpenAI-compatible (`/v1/*`) + Ollama-compatible (`/api/*`)
- **Smart Routing:** Model names auto-map to providers with intelligent fallback
- **Parameter Filtering:** Adapt requests for each provider's unique capabilities
- **Zero-Copy Streaming:** SSE pass-through with minimal allocations
- **Reasoning Cache:** DeepSeek multi-turn thinking content reuse
- **Production Ready:** HTTP/2, connection pooling, 99-test suite

---

## Quick Navigation

### For Implementation Tasks
- **Adding a new endpoint?** → See `Endpoints/` directory and `ARCHITECTURE.md` → Request Lifecycle
- **Fixing parameter issues?** → Edit `config/model-selection/*.json` or `RequestTransformer.cs`
- **Adding provider support?** → `Services/ProviderRegistry.cs` + new `config/model-selection/{provider}.json`
- **Debugging streaming?** → `Services/ChatStreamingService.cs` + `Endpoints/OpenAiEndpoints.cs`

### For Understanding
- **How does request routing work?** → `ARCHITECTURE.md` → Provider Resolution + Retry Loop
- **What parameters does each model support?** → `CONFIGURATION.md` → Parameter Mapping table
- **How do tests work?** → `TESTING.md` → Test Architecture section

### For Deployment
- **Docker setup?** → `Dockerfile` + `docker-compose.yml`
- **Environment variables?** → `CONFIGURATION.md` → Environment Setup
- **Health checks?** → `GET /health` (maps to `Endpoints/HealthEndpoints.cs`)

---

## Core Services (One-Liner Summaries)

| Service | Purpose | File |
|---------|---------|------|
| `ProviderHttpClientFactory` | Creates HTTP clients per provider with auth | `Services/ProviderHttpClientFactory.cs` |
| `ProviderRegistry` | Resolves model → provider + lists available providers | `Services/ProviderRegistry.cs` |
| `ModelSelectionStore` | Loads JSON configs from `config/model-selection/` | `Services/ModelSelectionStore.cs` |
| `ModelCatalogService` | Fetches live model list from all providers on startup | `Services/ModelCatalogService.cs` |
| `ReasoningCacheService` | Stores/retrieves DeepSeek thinking for multi-turn | `Services/ReasoningCacheService.cs` |
| `RequestTransformer` | Injects defaults + filters params per provider | `Services/RequestTransformer.cs` |
| `OllamaResponseBuilder` | Converts OpenAI response → Ollama format | `Services/OllamaResponseBuilder.cs` |
| `ChatStreamingService` | Handles SSE streaming + format conversion | `Services/ChatStreamingService.cs` |
| `ProviderBenchmarkService` | Background service monitoring provider health | `Services/ProviderBenchmarkService.cs` |

---

## Endpoints at a Glance

### OpenAI Format (`/v1/*`)
```
GET  /v1/models                    → List models (OpenAI format)
POST /v1/chat/completions          → Chat completion (streaming or non-streaming)
GET  /health                       → Health check + provider summary
```

### Ollama Format (`/api/*`)
```
GET  /api/version                  → Proxy version ("0.5.7")
GET  /api/tags                     → List models (Ollama format)
GET  /api/show?model=X             → Model info (GET variant)
POST /api/show                     → Model info (POST variant)
POST /api/chat                     → Chat completion (Ollama format)
```

---

## Key Workflows

### Adding a New Model

1. **Update config file:** `config/model-selection/{provider}.json`
   ```json
   {
     "match": "new-model-name",
     "priority": 99,
     "enabled": true,
     "execution": {
       "context_length": 128000,
       "max_output_tokens": 8000,
       "temperature": 0.7,
       "max_tokens": 4096,
       "timeout_seconds": 120
     }
   }
   ```

2. **Update provider routing:** If new provider, edit `ProviderRegistry.ResolveCandidates()`
3. **Restart proxy** (configuration is not reloaded on-the-fly)
4. **Test:** `dotnet test --filter EndpointTests`

### Fixing Parameter Filtering for a Provider

1. **Identify issue:** Test fails with unsupported parameter
2. **Locate transform logic:** `RequestTransformer.cs` → `TransformRequest()` or `ApplyExecutionDefaults()`
3. **Find provider switch:** Look for provider == "provider_name" check
4. **Add/remove parameter:** Modify JsonElement to exclude unsupported fields
5. **Add unit test:** `ParameterValidationTests.cs` with new provider theory case
6. **Run tests:** `dotnet test --filter ParameterValidationTests`

### Debugging a Streaming Response Issue

1. **Check endpoint:** `Endpoints/OpenAiEndpoints.cs` or `Endpoints/OllamaEndpoints.cs`
2. **Trace streaming:** `ChatStreamingService.StreamChatCompletion()`
3. **Format conversion:** If Ollama endpoint, see `OllamaResponseBuilder` for SSE→NDJSON transform
4. **Log streaming chunks:** Add debug breakpoint in `ChatStreamingService`
5. **Test with curl:**
   ```bash
   curl -X POST http://localhost:11434/v1/chat/completions \
     -H "Content-Type: application/json" \
     -d '{"model":"deepseek-v4-pro","messages":[{"role":"user","content":"hi"}],"stream":true}'
   ```

### Understanding a Test Failure

1. **Find test file:** Search `tests/ProxyTests/` by test name
2. **Check fixture:** Real tests use `ProxyFixture` (stub provider at localhost)
3. **Identify phase:**
   - **Parameter validation?** → `ParameterValidationTests.cs`
   - **Model selection?** → `ModelSelectionTests.cs`
   - **HTTP behavior?** → `EndpointTests.cs`
   - **Transform logic?** → `RequestTransformerTests.cs`
4. **Run single test:** `dotnet test --filter MyTestName=*`
5. **Debug:** Set breakpoint in the test method or the service it calls

---

## Common Parameter Gotchas

| Situation | Solution |
|-----------|----------|
| `reasoning_effort` breaks on non-DeepSeek | RequestTransformer filters it; check `ParameterValidationTests` |
| `top_p` + `reasoning_effort` causes API error | DeepSeek docs: omit `top_p` when reasoning_effort set |
| `top_k` not supported by OpenAI | Filtered in `RequestTransformer.TransformRequest()` |
| Custom default overridden by user input | User params take precedence; apply defaults only if missing |
| Model not in `/v1/models` list | Check `ModelCatalogService.AvailableModels` or `config/model-selection/` enabled flag |

---

## Config File Locations

```
config/model-selection/
├── deepseek.json       # v4-pro, v4-flash, coder models
├── openai.json         # gpt-5, gpt-4o, gpt-4-turbo
├── nvidia.json         # llama-*, mixtral-*, nemotron-*
├── groq.json           # mixtral, llama3
├── openrouter.json     # OpenRouter model catalog
└── ollamacloud.json    # Ollama Cloud models
```

Each file contains model execution defaults (temperature, max_tokens, reasoning_effort, etc.).

---

## Testing Cheat Sheet

```bash
# Run all tests
dotnet test

# Run endpoint tests only
dotnet test --filter ClassName=EndpointTests

# Run parameter validation for specific provider
dotnet test --filter ClassName=ParameterValidationTests

# Run model selection tests
dotnet test --filter ClassName=ModelSelectionTests

# Run single test by name
dotnet test --filter TestMethodName=MySpecificTest

# Verbose output
dotnet test --verbosity detailed

# Coverage report
dotnet test /p:CollectCoverage=true
```

---

## Environment Variables

```bash
# Required (set in .env or system)
PROVIDER_DEEPSEEK_API_KEY=sk-xxxxx
PROVIDER_OPENAI_API_KEY=sk-proj-xxxxx
PROVIDER_NVIDIA_API_KEY=nvapi-xxxxx

# Optional
PROXY_PORT=11434
LOG_LEVEL=Information
REQUEST_TIMEOUT=300
MAX_CONCURRENT_REQUESTS=1000
DEFAULT_MODEL=deepseek-v4-pro
```

---

## Architecture Concepts

### Request Transformation Pipeline
```
Client Request
  ↓ [Parse]
JsonElement (incoming)
  ↓ [ModelSelectionStore] Load defaults for requested model
JsonElement + defaults
  ↓ [Provider detection] Determine target provider
Decorated JsonElement
  ↓ [RequestTransformer] Filter unsupported params per provider
JsonElement (provider-ready)
  ↓ [Forward] Send to upstream API
```

### Multi-Turn Reasoning (DeepSeek)
```
Turn 1: User asks question
  ↓ DeepSeek returns {"thinking": "...", "content": "..."}
  ↓ ReasoningCacheService stores thinking
  ↓ Response sent to user

Turn 2: User asks follow-up
  ↓ RequestTransformer retrieves cached thinking
  ↓ Injects as assistant context
  ↓ Forward to DeepSeek with reasoning continuity
  ↓ Response + new thinking cached
```

### Streaming + Format Conversion
```
Client (Ollama format)
  ├─ POST /api/chat (expects NDJSON)
  │
  ├─ [Transform to OpenAI] (internal)
  │
  ├─ [Forward to upstream] (SSE stream)
  │
  ├─ [ChatStreamingService] SSE → NDJSON (on-the-fly)
  │
  └─ [Stream to client] (Ollama NDJSON)
```

---

## Common Scenarios

### Scenario: User wants to use NVIDIA model via Copilot
1. Copilot sends: `"model": "llama-3.3-70b"`
2. Proxy resolves: `ProviderRegistry.ResolveCandidates()` → `["nvidia", "openai"]`
3. Forward to: `https://api.nvcf.nvidia.com/v2/chat/completions`
4. Return response in OpenAI format

### Scenario: VS 2026 BYOM requests deepseek-v4-pro via Ollama
1. VS sends: `POST /api/chat { "model": "deepseek-v4-pro", "messages": [...] }`
2. Transform: Ollama format → OpenAI format
3. Resolve: DeepSeek provider
4. Forward: `https://api.deepseek.com/v1/chat/completions`
5. Convert response: OpenAI → Ollama format
6. Stream back as NDJSON

### Scenario: Parameter mismatch (e.g., `reasoning_effort` on OpenAI non-o model)
1. Request arrives with `reasoning_effort`
2. `RequestTransformer` detects model is `gpt-4` (not o-series)
3. Removes `reasoning_effort` from transform
4. Forwards to OpenAI without unsupported param
5. No error, request succeeds

---

## Debugging Tips

### Enable Verbose Logging
```bash
export LOG_LEVEL=Debug
dotnet run
```

### Check Model Availability
```bash
curl http://localhost:11434/v1/models | jq '.data[].id'
```

### Test Direct Provider (Bypass Proxy)
```bash
# Direct DeepSeek call
curl -X POST https://api.deepseek.com/v1/chat/completions \
  -H "Authorization: Bearer $PROVIDER_DEEPSEEK_API_KEY" \
  -d '{"model":"deepseek-v4-pro","messages":[{"role":"user","content":"hi"}]}'
```

### Monitor Streaming Response
```bash
curl -X POST http://localhost:11434/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{"model":"deepseek-v4-pro","messages":[{"role":"user","content":"hi"}],"stream":true}' \
  -N  # Disable buffering
```

### Inspect Request Transform
Add breakpoint in `RequestTransformer.ApplyExecutionDefaults()` and examine JsonElement before/after.

---

## Performance Notes

- **Connection pooling:** 256 per provider, HTTP/2 enabled
- **Streaming:** Zero-copy pass-through (not buffered)
- **Model metadata:** Loaded once on startup, cached in RAM
- **JSON parsing:** System.Text.Json source-generated (no reflection)
- **Typical latency:** <10ms proxy overhead

---

## Related Docs

- **[API.md](docs/API.md)** — Endpoint specifications and examples
- **[CONFIGURATION.md](docs/CONFIGURATION.md)** — Setup, providers, parameter mapping
- **[ARCHITECTURE.md](docs/ARCHITECTURE.md)** — System design, components, data flow
- **[TESTING.md](docs/TESTING.md)** — Test architecture, running tests, adding new tests
- **[DEPLOYMENT.md](docs/DEPLOYMENT.md)** — Docker, bare metal, monitoring, troubleshooting
