using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChatBot.Models;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Processing.Processors.Quantization;
using System.Net.Http;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using System.Runtime.Intrinsics.X86;
using System.IO;
using AngleSharp.Dom;
using static ChatBot.Models.GeminiChunkResponse;
using System.Data;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace ChatBot.Web.Services
{
    /// <summary>
    /// 聊天服务接口
    /// </summary>
    public interface IChatService
    {
        // <summary>
        /// 获取聊天响应流
        /// </summary>
        /// <param name="request">聊天请求</param>
        /// <returns>响应事件流</returns>


        IAsyncEnumerable<string> GenerateStreamAsync(ChatRequest request, CancellationToken cancellationToken);
        List<string> GetAvailableModels();
        List<ChatModelConfig> GetModels();
        ChatModelConfig GetModelConfig(string modelName);
    }

    /// <summary>
    /// 通义千问API聊天服务实现
    /// </summary>
    public class ChatService : IChatService
    {
        static string SessionId = string.Empty;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _configuration;
        private readonly ILogger<ChatService> _logger;
        private readonly JsonSerializerOptions _jsonOptions;
        private readonly ChatModelSettings _modelSettings;

        public ChatService(
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            ILogger<ChatService> logger,
            IOptions<ChatModelSettings> modelOptions)
        {
            _httpClientFactory = httpClientFactory;
            _configuration = configuration;
            _logger = logger;
            _modelSettings = modelOptions.Value;
            // 配置JSON序列化选项
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                //Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
        }

        public async Task<List<SearchResult>> PerformAdvancedSearch(string query, int maxResults = 5)
        {
            var apiKey = Environment.GetEnvironmentVariable("GoogleKey");
            var url = $"https://cdsjf.xyz/googleapis/customsearch/v1?" +
                      $"key={apiKey}&" +
                      $"cx=6443be91738ab4541&" +
                      $"hl=zh-CN&"+ 
                        $"safe=active&"+  
                        $"cr=countryCN&"+  
                        $"gl=cn&"+  
                        $"filter=1&"+
                      $"q={Uri.EscapeDataString(query)}&" +
                      $"num={maxResults}&" +
                      $"sort=date"; // 按日期排序
            var client = _httpClientFactory.CreateClient();
            var response = await client.GetStringAsync(url);
            var jsonDocument = JsonDocument.Parse(response);
            var root = jsonDocument.RootElement;

            var searchResults = new List<SearchResult>();

            if (root.TryGetProperty("items", out var items))
            {
                foreach (var item in items.EnumerateArray())
                {
                    var result = new SearchResult
                    {
                        Title = GetPropertyValueOrDefault(item, "title"),
                        Snippet = GetPropertyValueOrDefault(item, "snippet"),
                        Link = GetPropertyValueOrDefault(item, "link"),
                        // 尝试解析发布日期
                        PublishedDate = ParsePublishedDate(item),
                        // 模拟点击率（实际应用中需要更复杂的逻辑）
                        ClickRate = EstimateClickRate(item)
                    };

                    searchResults.Add(result);
                }
            }

            // 按综合相关性评分降序排序
            return searchResults
                .OrderByDescending(r => r.GetRelevanceScore())
                .ToList();
        }

        private string GetPropertyValueOrDefault(JsonElement item, string propertyName)
        {
            return item.TryGetProperty(propertyName, out var prop)
                ? prop.GetString()
                : string.Empty;
        }

        private DateTime ParsePublishedDate(JsonElement item)
        {
            try
            {
                // 尝试从pagemap中提取发布日期
                if (item.TryGetProperty("pagemap", out var pagemap) &&
                    pagemap.TryGetProperty("metatags", out var metatags))
                {
                    foreach (var meta in metatags.EnumerateArray())
                    {
                        if (meta.TryGetProperty("article:published_time", out var publishedTime))
                        {
                            return DateTime.Parse(publishedTime.GetString());
                        }
                    }
                }

                // 如果无法提取，返回当前时间
                return DateTime.Now;
            }
            catch
            {
                return DateTime.Now;
            }
        }

        private int EstimateClickRate(JsonElement item)
        {
            // 简单的点击率估算逻辑
            // 可以根据标题长度、关键词匹配等简单启发式方法
            int baseRate = 100;

            // 标题包含关键词加分
            int titleBonus = item.TryGetProperty("title", out var title) &&
                             title.GetString().Contains("热门") ? 50 : 0;

            // 链接质量加分
            int linkBonus = item.TryGetProperty("link", out var link) &&
                            (link.GetString().Contains(".edu") || link.GetString().Contains(".gov")) ? 30 : 0;

            return baseRate + titleBonus + linkBonus;
        }

        public Task<string> SummarizeSearchResults(string query, List<SearchResult> searchResults)
        {
            //AdvancedAngleSharpScraper advancedAngleSharpScraper = new AdvancedAngleSharpScraper();

            // 准备聊天消息
            var summaries = searchResults.Take(5).Select(r =>
    $"标题: {r.Title}\n链接: {r.Link}\n摘要: {r.Snippet}\n发布日期: {r.PublishedDate:yyyy-MM-dd}\n ").ToList();

            return Task.FromResult(string.Join("\n", summaries));
        }

        private async Task<List<string>> PerformGoogleSearch(string query)
        {
            var client = _httpClientFactory.CreateClient();
            var url = $"https://www.googleapis.com/customsearch/v1?" +
                      $"key=AIzaSyAqD-TIrIzgmpRNKKTQUiiK4tsWDLFEV-U&" +
                      $"cx=6443be91738ab4541&" +
                      $"q={Uri.EscapeDataString(query)}&" +
            $"num=5";

            var response = await client.GetStringAsync(url);
            var jsonDocument = JsonDocument.Parse(response);
            var root = jsonDocument.RootElement;

            var searchResults = new List<string>();

            if (root.TryGetProperty("items", out var items))
            {
                foreach (var item in items.EnumerateArray())
                {
                    if (item.TryGetProperty("snippet", out var snippet))
                    {
                        searchResults.Add(snippet.GetString());
                    }
                }
            }

            return searchResults;
        }
        public List<string> GetAvailableModels()
        {
            if (_modelSettings == null || _modelSettings.Count == 0)
            {
                _logger.LogWarning("未加载到任何聊天模型配置");
                return new List<string>();
            }

            var models = _modelSettings.Select(m => m.Name).ToList();
            _logger.LogInformation("加载了 {ModelCount} 个聊天模型配置: {Models}", models.Count, string.Join(", ", models));
            return models;
        }

        public List<ChatModelConfig> GetModels()
        {
            if (_modelSettings == null || _modelSettings.Count == 0)
            {
                _logger.LogWarning("未加载到任何聊天模型配置");
                return new List<ChatModelConfig>();
            }

            var models = _modelSettings.ToList();
            _logger.LogInformation("加载了 {ModelCount} 个聊天模型配置: {Models}", models.Count, string.Join(", ", models));
            return models;
        }

        public ChatModelConfig GetModelConfig(string modelName)
        {
            foreach (var model in _modelSettings)
            {
                if (model.Name == modelName)
                {
                    return model;
                }
            }
            throw new ArgumentException($"模型名称 '{modelName}' 未配置。");
        }

        public async IAsyncEnumerable<string> GenerateStreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var config = GetModelConfig(request.Model);
            if (config.Isprompt)
            {
                await foreach (var item in GenerateStreamViaDashScopeAsync(config, request, cancellationToken))
                {
                    yield return item;
                }
            }
            else
            {
                switch (config.ChatModelType)
                {
                    case ChatModelType.Llama:
                        await foreach (var item in GenerateStreamViallama32Async(config, request, cancellationToken))
                        {
                            yield return item;
                        }
                        break;
                    case ChatModelType.QwenVl:
                        {
                            await foreach (var item in GenerateStreamViaVLAsync(config, request, cancellationToken))
                            {
                                yield return item;
                            }
                            break;
                        }
                    case ChatModelType.DeepSeek:
                        {
                            await foreach (var item in DeepseekOpenAIAsync(config, request, cancellationToken))
                            {
                                yield return item;
                            }
                            break;
                        }
                    case ChatModelType.Deepbricks:
                        {
                            await foreach (var item in DeepbricksOpenAIAsync(config, request, cancellationToken))
                            {
                                yield return item;
                            }
                            break;
                        }
                    case ChatModelType.Claude:
                        {
                            await foreach (var item in ClaudeAsync(config, request, cancellationToken))
                            {
                                yield return item;
                            }
                            break;
                        }
                        case ChatModelType.Gemini:
                        {
                            await foreach (var item in GeminiAsync(config, request, cancellationToken))
                            {
                                yield return item;
                            }
                            break;
                        }
                        default:
                        {
                            await foreach (var item in OpenAIAsync(config, request, cancellationToken))
                            {
                                yield return item;
                            }
                            break;
                        }
                }
                
            }
        }

        // 阿里平台流式输出 - llama3.2
        public async IAsyncEnumerable<string> GenerateStreamViallama32Async(ChatModelConfig modelconfg, ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            // 验证配置
            var apiKey = Environment.GetEnvironmentVariable(modelconfg.EnvironmentApikeyName);
            var apiEndpoint = modelconfg.ApiEndpoint;

            if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiEndpoint))
            {
                throw new InvalidOperationException("API配置缺失");
            }

            // 创建HTTP客户端
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
            client.DefaultRequestHeaders.Add("X-DashScope-SSE", "enable");
            // 准备请求内容
            var requestContent = new
            {

                model = modelconfg.Model,
                input = new
                {
                    messages = ToMessagesllama32(request, modelconfg)
                }
                //stream = modelconfg.Stream,
                //temperature = modelconfg.Temperature,
                //max_tokens = modelconfg.MaxTokens,
                //enable_search = modelconfg.EnableSearch,
                //stream_options = new
                //{
                //    include_usage = modelconfg.Include_usage
                //}
            };

            var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, apiEndpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json")
            }, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);

            while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (string.IsNullOrEmpty(line)) continue;
                if (line.StartsWith("data:"))
                {
                    line = line.Substring(5);
                    if (line == "[DONE]") break;

                    var chunk = JsonSerializer.Deserialize<llama32ChunkResponse>(line);

                    var content = chunk?.output?.choices?.FirstOrDefault()?.message?.content[0].text;
                    if (!string.IsNullOrEmpty(content))
                    {
                        yield return content;
                    }
                }
            }
        }

        // 阿里平台流式输出 - 千问VL
        public async IAsyncEnumerable<string> GenerateStreamViaVLAsync(ChatModelConfig modelconfg, ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            // 验证配置
            var apiKey = Environment.GetEnvironmentVariable(modelconfg.EnvironmentApikeyName);
            var apiEndpoint = modelconfg.ApiEndpoint;

            if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiEndpoint))
            {
                throw new InvalidOperationException("API配置缺失");
            }

            // 创建HTTP客户端
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

            // 准备请求内容
            var requestContent = new
            {

                model = modelconfg.Model,
                messages = ToMessagesOpenAi(request, modelconfg),
                stream = modelconfg.Stream,
                //temperature = modelconfg.Temperature,
                //max_tokens = modelconfg.MaxTokens,
                //enable_search = modelconfg.EnableSearch,
                stream_options = new
                {
                    include_usage = modelconfg.Include_usage
                }
            };
           
            var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, apiEndpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json")
            }, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);

            while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (string.IsNullOrEmpty(line)) continue;
                if (line.StartsWith("data: "))
                {
                    line = line.Substring(6);
                    if (line == "[DONE]") break;

                    var chunk = JsonSerializer.Deserialize<OpenAIChunkResponse>(line);
                    var content = chunk?.choices?.FirstOrDefault()?.delta?.content;
                    if (!string.IsNullOrEmpty(content))
                    {
                        yield return content;
                    }
                }
            }
        }

        // 阿里平台流式输出 - OpenAI 兼容方式
        public async IAsyncEnumerable<string> GenerateStreamViaOpenAIAsync(ChatModelConfig modelconfg, ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            // 验证配置
            var apiKey = Environment.GetEnvironmentVariable(modelconfg.EnvironmentApikeyName);
            var apiEndpoint = modelconfg.ApiEndpoint;

            if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiEndpoint))
            {
                throw new InvalidOperationException("API配置缺失");
            }

            // 创建HTTP客户端
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

            // 准备请求内容
            var requestContent = new
            {

                model = modelconfg.Model,
                messages = ToMessagesOpenAi(request, modelconfg),
                stream = modelconfg.Stream,
                temperature = modelconfg.Temperature,
                //max_tokens = modelconfg.MaxTokens,
                enable_search = modelconfg.EnableSearch,
                stream_options = new
                {
                    include_usage = modelconfg.Include_usage
                }
            };
            
            var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, apiEndpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json")
            }, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);

            while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (string.IsNullOrEmpty(line)) continue;
                if (line.StartsWith("data: "))
                {
                    line = line.Substring(6);
                    if (line == "[DONE]") break;

                    var chunk = JsonSerializer.Deserialize<OpenAIChunkResponse>(line);
                    var content = chunk?.choices?.FirstOrDefault()?.delta?.content;
                    bool beging1 = false;
                    bool end1 = false;
                    if (!string.IsNullOrEmpty(content))
                    {
                        if (modelconfg.Model == "deepseek-r1")
                        {

                           
                          
                            
                                if (content=="<think>" &&!beging1 && !end1)
                                {
                                    yield return content + "\n" + "\n" + "```Thoughts" + "\n" + "\n";
                                    beging1 = true;
                                }
                                else
                                {
                                    if (content == "</think>" && beging1 && !end1)
                                    {
                                        yield return "\n" + "\n" + "```" + "\n" + "\n" + content + "\n";
                                        end1 = true;
                                    }
                                    else
                                    {
                                        yield return content;
                                    }

                                }

                            

                        }

                        else
                        {
                                yield return content;
                            }
                        }
                    }
                }
            }
        

        // 阿里平台流式输出 - DashScope 百练应用调用方式
        public async IAsyncEnumerable<string> GenerateStreamViaDashScopeAsync(ChatModelConfig modelconfg, ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            // 验证配置
            string baseUrl = modelconfg.ApiEndpoint;
            string endpoint = "completion";
            var apiEndpoint = $"{baseUrl}/{modelconfg.Promptid}/{endpoint}";

            var apiKey = Environment.GetEnvironmentVariable(modelconfg.EnvironmentApikeyName);

            if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiEndpoint))
            {
                throw new InvalidOperationException("API配置缺失");
            }

            // 创建HTTP客户端
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
            client.DefaultRequestHeaders.TryAddWithoutValidation("X-DashScope-SSE", "enable");
            string s_id = SessionId;

          
            // 准备请求内容
            var requestContent = new
            {
                input = new { prompt = ToMessage(request), session_id = s_id },
                parameters = new { enable_search = true, incremental_output = true }

            };

            var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, apiEndpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json")
            }, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            ;
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);

            while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (string.IsNullOrEmpty(line)) continue;
                if (line.StartsWith("data:"))
                {
                    line = line.Substring(5);
                    if (line == "[DONE]") break;

                    var chunk = JsonSerializer.Deserialize<DashScopeChunkResponse>(line);
                    if (chunk?.output?.Text is string text && !string.IsNullOrEmpty(text))
                    {
                        SessionId = chunk.output.SessionId;
                        yield return chunk.output.Text;
                    }
                }
            }
        }

        // Deepbricks平台流式输出 - OpenAI 兼容方式
        public async IAsyncEnumerable<string> DeepbricksOpenAIAsync(ChatModelConfig modelconfg, ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            // 验证配置
            var apiKey = Environment.GetEnvironmentVariable(modelconfg.EnvironmentApikeyName);
            var apiEndpoint = modelconfg.ApiEndpoint;

            if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiEndpoint))
            {
                throw new InvalidOperationException("API配置缺失");
            }

            // 创建HTTP客户端
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

            // 准备请求内容
            var requestContent = new
            {

                model = modelconfg.Model,
                messages = ToMessagesOpenAi(request, modelconfg),
                stream = modelconfg.Stream,
                
                //temperature = modelconfg.Temperature,
                //max_tokens = modelconfg.MaxTokens,
                //enable_search = modelconfg.EnableSearch,
                //stream_options = new
                //{
                //    include_usage = modelconfg.Include_usage
                //}
            };
            
            var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, apiEndpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json")
            }, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);

            while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (string.IsNullOrEmpty(line)) continue;
                if (modelconfg.Stream)
                {
                    if (line.StartsWith("data: "))
                    {
                        line = line.Substring(6);
                        if (line == "[DONE]") break;

                        var chunk = JsonSerializer.Deserialize<OpenAIChunkResponse>(line);
                        var content = chunk?.choices?.FirstOrDefault()?.delta?.content;
                        if (!string.IsNullOrEmpty(content))
                        {
                            yield return content;
                        }
                    }
                }
                else
                {
                    var chunk = JsonSerializer.Deserialize<OpenAIResponse>(line);
                    var content = chunk?.choices?.FirstOrDefault()?.message?.content;
                    if (!string.IsNullOrEmpty(content))
                    {
                        yield return content;
                    }
                }
            }
        }

        //  - OpenAI 官方 兼容方式
        public async IAsyncEnumerable<string> OpenAIAsync(ChatModelConfig modelconfg, ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            // 验证配置
            var apiKey = Environment.GetEnvironmentVariable(modelconfg.EnvironmentApikeyName);
            var apiEndpoint = modelconfg.ApiEndpoint;


            if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiEndpoint))
            {
                throw new InvalidOperationException("API配置缺失");
            }

            // 创建HTTP客户端
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
            string Search = string.Empty;
            if (request.EnableSearch)
            {

                var list = await PerformAdvancedSearch(request.History[request.History.Count - 1].Content);
                
                Search = await SummarizeSearchResults(request.History[^1].Content, list);
            }
            // 准备请求内容
            var requestContent = new
            {

                model = modelconfg.Model,
                messages = ToMessagesOpenAi(request, modelconfg),
                stream = modelconfg.Stream,
                
                //temperature = modelconfg.Temperature,
                //max_tokens = modelconfg.MaxTokens,
                //enable_search = modelconfg.EnableSearch,
                //stream_options = new
                //{
                //    include_usage = modelconfg.Include_usage
                //}
            };
            //var str = JsonSerializer.Serialize(requestContent, _jsonOptions);
            var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, apiEndpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json")
            }, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);

            while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (string.IsNullOrEmpty(line)) continue;
                if (modelconfg.Stream)
                {
                    if (line.StartsWith("data: "))
                    {
                        line = line.Substring(6);
                        if (line == "[DONE]") break;

                        var chunk = JsonSerializer.Deserialize<OpenAIChunkResponse>(line);
                        var content = chunk?.choices?.FirstOrDefault()?.delta?.content;
                        if (!string.IsNullOrEmpty(content))
                        {
                            yield return content;
                        }
                    }
                }
                else
                {
                    var chunk = JsonSerializer.Deserialize<OpenAIResponse>(line);
                    var content = chunk?.choices?.FirstOrDefault()?.message?.content;
                    if (!string.IsNullOrEmpty(content))
                    {
                        yield return content;
                    }
                }
            }
        }
        //  - Claude 官方 兼容方式
        public async IAsyncEnumerable<string> ClaudeAsync(ChatModelConfig modelconfg, ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            // 验证配置
            var apiKey = Environment.GetEnvironmentVariable(modelconfg.EnvironmentApikeyName);
            var apiEndpoint = modelconfg.ApiEndpoint;

            if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiEndpoint))
            {
                throw new InvalidOperationException("API配置缺失");
            }
            string Search = string.Empty;
            if (request.EnableSearch)
            {

                var list = await PerformAdvancedSearch(request.History[request.History.Count - 1].Content);
                Search = await SummarizeSearchResults(request.History[^1].Content, list);

            }
            // 创建HTTP客户端
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("x-api-key", $"{apiKey}");
            client.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
            // 准备请求内容
            var requestContent = new
            {

                model = modelconfg.Model,
                system = modelconfg.Systemprompt,
                messages = ToMessagesClaude(request, modelconfg),

                stream = modelconfg.Stream,
                //temperature = modelconfg.Temperature,
                max_tokens = modelconfg.MaxTokens,
                //enable_search = modelconfg.EnableSearch,
                //stream_options = new
                //{
                //    include_usage = modelconfg.Include_usage
                //}
            };
            
            var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, apiEndpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json")
            }, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);

            while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (string.IsNullOrEmpty(line)) continue;
                if (modelconfg.Stream)
                {
                    if (line.Equals("event: content_block_delta"))
                    {
                        var dataline = await reader.ReadLineAsync(cancellationToken);
                        dataline = dataline.Substring(6);
                        if (dataline == "event: message_stop") break;

                        var chunk = JsonSerializer.Deserialize<ClaudeChunkResponse>(dataline);
                        var content = chunk?.deltaitem?.text;
                        if (!string.IsNullOrEmpty(content))
                        {
                            yield return content;
                        }
                    }
                }
                else
                {
                    var chunk = JsonSerializer.Deserialize<OpenAIResponse>(line);
                    var content = chunk?.choices?.FirstOrDefault()?.message?.content;
                    if (!string.IsNullOrEmpty(content))
                    {
                        yield return content;
                    }
                }
            }
        }
        // Ddeepseek OpenAI 兼容方式
        public async IAsyncEnumerable<string> DeepseekOpenAIAsync(ChatModelConfig modelconfg, ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            // 验证配置
            var apiKey = Environment.GetEnvironmentVariable(modelconfg.EnvironmentApikeyName);
            var apiEndpoint = modelconfg.ApiEndpoint;

            if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiEndpoint))
            {
                throw new InvalidOperationException("API配置缺失");
            }

            // 创建HTTP客户端
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

            // 准备请求内容
            var requestContent = new
            {

                model = modelconfg.Model,
                messages = ToMessagesOpenAi(request, modelconfg),
                stream = modelconfg.Stream,
                //temperature = modelconfg.Temperature,
                ////max_tokens = modelconfg.MaxTokens,
                //enable_search = modelconfg.EnableSearch,
                //stream_options = new
                //{
                //    include_usage = modelconfg.Include_usage
                //}
            };
            
            var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, apiEndpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json")
            }, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);
            bool beging= false;
            bool end = false;
            bool beging1 = false;
            bool end1 = false;
            while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
            {
               
                if (modelconfg.Stream)
                {
                    var line = await reader.ReadLineAsync(cancellationToken);
                    if (string.IsNullOrEmpty(line)) continue;
                    if (line.StartsWith("data: "))
                    {
                        line = line.Substring(6);
                        if (line == "[DONE]") break;

                        var chunk = JsonSerializer.Deserialize<OpenAIChunkResponse>(line);
                        var content = chunk?.choices?.FirstOrDefault()?.delta?.content;
                        var reasoning_content = chunk?.choices?.FirstOrDefault()?.delta?.reasoning_content;
                        if (!string.IsNullOrEmpty(reasoning_content))
                        {
                            if (!beging)
                            {
                                yield return "<think>" + "\n" + "\n"+ "```Thoughts" + "\n" + "\n" + reasoning_content;
                                beging = true;
                            }
                            else
                            {
                                yield return reasoning_content;
                                
                            }
                            
                        }
                        if (!string.IsNullOrEmpty(content))
                        {
                            if (beging&&!end)
                            {
                                yield return "\n"+"\n" + "```"  + "\n"+ "\n" + "</think>"  + "\n"+"\n"  + content;
                                end = true;
                            }
                            else
                            {
                                if (content == "<think>" && !beging1 && !end1)
                                {
                                    yield return content + "\n" + "\n" + "```Thoughts" + "\n" + "\n";
                                    beging1 = true;
                                }
                                else
                                {
                                    if (content == "</think>" && beging1 && !end1)
                                    {
                                        yield return  "\n" + "\n" + "```" + "\n" + "\n" + content  + "\n";
                                        end1 = true;
                                    }
                                    else
                                    {
                                        yield return content;
                                    }
                                   
                                }
                                
                            }
                            
                        }
                        
                    }
                }
                else
                {
                    var line = await reader.ReadToEndAsync(cancellationToken);
                    if (string.IsNullOrEmpty(line)) continue;
                    var chunk = JsonSerializer.Deserialize<OpenAIResponse>(line);
                    var content = chunk?.choices?.FirstOrDefault()?.message?.content;
                    var reasoning_content = chunk?.choices?.FirstOrDefault()?.message?.reasoning_content;
                    if (!string.IsNullOrEmpty(reasoning_content))
                    {
                        reasoning_content = "<think>"+ reasoning_content+ "</think>";
                        reasoning_content = reasoning_content.Replace("<think>", "<think>" + "\n" + "\n" + "```Thoughts" + "\n" + "\n");
                        reasoning_content = reasoning_content.Replace("</think>", "\n" + "\n" + "```" + "\n" + "\n" + "</think>" + "\n" + "\n");
                        yield return reasoning_content+ content;
                    }
                    if (!string.IsNullOrEmpty(content))
                    {
                        content = content.Replace("<think>", "<think>" + "\n" + "\n" + "```Thoughts" + "\n" + "\n");
                        content = content.Replace("</think>", "\n" + "\n" + "```" + "\n" + "\n" + "</think>" + "\n" + "\n");
                        yield return content;
                    }
                }
            }
        }
        //  - google GeminiA
        public async IAsyncEnumerable<string> GeminiAsync(ChatModelConfig modelconfg, ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            // 验证配置
            var apiKey = Environment.GetEnvironmentVariable(modelconfg.EnvironmentApikeyName);
            var apiEndpoint = modelconfg.ApiEndpoint;
            apiEndpoint = apiEndpoint + @"/models/" + modelconfg.Model;


            if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiEndpoint))
            {
                throw new InvalidOperationException("配置缺失");
            }
            if (modelconfg.Stream)
            {
                apiEndpoint = apiEndpoint + $":streamGenerateContent?alt=sse&key={apiKey}";
                
            }
            else
            {
                apiEndpoint = apiEndpoint + $":generateContent?key={apiKey}";
            }
            // 创建HTTP客户端
            var client = _httpClientFactory.CreateClient();
            
            // 准备请求内容
            var requestContent = new
            {
                system_instruction = new
                {
                    parts = new { text = modelconfg.Systemprompt }
                },
               
                contents = ToMessagesGemini(request, modelconfg),
                //config = new
                //{
                //     system_instruction = new
                //    {
                //        parts = new { text = "你是一个得力的助手,请用简体中文回答。如果有推理过程也使用中文回答。如果输出中有LaTeX格式的公式请用$或$$包裹" }
                //    },
                //    thinking_config = new
                //    { include_thoughts = true }
                //}
                //tools = new List<object>
                //{
                //      new
                //    {
                //          google_search_retrieval= new
                //          {
                //            dynamic_retrieval_config = new
                //            {
                //                mode= "MODE_DYNAMIC",
                //                dynamic_threshold=0.3
                //            }
                //          }
                //    }
                //}

            };
            var str = JsonSerializer.Serialize(requestContent, _jsonOptions);
            var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, apiEndpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json")
            }, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);

            while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
            {
                
                if (modelconfg.Stream)
                {
                    var line = await reader.ReadLineAsync(cancellationToken);
                    if (string.IsNullOrEmpty(line)) continue;
                    if (line.StartsWith("data: "))
                    {
                        line = line.Substring(6);
                      
                        var chunk = JsonSerializer.Deserialize<GeminiChunkResponse>(line);
                        var content = chunk?.candidates?.FirstOrDefault()?.content?.parts?.FirstOrDefault()?.text;

                        if (!string.IsNullOrEmpty(content))
                        {
                            yield return content;
                        }
                    }
                }
                else
                {
                        var line = await reader.ReadToEndAsync(cancellationToken);
                    
                        var chunk = JsonSerializer.Deserialize<GeminiChunkResponse>(line);
                        var content = chunk?.candidates?.FirstOrDefault()?.content?.parts?.FirstOrDefault()?.text;

                        if (!string.IsNullOrEmpty(content))
                        {
                            yield return content;
                        }
                    
                }
            }
        }
        private static List<object> ToMessagesllama32(ChatRequest request, ChatModelConfig modelconfg)
        {

            var messages = new List<object>();

            // 添加系统提示词
            messages.Add(new
            {
                role = "system",
                content = new List<object> { new { text = modelconfg.Systemprompt } }
            });
            // 添加历史消息
            foreach (var msg in request.History)
            {
                if (msg.Images.Length == 0)
                {
                    messages.Add(new
                    {
                        role = msg.Role,
                        content = new List<object> { new { text = (msg.Role == "assistant" ? delstr(msg.Content, "<think>", "</think>") : msg.Content) } }
                    });
                }
                else
                {
                    messages.Add(new
                    {
                        role = msg.Role,
                        content = new List<object> { new { image = msg.Images }, new { text = (msg.Role == "assistant" ? delstr(msg.Content, "<think>", "</think>") : msg.Content) } }
                    });
                }
            }

            return messages;
        }
        private static List<object> ToMessagesOpenAi(ChatRequest request, ChatModelConfig modelconfg)
        {
           

            var messages = new List<object>();

            // 添加系统提示词
            if (!string.IsNullOrWhiteSpace( modelconfg.Systemprompt))
            {
                messages.Add(new
                {
                    role = "system",
                    content = new List<object> {

                        new { type = "text", text = modelconfg.Systemprompt} }

                });
            }
            // 添加历史消息

            foreach (var msg in request.History)
            {
                if (msg.Images?.Any() == true&& modelconfg.EnableImageUpload)
                {
                    var contentlist = new List<object>();
                    
                    contentlist.Add(new { type = "text", text = (msg.Role == "assistant" ? delstr(msg.Content, "<think>", "</think>") : msg.Content) });
                    foreach (var image in msg.Images)
                    {

                        contentlist.Add(new { type = "image_url", image_url = new { url = $"data:image/jpeg;base64,{ConvertUrlToBase64(image)}" } });
                        

                    }
                    messages.Add(new
                    {
                        role = msg.Role,
                        content = contentlist
                    });
                }
                else
                {
                    messages.Add(new
                    {
                        role = msg.Role,
                        content = (msg.Role == "assistant" ? delstr(msg.Content, "<think>", "</think>") : msg.Content)
                    });
                }
            }


            return messages;
        }
        private static List<object> ToMessagesGemini(ChatRequest request ,ChatModelConfig modelconfg)
        {
            

            var contents = new List<object>();

           

            foreach (var msg in request.History)
            {
                if (msg.Images?.Any() == true&& modelconfg.EnableImageUpload)
                {
                     
                    var contentlist = new List<object>();
                    contentlist.Add(new {  text = (msg.Role == "assistant" ? delstr(msg.Content, "<think>", "</think>") : msg.Content) });
                    foreach (var image in msg.Images)
                    {

                        contentlist.Add(new { inline_data = new { mime_type="image/jpeg", data = $"{ConvertUrlToBase64(image)}" } });
                    }
                    contents.Add(new
                    {
                        role = msg.Role == "assistant" ? "model" : msg.Role,
                        parts = contentlist
                    });
                }
                else
                {
                    var contentlist = new List<object>();
                    contentlist.Add(new {  text = (msg.Role == "assistant" ? delstr(msg.Content, "<think>", "</think>") : msg.Content) });
                    contents.Add(new
                    {
                        role = msg.Role== "assistant"? "model" : msg.Role,
                        parts = contentlist
                    });
                }
            }


            return contents;
        }
        private static List<object> ToMessagesClaude(ChatRequest request, ChatModelConfig modelconfg)
        {
            var messages = new List<object>();

            foreach (var msg in request.History)
            {
                if (msg.Images?.Any() == true)
                {
                    // Message contains images - use content array format
                    var contentList = new List<object>();

                    // Add text content if present
                    if (!string.IsNullOrEmpty(msg.Content))
                    {
                        contentList.Add(new
                        {
                            type = "text",
                            text = (msg.Role == "assistant" ? delstr(msg.Content, "<think>", "</think>") : msg.Content)
                        });
                    }

                    // Add images
                    foreach (var imageUrl in msg.Images)
                    {
                        contentList.Add(new
                        {
                            type = "image",
                            source = new
                            {
                                type = "base64",
                                media_type = @"image/jpeg", // Helper method to determine image type
                                data = ConvertUrlToBase64(imageUrl)      // Helper method to convert URL to base64
                            }
                        });
                    }

                    messages.Add(new
                    {
                        role = msg.Role,
                        content = contentList
                    });
                }
                else
                {
                    // Text-only message - use simple content format
                    messages.Add(new
                    {
                        role = msg.Role,
                        content = (msg.Role == "assistant" ? delstr(msg.Content, "<think>", "</think>") : msg.Content)
                    });
                }
            }

            return messages;
        }

        // Helper method to determine image media type
        private static string GetImageMediaType(string imageUrl)
        {
            string extension = Path.GetExtension(imageUrl).ToLower();
            return extension switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".gif" => "image/gif",

                ".webp" => "image/webp",
                _ => "image/jpeg" // Default to JPEG if unknown
            };
        }

        // 修改 ConvertUrlToBase64 方法，使用 ImageSharp 库进行图片压缩
        private static string ConvertUrlToBase64(string imageUrl)
        {
            // 下载并压缩图片
            using (var client = new HttpClient())
            {
                byte[] imageBytesOriginal = client.GetByteArrayAsync(imageUrl).Result;

                using (var ms = new MemoryStream(imageBytesOriginal))
                {
                    // 加载图片
                    using (var image = SixLabors.ImageSharp.Image.Load(ms))
                    {
                        // 可选：调整图片尺寸
                        int maxWidth = 1024;
                        if (image.Width > maxWidth)
                        {
                            var ratio = (double)maxWidth / image.Width;
                            int newHeight = (int)(image.Height * ratio);
                            image.Mutate(x => x.Resize(maxWidth, newHeight));
                        }

                        //// 设置压缩质量和选择编码器
                        //var encoder = image.Metadata.DecodedImageFormat.Name switch
                        //{
                        //    "JPEG" => (IImageEncoder)new JpegEncoder { Quality = 80 }, // 压缩质量，范围0-100
                        //    "PNG" => new PngEncoder { CompressionLevel = PngCompressionLevel.Level6 },
                        //    "GIF" => new GifEncoder(), // 支持 GIF 格式
                        //    "WEBP" => new WebpEncoder(), // 支持 WEBP 格式
                        //    _ => new JpegEncoder { Quality = 80 } // 默认使用 JPEG 编码
                        //};
                        // 设置压缩质量
                        var encoder = new JpegEncoder
                        {
                            Quality = 80 // 压缩质量，范围0-100
                        };

                        using (var msCompressed = new MemoryStream())
                        {
                            // 保存压缩后的图片到内存流
                            image.Save(msCompressed, encoder);

                            // 转换为Base64字符串
                            return Convert.ToBase64String(msCompressed.ToArray());
                        }
                    }
                }
            }
        }

        private static string ToMessage(ChatRequest request)
        {
            var messages = string.Empty;

            if (request.History.Count > 0 && request.History[request.History.Count - 1].Role == "user")
            {
                messages = (request.History[request.History.Count - 1].Role == "assistant" ? delstr(request.History[request.History.Count - 1].Content, "<think>", "</think>") : request.History[request.History.Count - 1].Content);
                //messages = request.History[request.History.Count - 1].Content;
            }



            return messages;
        }

        /// <summary>
        /// 生成错误流
        /// </summary>
        private static IAsyncEnumerable<StreamEvent> GetErrorStream(string errorMessage)
        {
            return GetErrorStreamInternal(errorMessage);
        }

        private static async IAsyncEnumerable<StreamEvent> GetErrorStreamInternal(string errorMessage)
        {
            yield return new StreamEvent
            {
                Event = StreamEventType.Error,
                Data = new ChatResponse
                {
                    Content = errorMessage
                }
            };
        }

        /// <summary>
        /// 从异常生成错误流
        /// </summary>
        private static IAsyncEnumerable<StreamEvent> GetErrorStreamFromException(Exception ex)
        {
            return GetErrorStreamFromExceptionInternal(ex);
        }

        private static async IAsyncEnumerable<StreamEvent> GetErrorStreamFromExceptionInternal(Exception ex)
        {
            var errorEvent = new StreamEvent
            {
                Event = StreamEventType.Error,
                Data = new ChatResponse
                {
                    Content = $"服务器内部错误: {ex.Message}"
                }
            };

            yield return errorEvent;
        }

        public static string delstr(string source, string startDelimiter, string endDelimiter)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(startDelimiter) || string.IsNullOrEmpty(endDelimiter) || !source.Contains(startDelimiter) || !source.Contains(endDelimiter))
                return source;

            // 使用 ReadOnlySpan 避免分配
            ReadOnlySpan<char> span = source.AsSpan();
            int startIndex = span.IndexOf(startDelimiter.AsSpan());

            if (startIndex == -1)
                return source;

            int endIndex = span[startIndex..].IndexOf(endDelimiter.AsSpan());
            if (endIndex == -1)
                return source;

            endIndex += startIndex; // 调整为完整字符串的索引

            // 使用 string.Create 高效创建结果字符串
            int finalLength = source.Length - (endIndex - startIndex + endDelimiter.Length);
            int endDelimiterlen = endDelimiter.Length;
            return string.Create(finalLength, (source, startIndex, endIndex, endDelimiterlen), (span, state) =>
            {

                source.AsSpan(0, state.startIndex).CopyTo(span);
                source.AsSpan(state.endIndex + state.endDelimiterlen)
                      .CopyTo(span[state.startIndex..]);
            });
        }
    }
}