using ChatBot.Models;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ChatBot.Web.Services;

public sealed class EmbeddingService(IConfiguration configuration, IHttpClientFactory httpFactory) : IDisposable
{
    public const string HttpClientName = "EmbeddingProvider";
    private const int BatchSize = 16;
    private const int MaxTextLength = 8192;
    private readonly SemaphoreSlim capacity = new(4);
    public bool Enabled => bool.TryParse(configuration["Embeddings:Enabled"], out bool enabled) && enabled;

    private sealed record Settings(Uri Endpoint, string ApiKey, bool DashScope,
        EmbeddingConfig Options, EmbeddingConfiguration Public);

    public EmbeddingConfiguration GetConfiguration() => Resolve().Public;

    private Settings Resolve()
    {
        EmbeddingConfig options;
        try { options = configuration.GetSection("Embeddings").Get<EmbeddingConfig>() ?? new(); }
        catch (InvalidOperationException) { throw ConfigurationError("向量模型配置的字段类型无效。"); }
        if (!options.Enabled) throw new EmbeddingServiceException(404, "EMBEDDING_DISABLED", "向量服务尚未启用。");
        string endpoint = options.ApiEndpoint;
        string keyName = options.EnvironmentApikeyName;
        if (!string.IsNullOrWhiteSpace(options.ChatModelName))
        {
            var matches = configuration.GetSection("ChatModels").GetChildren()
                .Where(section => section["Name"] == options.ChatModelName).ToArray();
            if (matches is not { Length: 1 }) throw ConfigurationError("Embeddings:ChatModelName 必须对应唯一的 ChatModels 名称。");
            if (string.IsNullOrWhiteSpace(endpoint)) endpoint = matches[0]["ApiEndpoint"] ?? "";
            if (string.IsNullOrWhiteSpace(keyName)) keyName = matches[0]["EnvironmentApikeyName"] ?? "";
        }
        if (string.IsNullOrWhiteSpace(options.Model)) throw ConfigurationError("请配置 Embeddings:Model 向量模型名称。");
        if (options.TimeoutSeconds is < 1 or > 600 || options.Dimensions is < 1 or > 32768
            || options.QueryInstruction?.Length > 2048)
            throw ConfigurationError("向量模型的超时、维度或查询指令配置无效。");
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0
            || uri.Scheme is not ("https" or "http" or "wss" or "ws"))
            throw ConfigurationError("请配置有效的向量 API 地址或可复用的 ChatModels 地址。");
        var builder = new UriBuilder(uri);
        if (uri.Scheme is "wss" or "ws")
        {
            builder.Scheme = uri.Scheme == "wss" ? "https" : "http";
            if (uri.IsDefaultPort) builder.Port = -1;
        }
        string path = builder.Path.TrimEnd('/');
        // DashScope 原生接口是完整地址，协议也与 OpenAI 兼容接口不同。
        bool dashScope = path.EndsWith("/services/embeddings/text-embedding/text-embedding", StringComparison.OrdinalIgnoreCase);
        if (!dashScope)
        {
            foreach (string suffix in new[] { "/chat/completions", "/responses" })
                if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) path = path[..^suffix.Length];
            if (!path.EndsWith("/embeddings", StringComparison.OrdinalIgnoreCase)) path += "/embeddings";
        }
        builder.Path = path;
        string? key = string.IsNullOrWhiteSpace(keyName) ? null : Environment.GetEnvironmentVariable(keyName.Trim());
        if (string.IsNullOrWhiteSpace(key) || key.Trim().Any(char.IsWhiteSpace))
            throw ConfigurationError("服务器未配置有效的向量 API 密钥环境变量。");
        options.Model = options.Model.Trim();
        int batchSize = dashScope && options.Model is ("text-embedding-v3" or "text-embedding-v4") ? 10 : BatchSize;
        // 配置版本使客户端在模型、地址、维度或指令变化时丢弃旧向量；不包含密钥。
        string id = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            endpoint = builder.Uri.AbsoluteUri, keyName, dashScope, batchSize, options.Model, options.Dimensions,
            options.QueryInstruction, options.SendEncodingFormat
        })));
        return new(builder.Uri, key.Trim(), dashScope, options,
            new(id, batchSize, MaxTextLength, options.Dimensions, options.QueryInstruction));
    }

    public async Task<EmbeddingResponse> GenerateAsync(EmbeddingRequest request, CancellationToken ct)
    {
        var settings = Resolve();
        if (request is null || request.Input is not { Length: > 0 } || request.Input.Length > settings.Public.BatchSize
            || request.Input.Any(t => string.IsNullOrWhiteSpace(t) || t.Length > MaxTextLength))
            throw new EmbeddingServiceException(400, "EMBEDDING_INPUT", $"每批最多 {settings.Public.BatchSize} 条非空文本，每条最多 8192 个字符。");
        if (!string.Equals(request.ConfigurationId, settings.Public.Id, StringComparison.Ordinal))
            throw new EmbeddingServiceException(409, "EMBEDDING_CONFIG_CHANGED", "向量模型配置已更新，请重新执行匹配。");
        if (!await capacity.WaitAsync(0, ct))
            throw new EmbeddingServiceException(429, "EMBEDDING_BUSY", "向量服务繁忙，请稍后重试。");
        try
        {
            var payload = new Dictionary<string, object> { ["model"] = settings.Options.Model };
            if (settings.DashScope)
            {
                payload["input"] = new { texts = request.Input };
                // 原生接口默认返回稠密浮点向量，不接受 OpenAI 的 encoding_format/dimensions。
                if (settings.Options.Dimensions.HasValue)
                    payload["parameters"] = new { dimension = settings.Options.Dimensions.Value };
            }
            else
            {
                payload["input"] = request.Input;
                if (settings.Options.SendEncodingFormat) payload["encoding_format"] = "float";
                if (settings.Options.Dimensions.HasValue) payload["dimensions"] = settings.Options.Dimensions.Value;
            }
            using var message = new HttpRequestMessage(HttpMethod.Post, settings.Endpoint);
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
            message.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var http = httpFactory.CreateClient(HttpClientName);
            http.Timeout = TimeSpan.FromSeconds(settings.Options.TimeoutSeconds);
            http.MaxResponseContentBufferSize = 32 * 1024 * 1024;
            using var response = await http.SendAsync(message, HttpCompletionOption.ResponseContentRead, ct);
            if (!response.IsSuccessStatusCode)
                throw new EmbeddingServiceException(response.StatusCode == System.Net.HttpStatusCode.TooManyRequests ? 429 : 502,
                    "EMBEDDING_PROVIDER", $"向量供应商未完成请求（HTTP {(int)response.StatusCode}），请检查服务器端的模型、密钥或额度配置。")
                    { ProviderStatusCode = (int)response.StatusCode };
            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return new(settings.Public.Id, ReadVectors(json.RootElement, request.Input.Length, settings.Options.Dimensions, settings.DashScope));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new EmbeddingServiceException(504, "EMBEDDING_TIMEOUT", "向量服务响应超时，请稍后重试。");
        }
        catch (HttpRequestException)
        {
            throw new EmbeddingServiceException(502, "EMBEDDING_CONNECTION", "服务器暂时无法连接向量供应商。");
        }
        catch (JsonException)
        {
            throw InvalidOutput();
        }
        finally { capacity.Release(); }
    }

    private static EmbeddingVector[] ReadVectors(JsonElement root, int count, int? dimensions, bool dashScope)
    {
        if (root.ValueKind != JsonValueKind.Object) throw InvalidOutput();
        JsonElement data;
        if (dashScope)
        {
            if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Object
                || !output.TryGetProperty("embeddings", out data)) throw InvalidOutput();
        }
        else if (!root.TryGetProperty("data", out data)) throw InvalidOutput();
        if (data.ValueKind != JsonValueKind.Array || data.GetArrayLength() != count) throw InvalidOutput();
        var result = new EmbeddingVector[count];
        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(dashScope ? "text_index" : "index", out var indexValue)
                || indexValue.ValueKind != JsonValueKind.Number || !indexValue.TryGetInt32(out int index)
                || index < 0 || index >= count || result[index] is not null
                || !item.TryGetProperty("embedding", out var vector) || vector.ValueKind != JsonValueKind.Array
                || vector.GetArrayLength() is < 1 or > 32768) throw InvalidOutput();
            dimensions ??= vector.GetArrayLength();
            if (dimensions.Value != vector.GetArrayLength()) throw InvalidOutput();
            var values = new double[dimensions.Value];
            int offset = 0;
            bool nonzero = false;
            foreach (var element in vector.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Number || !element.TryGetDouble(out double value) || !double.IsFinite(value))
                    throw InvalidOutput();
                values[offset++] = value;
                nonzero |= value != 0;
            }
            if (!nonzero) throw InvalidOutput();
            result[index] = new(index, values);
        }
        // 只返回验证过的向量，不透传供应商的错误、URL、请求头或其他字段。
        return result;
    }

    private static EmbeddingServiceException InvalidOutput() => new(502, "EMBEDDING_OUTPUT", "向量供应商返回了无效的向量数据。");
    private static EmbeddingServiceException ConfigurationError(string message) => new(503, "EMBEDDING_CONFIG", message);
    public void Dispose() => capacity.Dispose();
}

public sealed class EmbeddingServiceException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
    public int? ProviderStatusCode { get; init; }
}
