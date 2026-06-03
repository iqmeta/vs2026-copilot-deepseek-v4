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
Dictionary<string, ModelSelectionEntry[]> PROVIDER_MODEL_SELECTIONS = LoadProviderModelSelections();

// Resolve model → provider: look up by model name, fallback to first provider's default
ProviderInfo ResolveProvider(string? requestedModel) =>
    !string.IsNullOrWhiteSpace(requestedModel) && MODEL_TO_PROVIDER.TryGetValue(requestedModel, out ProviderInfo p)
        ? p : PROVIDERS[0];

string ResolveModel(string? requestedModel) =>
    !string.IsNullOrWhiteSpace(requestedModel) && MODEL_TO_PROVIDER.ContainsKey(requestedModel)
        ? requestedModel : MODEL;

// ─── State ───────────────────────────────────────────────────────────
SemaphoreSlim _refreshLock = new(1, 1);
const int ReasoningCacheMaxEntries = 512;
ConcurrentDictionary<string, string> ReasoningCache = new(StringComparer.Ordinal);
ConcurrentQueue<string> ReasoningCacheEvictionQueue = new();
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

// Fallback: legacy DEEPSEEK_API_KEY — always checked, allows mixing formats in .env
{
    string? legacyKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
    if (!string.IsNullOrWhiteSpace(legacyKey) && !PROVIDERS.Any(p => p.Name == "deepseek"))
    {
        string legacyUrl = Environment.GetEnvironmentVariable("DEEPSEEK_BASE_URL") ?? "https://api.deepseek.com";
        PROVIDERS.Add(new ProviderInfo("deepseek", legacyKey, legacyUrl,
            CreateProviderClient(legacyUrl, legacyKey)));
    }
}

if (PROVIDERS.Count == 0)
    throw new InvalidOperationException(
        "No AI provider configured. Set PROVIDER_<NAME>_API_KEY (e.g. PROVIDER_DEEPSEEK_API_KEY) or DEEPSEEK_API_KEY.");

await RefreshAvailableModels(CancellationToken.None);

WebApplication app = builder.Build();

