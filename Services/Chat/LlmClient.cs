using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RiskWeb.Services.Chat;

public interface ILlmClient
{
    Task<LlmResponse> ChatAsync(LlmRequest request);
    IAsyncEnumerable<string> StreamChatAsync(LlmRequest request, CancellationToken cancellationToken = default);
}

public class LlmRequest
{
    public List<LlmMessage> Messages { get; set; } = new();
    public List<LlmTool>? Tools { get; set; }
    public string? ToolChoice { get; set; }
    public int MaxTokens { get; set; } = 4096;
    public double Temperature { get; set; } = 0.7;
}

public class LlmMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("tool_calls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<LlmToolCall>? ToolCalls { get; set; }

    [JsonPropertyName("tool_call_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolCallId { get; set; }
}

public class LlmTool
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public LlmFunction Function { get; set; } = new();
}

public class LlmFunction
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("parameters")]
    public JsonElement Parameters { get; set; }
}

public class LlmToolCall
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public LlmFunctionCall Function { get; set; } = new();
}

public class LlmFunctionCall
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("arguments")]
    public string Arguments { get; set; } = string.Empty;
}

public class LlmResponse
{
    public bool Success { get; set; }
    public string? Content { get; set; }
    public List<LlmToolCall>? ToolCalls { get; set; }
    public string? ErrorMessage { get; set; }
    public string? FinishReason { get; set; }

    public static LlmResponse Ok(string content, string? finishReason = null, List<LlmToolCall>? toolCalls = null)
    {
        return new LlmResponse
        {
            Success = true,
            Content = content,
            FinishReason = finishReason,
            ToolCalls = toolCalls
        };
    }

    public static LlmResponse Error(string message)
    {
        return new LlmResponse
        {
            Success = false,
            ErrorMessage = message
        };
    }
}

