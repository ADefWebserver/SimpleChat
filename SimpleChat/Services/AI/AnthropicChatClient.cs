using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace SimpleChat.Services.AI;

/// <summary>
/// IChatClient implementation that calls the Anthropic REST API directly.
/// Ported from
/// https://github.com/AIStoryBuilders/AIStoryBuildersOnline/blob/main/AI/AnthropicChatClient.cs
/// </summary>
public class AnthropicChatClient : IChatClient, IDisposable
{
    private readonly string _apiKey;
    private readonly string _modelId;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private const string ApiUrl = "https://api.anthropic.com/v1/messages";

    public AnthropicChatClient(string apiKey, string modelId, HttpClient? httpClient = null)
    {
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _modelId = modelId ?? throw new ArgumentNullException(nameof(modelId));

        if (httpClient != null)
        {
            _httpClient = httpClient;
            _ownsHttpClient = false;
        }
        else
        {
            _httpClient = new HttpClient();
            _ownsHttpClient = true;
        }

        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("x-api-key", _apiKey);
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("anthropic-dangerous-direct-browser-access", "true");
    }

    public ChatClientMetadata Metadata => new ChatClientMetadata("AnthropicChatClient", null, _modelId);

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var systemText = "";
        var messages = new List<object>();

        foreach (var msg in chatMessages)
        {
            if (msg.Role == ChatRole.System)
            {
                systemText = msg.Text ?? "";
            }
            else if (msg.Role == ChatRole.User)
            {
                messages.Add(new { role = "user", content = msg.Text ?? "" });
            }
            else if (msg.Role == ChatRole.Assistant)
            {
                messages.Add(new { role = "assistant", content = msg.Text ?? "" });
            }
        }

        // Anthropic requires at least one user message.
        if (messages.Count == 0 && !string.IsNullOrEmpty(systemText))
        {
            messages.Add(new { role = "user", content = systemText });
            systemText = "";
        }

        var requestBody = new Dictionary<string, object>
        {
            ["model"] = _modelId,
            ["max_tokens"] = 4096,
            ["messages"] = messages
        };

        if (!string.IsNullOrEmpty(systemText))
        {
            requestBody["system"] = systemText;
        }

        if (options?.Temperature.HasValue == true
            && AICapabilities.AnthropicSupportsTemperature(_modelId))
        {
            requestBody["temperature"] = options.Temperature.Value;
        }

        var json = JsonSerializer.Serialize(requestBody);
        var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        var httpResponse = await _httpClient.SendAsync(request, cancellationToken);
        var responseJson = await httpResponse.Content.ReadAsStringAsync(cancellationToken);

        if (!httpResponse.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Anthropic API error ({httpResponse.StatusCode}): {responseJson}");
        }

        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        var responseText = "";
        if (root.TryGetProperty("content", out var contentArray))
        {
            foreach (var block in contentArray.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var type) && type.GetString() == "text"
                    && block.TryGetProperty("text", out var text))
                {
                    responseText += text.GetString();
                }
            }
        }

        var chatResponse = new ChatResponse(new ChatMessage(ChatRole.Assistant, responseText));

        if (root.TryGetProperty("usage", out var usage))
        {
            var inputTokens = usage.TryGetProperty("input_tokens", out var inp) ? inp.GetInt32() : 0;
            var outputTokens = usage.TryGetProperty("output_tokens", out var outp) ? outp.GetInt32() : 0;
            chatResponse.Usage = new UsageDetails
            {
                InputTokenCount = inputTokens,
                OutputTokenCount = outputTokens,
                TotalTokenCount = inputTokens + outputTokens
            };
        }

        return chatResponse;
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var systemText = "";
        var messages = new List<object>();

        foreach (var msg in chatMessages)
        {
            if (msg.Role == ChatRole.System)
            {
                systemText = msg.Text ?? "";
            }
            else if (msg.Role == ChatRole.User)
            {
                messages.Add(new { role = "user", content = msg.Text ?? "" });
            }
            else if (msg.Role == ChatRole.Assistant)
            {
                messages.Add(new { role = "assistant", content = msg.Text ?? "" });
            }
        }

        if (messages.Count == 0 && !string.IsNullOrEmpty(systemText))
        {
            messages.Add(new { role = "user", content = systemText });
            systemText = "";
        }

        var requestBody = new Dictionary<string, object>
        {
            ["model"] = _modelId,
            ["max_tokens"] = 4096,
            ["messages"] = messages,
            ["stream"] = true
        };

        if (!string.IsNullOrEmpty(systemText))
        {
            requestBody["system"] = systemText;
        }

        if (options?.Temperature.HasValue == true
            && AICapabilities.AnthropicSupportsTemperature(_modelId))
        {
            requestBody["temperature"] = options.Temperature.Value;
        }

        var json = JsonSerializer.Serialize(requestBody);
        using var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        using var httpResponse = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!httpResponse.IsSuccessStatusCode)
        {
            var errBody = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Anthropic API error ({httpResponse.StatusCode}): {errBody}");
        }

        await using var stream = await httpResponse.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) break;
            if (line.Length == 0) continue;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            var payload = line.AsSpan(5).Trim().ToString();
            if (payload.Length == 0 || payload == "[DONE]") continue;

            string? deltaText = null;
            try
            {
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeProp)
                    || typeProp.ValueKind != JsonValueKind.String)
                {
                    continue;
                }
                var evtType = typeProp.GetString();
                if (evtType != "content_block_delta") continue;

                if (root.TryGetProperty("delta", out var delta)
                    && delta.TryGetProperty("type", out var deltaType)
                    && deltaType.ValueKind == JsonValueKind.String
                    && deltaType.GetString() == "text_delta"
                    && delta.TryGetProperty("text", out var textEl)
                    && textEl.ValueKind == JsonValueKind.String)
                {
                    deltaText = textEl.GetString();
                }
            }
            catch (JsonException)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(deltaText))
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, deltaText);
            }
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceType == typeof(IChatClient)) return this;
        return null;
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient?.Dispose();
        }
    }
}