// ─── Optional proxy auth middleware ─────────────────────────────────
if (!string.IsNullOrEmpty(PROXY_API_KEY))
{
    app.Use(async (ctx, next) =>
    {
        string? auth = ctx.Request.Headers.Authorization;
        int bearerLen = "Bearer ".Length;
        bool valid = auth != null
            && auth.Length > bearerLen
            && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            && string.Equals(auth[bearerLen..], PROXY_API_KEY, StringComparison.Ordinal);
        if (valid)
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
            string providerName = MODEL_TO_PROVIDER.TryGetValue(m, out ProviderInfo prov) ? prov.Name : "unknown";
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
    string effectiveModel = ResolveModel(reqModel);
    ProviderInfo provider = ResolveProvider(effectiveModel);

    // Derive a per-conversation cache scope to prevent cross-user reasoning-cache pollution.
    // We use an explicit "conversation_id" if the client sends one (some Copilot builds do),
    // otherwise we fall back to a hash of the first user message as a stable proxy for the session.
    string convScope = ExtractConversationScope(root);

    // Inject cached reasoning_content + model execution defaults from provider config
    string? modified = ModifyRequest(doc, convScope);
    string bodyText = modified ?? rawBody;
    bodyText = ApplyExecutionDefaults(bodyText, effectiveModel);

    using CancellationTokenSource? timeoutCts = CreateModelTimeoutCts(effectiveModel, ct);
    CancellationToken requestCt = timeoutCts?.Token ?? ct;

    // For non-streaming: direct proxy via provider's HttpClient
    if (!isStream)
    {
        using StringContent content = new(bodyText, Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await provider.Client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = content },
            requestCt);

        string respBody = await response.Content.ReadAsStringAsync(ct);

        if (response.IsSuccessStatusCode)
            CacheReasoningFromResponse(respBody, convScope);

        ctx.Response.StatusCode = (int)response.StatusCode;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(respBody, ct);
        return;
    }

    // ── Streaming ──
    // Send upstream request BEFORE committing the response status so errors can still return a proper status code
    using StringContent reqContent = new(bodyText, Encoding.UTF8, "application/json");
    using HttpRequestMessage upstreamReq = new(HttpMethod.Post, "/v1/chat/completions")
    {
        Content = reqContent
    };
    upstreamReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

    HttpResponseMessage upstreamResp;
    try
    {
        upstreamResp = await provider.Client.SendAsync(
            upstreamReq, HttpCompletionOption.ResponseHeadersRead, requestCt);
    }
    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
    {
        // Upstream timed out (model-level timeout) — return a clean 504 before headers are sent
        ctx.Response.StatusCode = 504;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(
            """{"error":{"message":"Upstream model timed out","type":"timeout","code":"gateway_timeout"}}""", ct);
        return;
    }

    using (upstreamResp)
    {
        if (!upstreamResp.IsSuccessStatusCode)
        {
            string errBody = await upstreamResp.Content.ReadAsStringAsync(ct);
            ctx.Response.StatusCode = (int)upstreamResp.StatusCode;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(errBody, ct);
            return;
        }

        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";

        try
        {
            await StreamAndCache(upstreamResp, ctx.Response, ct, convScope);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timeout mid-stream: send a final SSE error chunk so the client parser closes cleanly
            try
            {
                await ctx.Response.WriteAsync(
                    "data: {\"error\":{\"message\":\"Upstream model timed out mid-stream\",\"type\":\"timeout\",\"code\":\"gateway_timeout\"}}\n\ndata: [DONE]\n\n", ct);
            }
            catch { /* client may have already disconnected */ }
        }
    }
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
                (int ContextLength, int MaxOutputTokens, bool SupportsTools, bool SupportsVision, string[] Capabilities, string Family) p = GetModelProfile(m);
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
    ModelExecutionConfig ollamaExec = GetExecutionConfigForModel(ollamaEffectiveModel);
    string ollamaConvScope = ExtractConversationScope(root);

    Dictionary<string, object?> reqObj = new()
    {
        ["model"] = ollamaEffectiveModel,
        ["messages"] = messages,
        ["stream"] = isStream,
        ["max_tokens"] = ollamaExec.MaxTokensPreferred ?? 8192
    };
    if (root.TryGetProperty("tools", out JsonElement tools))
        reqObj["tools"] = tools;

    if (ollamaExec.Temperature.HasValue)
        reqObj["temperature"] = ollamaExec.Temperature.Value;
    if (ollamaExec.TopP.HasValue)
        reqObj["top_p"] = ollamaExec.TopP.Value;
    if (!string.IsNullOrWhiteSpace(ollamaExec.ReasoningEffort))
        reqObj["reasoning_effort"] = ollamaExec.ReasoningEffort;

    string reqJson = JsonSerializer.Serialize(reqObj, JsonOpts);
    using StringContent reqContent = new(reqJson, Encoding.UTF8, "application/json");
    using CancellationTokenSource? ollamaTimeoutCts = CreateModelTimeoutCts(ollamaEffectiveModel, ct);
    CancellationToken ollamaCt = ollamaTimeoutCts?.Token ?? ct;

    if (!isStream)
    {
        using HttpResponseMessage resp = await ollamaProvider.Client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = reqContent }, ollamaCt);
        string respBody = await resp.Content.ReadAsStringAsync(ct);
        
        if (!resp.IsSuccessStatusCode)
        {
            ctx.Response.StatusCode = (int)resp.StatusCode;
            await ctx.Response.WriteAsync(respBody, ct);
            return;
        }
        
        CacheReasoningFromResponse(respBody, ollamaConvScope);
        
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
        using HttpRequestMessage upstreamReq = new(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = reqContent
        };
        upstreamReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        HttpResponseMessage upstreamResp;
        try
        {
            upstreamResp = await ollamaProvider.Client.SendAsync(
                upstreamReq, HttpCompletionOption.ResponseHeadersRead, ollamaCt);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            ctx.Response.StatusCode = 504;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(
                """{"error":{"message":"Upstream model timed out","type":"timeout","code":"gateway_timeout"}}""", ct);
            return;
        }

        using (upstreamResp)
        {
            if (!upstreamResp.IsSuccessStatusCode)
                return;

            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";

            try
            {
                await StreamAndCache(upstreamResp, ctx.Response, ct, ollamaConvScope);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try
                {
                    await ctx.Response.WriteAsync(
                        "data: {\"error\":{\"message\":\"Upstream model timed out mid-stream\",\"type\":\"timeout\",\"code\":\"gateway_timeout\"}}\n\ndata: [DONE]\n\n", ct);
                }
                catch { /* client may have already disconnected */ }
            }
        }
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

    // Non-blocking try: if another request is already refreshing, skip this refresh.
    if (!await _refreshLock.WaitAsync(0, ct))
        return;
    try
    {
        // Double-check inside the lock in case another refresh completed while we waited.
        if (DateTime.UtcNow - MODELS_LAST_REFRESH_UTC < MODELS_REFRESH_INTERVAL)
            return;
        await RefreshAvailableModels(ct);
    }
    finally
    {
        _refreshLock.Release();
    }
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

                // Keep only curated, powerful, code/reasoning-oriented models
                if (!IsPreferredModel(m, prov.Name))
                    continue;

                // Skip non-chat models (safety/guard/embed/reranker/etc.)
                (int ContextLength, int MaxOutputTokens, bool SupportsTools, bool SupportsVision, string[] Capabilities, string Family) profile = GetModelProfile(m);
                if (profile.ContextLength == 0)
                    continue;

                newMap[m] = prov;
                allModels.Add(m);
            }
        }

        if (allModels.Count > 0)
        {
            // Build the new array first, then publish both references atomically via Volatile.Write.
            // Readers using Volatile.Read see either the old or new complete pair — never a torn state.
            string[] newModels = allModels
                .OrderBy(model => GetPreferredModelPriority(model, newMap[model].Name))
                .ThenBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            Volatile.Write(ref MODEL_TO_PROVIDER, newMap);
            Volatile.Write(ref AVAILABLE_MODELS, newModels);
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

Dictionary<string, ModelSelectionEntry[]> LoadProviderModelSelections()
{
    Dictionary<string, ModelSelectionEntry[]> selections = new(StringComparer.OrdinalIgnoreCase);
    string[] candidateDirs =
    [
        Path.Combine(AppContext.BaseDirectory, "config", "model-selection"),
        Path.Combine(Directory.GetCurrentDirectory(), "config", "model-selection")
    ];

    foreach (string dir in candidateDirs.Distinct(StringComparer.OrdinalIgnoreCase))
    {
        if (!Directory.Exists(dir))
            continue;

        foreach (string file in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
                JsonElement root = doc.RootElement;

                if (!root.TryGetProperty("provider", out JsonElement provE) || provE.ValueKind != JsonValueKind.String)
                    continue;

                string provider = provE.GetString()!.Trim().ToLowerInvariant();

                if (!root.TryGetProperty("models", out JsonElement modelsE) || modelsE.ValueKind != JsonValueKind.Array)
                    continue;

                List<ModelSelectionEntry> entries = [];
                int idx = 0;
                foreach (JsonElement item in modelsE.EnumerateArray())
                {
                    idx++;
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        string? match = item.GetString();
                        if (!string.IsNullOrWhiteSpace(match))
                            entries.Add(new ModelSelectionEntry(match!, idx, true, new ModelExecutionConfig()));
                        continue;
                    }

                    if (item.ValueKind != JsonValueKind.Object)
                        continue;

                    string? matchValue = null;
                    if (item.TryGetProperty("match", out JsonElement matchE) && matchE.ValueKind == JsonValueKind.String)
                        matchValue = matchE.GetString();
                    else if (item.TryGetProperty("model", out JsonElement modelE) && modelE.ValueKind == JsonValueKind.String)
                        matchValue = modelE.GetString();
                    else if (item.TryGetProperty("id", out JsonElement idE) && idE.ValueKind == JsonValueKind.String)
                        matchValue = idE.GetString();

                    if (string.IsNullOrWhiteSpace(matchValue))
                        continue;

                    int priority = item.TryGetProperty("priority", out JsonElement priE) && priE.ValueKind == JsonValueKind.Number
                        ? priE.GetInt32() : idx;
                    bool enabled = !item.TryGetProperty("enabled", out JsonElement enE) || enE.ValueKind != JsonValueKind.False;

                    ModelExecutionConfig exec = new();
                    if (item.TryGetProperty("execution", out JsonElement execE) && execE.ValueKind == JsonValueKind.Object)
                    {
                        exec = new ModelExecutionConfig(
                            ContextLength: execE.TryGetProperty("context_length", out JsonElement ctxE) && ctxE.ValueKind == JsonValueKind.Number ? ctxE.GetInt32() : null,
                            MaxOutputTokens: execE.TryGetProperty("max_output_tokens", out JsonElement outE) && outE.ValueKind == JsonValueKind.Number ? outE.GetInt32() : null,
                            SupportsTools: execE.TryGetProperty("supports_tools", out JsonElement stE) && stE.ValueKind is JsonValueKind.True or JsonValueKind.False ? stE.GetBoolean() : null,
                            SupportsVision: execE.TryGetProperty("supports_vision", out JsonElement svE) && svE.ValueKind is JsonValueKind.True or JsonValueKind.False ? svE.GetBoolean() : null,
                            Family: execE.TryGetProperty("family", out JsonElement famE) && famE.ValueKind == JsonValueKind.String ? famE.GetString() : null,
                            Temperature: execE.TryGetProperty("temperature", out JsonElement tempE) && tempE.ValueKind == JsonValueKind.Number ? tempE.GetDouble() : null,
                            TopP: execE.TryGetProperty("top_p", out JsonElement topPE) && topPE.ValueKind == JsonValueKind.Number ? topPE.GetDouble() : null,
                            MaxTokensPreferred: execE.TryGetProperty("max_tokens", out JsonElement maxTokE) && maxTokE.ValueKind == JsonValueKind.Number ? maxTokE.GetInt32() : null,
                            ReasoningEffort: execE.TryGetProperty("reasoning_effort", out JsonElement reE) && reE.ValueKind == JsonValueKind.String ? reE.GetString() : null,
                            TimeoutSeconds: execE.TryGetProperty("timeout_seconds", out JsonElement timeoutE) && timeoutE.ValueKind == JsonValueKind.Number ? timeoutE.GetInt32() : null
                        );
                    }

                    entries.Add(new ModelSelectionEntry(matchValue!, priority, enabled, exec));
                }

                if (entries.Count > 0)
                    selections[provider] = entries.OrderBy(x => x.Priority).ToArray();
            }
            catch
            {
                // ignore malformed selection files
            }
        }
    }

    return selections;
}

