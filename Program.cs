// DeepSeek Copilot Proxy - Ultra-Low-Overhead Edition
// Uses direct HTTP proxying optimized for minimal allocations.
// Maintains full reasoning_content caching for multi-turn DeepSeek conversations.
// iqmeta GmbH | Otto Neff
// Version 2026.05.09

using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

// ─── Config (from environment) ───────────────────────────────────────
string MODEL = Environment.GetEnvironmentVariable("DEEPSEEK_MODEL") ?? "deepseek-v4-pro";
int PORT = int.TryParse(Environment.GetEnvironmentVariable("PROXY_PORT"), out int p) ? p : 11434;
string? PROXY_API_KEY = Environment.GetEnvironmentVariable("PROXY_API_KEY");

List<ProviderInfo> PROVIDERS = [];
Dictionary<string, ProviderInfo> MODEL_TO_PROVIDER = new(StringComparer.OrdinalIgnoreCase);
string[] AVAILABLE_MODELS = [MODEL];
DateTime MODELS_LAST_REFRESH_UTC = DateTime.MinValue;
TimeSpan MODELS_REFRESH_INTERVAL = TimeSpan.FromMinutes(5);

// Resolve model → provider: look up by model name, fallback to first provider's default
ProviderInfo ResolveProvider(string? requestedModel) =>
    !string.IsNullOrWhiteSpace(requestedModel) && MODEL_TO_PROVIDER.TryGetValue(requestedModel, out var p)
        ? p : PROVIDERS[0];

string ResolveModel(string? requestedModel) =>
    !string.IsNullOrWhiteSpace(requestedModel) && MODEL_TO_PROVIDER.ContainsKey(requestedModel)
        ? requestedModel : MODEL;

// ─── State ───────────────────────────────────────────────────────────
ConcurrentDictionary<string, string> ReasoningCache = new(StringComparer.Ordinal);
long _assistantMsgCounter = 0;

// ─── JSON Helpers ────────────────────────────────────────────────────
JsonSerializerOptions JsonOpts = new()
{
    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
};

// ─── Builder ─────────────────────────────────────────────────────────
WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.UseUrls($"http://0.0.0.0:{PORT}");

// Max-performance HTTP handler (shared across all providers)
SocketsHttpHandler sharedHandler = new()
{
    EnableMultipleHttp2Connections = true,
    MaxConnectionsPerServer = 256,
    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
    KeepAlivePingDelay = TimeSpan.FromSeconds(30),
    KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
    AutomaticDecompression = System.Net.DecompressionMethods.None,
    UseCookies = false,
    PreAuthenticate = false
};

HttpClient CreateProviderClient(string baseUrl, string apiKey)
{
    HttpClient client = new(sharedHandler)
    {
        Timeout = TimeSpan.FromMinutes(5),
        BaseAddress = new Uri(baseUrl)
    };
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    return client;
}

// Discover providers from prefixed env vars: PROVIDER_<NAME>_API_KEY
foreach (string providerName in new[] { "deepseek", "openai", "nvidia" })
{
    string prefix = providerName.ToUpperInvariant();
    string? apiKey = Environment.GetEnvironmentVariable($"PROVIDER_{prefix}_API_KEY");

    if (string.IsNullOrWhiteSpace(apiKey)) continue;

    string baseUrl = Environment.GetEnvironmentVariable($"PROVIDER_{prefix}_BASE_URL")
        ?? providerName switch
        {
            "deepseek" => "https://api.deepseek.com",
            "openai" => "https://api.openai.com",
            "nvidia" => "https://integrate.api.nvidia.com",
            _ => ""
        };

    HttpClient provClient = CreateProviderClient(baseUrl, apiKey);
    PROVIDERS.Add(new ProviderInfo(providerName, apiKey, baseUrl, provClient));
}

// Fallback: legacy DEEPSEEK_API_KEY (backward compatible)
if (PROVIDERS.Count == 0)
{
    string? legacyKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
    if (string.IsNullOrWhiteSpace(legacyKey))
        throw new InvalidOperationException(
            "No AI provider configured. Set PROVIDER_<NAME>_API_KEY (e.g. PROVIDER_DEEPSEEK_API_KEY) or DEEPSEEK_API_KEY.");

    string legacyUrl = Environment.GetEnvironmentVariable("DEEPSEEK_BASE_URL") ?? "https://api.deepseek.com";
    PROVIDERS.Add(new ProviderInfo("deepseek", legacyKey, legacyUrl,
        CreateProviderClient(legacyUrl, legacyKey)));
}

