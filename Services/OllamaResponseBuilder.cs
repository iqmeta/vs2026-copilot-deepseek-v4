internal sealed class OllamaResponseBuilder
{
    private readonly ModelCatalogService _modelCatalogService;

    public OllamaResponseBuilder(ModelCatalogService modelCatalogService)
    {
        _modelCatalogService = modelCatalogService;
    }

    internal Dictionary<string, object?> BuildOllamaShowResponse(string model)
    {
        (int ContextLength, int MaxOutputTokens, bool SupportsTools, bool SupportsVision, string[] Capabilities, string Family) p = _modelCatalogService.GetModelProfile(model);
        ModelExecutionConfig exec = _modelCatalogService.GetExecutionConfigForModel(model);

        return new Dictionary<string, object?>
        {
            ["model"] = model,
            ["modified_at"] = DateTime.UtcNow.ToString("o"),
            ["size"] = 3_826_793_677L,
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

    private static Dictionary<string, object?> BuildRecommendedParameters(ModelExecutionConfig exec)
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

    private static string BuildOllamaParametersString(int contextLength, int maxOutputTokens, ModelExecutionConfig exec)
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
}
