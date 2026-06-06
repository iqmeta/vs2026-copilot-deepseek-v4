internal sealed class ProviderRegistry
{
    private readonly List<ProviderInfo> _providers = [];
    private Dictionary<string, ProviderInfo> _modelToProvider = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _modelToUpstream = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, List<ProviderInfo>> _upstreamToProviders = new(StringComparer.OrdinalIgnoreCase);

    public ProviderRegistry(ProviderHttpClientFactory httpClientFactory)
    {
        DefaultModel = Environment.GetEnvironmentVariable("DEEPSEEK_MODEL") ?? "deepseek-v4-pro";
        DiscoverProviders(httpClientFactory);

        if (_providers.Count == 0)
        {
            Console.WriteLine("╔══════════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  ⚠️  No se encontraron API keys configuradas.                    ║");
            Console.WriteLine("║                                                                  ║");
            Console.WriteLine("║  Edita el archivo .env en la raíz del proyecto y descomenta     ║");
            Console.WriteLine("║  al menos un proveedor, por ejemplo:                            ║");
            Console.WriteLine("║                                                                  ║");
            Console.WriteLine("║    PROVIDER_DEEPSEEK_API_KEY=sk-tu-key-aqui                      ║");
            Console.WriteLine("║                                                                  ║");
            Console.WriteLine("║  Luego reinicia la aplicación.                                   ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════════╝");
        }
    }

    internal string DefaultModel { get; }

    internal IReadOnlyList<ProviderInfo> Providers => _providers;

    internal IReadOnlyDictionary<string, ProviderInfo> ModelToProvider => _modelToProvider;

    internal IReadOnlyDictionary<string, string> ModelToUpstream => _modelToUpstream;

    internal void UpdateModelMappings(
        Dictionary<string, ProviderInfo> modelToProvider,
        Dictionary<string, string> modelToUpstream,
        Dictionary<string, List<ProviderInfo>>? upstreamToProviders = null)
    {
        _modelToProvider = modelToProvider;
        _modelToUpstream = modelToUpstream;

        if (upstreamToProviders is not null)
        {
            // Caller (ModelCatalogService) already ordered providers by configured priority.
            _upstreamToProviders = upstreamToProviders;
            return;
        }

        // Fallback: build from modelToProvider, preserving discovery order as a tie-break.
        Dictionary<string, List<ProviderInfo>> upstream = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string modelId, ProviderInfo prov) in modelToProvider)
        {
            string up = modelToUpstream.TryGetValue(modelId, out string? u) ? u : modelId;
            if (!upstream.TryGetValue(up, out List<ProviderInfo>? list))
            {
                list = [];
                upstream[up] = list;
            }

            if (!list.Any(p => p.Name.Equals(prov.Name, StringComparison.OrdinalIgnoreCase)))
            {
                int order = _providers.FindIndex(p => p.Name.Equals(prov.Name, StringComparison.OrdinalIgnoreCase));
                int insertAt = list.FindIndex(p => _providers.FindIndex(q => q.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase)) > order);
                if (insertAt < 0)
                {
                    list.Add(prov);
                }
                else
                {
                    list.Insert(insertAt, prov);
                }
            }
        }

        _upstreamToProviders = upstream;
    }

    internal ProviderInfo? ResolveProvider(string? requestedModel)
    {
        if (_providers.Count == 0)
            return null;
        return !string.IsNullOrWhiteSpace(requestedModel) && _modelToProvider.TryGetValue(requestedModel, out ProviderInfo provider)
            ? provider
            : _providers[0];
    }

    internal string ResolveModel(string? requestedModel)
    {
        if (_providers.Count == 0)
            return DefaultModel;

        if (!string.IsNullOrWhiteSpace(requestedModel))
        {
            // Strip Ollama-style tag suffix (e.g., "deepseek-v4-pro:latest" → "deepseek-v4-pro")
            string cleanModel = StripTagSuffix(requestedModel);
            if (_modelToProvider.ContainsKey(cleanModel))
                return cleanModel;
        }

        return DefaultModel;
    }

    /// <summary>Removes the tag portion of an Ollama model name (e.g. "model:latest" → "model").</summary>
    private static string StripTagSuffix(string model)
    {
        int colonIdx = model.IndexOf(':');
        return colonIdx > 0 ? model[..colonIdx] : model;
    }

    internal string ResolveUpstreamModel(string? requestedModel)
    {
        string resolved = ResolveModel(requestedModel);
        return _modelToUpstream.TryGetValue(resolved, out string? upstream) ? upstream : resolved;
    }

    /// <summary>
    /// Returns the ordered list of (provider, upstreamModel) candidates for a requested model.
    /// A provider-qualified id ("model@provider") resolves to a single candidate (no failover).
    /// A bare model id returns every provider that offers it, ordered by configured priority.
    /// </summary>
    internal IReadOnlyList<(ProviderInfo Provider, string UpstreamModel)> ResolveCandidates(string? requestedModel)
    {
        string resolved = ResolveModel(requestedModel);
        string upstream = _modelToUpstream.TryGetValue(resolved, out string? up) ? up : resolved;

        // Explicit "model@provider" alias -> single, exact candidate.
        bool isQualified = resolved.Contains('@');
        if (isQualified && _modelToProvider.TryGetValue(resolved, out ProviderInfo exact))
        {
            return [(exact, upstream)];
        }

        if (_upstreamToProviders.TryGetValue(upstream, out List<ProviderInfo>? providers) && providers.Count > 0)
        {
            return providers.Select(p => (p, upstream)).ToArray();
        }

        ProviderInfo? fallback = ResolveProvider(resolved);
        return fallback is not null ? [((ProviderInfo)fallback, upstream)] : [];
    }

    private void DiscoverProviders(ProviderHttpClientFactory httpClientFactory)
    {
        foreach (string providerName in new[] { "deepseek", "openai", "nvidia", "openrouter", "groq", "ollama", "moonshot", "cerebras" })
        {
            string prefix = providerName.ToUpperInvariant();
            string? apiKey = providerName == "ollama"
                ? Environment.GetEnvironmentVariable("PROVIDER_OLLAMACLOUD_API_KEY")
                    ?? Environment.GetEnvironmentVariable($"PROVIDER_{prefix}_API_KEY")
                : Environment.GetEnvironmentVariable($"PROVIDER_{prefix}_API_KEY");

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                continue;
            }

            string? configuredBaseUrl = Environment.GetEnvironmentVariable($"PROVIDER_{prefix}_BASE_URL");
            string baseUrl = string.IsNullOrWhiteSpace(configuredBaseUrl)
                ? providerName switch
                {
                    "deepseek" => "https://api.deepseek.com",
                    "openai" => "https://api.openai.com",
                    "nvidia" => "https://integrate.api.nvidia.com",
                    "openrouter" => "https://openrouter.ai/api/",
                    "groq" => "https://api.groq.com/openai",
                    "ollama" => "https://ollama.com",
                    "moonshot" => "https://api.moonshot.ai",
                    "cerebras" => "https://api.cerebras.ai",
                    _ => ""
                }
                : configuredBaseUrl;

            HttpClient provClient = httpClientFactory.CreateProviderClient(providerName, baseUrl, apiKey);
            _providers.Add(new ProviderInfo(providerName, apiKey, baseUrl, provClient));
        }

        string? legacyKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
        if (!string.IsNullOrWhiteSpace(legacyKey) && !_providers.Any(p => p.Name == "deepseek"))
        {
            string legacyUrl = Environment.GetEnvironmentVariable("DEEPSEEK_BASE_URL") ?? "https://api.deepseek.com";
            _providers.Add(new ProviderInfo("deepseek", legacyKey, legacyUrl,
                httpClientFactory.CreateProviderClient("deepseek", legacyUrl, legacyKey)));
        }
    }
}