await RefreshAvailableModels(CancellationToken.None);

WebApplication app = builder.Build();

// ─── Optional proxy auth middleware ─────────────────────────────────
if (!string.IsNullOrEmpty(PROXY_API_KEY))
{
    app.Use(async (ctx, next) =>
    {
        string? auth = ctx.Request.Headers.Authorization;
        if (auth != null && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            && auth["Bearer ".Length..] == PROXY_API_KEY)
        {
            await next();
        }
        else
        {
            ctx.Response.StatusCode = 401;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("""{"error":"unauthorized"}""");
        }
    });
}

// ─── GET /v1/models ──────────────────────────────────────────────────
app.MapGet("/v1/models", async (HttpContext ctx) =>
{
    await RefreshAvailableModelsIfNeeded(ctx.RequestAborted);
    return Results.Json(new
    {
        @object = "list",
        data = AVAILABLE_MODELS.Select(m =>
        {
            string providerName = MODEL_TO_PROVIDER.TryGetValue(m, out var prov) ? prov.Name : "unknown";
            return new { id = m, @object = "model", created = 1700000000, owned_by = providerName };
        }).ToArray()
    }, JsonOpts);
});

// ─── GET /health ─────────────────────────────────────────────────────
app.MapGet("/health", () => Results.Ok(new {
    status = "ok",
    model = MODEL,
    available_models = AVAILABLE_MODELS,
    providers = PROVIDERS.Select(p => p.Name).ToArray(),
    models_last_refresh_utc = MODELS_LAST_REFRESH_UTC
}));

// ─── POST /v1/chat/completions ──────────────────────────────────────
app.MapPost("/v1/chat/completions", async (HttpContext ctx) =>
{
    CancellationToken ct = ctx.RequestAborted;

    // Read and parse request
    using StreamReader bodyReader = new(ctx.Request.Body, Encoding.UTF8, false, 1024);
    string rawBody = await bodyReader.ReadToEndAsync(ct);

    using JsonDocument doc = JsonDocument.Parse(rawBody);
    JsonElement root = doc.RootElement;
    bool isStream = root.TryGetProperty("stream", out JsonElement sp) && sp.GetBoolean();

    // Resolve provider from requested model
    string reqModel = root.TryGetProperty("model", out JsonElement rm) && rm.ValueKind == JsonValueKind.String
        ? rm.GetString()! : MODEL;
    ProviderInfo provider = ResolveProvider(reqModel);

    // Inject cached reasoning_content
    string? modified = ModifyRequest(doc);
    string bodyText = modified ?? rawBody;

    // For non-streaming: direct proxy via provider's HttpClient
    if (!isStream)
    {
        using StringContent content = new(bodyText, Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await provider.Client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = content },
            ct);

        string respBody = await response.Content.ReadAsStringAsync(ct);

        if (response.IsSuccessStatusCode)
            CacheReasoningFromResponse(respBody);

        ctx.Response.StatusCode = (int)response.StatusCode;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(respBody, ct);
        return;
    }

    // ── Streaming ──
    ctx.Response.StatusCode = 200;
    ctx.Response.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Response.Headers["X-Accel-Buffering"] = "no";

    using StringContent reqContent = new(bodyText, Encoding.UTF8, "application/json");
    using HttpRequestMessage upstreamReq = new(HttpMethod.Post, "/v1/chat/completions")
    {
        Content = reqContent
    };
    upstreamReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

    using HttpResponseMessage upstreamResp = await provider.Client.SendAsync(
        upstreamReq, HttpCompletionOption.ResponseHeadersRead, ct);

    if (!upstreamResp.IsSuccessStatusCode)
    {
        string errBody = await upstreamResp.Content.ReadAsStringAsync(ct);
        ctx.Response.StatusCode = (int)upstreamResp.StatusCode;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(errBody, ct);
        return;
    }

    await StreamAndCache(upstreamResp, ctx.Response, ct);
});

// ─── Ollama /api/version ─────────────────────────────────────────────
app.MapGet("/api/version", () => Results.Json(new { version = "0.5.7" }, JsonOpts));