ModelSelectionEntry[] GetDefaultPreferredModelSelections() =>
[
    new("deepseek-v4-pro", 1, true, new()),
    new("qwen3-coder-480b-a35b", 2, true, new()),
    new("qwen3.5-397b-a17b", 3, true, new()),
    new("mistral-large-3-675b-instruct-2512", 4, true, new()),
    new("llama-4-maverick-17b-128e-instruct", 5, true, new()),
    new("llama-3.1-nemotron-ultra-253b-v1", 6, true, new()),
    new("nemotron-4-340b-instruct", 7, true, new()),
    new("gpt-oss-120b", 8, true, new()),
    new("kimi-k2.6", 9, true, new()),
    new("llama-3.3-nemotron-super-49b-v1.5", 10, true, new())
];

ModelSelectionEntry[] GetProviderModelSelections(string providerName)
{
    if (PROVIDER_MODEL_SELECTIONS.TryGetValue(providerName, out ModelSelectionEntry[]? configured) && configured.Length > 0)
        return configured;

    return GetDefaultPreferredModelSelections();
}

ModelSelectionEntry? FindModelSelectionEntry(string model, string providerName)
{
    string m = model.ToLowerInvariant();

    // Exclude non-chat/support models
    if (m.Contains("guard") || m.Contains("safety") || m.Contains("embed") || m.Contains("retriever") || m.Contains("reranker")
        || m.Contains("reward") || m.Contains("parse") || m.Contains("detector") || m.Contains("clip") || m.Contains("riva-translate"))
        return null;

    ModelSelectionEntry[] entries = GetProviderModelSelections(providerName);
    foreach (ModelSelectionEntry entry in entries.OrderBy(x => x.Priority))
    {
        if (!entry.Enabled) continue;
        if (m.Contains(entry.Match, StringComparison.OrdinalIgnoreCase))
            return entry;
    }

    return null;
}

