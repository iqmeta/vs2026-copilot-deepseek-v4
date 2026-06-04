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
            throw new InvalidOperationException(
                "No AI provider configured. Set PROVIDER_<NAME>_API_KEY (e.g. PROVIDER_DEEPSEEK_API_KEY) or DEEPSEEK_API_KEY.");
        }
    }

    internal string DefaultModel { get; }

    internal IReadOnlyList<ProviderInfo> Providers => _providers;

    internal IReadOnlyDictionary<string, ProviderInfo> ModelToProvider => _modelToProvider;

    internal IReadOnlyDictionary<string, string> ModelToUpstream => _modelToUpstream;

    internal void UpdateModelMappings(Dictionary<string, ProviderInfo> modelToProvider, Dictionary<string, string> modelToUpstream)
    {
        _modelToProvider = modelToProvider;
        _modelToUpstream = modelToUpstream;

        Dictionary<string, List<ProviderInfo>> upstreamToProviders = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string modelId, ProviderInfo prov) in modelToProvider)
        {
            string upstream = modelToUpstream.TryGetValue(modelId, out string? up) ? up : modelId;
            if (!upstreamToProviders.TryGetValue(upstream, out List<ProviderInfo>? list))
            {
                list = [];
                upstreamToProviders[upstream] = list;
            }

            if (!list.Any(p => p.Name.Equals(prov.Name, StringComparison.OrdinalIgnoreCase)))
            {
                // Preserve provider discovery order so failover follows configured priority.
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

        _upstreamToProviders = upstreamToProviders;
    }

    internal ProviderInfo ResolveProvider(string? requestedModel) =>
        !string.IsNullOrWhiteSpace(requestedModel) && _modelToProvider.TryGetValue(requestedModel, out ProviderInfo provider)
            ? provider
            : _providers[0];

    internal string ResolveModel(string? requestedModel) =>
        !string.IsNullOrWhiteSpace(requestedModel) && _modelToProvider.ContainsKey(requestedModel)
            ? requestedModel
            : DefaultModel;

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

        return [(ResolveProvider(resolved), upstream)];
    }

    private void DiscoverProviders(ProviderHttpClientFactory httpClientFactory)
    {
        foreach (string providerName in new[] { "deepseek", "openai", "nvidia", "openrouter", "groq", "ollama", "moonshot" })
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