public class OpenRouterClient : ILlmClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OpenRouterClient> _logger;
    private readonly string _model;
    private readonly string _baseUrl;

    public OpenRouterClient(IConfiguration configuration, ILogger<OpenRouterClient> logger)
    {
        _logger = logger;

        var apiKey = configuration["OpenRouter:ApiKey"]
            ?? throw new InvalidOperationException("OpenRouter API key not configured");
        _baseUrl = configuration["OpenRouter:BaseUrl"] ?? "https://openrouter.ai/api/v1";
        _model = configuration["OpenRouter:ChatModel"] ?? "meta-llama/llama-3.1-8b-instruct:free";

        _logger.LogInformation("=== OpenRouter Client Initialization ===");
        _logger.LogInformation("Base URL: {BaseUrl}", _baseUrl);
        _logger.LogInformation("Model: {Model}", _model);
        _logger.LogInformation("API Key (first 20 chars): {ApiKey}...", apiKey.Substring(0, Math.Min(20, apiKey.Length)));

        // Ensure base URL ends with / for proper relative URL resolution
        var normalizedBaseUrl = _baseUrl.EndsWith("/") ? _baseUrl : _baseUrl + "/";
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(normalizedBaseUrl),
            Timeout = TimeSpan.FromSeconds(120)
        };
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        _httpClient.DefaultRequestHeaders.Add("HTTP-Referer", "https://riskweb.local");
        _httpClient.DefaultRequestHeaders.Add("X-Title", "RiskWeb Chat");

        _logger.LogInformation("HttpClient BaseAddress: {BaseAddress}", _httpClient.BaseAddress);
        _logger.LogInformation("=== Initialization Complete ===");
    }

    public async Task<LlmResponse> ChatAsync(LlmRequest request)
    {
        var requestId = Guid.NewGuid().ToString("N")[..8];
        _logger.LogInformation("[{RequestId}] === Starting ChatAsync ===", requestId);

        try
        {
            var requestBody = BuildRequestBody(request, stream: false);
            var jsonContent = JsonSerializer.Serialize(requestBody, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });

            // Use relative URL without leading slash to properly append to BaseAddress
            var fullUrl = new Uri(_httpClient.BaseAddress!, "chat/completions").ToString();
            _logger.LogInformation("[{RequestId}] Request URL: {Url}", requestId, fullUrl);
            _logger.LogInformation("[{RequestId}] Request Model: {Model}", requestId, _model);
            _logger.LogInformation("[{RequestId}] Request Body:\n{Body}", requestId, jsonContent);

            var httpContent = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            _logger.LogInformation("[{RequestId}] Sending HTTP POST request...", requestId);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            var response = await _httpClient.PostAsync("chat/completions", httpContent);

            stopwatch.Stop();
            _logger.LogInformation("[{RequestId}] Response received in {ElapsedMs}ms", requestId, stopwatch.ElapsedMilliseconds);
            _logger.LogInformation("[{RequestId}] Response Status: {StatusCode} ({StatusCodeInt})",
                requestId, response.StatusCode, (int)response.StatusCode);
            _logger.LogInformation("[{RequestId}] Response Headers:", requestId);
            foreach (var header in response.Headers)
            {
                _logger.LogInformation("[{RequestId}]   {Key}: {Value}", requestId, header.Key, string.Join(", ", header.Value));
            }

            var responseText = await response.Content.ReadAsStringAsync();

            // Log first 2000 chars of response
            var truncatedResponse = responseText.Length > 2000
                ? responseText.Substring(0, 2000) + "... [TRUNCATED]"
                : responseText;
            _logger.LogInformation("[{RequestId}] Response Body ({Length} chars):\n{Body}",
                requestId, responseText.Length, truncatedResponse);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("[{RequestId}] API returned error status: {StatusCode}", requestId, response.StatusCode);
                return LlmResponse.Error($"API error: {response.StatusCode} - {responseText}");
            }

            // Check if response looks like JSON
            if (!responseText.TrimStart().StartsWith("{") && !responseText.TrimStart().StartsWith("["))
            {
                _logger.LogError("[{RequestId}] Response is not JSON! First 100 chars: {Start}",
                    requestId, responseText.Substring(0, Math.Min(100, responseText.Length)));
                return LlmResponse.Error($"Invalid response format (not JSON): {responseText.Substring(0, Math.Min(200, responseText.Length))}");
            }

            _logger.LogInformation("[{RequestId}] Parsing JSON response...", requestId);
            var jsonResponse = JsonDocument.Parse(responseText);

            // Check for error in response
            if (jsonResponse.RootElement.TryGetProperty("error", out var errorElement))
            {
                var errorMessage = errorElement.TryGetProperty("message", out var msgElement)
                    ? msgElement.GetString()
                    : errorElement.GetRawText();
                _logger.LogError("[{RequestId}] API returned error in response: {Error}", requestId, errorMessage);
                return LlmResponse.Error($"API error: {errorMessage}");
            }

            if (!jsonResponse.RootElement.TryGetProperty("choices", out var choices))
            {
                _logger.LogError("[{RequestId}] Response missing 'choices' property", requestId);
                return LlmResponse.Error("Invalid response: missing 'choices'");
            }

            if (choices.GetArrayLength() == 0)
            {
                _logger.LogWarning("[{RequestId}] Response has empty 'choices' array", requestId);
                return LlmResponse.Error("No response from LLM (empty choices)");
            }

            var firstChoice = choices[0];

            if (!firstChoice.TryGetProperty("message", out var message))
            {
                _logger.LogError("[{RequestId}] First choice missing 'message' property", requestId);
                return LlmResponse.Error("Invalid response: missing 'message' in choice");
            }

            var finishReason = firstChoice.TryGetProperty("finish_reason", out var fr) ? fr.GetString() : null;
            _logger.LogInformation("[{RequestId}] Finish reason: {FinishReason}", requestId, finishReason);

            string? content = null;
            if (message.TryGetProperty("content", out var contentElement) && contentElement.ValueKind != JsonValueKind.Null)
            {
                content = contentElement.GetString();
                _logger.LogInformation("[{RequestId}] Content length: {Length} chars", requestId, content?.Length ?? 0);
            }

            List<LlmToolCall>? toolCalls = null;
            if (message.TryGetProperty("tool_calls", out var toolCallsElement) &&
                toolCallsElement.ValueKind == JsonValueKind.Array)
            {
                _logger.LogInformation("[{RequestId}] Tool calls found: {Count}", requestId, toolCallsElement.GetArrayLength());
                toolCalls = JsonSerializer.Deserialize<List<LlmToolCall>>(toolCallsElement.GetRawText());
                foreach (var tc in toolCalls ?? new List<LlmToolCall>())
                {
                    _logger.LogInformation("[{RequestId}] Tool call: {Name} with args: {Args}",
                        requestId, tc.Function.Name, tc.Function.Arguments);
                }
            }

            _logger.LogInformation("[{RequestId}] === ChatAsync completed successfully ===", requestId);
            return LlmResponse.Ok(content ?? "", finishReason, toolCalls);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "[{RequestId}] HTTP request failed: {Message}", requestId, ex.Message);
            return LlmResponse.Error($"HTTP error: {ex.Message}");
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogError(ex, "[{RequestId}] Request timed out", requestId);
            return LlmResponse.Error("Request timed out");
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "[{RequestId}] JSON parsing error: {Message}", requestId, ex.Message);
            return LlmResponse.Error($"JSON error: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{RequestId}] Unexpected error: {Message}", requestId, ex.Message);
            return LlmResponse.Error($"Error: {ex.Message}");
        }
    }

    public async IAsyncEnumerable<string> StreamChatAsync(
        LlmRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var requestId = Guid.NewGuid().ToString("N")[..8];
        _logger.LogInformation("[{RequestId}] === Starting StreamChatAsync ===", requestId);

        var requestBody = BuildRequestBody(request, stream: true);
        var jsonContent = JsonSerializer.Serialize(requestBody);

        _logger.LogInformation("[{RequestId}] Stream Request Body:\n{Body}", requestId, jsonContent);

        var httpContent = new StringContent(jsonContent, Encoding.UTF8, "application/json");
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = httpContent
        };

        HttpResponseMessage response;
        string? errorMessage = null;
        try
        {
            response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{RequestId}] Stream request failed", requestId);
            errorMessage = ex.Message;
            response = null!;
        }

        if (errorMessage != null)
        {
            yield return $"[Error: {errorMessage}]";
            yield break;
        }

        _logger.LogInformation("[{RequestId}] Stream Response Status: {StatusCode}", requestId, response.StatusCode);

        if (!response.IsSuccessStatusCode)
        {
            var errorText = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("[{RequestId}] Stream error response: {Response}", requestId, errorText);
            yield return $"[Error: {response.StatusCode} - {errorText}]";
            yield break;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (!line.StartsWith("data: ")) continue;

            var data = line[6..];
            if (data == "[DONE]")
            {
                _logger.LogInformation("[{RequestId}] Stream completed", requestId);
                break;
            }

            string? contentDelta = null;
            try
            {
                var json = JsonDocument.Parse(data);
                var choices = json.RootElement.GetProperty("choices");
                if (choices.GetArrayLength() > 0)
                {
                    var delta = choices[0].GetProperty("delta");
                    if (delta.TryGetProperty("content", out var content) &&
                        content.ValueKind != JsonValueKind.Null)
                    {
                        contentDelta = content.GetString();
                    }
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning("[{RequestId}] Failed to parse streaming chunk: {Error}", requestId, ex.Message);
            }

            if (!string.IsNullOrEmpty(contentDelta))
            {
                yield return contentDelta;
            }
        }
    }

    private object BuildRequestBody(LlmRequest request, bool stream)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = _model,
            ["messages"] = request.Messages,
            ["max_tokens"] = request.MaxTokens,
            ["temperature"] = request.Temperature,
            ["stream"] = stream
        };

        if (request.Tools != null && request.Tools.Count > 0)
        {
            body["tools"] = request.Tools;
            if (!string.IsNullOrEmpty(request.ToolChoice))
            {
                body["tool_choice"] = request.ToolChoice;
            }
        }

        return body;
    }
}