int GetPreferredModelPriority(string model, string providerName)
{
    ModelSelectionEntry? entry = FindModelSelectionEntry(model, providerName);
    return entry?.Priority ?? int.MaxValue;
}

bool IsPreferredModel(string model, string providerName) =>
    FindModelSelectionEntry(model, providerName) != null;

ModelExecutionConfig GetExecutionConfigForModel(string model)
{
    if (MODEL_TO_PROVIDER.TryGetValue(model, out ProviderInfo provider))
    {
        ModelSelectionEntry? entry = FindModelSelectionEntry(model, provider.Name);
        if (entry != null)
            return entry.Value.Execution;
    }

    foreach (KeyValuePair<string, ModelSelectionEntry[]> kv in PROVIDER_MODEL_SELECTIONS)
    {
        ModelSelectionEntry? entry = FindModelSelectionEntry(model, kv.Key);
        if (entry != null)
            return entry.Value.Execution;
    }

    return new ModelExecutionConfig();
}

(int ContextLength, int MaxOutputTokens, bool SupportsTools, bool SupportsVision, string[] Capabilities, string Family) GetModelProfile(string model)
{
    ModelExecutionConfig configured = GetExecutionConfigForModel(model);
    string m = model.ToLowerInvariant();
    bool tools = configured.SupportsTools ?? true;
    bool vision = configured.SupportsVision ?? (m.Contains("vision") || m.Contains("-vl") || m.Contains("neva") || m.Contains("vila") || m.Contains("fuyu") || m.Contains("kosmos"));
    int ctx, maxOut;

    // Hard exclude non-chat families first (must run before generic 'nemotron/llama' branches)
    if (m.Contains("guard") || m.Contains("safety") || m.Contains("embed") || m.Contains("retriever") || m.Contains("reranker") || m.Contains("reward") || m.Contains("parse") || m.Contains("detector") || m.Contains("clip") || m.Contains("nv-embed") || m.Contains("embedqa") || m.Contains("cached-model") || m.Contains("rerank") || m.Contains("classification") || m.Contains("riva-translate") || m.Contains("synthetic-video"))
    { ctx = 0; maxOut = 0; tools = false; }
    // DeepSeek models: 1M context, 384K output
    else if (m.Contains("deepseek"))
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
    // Default fallback
    else
    { ctx = 128_000; maxOut = 8192; }

    ctx = configured.ContextLength ?? ctx;
    maxOut = configured.MaxOutputTokens ?? maxOut;

    string[] capabilities = vision ? ["completion", "tools", "vision"] : ["completion", "tools"];

    string family = configured.Family
        ?? (m.Contains("deepseek") ? "deepseek"
        : m.Contains("nemotron") || m.Contains("llama-3.1-nemotron") || m.Contains("llama-3.3-nemotron") || m.Contains("nvidia-nemotron") || m.Contains("cosmos-reason") ? "nvidia"
        : m.Contains("llama") || m.Contains("codellama") ? "meta"
        : m.Contains("mistral") || m.Contains("mixtral") || m.Contains("codestral") || m.Contains("ministral") ? "mistralai"
        : m.Contains("qwen") ? "qwen"
        : m.Contains("gemma") || m.Contains("codegemma") ? "google"
        : m.Contains("phi-") ? "microsoft"
        : m.Contains("granite") ? "ibm"
        : m.Contains("gpt-oss") ? "openai"
        : m.Contains("nemotron") ? "nvidia"
        : MODEL_TO_PROVIDER.TryGetValue(model, out ProviderInfo prov) ? prov.Name
        : "api");

    return (ctx, maxOut, tools, vision, capabilities, family);
}

