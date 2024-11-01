using ChatBot.Web.Models;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ChatBot.Web.Services
{
    public class YiChatService
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiKey;
        private const string API_BASE_URL = @"https://dashscope.aliyuncs.com/api/v1/services/aigc/text-generation/generation";

        public YiChatService(IConfiguration configuration)
        {
            //_apiKey = configuration["YiChat:ApiKey"] ?? throw new ArgumentNullException("YiChat API key not found");
            _apiKey = "sk-f9c4450d10604891a9d912bb398a397b";
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        }

        // 流式响应方法
        public async IAsyncEnumerable<string> StreamChatAsync(List<ChatMessage> messages, ChatSettings settings)
        {
            var requestBody = new
            {
                model = settings.Model,
                messages = messages.Select(m => new { role = m.Role, content = m.Content }),
                temperature = settings.Temperature,
                stream = true
            };

            var response = await _httpClient.PostAsJsonAsync(API_BASE_URL, requestBody);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream);

            while (!reader.EndOfStream)
            {
                var line = await reader.ReadLineAsync();
                if (string.IsNullOrEmpty(line)) continue;
                if (line.StartsWith("data: "))
                {
                    var json = line.Substring(6);
                    if (json == "[DONE]") break;

                    var chunk = JsonSerializer.Deserialize<YiChatResponse>(json);
                    if (!string.IsNullOrEmpty(chunk?.Choices?[0]?.Delta?.Content))
                    {
                        yield return chunk.Choices[0].Delta.Content;
                    }
                }
            }
        }
    }

    // 义千问API响应模型
    class YiChatResponse
    {
        public Choice[] Choices { get; set; } = Array.Empty<Choice>();
    }

    class Choice
    {
        public Delta Delta { get; set; } = new();
    }

    class Delta
    {
        public string Content { get; set; } = string.Empty;
    }
}