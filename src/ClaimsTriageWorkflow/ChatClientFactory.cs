using System.ClientModel;
using Azure;
using Azure.AI.OpenAI;
using OpenAI;
using Microsoft.Extensions.AI;

namespace ClaimsTriageWorkflow;

public static class ChatClientFactory
{
    public static IChatClient Create()
    {
        var provider = Environment.GetEnvironmentVariable("LLM_PROVIDER") ?? "azure";

        if (provider == "ollama")
        {
            var endpoint = Environment.GetEnvironmentVariable("OLLAMA_ENDPOINT")
                           ?? "http://localhost:11434/v1";
            var model    = Environment.GetEnvironmentVariable("OLLAMA_MODEL")
                           ?? "qwen2.5:7b";

            // OpenAI SDK supports OpenAI-compatible endpoints (Ollama uses the same API surface)
            return new OpenAIClient(
                    new ApiKeyCredential("ollama"),
                    new OpenAIClientOptions { Endpoint = new Uri(endpoint) })
                .GetChatClient(model)
                .AsIChatClient();
        }

        // Default: Azure OpenAI
        return new AzureOpenAIClient(
                new Uri(Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")!),
                new AzureKeyCredential(Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY")!))
            .GetChatClient(Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT_NAME")!)
            .AsIChatClient();
    }
}