Dictionary<string, object?> BuildOllamaShowResponse(string model)
{
    (int ContextLength, int MaxOutputTokens, bool SupportsTools, bool SupportsVision, string[] Capabilities, string Family) p = GetModelProfile(model);
    ModelExecutionConfig exec = GetExecutionConfigForModel(model);

    return new Dictionary<string, object?>
    {
        ["model"] = model,
        ["modified_at"] = DateTime.UtcNow.ToString("o"),
        ["size"] = 0L,
        ["digest"] = "sha256:0000000000000000000000000000000000000000000000000000000000000000",
        ["license"] = "NIM API",
        ["modelfile"] = $"FROM {model}",
        ["parameters"] = BuildOllamaParametersString(p.ContextLength, p.MaxOutputTokens, exec),
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
        ["supports_images"] = p.SupportsVision,
        ["recommended_parameters"] = BuildRecommendedParameters(exec)
    };
}

Dictionary<string, object?> BuildRecommendedParameters(ModelExecutionConfig exec)
{
    Dictionary<string, object?> result = new();

    if (exec.Temperature.HasValue)
        result["temperature"] = exec.Temperature.Value;
    if (exec.TopP.HasValue)
        result["top_p"] = exec.TopP.Value;
    if (exec.MaxTokensPreferred.HasValue)
        result["max_tokens"] = exec.MaxTokensPreferred.Value;
    if (!string.IsNullOrWhiteSpace(exec.ReasoningEffort))
        result["reasoning_effort"] = exec.ReasoningEffort;
    if (exec.TimeoutSeconds.HasValue)
        result["timeout_seconds"] = exec.TimeoutSeconds.Value;

    return result;
}