// ─── Ollama /api/tags ────────────────────────────────────────────────
app.MapGet("/api/tags", async (HttpContext ctx) =>
{
    await RefreshAvailableModelsIfNeeded(ctx.RequestAborted);
    return Results.Json(new
    {
        models = AVAILABLE_MODELS.Select(m =>
            {
                var p = GetModelProfile(m);
                return new
                {
                    name = m,
                    model = m,
                    modified_at = DateTime.UtcNow.ToString("o"),
                    size = 0L,
                    digest = "sha256:0000000000000000000000000000000000000000000000000000000000000000",
                    details = new
                    {
                        parent_model = "",
                        format = "api",
                        family = p.Family,
                        families = new[] { p.Family },
                        parameter_size = "api",
                        quantization_level = "none"
                    },
                    capabilities = p.Capabilities,
                    context_length = p.ContextLength,
                    max_output_tokens = p.MaxOutputTokens,
                    input_token_limit = p.ContextLength,
                    output_token_limit = p.MaxOutputTokens,
                    supports_tools = p.SupportsTools,
                    supports_tool_calls = p.SupportsTools,
                    supports_vision = p.SupportsVision,
                    supports_images = p.SupportsVision
                };
            }).ToArray()
    }, JsonOpts);
});

// ─── Ollama /api/show (GET + POST) ──────────────────────────────────
app.MapGet("/api/show", async (HttpContext ctx, string? model) =>
{
    await RefreshAvailableModelsIfNeeded(ctx.RequestAborted);
    string resolved = ResolveModel(model);
    return Results.Json(BuildOllamaShowResponse(resolved), JsonOpts);
});

app.MapPost("/api/show", async (HttpContext ctx) =>
{
    await RefreshAvailableModelsIfNeeded(ctx.RequestAborted);
    using StreamReader reader = new(ctx.Request.Body);
    string body = await reader.ReadToEndAsync(ctx.RequestAborted);
    string? model = null;
    try
    {
        using JsonDocument d = JsonDocument.Parse(body);
        if (d.RootElement.TryGetProperty("model", out JsonElement m) && m.ValueKind == JsonValueKind.String)
            model = m.GetString();
    }
    catch { }

    string resolved = ResolveModel(model);
    return Results.Json(BuildOllamaShowResponse(resolved), JsonOpts);
});

// ─── Ollama /api/chat ────────────────────────────────────────────────
app.MapPost("/api/chat", async (HttpContext ctx) =>
{
    CancellationToken ct = ctx.RequestAborted;
    using StreamReader reader = new(ctx.Request.Body);
    string body = await reader.ReadToEndAsync(ct);
    using JsonDocument doc = JsonDocument.Parse(body);
    JsonElement root = doc.RootElement;
    bool isStream = root.TryGetProperty("stream", out JsonElement sp) && sp.GetBoolean();

    // Convert Ollama messages to OpenAI format
    List<object> messages = [];
    if (root.TryGetProperty("messages", out JsonElement omsgs))
    {
        foreach (JsonElement msg in omsgs.EnumerateArray())
        {
            string role = msg.GetProperty("role").GetString()!;
            string text = msg.TryGetProperty("content", out JsonElement c) ? c.GetString() ?? "" : "";
            
            object content;
            if (msg.TryGetProperty("images", out JsonElement imgs) && imgs.GetArrayLength() > 0)
            {
                List<object> parts = [new { type = "text", text }];
                foreach (JsonElement img in imgs.EnumerateArray())
                {
                    string url = img.GetString()!;
                    if (!url.StartsWith("data:") && !url.StartsWith("http"))
                        url = $"data:image/png;base64,{url}";
                    parts.Add(new { type = "image_url", image_url = new { url } });
                }

                content = parts;
            }
            else content = text;
            
            messages.Add(new { role, content });
        }
    }

    // Resolve model from Ollama request + provider
    string ollamaRequestedModel = root.TryGetProperty("model", out JsonElement om) && om.ValueKind == JsonValueKind.String
        ? om.GetString()! : MODEL;
    string ollamaEffectiveModel = ResolveModel(ollamaRequestedModel);
    ProviderInfo ollamaProvider = ResolveProvider(ollamaEffectiveModel);

    Dictionary<string, object?> reqObj = new()
    {
        ["model"] = ollamaEffectiveModel,
        ["messages"] = messages,
        ["stream"] = isStream,
        ["max_tokens"] = 8192
    };
    if (root.TryGetProperty("tools", out JsonElement tools))
        reqObj["tools"] = tools;

    string reqJson = JsonSerializer.Serialize(reqObj, JsonOpts);
    using StringContent reqContent = new(reqJson, Encoding.UTF8, "application/json");

    if (!isStream)
    {
        using HttpResponseMessage resp = await ollamaProvider.Client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = reqContent }, ct);
        string respBody = await resp.Content.ReadAsStringAsync(ct);
        
        if (!resp.IsSuccessStatusCode)
        {
            ctx.Response.StatusCode = (int)resp.StatusCode;
            await ctx.Response.WriteAsync(respBody, ct);
            return;
        }
        
        CacheReasoningFromResponse(respBody);
        
        using JsonDocument odoc = JsonDocument.Parse(respBody);
        JsonElement msg = odoc.RootElement.GetProperty("choices")[0].GetProperty("message");
        Dictionary<string, object?> ollamaResp = new()
        {
            ["model"] = ollamaEffectiveModel,
            ["created_at"] = DateTime.UtcNow.ToString("o"),
            ["message"] = new Dictionary<string, object?>
            {
                ["role"] = "assistant",
                ["content"] = msg.GetProperty("content").GetString() ?? ""
            },
            ["done"] = true,
            ["done_reason"] = "stop"
        };
        if (msg.TryGetProperty("tool_calls", out JsonElement tcs))
            ((Dictionary<string, object?>)ollamaResp["message"]!)["tool_calls"] = tcs;
        
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(ollamaResp, JsonOpts), ct);
    }
    else
    {
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";
        
        using HttpRequestMessage upstreamReq = new(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = reqContent
        };
        upstreamReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using HttpResponseMessage upstreamResp = await ollamaProvider.Client.SendAsync(
            upstreamReq, HttpCompletionOption.ResponseHeadersRead, ct);
        
        if (!upstreamResp.IsSuccessStatusCode)
            return;
        
        await StreamAndCache(upstreamResp, ctx.Response, ct);
    }
});

