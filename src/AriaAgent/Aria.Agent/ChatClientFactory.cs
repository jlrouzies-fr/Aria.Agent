using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.Configuration;
using OpenAI;
using OpenAI.Chat;

namespace Aria.Agent;

public static class ChatClientFactory
{
    public static ChatClient? TryBuildFromConfig(IConfiguration config, DelegatingHandler? handler = null)
    {
        if (!config.GetValue<bool>("LocalLLM:Enabled")) return null;

        var sources = config.GetSection("LocalLLM:ModelSources").Get<List<ModelSource>>();
        if (sources == null || sources.Count == 0) return null;

        var source = sources.First();
        var modelId = source.Models.FirstOrDefault() ?? "default";
        return Build(source, modelId, handler);
    }

    public static ChatClient Build(ModelSource source, string modelId, DelegatingHandler? handler = null, string? apiKeyOverride = null)
    {
        var apiKey = !string.IsNullOrEmpty(apiKeyOverride) ? apiKeyOverride : source.GetApiKey();
        var options = new OpenAIClientOptions
        {
            Endpoint         = new Uri(source.Url),
            NetworkTimeout   = TimeSpan.FromMinutes(10),
        };

        if (handler != null)
        {
            handler.InnerHandler ??= new HttpClientHandler();
            options.Transport = new HttpClientPipelineTransport(
                new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) });
        }
        else
        {
            // Default transport with extended timeout for slow local LLMs
            options.Transport = new HttpClientPipelineTransport(
                new HttpClient { Timeout = TimeSpan.FromMinutes(10) });
        }

        return new ChatClient(
            modelId,
            new ApiKeyCredential(string.IsNullOrEmpty(apiKey) ? "none" : apiKey),
            options
        );
    }
}
