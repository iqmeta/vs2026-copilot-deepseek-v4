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
string API_KEY = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY")
    ?? throw new InvalidOperationException("DEEPSEEK_API_KEY environment variable is required");
string BASE_URL = Environment.GetEnvironmentVariable("DEEPSEEK_BASE_URL") ?? "https://api.deepseek.com";
string MODEL = Environment.GetEnvironmentVariable("DEEPSEEK_MODEL") ?? "deepseek-v4-pro";
int PORT = int.TryParse(Environment.GetEnvironmentVariable("PROXY_PORT"), out int p) ? p : 5000;
string? PROXY_API_KEY = Environment.GetEnvironmentVariable("PROXY_API_KEY");

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

// Max-performance HTTP handler
SocketsHttpHandler handler = new()
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

HttpClient httpClient = new(handler)
{
    Timeout = TimeSpan.FromMinutes(5),
    BaseAddress = new Uri(BASE_URL)
};
httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", API_KEY);
httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

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
app.MapGet("/v1/models", () =>
    Results.Json(new
    {
        @object = "list",
        data = new[]
        {
            new { id = MODEL, @object = "model", created = 1700000000, owned_by = "deepseek" }
        }
    }, JsonOpts));

// ─── GET /health ─────────────────────────────────────────────────────
app.MapGet("/health", () => Results.Ok(new { status = "ok", model = MODEL }));

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

    // Inject cached reasoning_content and override model
    string? modified = ModifyRequest(doc);
    string bodyText = modified ?? rawBody;
    
    // For non-streaming: direct proxy via HttpClient
    if (!isStream)
    {
        using StringContent content = new(bodyText, Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await httpClient.SendAsync(
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
    
    using HttpResponseMessage upstreamResp = await httpClient.SendAsync(
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

// ─── Ollama /api/tags ────────────────────────────────────────────────
app.MapGet("/api/tags", () =>
    Results.Json(new
    {
        models = new[]
        {
            new
            {
                name = MODEL, model = MODEL,
                modified_at = DateTime.UtcNow.ToString("o"), size = 0L,
                digest = "sha256:0000000000000000000000000000000000000000000000000000000000000000",
                details = new { parent_model = "", format = "api", family = "deepseek",
                    families = Array.Empty<string>(), parameter_size = "", quantization_level = "" }
            }
        }
    }, JsonOpts));

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

    Dictionary<string, object?> reqObj = new()
    {
        ["model"] = MODEL,
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
        using HttpResponseMessage resp = await httpClient.SendAsync(
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
            ["model"] = MODEL,
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
        
        using HttpResponseMessage upstreamResp = await httpClient.SendAsync(
            upstreamReq, HttpCompletionOption.ResponseHeadersRead, ct);
        
        if (!upstreamResp.IsSuccessStatusCode)
            return;
        
        await StreamAndCache(upstreamResp, ctx.Response, ct);
    }
});

// ─── Start ───────────────────────────────────────────────────────────
Console.WriteLine($"╔════════════════════════════════════════╗");
Console.WriteLine($"║   DeepSeek Copilot Proxy (Ultra)       ║");
Console.WriteLine($"╠════════════════════════════════════════╣");
Console.WriteLine($"║  Version: 2026.06.02                   ║");
Console.WriteLine($"║  Model:   {MODEL,-32}                  ║");
Console.WriteLine($"║  URL:     http://localhost:{PORT}/v1   ║");
Console.WriteLine($"║  Auth:    {(string.IsNullOrEmpty(PROXY_API_KEY) ? "open (no key set)" : "required (PROXY_API_KEY)"),-18} ║");
Console.WriteLine($"╚════════════════════════════════════════╝");
app.Run();

// ══════════════════════════════════════════════════════════════════════
// Local functions (capture ReasoningCache, _assistantMsgCounter, httpClient)
// ══════════════════════════════════════════════════════════════════════

string? ModifyRequest(JsonDocument doc)
{
    JsonElement root = doc.RootElement;
    if (!root.TryGetProperty("messages", out JsonElement msgs))
        return null;
    
    int idx = 0;
    bool modified = false;
    using MemoryStream ms = new();
    using Utf8JsonWriter w = new(ms);
    
    w.WriteStartObject();
    foreach (JsonProperty prop in root.EnumerateObject())
    {
        if (prop.NameEquals("model"))
        {
            w.WriteString("model", MODEL);
            continue;
        }

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