// ─── Start ───────────────────────────────────────────────────────────
Console.WriteLine($"╔══════════════════════════════════════════════════════════════════╗");
Console.WriteLine($"║   DeepSeek / Multi-Provider Copilot Proxy (Ultra)               ║");
Console.WriteLine($"╠══════════════════════════════════════════════════════════════════╣");
Console.WriteLine($"║  Version: 2026.06.02                                             ║");
Console.WriteLine($"║  Default: {MODEL,-32}                                  ║");
Console.WriteLine($"║  Providers: {string.Join(", ", PROVIDERS.Select(p => p.Name)),-32}                          ║");
Console.WriteLine($"║  Models:   {string.Join(", ", AVAILABLE_MODELS),-32}                          ║");
Console.WriteLine($"║  URL:     http://localhost:{PORT}/v1                             ║");
Console.WriteLine($"║  Auth:    {(string.IsNullOrEmpty(PROXY_API_KEY) ? "open (no key set)" : "required (PROXY_API_KEY)"),-18} ║");
Console.WriteLine($"╚══════════════════════════════════════════════════════════════════╝");
app.Run();

// ══════════════════════════════════════════════════════════════════════
// Local functions (capture ReasoningCache, _assistantMsgCounter, httpClient)
// ══════════════════════════════════════════════════════════════════════

async Task RefreshAvailableModelsIfNeeded(CancellationToken ct)
{
    if (DateTime.UtcNow - MODELS_LAST_REFRESH_UTC < MODELS_REFRESH_INTERVAL)
        return;

    await RefreshAvailableModels(ct);
}

async Task RefreshAvailableModels(CancellationToken ct)
{
    try
    {
        Dictionary<string, ProviderInfo> newMap = new(StringComparer.OrdinalIgnoreCase);
        List<string> allModels = [];

        foreach (ProviderInfo prov in PROVIDERS)
        {
            string[] discovered = await TryGetModelsFromProvider(prov.Client, ct);
            foreach (string m in discovered)
            {
                if (string.IsNullOrWhiteSpace(m) || newMap.ContainsKey(m))
                    continue;
                // Skip non-chat models (safety/guard/embed/reranker/vision-only etc.)
                var profile = GetModelProfile(m);
                if (profile.ContextLength == 0)
                    continue;
                newMap[m] = prov;
                allModels.Add(m);
            }
        }

        if (allModels.Count > 0)
        {
            AVAILABLE_MODELS = allModels.ToArray();
            MODEL_TO_PROVIDER = newMap;
            MODELS_LAST_REFRESH_UTC = DateTime.UtcNow;
        }
    }
    catch
    {
        // keep current fallback list when discovery fails
    }
}

