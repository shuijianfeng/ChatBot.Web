using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace ChatBot.Web.Services;

public partial class ChatService
{
    private async Task<HttpResponseMessage> CreateGeminiPcmStreamResponseAsync(IReadOnlyList<string> texts, string? voice,
        string endpoint, string model, string apiKey, CancellationToken cancellationToken)
    {
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(TimeSpan.FromMinutes(5));
        var chunks = ReadGeminiPcmAsync(texts, voice, endpoint, model, apiKey, lifetime.Token).GetAsyncEnumerator();
        try
        {
            // Validate the first upstream chunk before committing HTTP 200 and audio headers.
            if (!await chunks.MoveNextAsync()) throw new InvalidDataException("Gemini TTS 返回了空音频。");
            var format = chunks.Current.Format;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new GeminiPcmStream(chunks, lifetime))
            };
            response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(
                $"audio/L16;codec=pcm;rate={format.SampleRate};channels={format.Channels}");
            return response;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or JsonException or FormatException or OperationCanceledException)
        {
            await chunks.DisposeAsync();
            lifetime.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
            var status = ex is HttpRequestException http ? http.StatusCode ?? HttpStatusCode.BadGateway
                : ex is OperationCanceledException ? HttpStatusCode.GatewayTimeout : HttpStatusCode.BadGateway;
            _logger.LogWarning("Gemini TTS 流式请求失败，HTTP {StatusCode}，类型 {ErrorType}", (int)status, ex.GetType().Name);
            return GeminiTtsError(status, ex is InvalidDataException or HttpRequestException ? ex.Message
                : ex is OperationCanceledException ? "Gemini TTS 请求超时。" : "Gemini TTS 流式音频数据无效。");
        }
    }

    private async IAsyncEnumerable<(byte[] Pcm, GeminiPcmFormat Format)> ReadGeminiPcmAsync(
        IReadOnlyList<string> texts, string? voice, string endpoint, string model, string apiKey,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var client = _httpClientFactory.CreateClient();
        client.Timeout = Timeout.InfiniteTimeSpan;
        var url = endpoint.TrimEnd('/') + "/models/" + Uri.EscapeDataString(model) + ":streamGenerateContent?alt=sse";
        GeminiPcmFormat? expectedFormat = null;
        foreach (var text in texts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("x-goog-api-key", apiKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            request.Content = new StringContent(JsonSerializer.Serialize(new
            {
                contents = new[] { new { role = "user", parts = new[] { new { text } } } },
                generationConfig = new
                {
                    responseModalities = new[] { "AUDIO" },
                    speechConfig = new { voiceConfig = new { voice = ResolveGeminiTtsVoice(voice) } }
                }
            }, _jsonOptions), Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var error = (await response.Content.ReadAsStringAsync(cancellationToken)).Replace(apiKey, "[redacted]", StringComparison.Ordinal);
                throw new HttpRequestException($"Gemini TTS 接口返回 HTTP {(int)response.StatusCode}：{error[..Math.Min(error.Length, 1000)]}", null, response.StatusCode);
            }
            if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Gemini TTS 中转未返回 SSE 音频流，请检查中转的 streamGenerateContent 支持。");

            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            bool stopped = false, hasAudio = false;
            await foreach (var data in ReadGeminiSseDataAsync(body, cancellationToken))
            {
                if (data.Trim() == "[DONE]") break;
                using var document = JsonDocument.Parse(data);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("error", out _)
                    || (root.TryGetProperty("promptFeedback", out var feedback) && feedback.ValueKind == JsonValueKind.Object && feedback.TryGetProperty("blockReason", out _)))
                    throw new InvalidDataException("Gemini TTS 上游拒绝或中断了音频生成。");
                if (!root.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array || candidates.GetArrayLength() == 0)
                    continue; // Heartbeats and usage-only events carry no audio.
                var candidate = candidates[0];
                if (candidate.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Gemini TTS 音频候选结果无效。");
                if (candidate.TryGetProperty("finishReason", out var finish) && finish.ValueKind == JsonValueKind.String)
                {
                    if (finish.GetString() != "STOP") throw new InvalidDataException("Gemini TTS 音频生成未正常完成，请缩短文本后重试。");
                    stopped = true;
                }
                if (!candidate.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Object
                    || !content.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array
                    || !parts.EnumerateArray().Any(p => p.ValueKind == JsonValueKind.Object && p.TryGetProperty("inlineData", out _))) continue;

                var decoded = DecodeGeminiTtsAudio(root);
                if (expectedFormat.HasValue && expectedFormat.Value != decoded.Format)
                    throw new InvalidDataException("Gemini TTS 流式音频的采样参数不一致。");
                expectedFormat = decoded.Format;
                hasAudio = true;
                // The shared decoder normalizes headers; only the PCM samples travel to the browser.
                yield return (decoded.Wave[44..], decoded.Format);
            }
            if (!hasAudio) throw new InvalidDataException("Gemini TTS 返回了空音频。");
            if (!stopped) throw new InvalidDataException("Gemini TTS 音频流意外中断，未收到完成标记。");
        }
    }

    private static async IAsyncEnumerable<string> ReadGeminiSseDataAsync(Stream stream, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0) { yield return data.ToString(); data.Clear(); }
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(line.AsSpan(5).TrimStart(' '));
                if (data.Length > 8 * 1024 * 1024) throw new InvalidDataException("Gemini TTS 音频事件过大。");
            }
        }
        if (data.Length > 0) yield return data.ToString();
    }

    // Pull-based forwarding keeps upstream responses alive without background tasks or unbounded queues.
    private sealed class GeminiPcmStream(IAsyncEnumerator<(byte[] Pcm, GeminiPcmFormat Format)> chunks,
        CancellationTokenSource lifetime) : Stream
    {
        private byte[]? current = chunks.Current.Pcm;
        private int offset;
        private bool disposed;
        public override bool CanRead => !disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (buffer.Length == 0) return 0;
            using var registration = cancellationToken.Register(lifetime.Cancel);
            if (current != null && offset == current.Length)
            {
                current = await chunks.MoveNextAsync() ? chunks.Current.Pcm : null;
                offset = 0;
            }
            if (current == null) return 0;
            int count = Math.Min(buffer.Length, current.Length - offset);
            current.AsMemory(offset, count).CopyTo(buffer);
            offset += count;
            return count;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, default).GetAwaiter().GetResult();
        protected override void Dispose(bool disposing)
        {
            if (disposing) DisposeAsync().AsTask().GetAwaiter().GetResult();
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            if (disposed) return;
            disposed = true;
            lifetime.Cancel();
            try { await chunks.DisposeAsync(); }
            finally { lifetime.Dispose(); }
            GC.SuppressFinalize(this);
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