string BuildOllamaParametersString(int contextLength, int maxOutputTokens, ModelExecutionConfig exec)
{
    List<string> lines =
    [
        $"num_ctx {contextLength}",
        $"num_predict {maxOutputTokens}"
    ];

    if (exec.Temperature.HasValue)
        lines.Add($"temperature {exec.Temperature.Value:0.###}");
    if (exec.TopP.HasValue)
        lines.Add($"top_p {exec.TopP.Value:0.###}");
    if (exec.MaxTokensPreferred.HasValue)
        lines.Add($"max_tokens {exec.MaxTokensPreferred.Value}");
    if (!string.IsNullOrWhiteSpace(exec.ReasoningEffort))
        lines.Add($"reasoning_effort {exec.ReasoningEffort}");

    return string.Join("\n", lines);
}

CancellationTokenSource? CreateModelTimeoutCts(string model, CancellationToken outer)
{
    ModelExecutionConfig exec = GetExecutionConfigForModel(model);
    if (!exec.TimeoutSeconds.HasValue || exec.TimeoutSeconds.Value <= 0)
        return null;

    CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(outer);
    linked.CancelAfter(TimeSpan.FromSeconds(exec.TimeoutSeconds.Value));
    return linked;
}

string ApplyExecutionDefaults(string rawBody, string model)
{
    ModelExecutionConfig exec = GetExecutionConfigForModel(model);
    if (!exec.Temperature.HasValue && !exec.TopP.HasValue && !exec.MaxTokensPreferred.HasValue && string.IsNullOrWhiteSpace(exec.ReasoningEffort))
        return rawBody;

    try
    {
        using JsonDocument original = JsonDocument.Parse(rawBody);
        JsonElement root = original.RootElement;
        using MemoryStream ms = new();
        using Utf8JsonWriter writer = new(ms);

        writer.WriteStartObject();

        bool hasTemperature = false;
        bool hasTopP = false;
        bool hasMaxTokens = false;
        bool hasReasoningEffort = false;

        foreach (JsonProperty prop in root.EnumerateObject())
        {
            if (prop.NameEquals("temperature")) hasTemperature = true;
            else if (prop.NameEquals("top_p")) hasTopP = true;
            else if (prop.NameEquals("max_tokens")) hasMaxTokens = true;
            else if (prop.NameEquals("reasoning_effort")) hasReasoningEffort = true;

            prop.WriteTo(writer);
        }

        if (!hasTemperature && exec.Temperature.HasValue)
            writer.WriteNumber("temperature", exec.Temperature.Value);
        if (!hasTopP && exec.TopP.HasValue)
            writer.WriteNumber("top_p", exec.TopP.Value);
        if (!hasMaxTokens && exec.MaxTokensPreferred.HasValue)
            writer.WriteNumber("max_tokens", exec.MaxTokensPreferred.Value);
        if (!hasReasoningEffort && !string.IsNullOrWhiteSpace(exec.ReasoningEffort))
            writer.WriteString("reasoning_effort", exec.ReasoningEffort);

        writer.WriteEndObject();
        writer.Flush();

        return Encoding.UTF8.GetString(ms.ToArray());
    }
    catch
    {
        return rawBody;
    }
}