public class AzureOpenAiClient : ILlmClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<AzureOpenAiClient> _logger;
    private readonly string _endpoint;
    private readonly string _deploymentName;
    private readonly string _apiVersion;

    public AzureOpenAiClient(IConfiguration configuration, ILogger<AzureOpenAiClient> logger)
    {
        _logger = logger;

        _endpoint = configuration["AzureOpenAI:Endpoint"]
            ?? throw new InvalidOperationException("Azure OpenAI endpoint not configured");
        var apiKey = configuration["AzureOpenAI:ApiKey"]
            ?? throw new InvalidOperationException("Azure OpenAI API key not configured");
        _deploymentName = configuration["AzureOpenAI:DeploymentName"]
            ?? throw new InvalidOperationException("Azure OpenAI deployment name not configured");
        _apiVersion = configuration["AzureOpenAI:ApiVersion"] ?? "2024-02-15-preview";

        _logger.LogInformation("=== Azure OpenAI Client Initialization ===");
        _logger.LogInformation("Endpoint: {Endpoint}", _endpoint);
        _logger.LogInformation("Deployment: {Deployment}", _deploymentName);
        _logger.LogInformation("API Version: {ApiVersion}", _apiVersion);
        _logger.LogInformation("API Key (first 20 chars): {ApiKey}...", apiKey.Substring(0, Math.Min(20, apiKey.Length)));

        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(120)
        };
        _httpClient.DefaultRequestHeaders.Add("api-key", apiKey);

        _logger.LogInformation("=== Initialization Complete ===");
    }

    public async Task<LlmResponse> ChatAsync(LlmRequest request)
    {
        var requestId = Guid.NewGuid().ToString("N")[..8];
        _logger.LogInformation("[{RequestId}] === Starting Azure OpenAI ChatAsync ===", requestId);

        try
        {
            var requestBody = BuildRequestBody(request, stream: false);
            var jsonContent = JsonSerializer.Serialize(requestBody, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });

            // Azure OpenAI URL format: {endpoint}/openai/deployments/{deployment}/chat/completions?api-version={version}
            var url = $"{_endpoint.TrimEnd('/')}/openai/deployments/{_deploymentName}/chat/completions?api-version={_apiVersion}";

            _logger.LogInformation("[{RequestId}] Request URL: {Url}", requestId, url);
            _logger.LogInformation("[{RequestId}] Request Body:\n{Body}", requestId, jsonContent);

            var httpContent = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            _logger.LogInformation("[{RequestId}] Sending HTTP POST request...", requestId);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            var response = await _httpClient.PostAsync(url, httpContent);

            stopwatch.Stop();
            _logger.LogInformation("[{RequestId}] Response received in {ElapsedMs}ms", requestId, stopwatch.ElapsedMilliseconds);
            _logger.LogInformation("[{RequestId}] Response Status: {StatusCode} ({StatusCodeInt})",
                requestId, response.StatusCode, (int)response.StatusCode);

            var responseText = await response.Content.ReadAsStringAsync();

            // Log first 2000 chars of response
            var truncatedResponse = responseText.Length > 2000
                ? responseText.Substring(0, 2000) + "... [TRUNCATED]"
                : responseText;
            _logger.LogInformation("[{RequestId}] Response Body ({Length} chars):\n{Body}",
                requestId, responseText.Length, truncatedResponse);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("[{RequestId}] API returned error status: {StatusCode}", requestId, response.StatusCode);
                return LlmResponse.Error($"API error: {response.StatusCode} - {responseText}");
            }

            // Check if response looks like JSON
            if (!responseText.TrimStart().StartsWith("{") && !responseText.TrimStart().StartsWith("["))
            {
                _logger.LogError("[{RequestId}] Response is not JSON! First 100 chars: {Start}",
                    requestId, responseText.Substring(0, Math.Min(100, responseText.Length)));
                return LlmResponse.Error($"Invalid response format (not JSON): {responseText.Substring(0, Math.Min(200, responseText.Length))}");
            }

            _logger.LogInformation("[{RequestId}] Parsing JSON response...", requestId);
            var jsonResponse = JsonDocument.Parse(responseText);

            // Check for error in response
            if (jsonResponse.RootElement.TryGetProperty("error", out var errorElement))
            {
                var errorMessage = errorElement.TryGetProperty("message", out var msgElement)
                    ? msgElement.GetString()
                    : errorElement.GetRawText();
                _logger.LogError("[{RequestId}] API returned error in response: {Error}", requestId, errorMessage);
                return LlmResponse.Error($"API error: {errorMessage}");
            }

            if (!jsonResponse.RootElement.TryGetProperty("choices", out var choices))
            {
                _logger.LogError("[{RequestId}] Response missing 'choices' property", requestId);
                return LlmResponse.Error("Invalid response: missing 'choices'");
            }

            if (choices.GetArrayLength() == 0)
            {
                _logger.LogWarning("[{RequestId}] Response has empty 'choices' array", requestId);
                return LlmResponse.Error("No response from LLM (empty choices)");
            }

            var firstChoice = choices[0];

            if (!firstChoice.TryGetProperty("message", out var message))
            {
                _logger.LogError("[{RequestId}] First choice missing 'message' property", requestId);
                return LlmResponse.Error("Invalid response: missing 'message' in choice");
            }

            var finishReason = firstChoice.TryGetProperty("finish_reason", out var fr) ? fr.GetString() : null;
            _logger.LogInformation("[{RequestId}] Finish reason: {FinishReason}", requestId, finishReason);

            string? content = null;
            if (message.TryGetProperty("content", out var contentElement) && contentElement.ValueKind != JsonValueKind.Null)
            {
                content = contentElement.GetString();
                _logger.LogInformation("[{RequestId}] Content length: {Length} chars", requestId, content?.Length ?? 0);
            }

            List<LlmToolCall>? toolCalls = null;
            if (message.TryGetProperty("tool_calls", out var toolCallsElement) &&
                toolCallsElement.ValueKind == JsonValueKind.Array)
            {
                _logger.LogInformation("[{RequestId}] Tool calls found: {Count}", requestId, toolCallsElement.GetArrayLength());
                toolCalls = JsonSerializer.Deserialize<List<LlmToolCall>>(toolCallsElement.GetRawText());
                foreach (var tc in toolCalls ?? new List<LlmToolCall>())
                {
                    _logger.LogInformation("[{RequestId}] Tool call: {Name} with args: {Args}",
                        requestId, tc.Function.Name, tc.Function.Arguments);
                }
            }

            _logger.LogInformation("[{RequestId}] === ChatAsync completed successfully ===", requestId);
            return LlmResponse.Ok(content ?? "", finishReason, toolCalls);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "[{RequestId}] HTTP request failed: {Message}", requestId, ex.Message);
            return LlmResponse.Error($"HTTP error: {ex.Message}");
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogError(ex, "[{RequestId}] Request timed out", requestId);
            return LlmResponse.Error("Request timed out");
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "[{RequestId}] JSON parsing error: {Message}", requestId, ex.Message);
            return LlmResponse.Error($"JSON error: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{RequestId}] Unexpected error: {Message}", requestId, ex.Message);
            return LlmResponse.Error($"Error: {ex.Message}");
        }
    }

    public async IAsyncEnumerable<string> StreamChatAsync(
        LlmRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var requestId = Guid.NewGuid().ToString("N")[..8];
        _logger.LogInformation("[{RequestId}] === Starting Azure OpenAI StreamChatAsync ===", requestId);

        var requestBody = BuildRequestBody(request, stream: true);
        var jsonContent = JsonSerializer.Serialize(requestBody);

        var url = $"{_endpoint.TrimEnd('/')}/openai/deployments/{_deploymentName}/chat/completions?api-version={_apiVersion}";
        _logger.LogInformation("[{RequestId}] Stream Request URL: {Url}", requestId, url);
        _logger.LogInformation("[{RequestId}] Stream Request Body:\n{Body}", requestId, jsonContent);

        var httpContent = new StringContent(jsonContent, Encoding.UTF8, "application/json");
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = httpContent
        };

        HttpResponseMessage response;
        string? errorMessage = null;
        try
        {
            response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{RequestId}] Stream request failed", requestId);
            errorMessage = ex.Message;
            response = null!;
        }

        if (errorMessage != null)
        {
            yield return $"[Error: {errorMessage}]";
            yield break;
        }

        _logger.LogInformation("[{RequestId}] Stream Response Status: {StatusCode}", requestId, response.StatusCode);

        if (!response.IsSuccessStatusCode)
        {
            var errorText = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("[{RequestId}] Stream error response: {Response}", requestId, errorText);
            yield return $"[Error: {response.StatusCode} - {errorText}]";
            yield break;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (!line.StartsWith("data: ")) continue;

            var data = line[6..];
            if (data == "[DONE]")
            {
                _logger.LogInformation("[{RequestId}] Stream completed", requestId);
                break;
            }

            string? contentDelta = null;
            try
            {
                var json = JsonDocument.Parse(data);
                var choices = json.RootElement.GetProperty("choices");
                if (choices.GetArrayLength() > 0)
                {
                    var delta = choices[0].GetProperty("delta");
                    if (delta.TryGetProperty("content", out var content) &&
                        content.ValueKind != JsonValueKind.Null)
                    {
                        contentDelta = content.GetString();
                    }
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning("[{RequestId}] Failed to parse streaming chunk: {Error}", requestId, ex.Message);
            }

            if (!string.IsNullOrEmpty(contentDelta))
            {
                yield return contentDelta;
            }
        }
    }

    private object BuildRequestBody(LlmRequest request, bool stream)
    {
        var body = new Dictionary<string, object>
        {
            ["messages"] = request.Messages,
            ["max_tokens"] = request.MaxTokens,
            ["temperature"] = request.Temperature,
            ["stream"] = stream
        };

        if (request.Tools != null && request.Tools.Count > 0)
        {
            body["tools"] = request.Tools;
            if (!string.IsNullOrEmpty(request.ToolChoice))
            {
                body["tool_choice"] = request.ToolChoice;
            }
        }

        return body;
    }
}