async Task<string[]> TryGetModelsFromProvider(HttpClient client, CancellationToken ct)
{
    try
    {
        using HttpResponseMessage resp = await client.GetAsync("/v1/models", ct);
        if (!resp.IsSuccessStatusCode)
            return [];

        string body = await resp.Content.ReadAsStringAsync(ct);
        using JsonDocument doc = JsonDocument.Parse(body);
        return ExtractModels(doc.RootElement);
    }
    catch
    {
        return [];
    }
}

string[] ExtractModels(JsonElement root)
{
    IEnumerable<JsonElement> items = [];

    if (root.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Array)
    {
        items = data.EnumerateArray();
    }
    else if (root.TryGetProperty("models", out JsonElement models) && models.ValueKind == JsonValueKind.Array)
    {
        items = models.EnumerateArray();
    }

    return items
        .Select(item =>
        {
            if (item.ValueKind == JsonValueKind.String)
                return item.GetString();

            if (item.ValueKind != JsonValueKind.Object)
                return null;

            if (item.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.String)
                return id.GetString();

            if (item.TryGetProperty("name", out JsonElement name) && name.ValueKind == JsonValueKind.String)
                return name.GetString();

            if (item.TryGetProperty("model", out JsonElement model) && model.ValueKind == JsonValueKind.String)
                return model.GetString();

            return null;
        })
        .Where(x => !string.IsNullOrWhiteSpace(x))
        .Select(x => x!)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

(int ContextLength, int MaxOutputTokens, bool SupportsTools, bool SupportsVision, string[] Capabilities, string Family) GetModelProfile(string model)
{
    string m = model.ToLowerInvariant();
    bool tools = true;
    bool vision = m.Contains("vision") || m.Contains("-vl") || m.Contains("neva") || m.Contains("vila") || m.Contains("fuyu") || m.Contains("kosmos");
    int ctx, maxOut;

    // DeepSeek models: 1M context, 384K output
    if (m.Contains("deepseek"))
    { ctx = 1_000_000; maxOut = 384_000; }
    // NVIDIA Nemotron Super (1M context)
    else if (m.Contains("nemotron-3-super"))
    { ctx = 1_000_000; maxOut = 16384; }
    // NVIDIA Nemotron Ultra 253B
    else if (m.Contains("nemotron") && m.Contains("ultra"))
    { ctx = 128_000; maxOut = 16384; }
    // NVIDIA Nemotron 70B / 51B / 340B
    else if (m.Contains("nemotron") || m.Contains("nvidia-nemotron"))
    { ctx = 128_000; maxOut = 16384; }
    // Llama 4 / 3.3 / 3.2 / 3.1
    else if (m.Contains("llama-4") || m.Contains("llama-3.3") || m.Contains("llama-3.2") || m.Contains("llama-3.1"))
    { ctx = 128_000; maxOut = 16384; }
    // Llama 2 / CodeLlama
    else if (m.Contains("llama-2") || m.Contains("codellama"))
    { ctx = 4096; maxOut = 4096; }
    // Mistral Large 3 / Large 2
    else if (m.Contains("mistral-large-3") || m.Contains("mistral-large-2") || m.Contains("mistral-large"))
    { ctx = 128_000; maxOut = 16384; }
    // Mistral Medium / Small / Mixtral 8x22B
    else if (m.Contains("mistral") && (m.Contains("medium") || m.Contains("small")))
    { ctx = 128_000; maxOut = 16384; }
    else if (m.Contains("mixtral-8x22b"))
    { ctx = 65536; maxOut = 4096; }
    else if (m.Contains("mixtral") || m.Contains("mistral") || m.Contains("codestral") || m.Contains("ministral") || m.Contains("mistral-nemo"))
    { ctx = 32768; maxOut = 4096; }
    // Qwen3 Coder 480B
    else if (m.Contains("qwen3-coder"))
    { ctx = 128_000; maxOut = 16384; }
    else if (m.Contains("qwen"))
    { ctx = 128_000; maxOut = 8192; }
    // CodeGemma / Gemma 4 / Gemma 3 / Gemma 2
    else if (m.Contains("gemma-4"))
    { ctx = 128_000; maxOut = 16384; }
    else if (m.Contains("gemma-3"))
    { ctx = 32768; maxOut = 8192; }
    else if (m.Contains("gemma-2") || m.Contains("gemma-2b") || m.Contains("codegemma"))
    { ctx = 8192; maxOut = 4096; }
    // Phi-4 / Phi-3.5
    else if (m.Contains("phi-4"))
    { ctx = 128_000; maxOut = 16384; }
    else if (m.Contains("phi-3"))
    { ctx = 128_000; maxOut = 4096; }
    // Granite Code 34B / 8B
    else if (m.Contains("granite-34b-code"))
    { ctx = 128_000; maxOut = 4096; }
    else if (m.Contains("granite"))
    { ctx = 128_000; maxOut = 4096; }
    // StarCoder2
    else if (m.Contains("starcoder2"))
    { ctx = 16384; maxOut = 4096; }
    // GPT-OSS
    else if (m.Contains("gpt-oss"))
    { ctx = 128_000; maxOut = 16384; }
    // DBRX / Jamba
    else if (m.Contains("dbrx") || m.Contains("jamba"))
    { ctx = 32768; maxOut = 4096; }
    // Yi-large / Seed-OSS / Kimi / Step / GLM
    else if (m.Contains("yi-large") || m.Contains("seed-oss"))
    { ctx = 32768; maxOut = 4096; }
    else if (m.Contains("kimi"))
    { ctx = 128_000; maxOut = 8192; }
    else if (m.Contains("step-3"))
    { ctx = 128_000; maxOut = 16384; }
    else if (m.Contains("glm-5"))
    { ctx = 128_000; maxOut = 8192; }
    // Solar / Zamba
    else if (m.Contains("solar") || m.Contains("zamba"))
    { ctx = 4096; maxOut = 4096; }
    // Palmyra
    else if (m.Contains("palmyra"))
    { ctx = 32768; maxOut = 4096; }
    // Safety / Embed / Guard / Reranker / Reward / Clip / Parse / Detector / cached-model / Translate — exclude from chat
    else if (m.Contains("guard") || m.Contains("safety") || m.Contains("embed") || m.Contains("retriever") || m.Contains("reranker") || m.Contains("reward") || m.Contains("parse") || m.Contains("detector") || m.Contains("clip") || m.Contains("nv-embed") || m.Contains("embedqa") || m.Contains("cached-model") || m.Contains("rerank") || m.Contains("classification") || m.Contains("riva-translate") || m.Contains("synthetic-video"))
    { ctx = 0; maxOut = 0; tools = false; }
    // Default fallback
    else
    { ctx = 128_000; maxOut = 8192; }

    string[] capabilities = vision ? ["completion", "tools", "vision"] : ["completion", "tools"];

    string family = m.Contains("deepseek") ? "deepseek"
        : m.Contains("nemotron") || m.Contains("llama-3.1-nemotron") || m.Contains("llama-3.3-nemotron") || m.Contains("nvidia-nemotron") || m.Contains("cosmos-reason") ? "nvidia"
        : m.Contains("llama") || m.Contains("codellama") ? "meta"
        : m.Contains("mistral") || m.Contains("mixtral") || m.Contains("codestral") || m.Contains("ministral") ? "mistralai"
        : m.Contains("qwen") ? "qwen"
        : m.Contains("gemma") || m.Contains("codegemma") ? "google"
        : m.Contains("phi-") ? "microsoft"
        : m.Contains("granite") ? "ibm"
        : m.Contains("gpt-oss") ? "openai"
        : m.Contains("nemotron") ? "nvidia"
        : MODEL_TO_PROVIDER.TryGetValue(model, out var prov) ? prov.Name
        : "api";

    return (ctx, maxOut, tools, vision, capabilities, family);
}

Dictionary<string, object?> BuildOllamaShowResponse(string model)
{
    var p = GetModelProfile(model);

    return new Dictionary<string, object?>
    {
        ["model"] = model,
        ["modified_at"] = DateTime.UtcNow.ToString("o"),
        ["size"] = 0L,
        ["digest"] = "sha256:0000000000000000000000000000000000000000000000000000000000000000",
        ["license"] = "NIM API",
        ["modelfile"] = $"FROM {model}",
        ["parameters"] = $"num_ctx {p.ContextLength}\nnum_predict {p.MaxOutputTokens}",
        ["template"] = "{{ .Prompt }}",
        ["details"] = new Dictionary<string, object?>
        {
            ["parent_model"] = "",
            ["format"] = "api",
            ["family"] = p.Family,
            ["families"] = new[] { p.Family },
            ["parameter_size"] = "api",
            ["quantization_level"] = "none"
        },
        ["model_info"] = new Dictionary<string, object?>
        {
            ["general.architecture"] = p.Family,
            ["general.basename"] = model,
            ["general.context_length"] = p.ContextLength,
            ["context_length"] = p.ContextLength,
            ["max_output_tokens"] = p.MaxOutputTokens,
            ["input_token_limit"] = p.ContextLength,
            ["output_token_limit"] = p.MaxOutputTokens,
            ["supports_tools"] = p.SupportsTools,
            ["supports_tool_calls"] = p.SupportsTools,
            ["supports_vision"] = p.SupportsVision,
            ["supports_images"] = p.SupportsVision
        },
        ["capabilities"] = p.Capabilities,
        ["context_length"] = p.ContextLength,
        ["max_output_tokens"] = p.MaxOutputTokens,
        ["input_token_limit"] = p.ContextLength,
        ["output_token_limit"] = p.MaxOutputTokens,
        ["supports_tools"] = p.SupportsTools,
        ["supports_tool_calls"] = p.SupportsTools,
        ["supports_vision"] = p.SupportsVision,
        ["supports_images"] = p.SupportsVision
    };
}

string? ModifyRequest(JsonDocument doc)
{
    JsonElement root = doc.RootElement;
    if (!root.TryGetProperty("messages", out JsonElement msgs))
        return null;

    // Keep model as-is for multi-provider routing; inject reasoning_content only
    int idx = 0;
    bool modified = false;
    using MemoryStream ms = new();
    using Utf8JsonWriter w = new(ms);

    w.WriteStartObject();
    foreach (JsonProperty prop in root.EnumerateObject())
    {
        if (!prop.NameEquals("messages"))
        {
            prop.WriteTo(w);
            continue;
        }
        
        w.WritePropertyName("messages");
        w.WriteStartArray();
        foreach (JsonElement msg in msgs.EnumerateArray())
        {
            string? role = msg.TryGetProperty("role", out JsonElement r) ? r.GetString() : null;
            if (role == "assistant")
            {
                bool hasTc = msg.TryGetProperty("tool_calls", out JsonElement tcArr) && tcArr.GetArrayLength() > 0;
                string? key = null;
                
                if (hasTc)
                {
                    List<string> ids = [];
                    foreach (JsonElement tc in tcArr.EnumerateArray())
                        if (tc.TryGetProperty("id", out JsonElement idE) && idE.ValueKind == JsonValueKind.String)
                            ids.Add(idE.GetString()!);
                    if (ids.Count > 0) key = $"toolcall:{string.Join("|", ids)}";
                }
                else
                {
                    key = $"assistant:{idx++}";
                }
                
                if (key != null && ReasoningCache.TryGetValue(key, out string? rc))
                {
                    bool needsInject = !msg.TryGetProperty("reasoning_content", out JsonElement exRc)
                        || exRc.ValueKind != JsonValueKind.String
                        || string.IsNullOrEmpty(exRc.GetString());
                    
                    if (needsInject)
                    {
                        w.WriteStartObject();
                        foreach (JsonProperty mp in msg.EnumerateObject())
                            mp.WriteTo(w);
                        w.WriteString("reasoning_content", rc);
                        w.WriteEndObject();
                        modified = true;
                        continue;
                    }
                }
            }

            msg.WriteTo(w);
        }

        w.WriteEndArray();
    }

    w.WriteEndObject();
    w.Flush();
    
    return modified ? Encoding.UTF8.GetString(ms.ToArray()) : null;
}

void CacheReasoningFromResponse(string json)
{
    try
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        if (!root.TryGetProperty("choices", out JsonElement choices) || choices.GetArrayLength() == 0) return;

        JsonElement msg = choices[0].TryGetProperty("message", out JsonElement m) ? m : choices[0].TryGetProperty("delta", out JsonElement d) ? d : default;
        if (msg.ValueKind == JsonValueKind.Undefined) return;
        if (!msg.TryGetProperty("reasoning_content", out JsonElement rc) || string.IsNullOrEmpty(rc.GetString())) return;
        
        string key;
        if (msg.TryGetProperty("tool_calls", out JsonElement tcs) && tcs.GetArrayLength() > 0)
        {
            List<string> ids = [];
            foreach (JsonElement tc in tcs.EnumerateArray())
                if (tc.TryGetProperty("id", out JsonElement idE) && idE.ValueKind == JsonValueKind.String)
                    ids.Add(idE.GetString()!);
            key = $"toolcall:{string.Join("|", ids)}";
        }
        else
        {
            key = $"assistant:{Interlocked.Increment(ref _assistantMsgCounter) - 1}";
        }
        
        ReasoningCache[key] = rc.GetString()!;
    }
    catch { /* cache errors are non-critical */ }
}