// Returns a stable, per-conversation cache scope string.
// Priority: explicit "conversation_id" field > hash of first user message content > empty string (single-user or unknown).
string ExtractConversationScope(JsonElement root)
{
    // 1. Explicit conversation_id (sent by some Copilot / OpenAI clients)
    if (root.TryGetProperty("conversation_id", out JsonElement cid) && cid.ValueKind == JsonValueKind.String)
    {
        string? id = cid.GetString();
        if (!string.IsNullOrWhiteSpace(id))
            return id!;
    }

    // 2. Stable hash of the first user message — acts as a session fingerprint without storing PII keys.
    if (root.TryGetProperty("messages", out JsonElement msgs))
    {
        foreach (JsonElement msg in msgs.EnumerateArray())
        {
            if (msg.TryGetProperty("role", out JsonElement rEl) && rEl.GetString() == "user"
                && msg.TryGetProperty("content", out JsonElement cEl))
            {
                string content = cEl.ValueKind == JsonValueKind.String
                    ? cEl.GetString() ?? ""
                    : cEl.GetRawText();
                if (!string.IsNullOrEmpty(content))
                    return $"conv_{Math.Abs(content.GetHashCode())}";
            }
        }
    }

    return "shared";
}

string? ModifyRequest(JsonDocument doc, string convScope)
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
                    if (ids.Count > 0) key = $"{convScope}:toolcall:{string.Join("|", ids)}";
                }
                else
                {
                    key = $"{convScope}:assistant:{idx++}";
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

void CacheReasoningFromResponse(string json, string convScope)
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
            key = $"{convScope}:toolcall:{string.Join("|", ids)}";
        }
        else
        {
            key = $"{convScope}:assistant:{Interlocked.Increment(ref _assistantMsgCounter) - 1}";
        }

        // Bounded eviction: remove oldest entries when cache is full.
        if (ReasoningCache.Count >= ReasoningCacheMaxEntries && ReasoningCacheEvictionQueue.TryDequeue(out string? oldest))
            ReasoningCache.TryRemove(oldest, out _);

        ReasoningCache[key] = rc.GetString()!;
        ReasoningCacheEvictionQueue.Enqueue(key);
    }
    catch { /* cache errors are non-critical */ }
}

async Task StreamAndCache(HttpResponseMessage upstream, HttpResponse downstream, CancellationToken ct, string convScope = "")
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
                                        key = $"{convScope}:toolcall:{string.Join("|", tcIds)}";
                                    else
                                        key = $"{convScope}:assistant:{asstIdx ?? (int)(Interlocked.Increment(ref _assistantMsgCounter) - 1)}";

                                    // Bounded eviction before inserting
                                    if (ReasoningCache.Count >= ReasoningCacheMaxEntries && ReasoningCacheEvictionQueue.TryDequeue(out string? oldest))
                                        ReasoningCache.TryRemove(oldest, out _);

                                    ReasoningCache[key] = reasoning;
                                    ReasoningCacheEvictionQueue.Enqueue(key);
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
record struct ModelSelectionEntry(string Match, int Priority, bool Enabled, ModelExecutionConfig Execution);
record struct ModelExecutionConfig(
    int? ContextLength = null,
    int? MaxOutputTokens = null,
    bool? SupportsTools = null,
    bool? SupportsVision = null,
    string? Family = null,
    double? Temperature = null,
    double? TopP = null,
    int? MaxTokensPreferred = null,
    string? ReasoningEffort = null,
    int? TimeoutSeconds = null
);

