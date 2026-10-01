using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ChatBot.Web.Services;

public partial class ChatService
{
    private async Task<string> TextToSpeechViaGeminiTtsAsync(List<string> texts, string? voice, CancellationToken cancellationToken)
    {
        using var response = await CreateGeminiTtsResponseAsync(texts, voice, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return await response.Content.ReadAsStringAsync(cancellationToken);

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return await SaveCombinedSpeechAsync([(bytes, null, "audio/wav")], ResolveGeminiTtsVoice(voice), "wav", cancellationToken);
    }

    private string ResolveGeminiTtsVoice(string? voice) => string.IsNullOrWhiteSpace(voice)
        ? (_configuration["TextToSpeech:GeminiTTS:Voice:Voiceid"] ?? "Kore")
        : voice;

    private async Task<HttpResponseMessage> CreateGeminiTtsResponseAsync(IReadOnlyList<string> texts, string? voice, CancellationToken cancellationToken, bool stream = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var keyEnvironmentName = _configuration["TextToSpeech:GeminiTTS:ApiKeyEnvironmentName"] ?? "GeminiKey";
        var apiKey = string.IsNullOrWhiteSpace(keyEnvironmentName) ? null : Environment.GetEnvironmentVariable(keyEnvironmentName);
        if (string.IsNullOrWhiteSpace(apiKey))
            return GeminiTtsError(HttpStatusCode.BadRequest, "未配置 Gemini TTS API Key，请设置 TextToSpeech:GeminiTTS:ApiKeyEnvironmentName 对应的环境变量。");

        var format = (_configuration["TextToSpeech:GeminiTTS:ResponseFormat"] ?? "wav").Trim();
        if (!string.Equals(format, "wav", StringComparison.OrdinalIgnoreCase))
        {
            if (!stream)
                return GeminiTtsError(HttpStatusCode.BadRequest, "Gemini TTS 仅支持 wav，请将 TextToSpeech:GeminiTTS:ResponseFormat 设置为 wav。");

            // Streaming always transports native PCM and persists WAV; file-format settings do not apply.
            _logger.LogWarning("Gemini TTS 流式模式忽略 ResponseFormat 配置，使用 PCM 传输并缓存为 WAV。");
        }

        var endpoint = _configuration["TextToSpeech:GeminiTTS:ApiEndpoint"] ?? "https://cdsjf.xyz/gemini/v1beta";
        var model = _configuration["TextToSpeech:GeminiTTS:Model"] ?? "gemini-3.8-flash-tts";
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
            || string.IsNullOrWhiteSpace(model))
            return GeminiTtsError(HttpStatusCode.BadRequest, "Gemini TTS 接口地址或模型配置无效。");

        if (stream)
            return await CreateGeminiPcmStreamResponseAsync(texts, voice, endpoint, model, apiKey, cancellationToken);

        endpoint = endpoint.TrimEnd('/') + "/models/" + Uri.EscapeDataString(model) + ":generateContent";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var token = timeout.Token;
        using var client = _httpClientFactory.CreateClient();
        client.Timeout = Timeout.InfiniteTimeSpan;
        var segments = new List<byte[]>();
        GeminiPcmFormat? expectedFormat = null;
        try
        {
            foreach (var text in texts)
            {
                token.ThrowIfCancellationRequested();
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
                request.Headers.Add("x-goog-api-key", apiKey);
                request.Content = new StringContent(JsonSerializer.Serialize(new
                {
                    contents = new[] { new { role = "user", parts = new[] { new { text } } } },
                    generationConfig = new
                    {
                        responseModalities = new[] { "AUDIO" },
                        speechConfig = new { voiceConfig = new { voice = ResolveGeminiTtsVoice(voice) } }
                    }
                }, _jsonOptions), Encoding.UTF8, "application/json");

                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                if (!response.IsSuccessStatusCode)
                {
                    var message = await response.Content.ReadAsStringAsync(token);
                    // Do not expose a credential even if a proxy echoes it in an error response.
                    message = message.Replace(apiKey, "[redacted]", StringComparison.Ordinal);
                    if (message.Length > 1000) message = message[..1000];
                    _logger.LogWarning("Gemini TTS 上游请求失败，HTTP {StatusCode}", (int)response.StatusCode);
                    return GeminiTtsError(response.StatusCode, $"Gemini TTS 接口返回 HTTP {(int)response.StatusCode}：{message}");
                }

                await using var body = await response.Content.ReadAsStreamAsync(token);
                using var json = await JsonDocument.ParseAsync(body, cancellationToken: token);
                var decoded = DecodeGeminiTtsAudio(json.RootElement);
                if (expectedFormat.HasValue && expectedFormat.Value != decoded.Format)
                    throw new InvalidDataException("Gemini TTS 音频分段的采样参数不一致，无法合并。");
                expectedFormat = decoded.Format;
                segments.Add(decoded.Wave);
            }

            token.ThrowIfCancellationRequested();
            if (segments.Count == 0) throw new InvalidDataException("Gemini TTS 未返回可用音频。");
            var audio = segments.Count == 1 ? segments[0] : MergeWaveAudio(segments);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(audio)
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("audio/wav") }
                }
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return GeminiTtsError(HttpStatusCode.GatewayTimeout, "Gemini TTS 请求超时。");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or JsonException or FormatException)
        {
            _logger.LogWarning("Gemini TTS 请求或音频解析失败，类型 {ErrorType}", ex.GetType().Name);
            var message = ex is InvalidDataException ? ex.Message : "Gemini TTS 请求失败或返回的音频数据无效。";
            return GeminiTtsError(HttpStatusCode.BadGateway, message);
        }
    }

    private static HttpResponseMessage GeminiTtsError(HttpStatusCode status, string message) => new(status)
    {
        Content = new StringContent("生成失败：" + message, Encoding.UTF8, "text/plain")
    };

    private readonly record struct GeminiPcmFormat(int SampleRate, short Channels, short BitsPerSample);

    internal static byte[] CompleteStreamingPcmWave(byte[] pcm, string contentType)
        => NormalizeGeminiTtsAudio(pcm, contentType).Wave;

    private static (byte[] Wave, GeminiPcmFormat Format) DecodeGeminiTtsAudio(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("candidates", out var candidates)
            || candidates.ValueKind != JsonValueKind.Array || candidates.GetArrayLength() == 0)
            throw new InvalidDataException("Gemini TTS 未返回音频候选结果，可能被上游拒绝或拦截。");

        // Candidates are alternatives, whereas parts within one candidate are sequential audio.
        var candidate = candidates[0];
        if (candidate.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Gemini TTS 音频候选结果无效。");
        if (candidate.TryGetProperty("finishReason", out var reason)
            && reason.ValueKind == JsonValueKind.String && reason.GetString() != "STOP")
            throw new InvalidDataException("Gemini TTS 音频生成未正常完成，请缩短文本或检查内容后重试。");
        if (!candidate.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Object
            || !content.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Gemini TTS 未返回音频内容。");

        var waves = new List<byte[]>();
        GeminiPcmFormat? format = null;
        foreach (var part in parts.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object
                || (part.TryGetProperty("thought", out var thought) && thought.ValueKind == JsonValueKind.True)
                || !part.TryGetProperty("inlineData", out var inline)) continue;
            if (inline.ValueKind != JsonValueKind.Object
                || !inline.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.String
                || !inline.TryGetProperty("mimeType", out var mime) || mime.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Gemini TTS 音频数据或类型缺失。");

            var bytes = Convert.FromBase64String(data.GetString()!);
            var decoded = NormalizeGeminiTtsAudio(bytes, mime.GetString()!);
            if (format.HasValue && format.Value != decoded.Format)
                throw new InvalidDataException("Gemini TTS 音频块的采样参数不一致。");
            format = decoded.Format;
            waves.Add(decoded.Wave);
        }
        if (waves.Count == 0 || !format.HasValue) throw new InvalidDataException("Gemini TTS 返回了空音频。");
        return (waves.Count == 1 ? waves[0] : MergeWaveAudio(waves), format.Value);
    }

    private static (byte[] Wave, GeminiPcmFormat Format) NormalizeGeminiTtsAudio(byte[] bytes, string mimeType)
    {
        if (bytes.Length == 0) throw new InvalidDataException("Gemini TTS 返回了空音频。");
        if (!MediaTypeHeaderValue.TryParse(mimeType, out var mime))
            throw new InvalidDataException("Gemini TTS 音频类型无效。");

        var mediaType = mime.MediaType?.ToLowerInvariant();
        if (mediaType is "audio/wav" or "audio/x-wav" or "audio/wave")
            return NormalizeGeminiWave(bytes);

        if (mediaType is not ("audio/l16" or "audio/pcm"))
            throw new InvalidDataException("Gemini TTS 返回了不支持的音频类型。");

        string? Parameter(string name) => mime.Parameters.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value?.Trim('"');
        if (!int.TryParse(Parameter("rate"), NumberStyles.None, CultureInfo.InvariantCulture, out var rate)
            || !short.TryParse(Parameter("channels") ?? "1", NumberStyles.None, CultureInfo.InvariantCulture, out var channels)
            || (Parameter("codec") is { } codec && !codec.Equals("pcm", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Gemini TTS PCM 音频缺少有效的采样参数。");

        // Gemini's L16/PCM payload is signed 16-bit little-endian, mono unless specified.
        var format = new GeminiPcmFormat(rate, channels, 16);
        return (WrapGeminiPcm(bytes, format), format);
    }

    private static (byte[] Wave, GeminiPcmFormat Format) NormalizeGeminiWave(byte[] bytes)
    {
        var span = bytes.AsSpan();
        if (bytes.Length < 44 || !span[..4].SequenceEqual("RIFF"u8) || !span.Slice(8, 4).SequenceEqual("WAVE"u8)
            || BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(4, 4)) != bytes.Length - 8)
            throw new InvalidDataException("Gemini TTS 返回了无效或不完整的 WAV 文件。");

        GeminiPcmFormat? format = null;
        using var pcm = new MemoryStream();
        var offset = 12;
        while (offset < bytes.Length)
        {
            if (bytes.Length - offset < 8) throw new InvalidDataException("Gemini TTS WAV 音频块不完整。");
            var size = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset + 4, 4));
            if (size > bytes.Length - offset - 8) throw new InvalidDataException("Gemini TTS WAV 音频块不完整。");
            var chunk = span.Slice(offset + 8, (int)size);
            if (span.Slice(offset, 4).SequenceEqual("fmt "u8))
            {
                if (format.HasValue || size < 16 || BinaryPrimitives.ReadInt16LittleEndian(chunk) != 1)
                    throw new InvalidDataException("Gemini TTS WAV 必须为 PCM 格式。");
                format = new GeminiPcmFormat(BinaryPrimitives.ReadInt32LittleEndian(chunk[4..]),
                    BinaryPrimitives.ReadInt16LittleEndian(chunk[2..]), BinaryPrimitives.ReadInt16LittleEndian(chunk[14..]));
                ValidateGeminiPcmFormat(format.Value);
                var align = format.Value.Channels * format.Value.BitsPerSample / 8;
                if (BinaryPrimitives.ReadInt16LittleEndian(chunk[12..]) != align
                    || BinaryPrimitives.ReadInt32LittleEndian(chunk[8..]) != format.Value.SampleRate * align)
                    throw new InvalidDataException("Gemini TTS WAV 采样参数无效。");
            }
            else if (span.Slice(offset, 4).SequenceEqual("data"u8)) pcm.Write(chunk);
            offset += 8 + (int)size + (int)(size & 1);
        }
        if (offset != bytes.Length || !format.HasValue) throw new InvalidDataException("Gemini TTS WAV 文件结构无效。");
        // Canonicalize headers before using the existing merger, which expects fmt at offset 12.
        return (WrapGeminiPcm(pcm.ToArray(), format.Value), format.Value);
    }

    private static void ValidateGeminiPcmFormat(GeminiPcmFormat format)
    {
        if (format.SampleRate is < 1 or > 384000 || format.Channels is < 1 or > 2 || format.BitsPerSample != 16)
            throw new InvalidDataException("Gemini TTS 仅支持有效采样率的 16 位单声道或双声道 PCM 音频。");
    }

    private static byte[] WrapGeminiPcm(byte[] pcm, GeminiPcmFormat format)
    {
        ValidateGeminiPcmFormat(format);
        var align = (short)(format.Channels * format.BitsPerSample / 8);
        if (pcm.Length == 0 || pcm.Length % align != 0)
            throw new InvalidDataException("Gemini TTS PCM 音频为空或采样数据不完整。");
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        writer.Write("RIFF"u8);
        writer.Write(36 + pcm.Length);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(format.Channels);
        writer.Write(format.SampleRate);
        writer.Write(format.SampleRate * align);
        writer.Write(align);
        writer.Write(format.BitsPerSample);
        writer.Write("data"u8);
        writer.Write(pcm.Length);
        writer.Write(pcm);
        return output.ToArray();
    }
}