async Task StreamAndCache(HttpResponseMessage upstream, HttpResponse downstream, CancellationToken ct)
{
    using Stream upstreamStream = await upstream.Content.ReadAsStreamAsync(ct);
    using StreamReader reader = new(upstreamStream);
    await using StreamWriter writer = new(downstream.Body, leaveOpen: true) { NewLine = "\n" };

    StringBuilder sb = new(4096);
    List<string>? tcIds = null;
    bool hasTc = false;
    int? asstIdx = null;
    
    while (true)
    {
        string? line = await reader.ReadLineAsync(ct);
        if (line == null) break;
        
        if (line.StartsWith("data:"))
        {
            string json = line.Substring(5).TrimStart();
            if (json.Length > 0 && json != "[DONE]")
            {
                try
                {
                    using JsonDocument chunk = JsonDocument.Parse(json);
                    JsonElement cr = chunk.RootElement;
                    if (cr.TryGetProperty("choices", out JsonElement choices) && choices.GetArrayLength() > 0)
                    {
                        JsonElement delta = choices[0].TryGetProperty("delta", out JsonElement d) ? d
                            : choices[0].TryGetProperty("message", out JsonElement mm) ? mm : default;
                        
                        if (delta.ValueKind != JsonValueKind.Undefined)
                        {
                            if (delta.TryGetProperty("reasoning_content", out JsonElement rc) && rc.ValueKind == JsonValueKind.String)
                            {
                                string? rct = rc.GetString();
                                if (!string.IsNullOrEmpty(rct)) sb.Append(rct);
                            }

                            if (delta.TryGetProperty("tool_calls", out JsonElement tcs) && tcs.ValueKind == JsonValueKind.Array)
                            {
                                hasTc = true;
                                foreach (JsonElement tc in tcs.EnumerateArray())
                                {
                                    if (tc.TryGetProperty("id", out JsonElement idE) && idE.ValueKind == JsonValueKind.String)
                                    {
                                        tcIds ??= [];
                                        string id = idE.GetString()!;
                                        if (!tcIds.Contains(id)) tcIds.Add(id);
                                    }
                                }
                            }

                            if (choices[0].TryGetProperty("finish_reason", out JsonElement fr) && fr.ValueKind != JsonValueKind.Null)
                            {
                                string reasoning = sb.ToString();
                                if (!string.IsNullOrEmpty(reasoning))
                                {
                                    string key;
                                    if (hasTc && tcIds != null && tcIds.Count > 0)
                                        key = $"toolcall:{string.Join("|", tcIds)}";
                                    else
                                        key = $"assistant:{asstIdx ?? (int)(Interlocked.Increment(ref _assistantMsgCounter) - 1)}";
                                    ReasoningCache[key] = reasoning;
                                }
                            }
                        }
                    }
                }
                catch { /* parse errors are non-critical */ }
                
                // Pass-through all data lines unmodified
                await writer.WriteAsync("data: ");
                await writer.WriteAsync(json);
                await writer.WriteLineAsync();
            }
            else
            {
                await writer.WriteLineAsync(line);
            }
        }
        else
        {
            await writer.WriteLineAsync(line);
        }

        await writer.FlushAsync(ct);
    }
}

// ══════════════════════════════════════════════════════════════════════
// Types
// ══════════════════════════════════════════════════════════════════════

record struct ProviderInfo(string Name, string ApiKey, string BaseUrl, HttpClient Client);

