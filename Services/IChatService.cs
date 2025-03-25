using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChatBot.Models;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;

using SixLabors.ImageSharp.Processing;

using System.Data;

using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

using DocumentFormat.OpenXml;
using Color = DocumentFormat.OpenXml.Wordprocessing.Color;
using PdfSharp.Fonts;
using System.Text.RegularExpressions;

using Npgsql;
using System.Threading;
using System.Security.Cryptography.Xml;
using DocumentFormat.OpenXml.Bibliography;
//using DocumentFormat.OpenXml.Spreadsheet;



namespace ChatBot.Web.Services
{
    /// <summary>
    /// 聊天服务接口
    /// </summary>
    public interface IChatService
    {
        IHttpClientFactory HttpClientFactory { get; }

        /// <summary>
        /// 验证用户ID是否有效
        /// </summary>
        /// <param name="userId">用户ID</param>
        /// <returns>如果用户ID有效则返回true，否则返回false</returns>
        Task<bool> ValidateUserIdAsync(string userId);
        // <summary>
        /// 获取聊天响应流
        /// </summary>
        /// <param name="request">聊天请求</param>
        /// <returns>响应事件流</returns>


        IAsyncEnumerable<string> GenerateStreamAsync(ChatRequest request, CancellationToken cancellationToken);
        List<string> GetAvailableModels();
        List<ChatModelConfig> GetModels();
        ChatModelConfig GetModelConfig(string modelName);
        Task<byte[]> ExportMessageToPdf(string content);
        Task<byte[]> ExportMessageToDocx(string content);

    }

    /// <summary>
    /// 通义千问API聊天服务实现
    /// </summary>
    public class ChatService : IChatService
    {
        private const int maxSearchCount = 5;
        private const int SearchCount = 10;
        static string SessionId = string.Empty;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _configuration;
        private readonly ILogger<ChatService> _logger;
        private readonly JsonSerializerOptions _jsonOptions;
        private readonly ChatModelSettings _modelSettings;
        private readonly JinaSearch _jinaSearch;
        private readonly OpenWeather _openWeather;

        public IHttpClientFactory HttpClientFactory => _httpClientFactory;

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
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };

            _jinaSearch = new JinaSearch(_httpClientFactory);
            _openWeather = new OpenWeather(_httpClientFactory);

        }

        #region 搜索相关

        #region google搜索相关
        /// <summary>
        /// google搜索api
        /// </summary>
        /// <param name="query"></param>
        /// <param name="maxResults"></param>
        /// <returns></returns>
        public async Task<List<SearchResult>> PerformAdvancedSearch(string query, int maxResults = 2)
        {
            var apiKey = Environment.GetEnvironmentVariable("GeminiKey");

            var url = $"https://cdsjf.xyz/googleapis/customsearch/v1?" +
                      $"key={apiKey}&" +
                      $"cx=6443be91738ab4541&" +
                      $"hl=zh-CN&" +
                        $"safe=active&" +
                        //$"cr=countryCN&" +
                        //$"gl=cn&" +
                        $"filter=1&" +
                      $"q={Uri.EscapeDataString(query)}&" +
                      $"num={maxResults}&" +
                      $"sort=date"; // 按日期排序
            var client = _httpClientFactory.CreateClient();
            try
            {
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
            catch (Exception ex)
            {
                _logger.LogError(ex, "搜索失败");
                return new List<SearchResult>();
            }

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

            // 准备聊天消息
            var summaries = searchResults.Take(5).Select(r =>
    $"标题: {r.Title}\n链接: {r.Link}\n摘要: {r.Snippet}\n发布日期: {r.PublishedDate:yyyy-MM-dd}\n ").ToList();

            return Task.FromResult(string.Join("\n", summaries));
        }
        #endregion

        #region JinaAi相关
        /// <summary>
        /// JinaAi页面抓取api
        /// </summary>
        /// <param name="query"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task<string> JinaAiRead(string query, CancellationToken cancellationToken)
        {

            if (cancellationToken.IsCancellationRequested) return await Task.FromResult(string.Empty);
            var apiKey = Environment.GetEnvironmentVariable("JinaAiApi");
            var apiEndpoint = $"https://r.jina.ai";

            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromMinutes(30);

            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
            //client.DefaultRequestHeaders.Add("Accept", "text/event-stream");
            client.DefaultRequestHeaders.Add("X-Retain-Images", "none");
            //client.DefaultRequestHeaders.Add("X-Return-Format", "text");
            var requestContent = new
            {
                url = query


            };

            var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, apiEndpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json")
            }, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.OK)
            {
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(cancellationToken);
            }
            else
            {
                return await Task.FromResult(string.Empty);
            }
        }

        public async Task<JinaSearchResult> JinaAiSearch(string query, CancellationToken cancellationToken, int searchtermCount, int searchMaxCount, bool isNoCache = false, bool isdirect = true)
        {
            int Rerankcount = 3;
            if (cancellationToken.IsCancellationRequested) return await Task.FromResult(new JinaSearchResult());
            var apiKey = Environment.GetEnvironmentVariable("JinaAiApi");
            var apiEndpoint = $"https://s.jina.ai";

            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromMinutes(30);

            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
            client.DefaultRequestHeaders.Add("Accept", "application/json");
            client.DefaultRequestHeaders.Add("X-Retain-Images", "none");
            //client.DefaultRequestHeaders.Add("X-Return-Format", "text");
            if (isdirect)
            {
                client.DefaultRequestHeaders.Add("X-Engine", "direct");
            }
            if (isNoCache)
            {
                client.DefaultRequestHeaders.Add("X-No-Cache", "true");
            }

            int searchCount1 = searchMaxCount / searchtermCount;
            if (searchCount1 == 0)
            {
                searchCount1 = 1;
            }
            if (searchCount1 > 5)
            {
                searchCount1 = 5;
            }

            //int rerankcount1 = searchMaxCount / searchtermCount;
            //if (rerankcount1 == 0)
            //{
            //    rerankcount1 = 1;
            //}
            //if (rerankcount1 > 3)
            //{
            //    rerankcount1 = 3;
            //}
            var requestContent = new

            {
                q = query,
                count = searchCount1

            };

            var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, apiEndpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json")
            }, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.OK)
            {
                response.EnsureSuccessStatusCode();
                string rsteing = await response.Content.ReadAsStringAsync(cancellationToken);
                var jinaSearchResult = JsonSerializer.Deserialize<JinaSearchResult>(rsteing);

                JinaAiRerank(query, jinaSearchResult, cancellationToken, Rerankcount);
                return jinaSearchResult;
            }
            else
            {
                return await Task.FromResult(new JinaSearchResult());
            }
        }

        //public async void JinaAiRerank(string query, JinaSearchResult jinaSearchResult, CancellationToken cancellationToken, int rerankCount = 3)
        //{

        //    if (cancellationToken.IsCancellationRequested) return ;
        //    var apiKey = Environment.GetEnvironmentVariable("JinaAiApi");
        //    var apiEndpoint = $"https://api.jina.ai/v1/rerank";

        //    var client = _httpClientFactory.CreateClient();
        //    client.Timeout = TimeSpan.FromMinutes(30);

        //    client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

        //    jinaSearchResult.AddIndex();

        //    List<string> list = jinaSearchResult.ToData();
        //    var requestContent = new
        //    {
        //        model= "jina-reranker-v2-base-multilingual",
        //        query= query,
        //        top_n= rerankCount,
        //        documents = list

        //    };

        //    var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, apiEndpoint)
        //    {
        //        Content = new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json")
        //    }, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        //    if (response.StatusCode == System.Net.HttpStatusCode.OK)
        //    {
        //        response.EnsureSuccessStatusCode();
        //        string rsteing = await response.Content.ReadAsStringAsync(cancellationToken);
        //        var jinaRerankResult = JsonSerializer.Deserialize<JinaRerankResult>(rsteing);
        //       jinaSearchResult.UpdateRerank(jinaRerankResult);

        //    }
        //    else
        //    {
        //        return ;
        //    }
        //}

        public async void JinaAiRerank(string query, JinaSearchResult jinaSearchResult, CancellationToken cancellationToken, int rerankCount = 3)
        {


            var apiKey = Environment.GetEnvironmentVariable("JinaAiApi");
            var apiEndpoint = $"https://api.jina.ai/v1/rerank";

            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromMinutes(30);

            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

            foreach (var item in jinaSearchResult.Data)
            {



                var requestContent = new
                {
                    model = "jina-reranker-v2-base-multilingual",
                    query = query,
                    top_n = rerankCount,
                    documents = (new TextSplitter()).Split(item.Content)

                };

                var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, apiEndpoint)
                {
                    Content = new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json")
                }, HttpCompletionOption.ResponseHeadersRead);
                if (response.StatusCode == System.Net.HttpStatusCode.OK)
                {
                    response.EnsureSuccessStatusCode();
                    string rsteing = await response.Content.ReadAsStringAsync();
                    var jinaRerankResult = JsonSerializer.Deserialize<JinaRerankResult>(rsteing);
                    if (jinaRerankResult != null)
                    {
                        StringBuilder sb = new StringBuilder();
                        for (int i = 0; i < System.Math.Min(jinaRerankResult.Results.Count, rerankCount); i++)
                        {
                            sb.AppendLine(jinaRerankResult.Results[i].Document.Text);
                        }
                        item.Content = sb.ToString();
                    }

                }
                else
                {
                    return;
                }
            }



        }

        /// <summary>
        /// 抓取并清洗数据
        /// </summary>
        /// <param name="searchResults"></param>
        /// <param name="modelconfg"></param>
        /// <param name="searchterm"></param>
        /// <param name="outinfo"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        //public async IAsyncEnumerable<string> SearchResults(List<JinaSearchResult> searchResults, ChatModelConfig modelconfg, string searchterm, StringBuilder outinfo, CancellationToken cancellationToken)
        //{

        //    string resultliat = string.Empty;
        //    JinaSearch js = new JinaSearch(_httpClientFactory);
        //    foreach (var item in searchResults)
        //    {
        //        js.JinaAiRerank(searchterm, item);
        //        foreach (var dataitem in item.Data)
        //        {
        //            {
        //                string bh = (searchResults.IndexOf(item) + 1).ToString();
        //                yield return "<details>";
        //                yield return $"\n<summary>{bh} 读取分析：{dataitem.Title}</summary>";
        //                yield return $"\n\n链接： {dataitem.Url}";

        //                if (!string.IsNullOrEmpty(dataitem.Content))
        //                {
        //                    string info = string.Empty;



        //                    if (!string.IsNullOrEmpty(info))
        //                    {

        //                        outinfo.Append('\n');
        //                        outinfo.Append(JsonSerializer.Serialize(new
        //                        {
        //                            Title = dataitem.Title,
        //                            Url = dataitem.Url,
        //                            Description = dataitem.Description,

        //                            Content = info
        //                        }, _jsonOptions));


        //                        yield return $"\n\n内容摘要：\n\n{info}";

        //                    }
        //                }
        //            }
        //            yield return "\n</details>\n";


        //        }
        //        yield return "\n";
        //        yield return "\n";
        //        yield return "---";
        //        yield return "\n";
        //        yield return "\n";
        //    }
        //}

        /// <summary>
        /// 抓取方法
        /// </summary>
        /// <param name="item"></param>
        /// <param name="token"></param>
        /// <returns></returns>
        private async Task<string> ProcessSearchResult(SearchResult item, CancellationToken token)
        {
            try
            {
                return await JinaAiRead(item.Link, token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"处理链接 {item.Link} 时出错");
                return string.Empty;
            }
        }
        #endregion

        #endregion

        #region 模型相关配置
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
        #endregion

        #region chat
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
                    case ChatModelType.OpenAiDeepResearch:
                        {


                            await foreach (var item in OpenAIDeepResearchAsync(config, request, cancellationToken))
                            {
                                yield return item;
                            }
                            break;
                        }
                    case ChatModelType.GeminiDeepResearch:
                        {
                            await foreach (var item in GeminiDeepResearchAsync(config, request, cancellationToken))
                            {
                                yield return item;
                            }
                            break;
                        }
                    case ChatModelType.Dify:
                        // 添加对Dify的支持

                        {
                            await foreach (var item in DifyAsync(config, request, cancellationToken))
                            {
                                yield return item;
                            }
                            break;
                        }
                    case ChatModelType.OpenAiResponses:
                        // 添加对Dify的支持

                        {
                            await foreach (var item in OpenAIResponsesAsync(config, request, cancellationToken))
                            {
                                yield return item;
                            }
                            break;
                        }
                    default:
                        {
                            //OpenAIService openAIService = new OpenAIService(config, _httpClientFactory);
                            //await foreach (var item in openAIService.CompleteChatStreamingAsync1(request.History))
                            //{
                            //    yield return item;
                            //}

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
            if (response.StatusCode != System.Net.HttpStatusCode.OK)
            {
                yield return "失败: StatusCode " + response.StatusCode.ToString();
                yield break;
            }
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

            HttpResponseMessage response = null;
            if (modelconfg.Temperature >= 0)
            {
                var requestContent = new
                {

                    model = modelconfg.Model,
                    messages = ToMessagesOpenAi(request, modelconfg),
                    stream = modelconfg.Stream,
                    temperature = modelconfg.Temperature,

                    stream_options = new
                    {
                        include_usage = modelconfg.Include_usage
                    }
                };

                response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, apiEndpoint)
                {
                    Content = new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json")
                }, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            else
            {
                var requestContent = new
                {

                    model = modelconfg.Model,
                    messages = ToMessagesOpenAi(request, modelconfg),
                    stream = modelconfg.Stream,

                    stream_options = new
                    {
                        include_usage = modelconfg.Include_usage
                    }
                };

                response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, apiEndpoint)
                {
                    Content = new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json")
                }, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            if (response.StatusCode != System.Net.HttpStatusCode.OK)
            {
                yield return "失败: StatusCode " + response.StatusCode.ToString();
                yield break;
            }
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

        // 阿里平台流式输出 OpenAI 兼容方式
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

            HttpResponseMessage response = null;
            if (modelconfg.Temperature >= 0)
            {
                var requestContent = new
                {

                    model = modelconfg.Model,
                    messages = ToMessagesOpenAi(request, modelconfg),
                    stream = modelconfg.Stream,
                    temperature = modelconfg.Temperature,
                    enable_search = modelconfg.EnableSearch,
                    stream_options = new
                    {
                        include_usage = modelconfg.Include_usage
                    }
                };

                response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, apiEndpoint)
                {
                    Content = new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json")
                }, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            else
            {
                var requestContent = new
                {

                    model = modelconfg.Model,
                    messages = ToMessagesOpenAi(request, modelconfg),
                    stream = modelconfg.Stream,

                    enable_search = modelconfg.EnableSearch,
                    stream_options = new
                    {
                        include_usage = modelconfg.Include_usage
                    }
                };

                response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, apiEndpoint)
                {
                    Content = new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json")
                }, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }

            if (response.StatusCode != System.Net.HttpStatusCode.OK)
            {
                yield return "失败: StatusCode " + response.StatusCode.ToString();
                yield break;
            }

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




                            if (content == "<think>" && !beging1 && !end1)
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

            if (response.StatusCode != System.Net.HttpStatusCode.OK)
            {
                yield return "失败: StatusCode " + response.StatusCode.ToString();
                yield break;
            }
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
        // Deepbricks OpenAI 兼容方式
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
            client.Timeout = TimeSpan.FromMinutes(30);
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

            HttpResponseMessage response = null;
            if (modelconfg.Temperature >= 0)
            {
                var requestContent = new
                {

                    model = modelconfg.Model,
                    messages = ToMessagesOpenAi(request, modelconfg),
                    stream = modelconfg.Stream,

                    temperature = modelconfg.Temperature,

                };

                response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, apiEndpoint)
                {
                    Content = new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json")
                }, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            else
            {
                var requestContent = new
                {

                    model = modelconfg.Model,
                    messages = ToMessagesOpenAi(request, modelconfg),
                    stream = modelconfg.Stream,


                };

                response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, apiEndpoint)
                {
                    Content = new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json")
                }, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            if (response.StatusCode != System.Net.HttpStatusCode.OK)
            {
                yield return "失败: StatusCode " + response.StatusCode.ToString();
                yield break;
            }
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

        public async IAsyncEnumerable<string> OpenAIResponsesAsync(ChatModelConfig modelconfg, ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken, HttpClient inputclient = null, List<object> toolsmessages = null)
        {
            var apiKey = Environment.GetEnvironmentVariable(modelconfg.EnvironmentApikeyName);
            var apiEndpoint = modelconfg.ApiEndpoint;


            if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiEndpoint))
            {
                throw new InvalidOperationException("API配置缺失");
            }


            // 创建HTTP客户端
            HttpClient client = inputclient ?? _httpClientFactory.CreateClient();
            if (inputclient == null)
            {
                client.Timeout = TimeSpan.FromMinutes(30);
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
            }

            var messages = ToMessagesResponsesOpenAi(request, modelconfg);
            toolsmessages ??= new List<object>();
            messages.AddRange(toolsmessages);
            //toolsmessages.Clear();
            List<object> tools = request.EnableSearch
        ? new List<object>
        {
            new
            {
                type = "function",

                    name = nameof(JinaAiSearch),
                    description = "执行网页搜索并返回结果",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            query = new
                            {
                                type = "string",
                                description = "搜索词"
                            }
                        },
                        required = new[] { "query" }
                    }

            },
            new
            {
                type = "function",

                    name = nameof(GetWeather),
                    description = "获取天气预报并返回结果",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            city = new
                            {
                                type = "string",
                                description = "城市(用英文表示)"
                            }
                        },
                        required = new[] { "city" }
                    }

            },
            
            new
            {
                type = "function",

                    name = nameof(GetCurrentDataTime),
                    description = "获取当前日期和时间并返回结果",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                              city = new
                            {
                                type = "string",
                                description = "城市(用英文表示)"
                            }
                        },
                         required = new[] { "city" }
                    }

            },
            new
            {
                type = "function",

                    name = nameof(SearchTrainTicket),
                    description = "获取指定日期的火车票、火车车次",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {

                                startingplace = new
                            {
                                type = "string",
                                description = "起始城市"
                            },
                                  arrivalplace = new
                            {
                                type = "string",
                                description = "到达城市"
                            },

                              date = new
                            {
                                type = "string",
                                description = "日期(格式:YYYY-MM-DD)"
                            }
                        }
                       },
                         required = new[] { "startingplace", "arrivalplace", "date" }
            }
        }
        : null;


            var requestContent = new
            {

                model = modelconfg.Model,
                input = messages,
                stream = modelconfg.Stream,
                temperature = modelconfg.Temperature >= 0 ? (float?)modelconfg.Temperature : null,

                tools = tools,
            };

            using (var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, modelconfg.ApiEndpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json")
            }, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {



                if (response.StatusCode != System.Net.HttpStatusCode.OK)
                {
                    yield return "失败: StatusCode " + response.StatusCode.ToString();
                    yield break;
                }
                response.EnsureSuccessStatusCode();

                using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var reader = new StreamReader(stream);
                bool beging = false;
                bool end = false;
                bool beging1 = false;
                bool end1 = false;
                List<tool_callnew> tool_calls = new();
                var contentBuilder = new StringBuilder();
                bool iscitations = false;
                string citationsstring = string.Empty;
                while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
                {

                    if (modelconfg.Stream)
                    {
                        var line = await reader.ReadLineAsync(cancellationToken);
                        if (string.IsNullOrEmpty(line)) continue;
                        if (line.StartsWith("data: "))
                        {
                            line = line.Substring(6);


                            var chunk = JsonSerializer.Deserialize<OpenAIChunkResponsenew>(line);


                            switch (chunk?.type)
                            {
                                case "response.output_text.delta":
                                    {
                                        var content = chunk?.delta;
                                        if (!string.IsNullOrEmpty(content))
                                        {
                                            content = Regex.Replace(content, @"(\[\^?\d+\])(?=\[\^?\d+\])", "$1 ");
                                            contentBuilder.Append(content);
                                        }
                                        if (!string.IsNullOrEmpty(content))
                                        {
                                            if (beging && !end)
                                            {
                                                yield return "\n" + "\n" + "```" + "\n" + "\n" + "</think>" + "\n" + "\n" + content;
                                                end = true;
                                            }
                                            else
                                            {
                                                if (content.Contains("<think>") && !beging1 && !end1)
                                                {
                                                    yield return content.Replace("<think>", "<think>" + "\n" + "\n" + "```Thoughts" + "\n" + "\n");
                                                    beging1 = true;
                                                }
                                                else
                                                {
                                                    if (content.Contains("</think>") && beging1 && !end1)
                                                    {

                                                        yield return content.Replace("</think>", "\n" + "\n" + "```" + "\n" + "\n" + "</think>" + "\n" + "\n");
                                                        end1 = true;
                                                    }
                                                    else
                                                    {
                                                        yield return content;
                                                    }

                                                }

                                            }

                                        }
                                        break;
                                    }
                                case "response.output_item.added":
                                    {
                                        if (chunk?.item?.type == "function_call")
                                        {
                                            if (chunk?.item != null)
                                            {
                                                tool_calls.Add(chunk.item);


                                            }
                                        }
                                        break;

                                    }
                                case "response.function_call_arguments.delta":
                                    {
                                        if (chunk?.output_index < tool_calls.Count)
                                        {
                                            tool_calls[(int)chunk.output_index].arguments += chunk.delta ?? string.Empty;
                                        }
                                        break;
                                    }
                                case "response.function_call_arguments.done":
                                    {
                                       
                                        break;
                                    }

                                case "response.output_item.done":
                                    {
                                        //if (chunk?.output_index < tool_calls.Count)
                                        {

                                            if (tool_calls.Count > 0)
                                            {


                                                foreach (var pair in tool_calls)
                                                {
                                                    string toolResult = string.Empty;
                                                    switch (pair.name)
                                                    {
                                                        case nameof(GetCurrentDataTime):
                                                            {
                                                                
                                                               
                                                                toolResult = await GetCurrentDataTime();
                                                                break;
                                                            }
                                                        case nameof(JinaAiSearch):
                                                            {
                                                                using JsonDocument argumentsJson = JsonDocument.Parse(pair.arguments);
                                                                bool query = argumentsJson.RootElement.TryGetProperty("query", out JsonElement outquery);


                                                                if (!query)
                                                                {
                                                                    throw new ArgumentNullException(nameof(query), "The location argument is required.");
                                                                }
                                                                toolResult = await JinaAiSearch(outquery.GetString() ?? throw new ArgumentNullException(nameof(outquery), "Query cannot be null."));
                                                                break;
                                                            }
                                                        case nameof(SearchTrainTicket):
                                                            {
                                                                using JsonDocument argumentsJson = JsonDocument.Parse(pair.arguments);
                                                                bool query = argumentsJson.RootElement.TryGetProperty("startingplace", out JsonElement startingplace);

                                                                 query = argumentsJson.RootElement.TryGetProperty("arrivalplace", out JsonElement arrivalplace);
                                                                query = argumentsJson.RootElement.TryGetProperty("date", out JsonElement date);
                                                                if (!query)
                                                                {
                                                                    throw new ArgumentNullException(nameof(query), "The location argument is required.");
                                                                }
                                                                toolResult = await SearchTrainTicket(startingplace.GetString(), arrivalplace.GetString(),date.GetString());
                                                                break;
                                                            }
                                                        case nameof(GetWeather):
                                                            {
                                                                using JsonDocument argumentsJson = JsonDocument.Parse(pair.arguments);
                                                                bool query = argumentsJson.RootElement.TryGetProperty("city", out JsonElement outquery);


                                                                if (!query)
                                                                {
                                                                    throw new ArgumentNullException(nameof(query), "The location argument is required.");
                                                                }
                                                                toolResult = await GetWeather(outquery.GetString() ?? throw new ArgumentNullException(nameof(outquery), "City cannot be null."));
                                                                break;
                                                            }
                                                        default:
                                                            {
                                                                yield return "未知工具调用";
                                                                break;
                                                            }
                                                    }
                                                    toolsmessages.Add(pair);
                                                    toolsmessages.Add(new
                                                    {
                                                        type = "function_call_output",
                                                        call_id = pair.call_id,
                                                        output = toolResult
                                                    });



                                                }
                                                contentBuilder.Clear();
                                                tool_calls.Clear();
                                                response.Content.Dispose();
                                                await foreach (var item in OpenAIResponsesAsync(modelconfg, request, cancellationToken, client, toolsmessages))
                                                {
                                                    yield return item;
                                                }
                                                break;
                                            }
                                        }
                                        break;
                                    }
                            }

                        }
                    }
                    else
                    {
                        var line = await reader.ReadToEndAsync(cancellationToken);
                        if (string.IsNullOrEmpty(line)) continue;
                        var chunk = JsonSerializer.Deserialize<OpenAIResponsenew>(line);

                        var output = chunk?.output;
                        if (output == null || output.Length == 0) continue;
                        foreach (var item in output)
                        {
                            if (item.type == "function_call")
                            {
                                var content1 = item?.content?.FirstOrDefault()?.text;
                                if (!string.IsNullOrEmpty(content1))
                                {
                                    content1 = Regex.Replace(content1, @"(\[\^?\d+\])(?=\[\^?\d+\])", "$1 ");
                                    contentBuilder.Append(content1);
                                }
                                string toolResult = string.Empty;
                                switch (item.name)
                                {
                                    case nameof(GetCurrentDataTime):
                                        {
                                            
                                            toolResult = await GetCurrentDataTime();
                                            break;
                                        }
                                    case nameof(SearchTrainTicket):
                                        {
                                            using JsonDocument argumentsJson = JsonDocument.Parse(item.arguments);
                                            bool query = argumentsJson.RootElement.TryGetProperty("startingplace", out JsonElement startingplace);

                                            query = argumentsJson.RootElement.TryGetProperty("arrivalplace", out JsonElement arrivalplace);
                                            query = argumentsJson.RootElement.TryGetProperty("date", out JsonElement date);
                                            if (!query)
                                            {
                                                throw new ArgumentNullException(nameof(query), "The location argument is required.");
                                            }
                                            toolResult = await SearchTrainTicket(startingplace.GetString(), arrivalplace.GetString(), date.GetString());
                                            break;
                                        }
                                    case nameof(JinaAiSearch):
                                        {
                                            using JsonDocument argumentsJson = JsonDocument.Parse(item.arguments);
                                            bool query = argumentsJson.RootElement.TryGetProperty("query", out JsonElement outquery);


                                            if (!query)
                                            {
                                                throw new ArgumentNullException(nameof(query), "The location argument is required.");
                                            }
                                            toolResult = await JinaAiSearch(outquery.GetString() ?? throw new ArgumentNullException(nameof(outquery), "Query cannot be null."));
                                            break;
                                        }
                                    case nameof(GetWeather):
                                        {
                                            using JsonDocument argumentsJson = JsonDocument.Parse(item.arguments);
                                            bool query = argumentsJson.RootElement.TryGetProperty("city", out JsonElement outquery);


                                            if (!query)
                                            {
                                                throw new ArgumentNullException(nameof(query), "The location argument is required.");
                                            }
                                            toolResult = await GetWeather(outquery.GetString() ?? throw new ArgumentNullException(nameof(outquery), "City cannot be null."));
                                            break;
                                        }
                                    default:
                                        {
                                            yield return "未知工具调用";
                                            break;
                                        }
                                }

                                toolsmessages.Add(new
                                {
                                    arguments = item.arguments,
                                    name = item.name,
                                    type = item.type,
                                    call_id = item.call_id,
                                    id = item.id
                                });
                                toolsmessages.Add(new
                                {
                                    type = "function_call_output",
                                    call_id = item.call_id,
                                    output = toolResult
                                });
                            }
                            else
                            {
                                var content1 = item?.content?.FirstOrDefault()?.text;
                                if (!string.IsNullOrEmpty(content1))
                                {
                                    content1 = Regex.Replace(content1, @"(\[\^?\d+\])(?=\[\^?\d+\])", "$1 ");
                                    contentBuilder.Append(content1);
                                }
                            }
                        }
                        var content = contentBuilder.ToString();
                        if (!string.IsNullOrEmpty(content))
                        {
                            content = content.Replace("<think>", "<think>" + "\n" + "\n" + "```Thoughts" + "\n" + "\n");
                            content = content.Replace("</think>", "\n" + "\n" + "```" + "\n" + "\n" + "</think>" + "\n" + "\n");
                            yield return content;
                        }
                        if (toolsmessages.Count > 0)
                        {
                            await foreach (var item in OpenAIResponsesAsync(modelconfg, request, cancellationToken, client, toolsmessages))
                            {
                                yield return item;
                            }
                            break;
                        }
                       
                    }
                }

            }
        }
        public async IAsyncEnumerable<string> OpenAIAsync(ChatModelConfig modelconfg, ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken, HttpClient inputclient = null, List<object> toolsmessages = null)
        {
            var apiKey = Environment.GetEnvironmentVariable(modelconfg.EnvironmentApikeyName);
            var apiEndpoint = modelconfg.ApiEndpoint;


            if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiEndpoint))
            {
                throw new InvalidOperationException("API配置缺失");
            }


            // 创建HTTP客户端
            HttpClient client = inputclient ?? _httpClientFactory.CreateClient();
            if (inputclient == null)
            {
                client.Timeout = TimeSpan.FromMinutes(30);
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
            }

            var messages = ToMessagesOpenAi(request, modelconfg);
            toolsmessages ??= new List<object>();
            messages.AddRange(toolsmessages);
            //toolsmessages.Clear();
            List<object> tools = request.EnableSearch
        ? new List<object>
        {
            new
            {
                type = "function",
                function = new
                {
                    name = nameof(JinaAiSearch),
                    description = "执行网页搜索并返回结果",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            query = new
                            {
                                type = "string",
                                description = "搜索词"
                            }
                        },
                        required = new[] { "query" }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = nameof(GetWeather),
                    description = "获取天气预报并返回结果",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            city = new
                            {
                                type = "string",
                                description = "城市(用英文表示)"
                            }
                        },
                        required = new[] { "city" }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = nameof(GetCurrentDataTime),
                    description = "获取当前日期和时间并返回结果",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {

                        }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = nameof(SearchTrainTicket),
                    description = "获取指定日期的火车票、火车车次",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {

                                startingplace = new
                            {
                                type = "string",
                                description = "起始城市"
                            },
                                  arrivalplace = new
                            {
                                type = "string",
                                description = "到达城市"
                            },

                              date = new
                            {
                                type = "string",
                                description = "日期(格式:YYYY-MM-DD)"
                            }
                        }
                       ,
                         required = new[] { "startingplace", "arrivalplace", "date" }
                    }
                }
            }
        }
        : null;


            var requestContent = new
            {

                model = modelconfg.Model,
                messages = messages,
                stream = modelconfg.Stream,
                temperature = modelconfg.Temperature >= 0 ? (float?)modelconfg.Temperature : null,

                tools = tools,
            };
            var str = JsonSerializer.Serialize(requestContent, _jsonOptions);
            using (var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, modelconfg.ApiEndpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json")
            }, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {



                if (response.StatusCode != System.Net.HttpStatusCode.OK)
                {
                    yield return "失败: StatusCode " + response.StatusCode.ToString();
                    yield break;
                }
                response.EnsureSuccessStatusCode();

                using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var reader = new StreamReader(stream);
                bool beging = false;
                bool end = false;
                bool beging1 = false;
                bool end1 = false;
                List<tool_call> tool_calls = new();
                var contentBuilder = new StringBuilder();
                bool iscitations = false;
                string citationsstring = string.Empty;
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

                            var citations = chunk?.citations;
                            if (citations != null && citations.Length > 0 && !iscitations)
                            {
                                StringBuilder sb = new StringBuilder();
                                sb.AppendLine("");
                                for (int i = 0; i < citations.Length; i++)
                                {
                                    sb.AppendLine("[" + (i + 1).ToString() + "]: " + citations[i]);

                                }
                                sb.AppendLine("");
                                citationsstring = sb.ToString();
                                iscitations = true;
                            }
                            var content = chunk?.choices?.FirstOrDefault()?.delta?.content;
                            var reasoning_content = chunk?.choices?.FirstOrDefault()?.delta?.reasoning_content;
                            if (!string.IsNullOrEmpty(content))
                            {
                                content = Regex.Replace(content, @"(\[\d+\])(?=\[\d+\])", "$1 ");

                                contentBuilder.Append(content);
                            }
                            if (!string.IsNullOrEmpty(reasoning_content))
                            {
                                reasoning_content = Regex.Replace(reasoning_content, @"(\[\d+\])(?=\[\d+\])", "$1 ");
                            }

                            if (chunk?.choices?.FirstOrDefault()?.delta?.tool_calls?.FirstOrDefault() != null)
                            {
                                if (!string.IsNullOrEmpty(chunk?.choices?.FirstOrDefault()?.delta?.tool_calls?.FirstOrDefault()?.function?.name))
                                {
                                    tool_calls.Add(chunk.choices.FirstOrDefault().delta.tool_calls.FirstOrDefault());
                                }
                                else
                                {
                                    int index = chunk.choices.FirstOrDefault().delta.tool_calls.FirstOrDefault().index;
                                    tool_calls[index].function.arguments += chunk.choices.FirstOrDefault().delta.tool_calls.FirstOrDefault().function?.arguments;


                                }
                                //continue;
                            }
                            if (!string.IsNullOrEmpty(reasoning_content))
                            {
                                if (!beging)
                                {
                                    yield return "<think>" + "\n" + "\n" + "```Thoughts" + "\n" + "\n" + reasoning_content;
                                    beging = true;
                                }
                                else
                                {
                                    yield return reasoning_content;

                                }

                            }
                            if (!string.IsNullOrEmpty(content))
                            {
                                if (beging && !end)
                                {
                                    yield return "\n" + "\n" + "```" + "\n" + "\n" + "</think>" + "\n" + "\n" + content;
                                    end = true;
                                }
                                else
                                {
                                    if (content.Contains("<think>") && !beging1 && !end1)
                                    {
                                        yield return content.Replace("<think>", "<think>" + "\n" + "\n" + "```Thoughts" + "\n" + "\n");
                                        beging1 = true;
                                    }
                                    else
                                    {
                                        if (content.Contains("</think>") && beging1 && !end1)
                                        {

                                            yield return content.Replace("</think>", "\n" + "\n" + "```" + "\n" + "\n" + "</think>" + "\n" + "\n");
                                            end1 = true;
                                        }
                                        else
                                        {
                                            yield return content;
                                        }

                                    }

                                }

                            }
                            if (tool_calls.Count > 0 && (chunk?.choices?.FirstOrDefault()?.finish_reason == "tool_calls" || chunk?.choices?.FirstOrDefault()?.finish_reason == "stop"))
                            {
                                List<object> tool_calls1 = new List<object>();
                                tool_calls1.AddRange(tool_calls);
                                toolsmessages.Add(new
                                {
                                    role = "assistant",

                                    content = contentBuilder.ToString(),
                                    tool_calls = tool_calls1
                                });

                                foreach (var pair in tool_calls)
                                {
                                    string toolResult = string.Empty;
                                    switch (pair.function.name)
                                    {
                                        case nameof(GetCurrentDataTime):
                                            {


                                                toolResult = await GetCurrentDataTime();
                                                break;
                                            }
                                        case nameof(JinaAiSearch):
                                            {
                                                using JsonDocument argumentsJson = JsonDocument.Parse(pair.function.arguments);
                                                bool query = argumentsJson.RootElement.TryGetProperty("query", out JsonElement outquery);


                                                if (!query)
                                                {
                                                    throw new ArgumentNullException(nameof(query), "The location argument is required.");
                                                }
                                                toolResult = await JinaAiSearch(outquery.GetString() ?? throw new ArgumentNullException(nameof(outquery), "Query cannot be null."));
                                                break;
                                            }
                                        case nameof(SearchTrainTicket):
                                            {
                                                using JsonDocument argumentsJson = JsonDocument.Parse(pair.function.arguments);
                                                bool query = argumentsJson.RootElement.TryGetProperty("startingplace", out JsonElement startingplace);

                                                query = argumentsJson.RootElement.TryGetProperty("arrivalplace", out JsonElement arrivalplace);
                                                query = argumentsJson.RootElement.TryGetProperty("date", out JsonElement date);
                                                if (!query)
                                                {
                                                    throw new ArgumentNullException(nameof(query), "The location argument is required.");
                                                }
                                                toolResult = await SearchTrainTicket(startingplace.GetString(), arrivalplace.GetString(), date.GetString());
                                                break;
                                            }
                                        case nameof(GetWeather):
                                            {
                                                using JsonDocument argumentsJson = JsonDocument.Parse(pair.function.arguments);
                                                bool query = argumentsJson.RootElement.TryGetProperty("city", out JsonElement outquery);


                                                if (!query)
                                                {
                                                    throw new ArgumentNullException(nameof(query), "The location argument is required.");
                                                }
                                                toolResult = await GetWeather(outquery.GetString() ?? throw new ArgumentNullException(nameof(outquery), "City cannot be null."));
                                                break;
                                            }
                                        default:
                                            {
                                                yield return "未知工具调用";
                                                break;
                                            }
                                    }

                                    toolsmessages.Add(new
                                    {
                                        role = "tool",
                                        tool_call_id = pair.id,
                                        content = toolResult
                                    });



                                }
                                contentBuilder.Clear();
                                tool_calls.Clear();
                                response.Content.Dispose();
                                await foreach (var item in OpenAIAsync(modelconfg, request, cancellationToken, client, toolsmessages))
                                {
                                    yield return item;
                                }
                                break;
                            }

                           

                        }
                    }
                    else
                    {
                        var line = await reader.ReadToEndAsync(cancellationToken);
                        if (string.IsNullOrEmpty(line)) continue;
                        var chunk = JsonSerializer.Deserialize<OpenAIResponse>(line);
                        var citations = chunk?.citations;
                        if (citations != null && citations.Length > 0 && !iscitations)
                        {
                            StringBuilder sb = new StringBuilder();
                            sb.Append('\n');
                            sb.Append('\n');
                            for (int i = 0; i < citations.Length; i++)
                            {
                                sb.AppendLine("[" + (i + 1).ToString() + "]: " + citations[i]);

                            }
                            sb.Append('\n');
                            citationsstring = sb.ToString();
                            iscitations = true;
                        }
                        var content = chunk?.choices?.FirstOrDefault()?.message?.content;
                        var reasoning_content = chunk?.choices?.FirstOrDefault()?.message?.reasoning_content;
                        if (!string.IsNullOrEmpty(content))
                        {
                            content = Regex.Replace(content, @"(\[\d+\])(?=\[\d+\])", "$1 ");
                            contentBuilder.Append(content);
                        }
                        if (!string.IsNullOrEmpty(reasoning_content))
                        {
                            reasoning_content = Regex.Replace(reasoning_content, @"(\[\d+\])(?=\[\d+\])", "$1 ");
                        }

                        if (chunk?.choices?.FirstOrDefault()?.message?.tool_calls?.FirstOrDefault() != null)
                        {


                            var toolCalls = chunk?.choices?.FirstOrDefault()?.message?.tool_calls;
                            if (toolCalls != null)
                            {
                                tool_calls.AddRange(toolCalls.Cast<tool_call>());
                            }
                            if (tool_calls.Count > 0)
                            {
                                List<object> tool_calls1 = new List<object>();
                                tool_calls1.AddRange(tool_calls);
                                toolsmessages.Add(new
                                {
                                    role = "assistant",

                                    content = contentBuilder.ToString(),
                                    tool_calls = tool_calls1
                                });

                                foreach (var pair in tool_calls)
                                {
                                    string toolResult = string.Empty;
                                    switch (pair.function.name)
                                    {
                                        case nameof(GetCurrentDataTime):
                                            {


                                                toolResult = await GetCurrentDataTime();
                                                break;
                                            }
                                        case nameof(JinaAiSearch):
                                            {
                                                using JsonDocument argumentsJson = JsonDocument.Parse(pair.function.arguments);
                                                bool query = argumentsJson.RootElement.TryGetProperty("query", out JsonElement outquery);


                                                if (!query)
                                                {
                                                    throw new ArgumentNullException(nameof(query), "The location argument is required.");
                                                }
                                                toolResult = await JinaAiSearch(outquery.GetString() ?? throw new ArgumentNullException(nameof(outquery), "Query cannot be null."));
                                                break;
                                            }
                                        case nameof(SearchTrainTicket):
                                            {
                                                using JsonDocument argumentsJson = JsonDocument.Parse(pair.function.arguments);
                                                bool query = argumentsJson.RootElement.TryGetProperty("startingplace", out JsonElement startingplace);

                                                query = argumentsJson.RootElement.TryGetProperty("arrivalplace", out JsonElement arrivalplace);
                                                query = argumentsJson.RootElement.TryGetProperty("date", out JsonElement date);
                                                if (!query)
                                                {
                                                    throw new ArgumentNullException(nameof(query), "The location argument is required.");
                                                }
                                                toolResult = await SearchTrainTicket(startingplace.GetString(), arrivalplace.GetString(), date.GetString());
                                                break;
                                            }
                                        case nameof(GetWeather):
                                            {
                                                using JsonDocument argumentsJson = JsonDocument.Parse(pair.function.arguments);
                                                bool query = argumentsJson.RootElement.TryGetProperty("city", out JsonElement outquery);


                                                if (!query)
                                                {
                                                    throw new ArgumentNullException(nameof(query), "The location argument is required.");
                                                }
                                                toolResult = await GetWeather(outquery.GetString() ?? throw new ArgumentNullException(nameof(outquery), "City cannot be null."));
                                                break;
                                            }
                                        default:
                                            {
                                                yield return "未知工具调用";
                                                break;
                                            }
                                    }

                                    toolsmessages.Add(new
                                    {
                                        role = "tool",
                                        tool_call_id = pair.id,
                                        content = toolResult
                                    });



                                }
                                contentBuilder.Clear();
                                tool_calls.Clear();
                                response.Content.Dispose();
                                await foreach (var item in OpenAIAsync(modelconfg, request, cancellationToken, client, toolsmessages))
                                {
                                    yield return item;
                                }
                                break;
                            }
                        }

                        if (!string.IsNullOrEmpty(reasoning_content))
                        {
                            reasoning_content = "<think>" + reasoning_content + "</think>";
                            reasoning_content = reasoning_content.Replace("<think>", "<think>" + "\n" + "\n" + "```Thoughts" + "\n" + "\n");
                            reasoning_content = reasoning_content.Replace("</think>", "\n" + "\n" + "```" + "\n" + "\n" + "</think>" + "\n" + "\n");
                            yield return reasoning_content + content;
                        }
                        if (!string.IsNullOrEmpty(content))
                        {
                            content = content.Replace("<think>", "<think>" + "\n" + "\n" + "```Thoughts" + "\n" + "\n");
                            content = content.Replace("</think>", "\n" + "\n" + "```" + "\n" + "\n" + "</think>" + "\n" + "\n");
                            yield return content;
                        }
                    }
                }
                if (!string.IsNullOrEmpty(citationsstring))
                {
                    yield return "\n\n" + citationsstring;
                }
            }
        }
        //Claude 
        public async IAsyncEnumerable<string> ClaudeAsync(ChatModelConfig modelconfg, ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken, HttpClient inputclient = null, List<object> toolsmessages = null)
        {
            // 验证配置
            var apiKey = Environment.GetEnvironmentVariable(modelconfg.EnvironmentApikeyName);
            var apiEndpoint = modelconfg.ApiEndpoint;

            if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiEndpoint))
            {
                throw new InvalidOperationException("API配置缺失");
            }


            HttpClient client = inputclient ?? _httpClientFactory.CreateClient();
            if (inputclient == null)
            {
                client.Timeout = TimeSpan.FromMinutes(30);
                client.DefaultRequestHeaders.Add("x-api-key", $"{apiKey}");
                client.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
            }


            var messages = ToMessagesClaude(request, modelconfg);
            toolsmessages ??= new List<object>();
            messages.AddRange(toolsmessages);
            //toolsmessages.Clear();
            List<object> tools = request.EnableSearch
        ? new List<object>
        {
             new
                {


                    name = nameof(JinaAiSearch),
                    description = "执行网页搜索并返回结果",
                    input_schema = new
                    {
                        type = "object",
                        properties = new
                        {
                            query = new
                            {
                                type = "string",
                                description = "搜索词"

                            }
                        },
                        required = new[] { "query" }
                    }

                },
             new
                {


                    name = nameof(GetWeather),
                    description = "获取天气预报并返回结果",
                    input_schema = new
                    {
                        type = "object",
                        properties = new
                        {
                            city = new
                            {
                                type = "string",
                                description = "城市(用英文表示)"

                            }
                        },
                        required = new[] { "city" }
                    }

                },
             new
            {
               
                    name = nameof(GetCurrentDataTime),
                    description = "获取当前日期和时间并返回结果",
                    input_schema = new
                    {
                        type = "object",
                        properties = new
                        {

                        }
                    }

            },
             new
            {
                
                    name = nameof(SearchTrainTicket),
                    description = "获取指定日期的火车票、火车车次",
                    input_schema = new
                    {
                        type = "object",
                        properties = new
                        {

                                startingplace = new
                            {
                                type = "string",
                                description = "起始城市"
                            },
                                  arrivalplace = new
                            {
                                type = "string",
                                description = "到达城市"
                            },

                              date = new
                            {
                                type = "string",
                                description = "日期(格式:YYYY-MM-DD)"
                            }
                        }
                       ,
                         required = new[] { "startingplace", "arrivalplace", "date" }
                    }
            }
        } : null;


            // 创建HTTP客户端

            var requestContent = new
            {

                model = modelconfg.Model,
                system = modelconfg.Systemprompt,
                messages = messages,

                stream = modelconfg.Stream,
                temperature = modelconfg.Temperature >= 0 ? (float?)modelconfg.Temperature : null,
                max_tokens = modelconfg.MaxTokens,
                thinking = modelconfg.ThinkingTokens > 1024 && modelconfg.ThinkingTokens < modelconfg.MaxTokens ?
                            new { type = "enabled", budget_tokens = modelconfg.ThinkingTokens } : null,
                tools = tools,

            };

            using (var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, apiEndpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json")
            }, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                if (response.StatusCode != System.Net.HttpStatusCode.OK)
                {
                    yield return "失败: StatusCode " + response.StatusCode.ToString();
                    yield break;
                }

                response.EnsureSuccessStatusCode();

                using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var reader = new StreamReader(stream);
                int index = 0;
                List<ClaudeChunkResponse.Delta> tool_calls = new List<ClaudeChunkResponse.Delta>();
                string text = string.Empty;
                string textthinking = string.Empty;
                string textsignature= string.Empty;
                bool beging = false;
                bool end = false;
                while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(cancellationToken);
                    if (string.IsNullOrEmpty(line)) continue;
                    if (modelconfg.Stream)
                    {
                        if (!line.StartsWith("data:")) continue;
                        line = line.Substring(6);
                        var chunk = JsonSerializer.Deserialize<ClaudeChunkResponse>(line);
                        switch (chunk.type)
                        {
                            case "message_start":
                                {
                                    break;

                                }
                            case "content_block_start":
                                {
                                    index = chunk.index;
                                    if (chunk.content_block.type == "tool_use")
                                    {
                                        tool_calls.Add(chunk.content_block);
                                        tool_calls[tool_calls.Count - 1].text = text;
                                        tool_calls[tool_calls.Count - 1].thinking = textthinking;
                                        tool_calls[tool_calls.Count - 1].signature = textsignature;
                                        text = string.Empty;
                                        textsignature = string.Empty;
                                        textthinking = string.Empty;
                                    }
                                    break;

                                }
                            case "ping":
                                {
                                    break;

                                }
                            case "content_block_delta":
                                {
                                    if (chunk.delta.type == "text_delta")
                                    {
                                        
                                        text += Regex.Replace(chunk.delta.text, @"(\[\d+\])(?=\[\d+\])", "$1 ");
                                        if (beging && !end)
                                        {
                                            yield return "\n" + "\n" + "```" + "\n" + "\n" + "</think>" + "\n" + "\n" + Regex.Replace(chunk.delta.text, @"(\[\^?\d+\])(?=\[\^?\d+\])", "$1 ");
                                            end = true;
                                        }
                                        else
                                        {
                                            yield return Regex.Replace(chunk.delta.text, @"(\[\d+\])(?=\[\d+\])", "$1 ");
                                        }
                                    }
                                    if (chunk.delta.type == "thinking_delta")
                                    {
                                        textthinking +=  chunk.delta.thinking ;
                                        if (!beging)
                                        {
                                            yield return "<think>" + "\n" + "\n" + "```Thoughts" + "\n" + "\n" + chunk.delta.thinking;
                                            beging = true;
                                        }
                                        else
                                        {

                                            yield return chunk.delta.thinking;
                                        }
                                    }
                                    if (chunk.delta.type == "signature_delta")
                                    {
                                        textsignature += chunk.delta.signature;
                                        
                                    }
                                    if (chunk.delta.type == "input_json_delta")
                                    {
                                        tool_calls[tool_calls.Count - 1].partial_json += chunk.delta.partial_json;
                                       
                                        continue;
                                    }
                                    break;
                                }
                            case "content_block_stop":
                                {

                                    break;
                                }
                            case "message_delta":
                                {
                                    if (chunk.delta.stop_reason == "tool_use")
                                    {


                                        //text = string.Empty;
                                        foreach (var pair in tool_calls)
                                        {
                                            object ob = null;
                                            if (string.IsNullOrEmpty(pair.thinking))
                                            {
                                                if (!string.IsNullOrEmpty(pair.text))
                                                {
                                                    ob = new
                                                    {
                                                        type = "text",
                                                        text = pair.text,

                                                    };
                                                }
                                            }
                                            else
                                            {
                                                ob = new
                                                {
                                                    type = "thinking",
                                                    signature = pair.signature,
                                                    thinking = pair.thinking

                                                };
                                            }
                                            var content = new List<object>();
                                            if (ob != null) content.Add(ob);
                                            string toolResult = string.Empty;
                                            switch (pair.name)
                                            {
                                                case nameof(GetCurrentDataTime):
                                                    {
                                                        
                                                        content.Add(new
                                                        {
                                                            type = "tool_use",
                                                            id = pair.id,
                                                            name = pair.name,
                                                            input = new { }
                                                        });
                                                        toolsmessages.Add(new
                                                        {
                                                            role = "assistant",
                                                            content = content


                                                        });

                                                        toolResult = await GetCurrentDataTime();
                                                        break;
                                                    }
                                                case nameof(JinaAiSearch):
                                                    {
                                                        using JsonDocument argumentsJson = JsonDocument.Parse(pair.partial_json);
                                                        bool query = argumentsJson.RootElement.TryGetProperty("query", out JsonElement outquery);
                                                        //JsonSerializer.Deserialize(argumentsJson;

                                                        if (!query)
                                                        {
                                                            throw new ArgumentNullException(nameof(query), "The location argument is required.");
                                                        }
                                                        content.Add(new
                                                        {
                                                            type = "tool_use",
                                                            id = pair.id,
                                                            name = pair.name,
                                                            input = new { query = outquery.GetString() }
                                                        });
                                                        toolsmessages.Add(new
                                                        {
                                                            role = "assistant",
                                                            content = content


                                                        });
                                                       
                                                        toolResult = await JinaAiSearch(outquery.GetString() ?? throw new ArgumentNullException(nameof(outquery), "Query cannot be null."));
                                                        break;
                                                    }
                                                case nameof(SearchTrainTicket):
                                                    {
                                                        using JsonDocument argumentsJson = JsonDocument.Parse(pair.partial_json);
                                                        bool query = argumentsJson.RootElement.TryGetProperty("startingplace", out JsonElement startingplace);

                                                        query = argumentsJson.RootElement.TryGetProperty("arrivalplace", out JsonElement arrivalplace);
                                                        query = argumentsJson.RootElement.TryGetProperty("date", out JsonElement date);
                                                        if (!query)
                                                        {
                                                            throw new ArgumentNullException(nameof(query), "The location argument is required.");
                                                        }
                                                        
                                                        content.Add(new
                                                        {
                                                            type = "tool_use",
                                                            id = pair.id,
                                                            name = pair.name,
                                                            input = new { startingplace = startingplace.GetString(), arrivalplace= arrivalplace.GetString(), date= date.GetString() }
                                                        });
                                                        toolsmessages.Add(new
                                                        {
                                                            role = "assistant",
                                                            content = content


                                                        });

                                                        toolResult = await SearchTrainTicket(startingplace.GetString(), arrivalplace.GetString(), date.GetString());
                                                        break;
                                                    }
                                                case nameof(GetWeather):
                                                    {
                                                        using JsonDocument argumentsJson = JsonDocument.Parse(pair.partial_json);
                                                        bool query = argumentsJson.RootElement.TryGetProperty("city", out JsonElement outquery);

                                                       
                                                        if (!query)
                                                        {
                                                            throw new ArgumentNullException(nameof(query), "The location argument is required.");
                                                        }
                                                        content.Add(new
                                                        {
                                                            type = "tool_use",
                                                            id = pair.id,
                                                            name = pair.name,
                                                            input = new { city = outquery.GetString() }
                                                        });
                                                        toolsmessages.Add(new
                                                        {
                                                            role = "assistant",
                                                            content = content


                                                        });
                                                       
                                                        toolResult = await GetWeather(outquery.GetString() ?? throw new ArgumentNullException(nameof(outquery), "city cannot be null."));
                                                        break;
                                                    }
                                                default:
                                                    {
                                                        yield return "未知工具调用";
                                                        break;
                                                    }
                                            }

                                            toolsmessages.Add(new
                                            {
                                                role = "user",
                                                content = new List<object>
                                                            {
                                                                new
                                                                    {
                                                                    type= "tool_result",
                                                                    tool_use_id= pair.id,
                                                                    content = toolResult
                                                                    }

                                                                }
                                            });




                                        }

                                        tool_calls.Clear();
                                        response.Content.Dispose();
                                        await foreach (var item in ClaudeAsync(modelconfg, request, cancellationToken, client, toolsmessages))
                                        {
                                            yield return item;
                                        }
                                    }

                                    break;
                                }
                            case "message_stop":
                                {

                                    break;
                                }
                        }

                    }
                    else
                    {
                       

                        var chunk = JsonSerializer.Deserialize<ClaudeResponse>(line);
                        var content = chunk?.Content;
                        var tool_calls1 = new List<ClaudeResponse.ClaudeResponseContent>();
                        if (content != null)
                        {
                            foreach (var item in content)
                            {
                                switch (item.type)
                                {
                                    case "thinking":
                                        {
                                            textthinking = item.thinking??string.Empty;
                                            textsignature = item.signature?? string.Empty;

                                           var textthinking1 = "<think>" + textthinking + "</think>";
                                            textthinking1 = textthinking1.Replace("<think>", "<think>" + "\n" + "\n" + "```Thoughts" + "\n" + "\n");
                                            textthinking1 = textthinking1.Replace("</think>", "\n" + "\n" + "```" + "\n" + "\n" + "</think>" + "\n" + "\n");
                                            yield return textthinking1;

                                            break;
                                        }
                                    case "tool_use":
                                        {
                                            tool_calls1.Add(item);
                                            tool_calls1[tool_calls1.Count - 1].text = text;
                                            tool_calls1[tool_calls1.Count - 1].thinking = textthinking;
                                            tool_calls1[tool_calls1.Count - 1].signature = textsignature;

                                            text = string.Empty;
                                            textsignature = string.Empty;
                                            textthinking = string.Empty;
                                            break;
                                        }
                                    default:
                                        {
                                            yield return item.text ?? string.Empty;
                                            text += item.text?? string.Empty;
                                            break;
                                        }
                                }

                            }
                            foreach (var pair in tool_calls1)
                            {
                                object ob = null;
                                if (string.IsNullOrEmpty(pair.thinking))
                                {
                                    if (!string.IsNullOrEmpty(pair.text))
                                    {
                                        ob = new
                                        {
                                            type = "text",
                                            text = pair.text,

                                        };
                                    }
                                }
                                else
                                {
                                    ob = new
                                    {
                                        type = "thinking",
                                        signature = pair.signature,
                                        thinking = pair.thinking

                                    };
                                }
                                string toolResult = string.Empty;
                                var content1 = new List<object>();
                                if (ob != null) content1.Add(ob);
                                switch (pair.name)
                                {
                                    case nameof(GetCurrentDataTime):
                                        {

                                            content1.Add(new
                                            {
                                                type = "tool_use",
                                                id = pair.id,
                                                name = pair.name,
                                                input = new { }
                                            });
                                            toolsmessages.Add(new
                                            {
                                                role = "assistant",
                                                content = content


                                            });
                                            text = string.Empty;
                                            toolResult = await GetCurrentDataTime();
                                            break;
                                        }
                                    case nameof(SearchTrainTicket):
                                        {
                                            using JsonDocument argumentsJson = JsonDocument.Parse(pair.input.ToString());
                                            bool query = argumentsJson.RootElement.TryGetProperty("startingplace", out JsonElement startingplace);

                                            query = argumentsJson.RootElement.TryGetProperty("arrivalplace", out JsonElement arrivalplace);
                                            query = argumentsJson.RootElement.TryGetProperty("date", out JsonElement date);
                                            if (!query)
                                            {
                                                throw new ArgumentNullException(nameof(query), "The location argument is required.");
                                            }

                                            content1.Add(new
                                            {
                                                type = "tool_use",
                                                id = pair.id,
                                                name = pair.name,
                                                input = new { startingplace = startingplace.GetString(), arrivalplace = arrivalplace.GetString(), date = date.GetString() }
                                            });
                                            toolsmessages.Add(new
                                            {
                                                role = "assistant",
                                                content = content1


                                            });

                                            toolResult = await SearchTrainTicket(startingplace.GetString(), arrivalplace.GetString(), date.GetString());
                                            break;
                                        }
                                    case nameof(JinaAiSearch):
                                        {
                                            using JsonDocument argumentsJson = JsonDocument.Parse(pair.input.ToString());
                                            bool query = argumentsJson.RootElement.TryGetProperty("query", out JsonElement outquery);


                                            if (!query)
                                            {
                                                throw new ArgumentNullException(nameof(query), "The location argument is required.");
                                            }
                                            content1.Add(new
                                            {
                                                type = "tool_use",
                                                id = pair.id,
                                                name = pair.name,
                                                input = pair.input
                                            });
                                            toolsmessages.Add(new
                                            {
                                                role = "assistant",
                                                content = content1


                                            });
                                            text = string.Empty;
                                            toolResult = await JinaAiSearch(outquery.GetString() ?? throw new ArgumentNullException(nameof(outquery), "Query cannot be null."));
                                            break;
                                        }
                                    case nameof(GetWeather):
                                        {
                                            using JsonDocument argumentsJson = JsonDocument.Parse(pair.input.ToString());
                                            bool query = argumentsJson.RootElement.TryGetProperty("city", out JsonElement outquery);


                                            if (!query)
                                            {
                                                throw new ArgumentNullException(nameof(query), "The location argument is required.");
                                            }
                                            content1.Add(new
                                            {
                                                type = "tool_use",
                                                id = pair.id,
                                                name = pair.name,
                                                input = pair.input
                                            });
                                            toolsmessages.Add(new
                                            {
                                                role = "assistant",
                                                content = content1


                                            });
                                            text = string.Empty;
                                            toolResult = await GetWeather(outquery.GetString() ?? throw new ArgumentNullException(nameof(outquery), "city cannot be null."));
                                            break;
                                        }
                                    default:
                                        {
                                            yield return "未知工具调用";
                                            break;
                                        }
                                }

                                toolsmessages.Add(new
                                {
                                    role = "user",
                                    content = new List<object>
                                                            {
                                                                new
                                                                    {
                                                                    type= "tool_result",
                                                                    tool_use_id= pair.id,
                                                                    content = toolResult
                                                                    }

                                                                }
                                });




                            }
                            if (tool_calls1.Count > 0)
                            {


                                tool_calls1.Clear();
                                response.Content.Dispose();
                                await foreach (var item in ClaudeAsync(modelconfg, request, cancellationToken, client, toolsmessages))
                                {

                                    yield return item;
                                }
                            }
                            
                        }

                    }
                }
            }
        }
        //Gemini
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

            string Search = string.Empty;
            if (request.EnableSearch)
            {
                string Searchterm = await GeminiSearchtermAsync(modelconfg, request.History[request.History.Count - 1].Content, cancellationToken);
                SearchTermsResponse Searchtermlist = null;
                try
                {
                    Searchtermlist = JsonSerializer.Deserialize<SearchTermsResponse>(Searchterm);
                }
                catch (JsonException ex)
                {
                    _logger.LogError(ex, "反序列化 Searchterm 失败");
                    // 根据需要处理异常，例如返回默认值或重新抛出
                }

                List<JinaSearchResultData> Searchlist = new List<JinaSearchResultData>();

                if (Searchtermlist == null || Searchtermlist.SearchTerms.Count == 0)
                {
                    var list = await JinaAiSearch(request.History[request.History.Count - 1].Content, cancellationToken, 1, maxSearchCount);

                    Searchlist.AddRange(list.Data);
                }
                else
                {

                    for (int i = 0; i < (Searchtermlist.SearchTerms.Count < 3 ? Searchtermlist.SearchTerms.Count : 3); i++)
                    {
                        var list = await JinaAiSearch(Searchtermlist.SearchTerms[i], cancellationToken, Searchtermlist.SearchTerms.Count < 3 ? Searchtermlist.SearchTerms.Count : 3, maxSearchCount);

                        Searchlist.AddRange(list.Data);
                    }
                }
                Search = JsonSerializer.Serialize(Searchlist, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });
            }
            if (!string.IsNullOrWhiteSpace(Search))
            {
                Search = "\n\n以下是相关资料：\n\n" + Search;
            }
            // 创建HTTP客户端
            var client = _httpClientFactory.CreateClient();

            HttpResponseMessage response = null;
            if (modelconfg.Temperature >= 0)
            {
                var requestContent = new
                {
                    system_instruction = new
                    {
                        parts = new { text = modelconfg.Systemprompt + Search }
                    },

                    contents = ToMessagesGemini(request, modelconfg),
                    generationConfig = new { temperature = modelconfg.Temperature }


                };
                var str = JsonSerializer.Serialize(requestContent, _jsonOptions);
                response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, apiEndpoint)
                {
                    Content = new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json")
                }, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            else
            {
                var requestContent = new
                {
                    system_instruction = new
                    {
                        parts = new { text = modelconfg.Systemprompt + Search }
                    },

                    contents = ToMessagesGemini(request, modelconfg),



                };
                var str = JsonSerializer.Serialize(requestContent, _jsonOptions);
                response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, apiEndpoint)
                {
                    Content = new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json")
                }, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            if (response.StatusCode != System.Net.HttpStatusCode.OK)
            {
                yield return "失败: StatusCode " + response.StatusCode.ToString();
                yield break;
            }
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
        //Deepseek OpenAI 兼容方式
        public async IAsyncEnumerable<string> DeepseekOpenAIAsync(ChatModelConfig modelconfg, ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
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
                string Searchterm = await OpenAISearchtermAsync(modelconfg, request.History[request.History.Count - 1].Content, cancellationToken);
                SearchTermsResponse Searchtermlist = null;
                try
                {
                    Searchtermlist = JsonSerializer.Deserialize<SearchTermsResponse>(Searchterm);
                }
                catch (JsonException ex)
                {
                    _logger.LogError(ex, "反序列化 Searchterm 失败");
                    // 根据需要处理异常，例如返回默认值或重新抛出
                }

                List<JinaSearchResultData> Searchlist = new List<JinaSearchResultData>();

                if (Searchtermlist == null || Searchtermlist.SearchTerms.Count == 0)
                {
                    var list = await JinaAiSearch(request.History[request.History.Count - 1].Content, cancellationToken, 1, maxSearchCount);

                    Searchlist.AddRange(list.Data);
                }
                else
                {

                    for (int i = 0; i < Searchtermlist.SearchTerms.Count; i++)
                    {
                        var list = await JinaAiSearch(Searchtermlist.SearchTerms[i], cancellationToken, Searchtermlist.SearchTerms.Count, maxSearchCount);

                        Searchlist.AddRange(list.Data);
                    }
                }
                Search = JsonSerializer.Serialize(Searchlist, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });
            }
            // 创建HTTP客户端
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromMinutes(30);
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

            HttpResponseMessage response = null;
            if (modelconfg.Temperature >= 0)
            {
                var requestContent = new
                {

                    model = modelconfg.Model,
                    messages = ToMessagesOpenAi(request, modelconfg, Search),
                    stream = modelconfg.Stream,
                    temperature = modelconfg.Temperature,

                };

                response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, apiEndpoint)
                {
                    Content = new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json")
                }, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            else
            {
                var requestContent = new
                {

                    model = modelconfg.Model,
                    messages = ToMessagesOpenAi(request, modelconfg, Search),
                    stream = modelconfg.Stream,


                };

                response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, apiEndpoint)
                {
                    Content = new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json")
                }, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }

            if (response.StatusCode != System.Net.HttpStatusCode.OK)
            {
                yield return "失败: StatusCode " + response.StatusCode.ToString();
                yield break;
            }
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);
            bool beging = false;
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
                                yield return "<think>" + "\n" + "\n" + "```Thoughts" + "\n" + "\n" + reasoning_content;
                                beging = true;
                            }
                            else
                            {
                                yield return reasoning_content;

                            }

                        }
                        if (!string.IsNullOrEmpty(content))
                        {
                            if (beging && !end)
                            {
                                yield return "\n" + "\n" + "```" + "\n" + "\n" + "</think>" + "\n" + "\n" + content;
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
                                        yield return "\n" + "\n" + "```" + "\n" + "\n" + content + "\n";
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
                        reasoning_content = "<think>" + reasoning_content + "</think>";
                        reasoning_content = reasoning_content.Replace("<think>", "<think>" + "\n" + "\n" + "```Thoughts" + "\n" + "\n");
                        reasoning_content = reasoning_content.Replace("</think>", "\n" + "\n" + "```" + "\n" + "\n" + "</think>" + "\n" + "\n");
                        yield return reasoning_content + content;
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

        /// <summary>
        /// 使用 Dify API 生成消息内容，支持流式输出
        /// </summary>
        /// <param name="modelconfg">模型配置信息</param>
        /// <param name="request">聊天请求</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>流式生成的内容</returns>
        public async IAsyncEnumerable<string> DifyAsync(ChatModelConfig modelconfg, ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            // 验证配置
            //var apiKey = Environment.GetEnvironmentVariable(modelconfg.EnvironmentApikeyName);
            var apiKey = "app-Tdwz7Lm8oZRNgDbpYTg1Nrdb";
            var apiEndpoint = modelconfg.ApiEndpoint;

            if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiEndpoint))
            {
                throw new InvalidOperationException("API配置缺失");
            }

            // 创建HTTP客户端
            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromMinutes(10);
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

            // 准备消息数据
            var messages = new List<object>();

            // 转换历史消息为Dify格式
            foreach (var msg in request.History)
            {
                messages.Add(new
                {
                    role = msg.Role,
                    content = msg.Role == "assistant" ? delstr(msg.Content, "<think>", "</think>") : msg.Content,
                    // 处理消息中的图片，如果Dify支持的话
                    image_url = msg.Images?.FirstOrDefault()
                });
            }

            // 确定是否为流式响应
            bool streamMode = modelconfg.Stream;
            string responseMode = streamMode ? "streaming" : "blocking";

            // 准备请求内容
            var requestContent = new
            {
                inputs = new Dictionary<string, object>(),  // Dify可能需要的特定输入参数
                query = request.History.Last().Content,     // 当前查询内容
                response_mode = responseMode,               // 根据配置使用流式或阻塞模式
                conversation_id = "",   // 会话ID，如果有的话
                user = "abc-123",                      // 用户ID，如果有的话
                //messages = messages                         // 历史消息
            };


            if (streamMode)
            {
                // 流式响应处理
                using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, apiEndpoint)
                {
                    Content = new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json")
                }, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                if (response.StatusCode != System.Net.HttpStatusCode.OK)
                {
                    yield return $"Dify API调用失败: StatusCode {response.StatusCode}";
                    yield break;
                }

                response.EnsureSuccessStatusCode();

                // 处理流式响应
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


                        {
                            var chunk = JsonSerializer.Deserialize<DifyChunkResponse>(line);

                            // 处理不同类型的事件
                            switch (chunk?.Event)
                            {
                                case "message":
                                    if (!string.IsNullOrEmpty(chunk?.answer))
                                    {
                                        yield return chunk.answer;
                                    }
                                    break;

                                case "error":
                                    yield return $"Dify错误: {chunk?.data?.error}";
                                    break;

                                case "completed":
                                    // 完成事件，流式响应结束
                                    break;

                                default:
                                    // 忽略其他事件类型
                                    break;
                            }
                        }

                    }
                }
            }
            else
            {
                // 阻塞模式处理
                using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, apiEndpoint)
                {
                    Content = new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json")
                }, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    yield return $"Dify API调用失败: StatusCode {response.StatusCode}";
                    yield break;
                }

                var result = await response.Content.ReadAsStringAsync(cancellationToken);
                var difyResponse = JsonSerializer.Deserialize<DifyBlockingResponse>(result);

                if (difyResponse?.answer != null)
                {
                    yield return difyResponse.answer;
                }
                else if (!string.IsNullOrEmpty(difyResponse?.error))
                {
                    yield return $"Dify错误: {difyResponse.error}";
                }
                else
                {
                    yield return "Dify返回了空响应";
                }
            }

        }


        #region 深度研究
        //Claude 获取聊天消息搜索词
        public async Task<string> ClaudeSearchtermAsync(ChatModelConfig modelconfg, string searchterm, [EnumeratorCancellation] CancellationToken cancellationToken)
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
            client.DefaultRequestHeaders.Add("x-api-key", $"{apiKey}");
            client.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
            // 准备请求内容
            HttpResponseMessage response = null;
            var contents = new List<object>();

            contents.Add(new
            {
                role = "user",
                content = (new StringBuilder()).Append("要深度研究：")
                        .Append('\n')
                        .Append(searchterm)
                        .Append('\n')
                        .Append(" ，需要联网搜索权威可信的资料， 要求只输出精准的搜索词并按重要性排序\r\n以 JSON 的形式输出，输出的 JSON 需遵守以下的格式：\r\n\r\n{\r\n\tsearch_terms:[\r\n\t  <搜索关键词>,\r\n\t  <搜索关键词>,\r\n\t]\r\n}").ToString()
            });
            if (modelconfg.Temperature >= 0)
            {
                var requestContent = new
                {

                    model = modelconfg.Model,
                    system = modelconfg.Systemprompt,
                    messages = contents,

                    stream = false,
                    temperature = modelconfg.Temperature,
                    max_tokens = modelconfg.MaxTokens,

                };

                response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, apiEndpoint)
                {
                    Content = new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json")
                }, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            else
            {
                var requestContent = new
                {

                    model = modelconfg.Model,
                    system = modelconfg.Systemprompt,
                    messages = contents,

                    stream = false,

                    max_tokens = modelconfg.MaxTokens,

                };

                response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, apiEndpoint)
                {
                    Content = new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json")
                }, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }

            if (response.StatusCode != System.Net.HttpStatusCode.OK)
            {
                return await Task.FromResult("错误：StatusCode " + response.StatusCode.ToString());

            }

            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);

            while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (string.IsNullOrEmpty(line)) continue;

                {
                    var chunk = JsonSerializer.Deserialize<OpenAIResponse>(line);
                    var content = chunk?.choices?.FirstOrDefault()?.message?.content;
                    if (!string.IsNullOrEmpty(content))
                    {
                        int beging = content.IndexOf("```json");
                        if (beging >= 0)
                        {
                            int end = content.IndexOf("```", 7 + beging);
                            if (end > 0 && end > beging)
                            {
                                content = content.Substring(beging + 7, end - beging - 7);

                            }
                        }
                        return await Task.FromResult(content);

                    }
                }
            }
            return await Task.FromResult(string.Empty);
        }
        public async IAsyncEnumerable<string> OpenAIDeepResearchAsync(ChatModelConfig modelconfg, ChatRequest request, List<object> toolsmessages, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var apiKey = Environment.GetEnvironmentVariable(modelconfg.EnvironmentApikeyName);
            var apiEndpoint = modelconfg.ApiEndpoint;


            if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiEndpoint))
            {
                throw new InvalidOperationException("API配置缺失");
            }

            // 创建HTTP客户端
            using (var client = _httpClientFactory.CreateClient())
            {
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
                HttpResponseMessage response = null;
                StringContent content1 = null;
                List<object> tools = new List<object>();

                var messages = ToMessagesOpenAi(request, modelconfg, generateSystemPrompt("", request.History[request.History.Count - 1].Content));
                if (toolsmessages == null)
                {
                    toolsmessages = new List<object>();
                }


                messages.AddRange(toolsmessages);

                if (request.EnableSearch)
                {
                    var Search = new
                    {
                        type = "function",
                        function = new
                        {
                            name = nameof(JinaAiSearch),
                            description = "执行网页搜索并返回结果",
                            parameters = new
                            {
                                type = "object",
                                properties = new
                                {
                                    query = new
                                    {
                                        type = "string",
                                        description = "搜索词"

                                    }
                                },
                                required = new[] { "query" }
                            }
                        }
                    };
                    tools.Add(Search);
                }
                if (modelconfg.Temperature >= 0)
                {
                    if (request.EnableSearch)
                    {
                        var requestContent = new
                        {

                            model = modelconfg.Model,
                            messages = messages,
                            stream = modelconfg.Stream,
                            temperature = modelconfg.Temperature,
                            tool_choice = "auto",
                            tools = tools,
                        };
                        content1 = new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json");
                    }
                    else
                    {
                        var requestContent = new
                        {

                            model = modelconfg.Model,
                            messages = messages,
                            stream = modelconfg.Stream,
                            temperature = modelconfg.Temperature,

                        };
                        content1 = new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json");
                    }

                }
                else
                {
                    if (request.EnableSearch)
                    {
                        var requestContent = new
                        {

                            model = modelconfg.Model,
                            messages = messages,
                            stream = modelconfg.Stream,
                            tool_choice = "auto",
                            tools = tools,

                        };
                        content1 = new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json");
                    }
                    else
                    {
                        var requestContent = new
                        {

                            model = modelconfg.Model,
                            messages = messages,
                            stream = modelconfg.Stream,


                        };
                        content1 = new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json");
                    }
                }
                ;


                using (response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, modelconfg.ApiEndpoint)
                {
                    Content = content1
                }, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                {



                    if (response.StatusCode != System.Net.HttpStatusCode.OK)
                    {
                        yield return "失败: StatusCode " + response.StatusCode.ToString();
                        yield break;
                    }
                    response.EnsureSuccessStatusCode();

                    using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    using var reader = new StreamReader(stream);
                    bool beging = false;
                    bool end = false;
                    bool beging1 = false;
                    bool end1 = false;
                    List<tool_call> tool_calls = new();
                    string content10 = string.Empty;
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
                                content10 += content;
                                if (chunk?.choices?.FirstOrDefault()?.delta?.tool_calls?.FirstOrDefault() != null)
                                {
                                    if (chunk?.choices?.FirstOrDefault()?.delta?.tool_calls?.FirstOrDefault()?.type == "function")
                                    {
                                        tool_calls.Add(chunk.choices.FirstOrDefault().delta.tool_calls.FirstOrDefault());
                                    }
                                    else
                                    {
                                        int index = chunk.choices.FirstOrDefault().delta.tool_calls.FirstOrDefault().index;
                                        tool_calls[index].function.arguments += chunk.choices.FirstOrDefault().delta.tool_calls.FirstOrDefault().function?.arguments;

                                        //&& (chunk?.choices?.FirstOrDefault()?.finish_reason == "tool_calls")
                                    }
                                    //continue;
                                }
                                if (tool_calls.Count > 0 && (chunk?.choices?.FirstOrDefault()?.finish_reason == "tool_calls" || chunk?.choices?.FirstOrDefault()?.finish_reason == "stop"))
                                {
                                    List<object> tool_calls1 = new List<object>();
                                    tool_calls1.AddRange(tool_calls);
                                    toolsmessages.Add(new
                                    {
                                        role = "assistant",

                                        content = content10,
                                        tool_calls = tool_calls1
                                    });

                                    foreach (var pair in tool_calls)
                                    {
                                        string toolResult = string.Empty;
                                        switch (pair.function.name)
                                        {
                                            case nameof(JinaAiSearch):
                                                {
                                                    using JsonDocument argumentsJson = JsonDocument.Parse(pair.function.arguments);
                                                    bool query = argumentsJson.RootElement.TryGetProperty("query", out JsonElement outquery);


                                                    if (!query)
                                                    {
                                                        throw new ArgumentNullException(nameof(query), "The location argument is required.");
                                                    }
                                                    toolResult = await JinaAiSearch(outquery.GetString() ?? throw new ArgumentNullException(nameof(outquery), "Query cannot be null."));
                                                    break;
                                                }
                                            default:
                                                {
                                                    yield return "未知工具调用";
                                                    break;
                                                }
                                        }

                                        toolsmessages.Add(new
                                        {
                                            role = "tool",
                                            tool_call_id = pair.id,
                                            content = toolResult
                                        });



                                    }
                                    content10 = string.Empty;
                                    tool_calls.Clear();
                                    response.Content.Dispose();
                                    await foreach (var item in OpenAIDeepResearchAsync(modelconfg, request, toolsmessages, cancellationToken))
                                    {
                                        yield return item;
                                    }
                                    break;
                                }

                                if (!string.IsNullOrEmpty(reasoning_content))
                                {
                                    if (!beging)
                                    {
                                        yield return "<think>" + "\n" + "\n" + "```Thoughts" + "\n" + "\n" + reasoning_content;
                                        beging = true;
                                    }
                                    else
                                    {
                                        yield return reasoning_content;

                                    }

                                }
                                if (!string.IsNullOrEmpty(content))
                                {
                                    if (beging && !end)
                                    {
                                        yield return "\n" + "\n" + "```" + "\n" + "\n" + "</think>" + "\n" + "\n" + content;
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
                                                yield return "\n" + "\n" + "```" + "\n" + "\n" + content + "\n";
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
                            content10 += content;
                            if (chunk?.choices?.FirstOrDefault()?.message?.tool_calls?.FirstOrDefault() != null)
                            {


                                var toolCalls = chunk?.choices?.FirstOrDefault()?.message?.tool_calls;
                                if (toolCalls != null)
                                {
                                    tool_calls.AddRange(toolCalls.Cast<tool_call>());
                                }
                                if (tool_calls.Count > 0)
                                {
                                    List<object> tool_calls1 = new List<object>();
                                    tool_calls1.AddRange(tool_calls);
                                    toolsmessages.Add(new
                                    {
                                        role = "assistant",

                                        content = content10,
                                        tool_calls = tool_calls1
                                    });

                                    foreach (var pair in tool_calls)
                                    {
                                        string toolResult = string.Empty;
                                        switch (pair.function.name)
                                        {
                                            case nameof(JinaAiSearch):
                                                {
                                                    using JsonDocument argumentsJson = JsonDocument.Parse(pair.function.arguments);
                                                    bool query = argumentsJson.RootElement.TryGetProperty("query", out JsonElement outquery);


                                                    if (!query)
                                                    {
                                                        throw new ArgumentNullException(nameof(query), "The location argument is required.");
                                                    }
                                                    toolResult = await JinaAiSearch(outquery.GetString() ?? throw new ArgumentNullException(nameof(outquery), "Query cannot be null."));
                                                    break;
                                                }
                                            default:
                                                {
                                                    yield return "未知工具调用";
                                                    break;
                                                }
                                        }

                                        toolsmessages.Add(new
                                        {
                                            role = "tool",
                                            tool_call_id = pair.id,
                                            content = toolResult
                                        });



                                    }
                                    content10 = string.Empty;
                                    tool_calls.Clear();
                                    response.Content.Dispose();
                                    await foreach (var item in OpenAIAsync(modelconfg, request, cancellationToken, client, toolsmessages))
                                    {
                                        yield return item;
                                    }
                                    break;
                                }
                            }

                            if (!string.IsNullOrEmpty(reasoning_content))
                            {
                                reasoning_content = "<think>" + reasoning_content + "</think>";
                                reasoning_content = reasoning_content.Replace("<think>", "<think>" + "\n" + "\n" + "```Thoughts" + "\n" + "\n");
                                reasoning_content = reasoning_content.Replace("</think>", "\n" + "\n" + "```" + "\n" + "\n" + "</think>" + "\n" + "\n");
                                yield return reasoning_content + content;
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
            }
        }
        //OpenAI 获取聊天消息搜索词
        public async Task<string> DeepseekOpenAISearchtermAsync(ChatModelConfig modelconfg, string searchterm, [EnumeratorCancellation] CancellationToken cancellationToken)
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
            client.Timeout = TimeSpan.FromMinutes(30);
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

            string Search = string.Empty;


            HttpResponseMessage response = null;
            var contents = new List<object>();
            contents.Add(new
            {
                role = "user",
                content = (new StringBuilder()).Append("要深度研究：")
                        .Append('\n')
                        .Append(searchterm)
                        .Append('\n')
                        .Append(" ，需要联网搜索权威可信的资料， 要求只输出精准的搜索词并按重要性排序\r\n以 JSON 的形式输出，输出的 JSON 需遵守以下的格式：\r\n\r\n{\r\n\tsearch_terms:[\r\n\t  <搜索关键词>,\r\n\t  <搜索关键词>,\r\n\t]\r\n}").ToString()
            });
            if (modelconfg.Temperature >= 0)
            {
                var requestContent = new
                {

                    model = modelconfg.Model,
                    messages = contents,
                    stream = false,
                    temperature = modelconfg.Temperature,

                };
                response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, apiEndpoint)
                {
                    Content = new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json")
                }, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            }
            else
            {
                var requestContent = new
                {

                    model = modelconfg.Model,
                    messages = contents,
                    stream = false,


                };
                response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, apiEndpoint)
                {
                    Content = new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json")
                }, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            }
            if (response.StatusCode != System.Net.HttpStatusCode.OK)
            {
                return await Task.FromResult("错误：StatusCode " + response.StatusCode.ToString());

            }
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);

            while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadToEndAsync(cancellationToken);
                if (string.IsNullOrEmpty(line)) continue;


                var chunk = JsonSerializer.Deserialize<OpenAIResponse>(line);
                var content = chunk?.choices?.FirstOrDefault()?.message?.content;
                if (!string.IsNullOrEmpty(content))
                {

                    int beging = content.IndexOf("```json");
                    if (beging >= 0)
                    {
                        int end = content.IndexOf("```", 7 + beging);
                        if (end > 0 && end > beging)
                        {
                            content = content.Substring(beging + 7, end - beging - 7);
                            //return await Task.FromResult(content);
                        }
                    }
                    return await Task.FromResult(content);
                }

            }
            return await Task.FromResult(string.Empty);
        }
        //OpenAI 获取聊天消息搜索词
        public async Task<string> OpenAISearchtermAsync(ChatModelConfig modelconfg, string searchterm, [EnumeratorCancellation] CancellationToken cancellationToken)
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
            client.Timeout = TimeSpan.FromMinutes(30);
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

            string Search = string.Empty;


            HttpResponseMessage response = null;
            var contents = new List<object>();
            contents.Add(new
            {
                role = "user",
                content = (new StringBuilder()).Append("要深度研究：")
                        .Append('\n')
                        .Append(searchterm)
                        .Append('\n')
                        .Append(" ，需要联网搜索权威可信的资料， 要求只输出精准的搜索词并按重要性排序\r\n以 JSON 的形式输出，输出的 JSON 需遵守以下的格式：\r\n\r\n{\r\n\tsearch_terms:[\r\n\t  <搜索关键词>,\r\n\t  <搜索关键词>,\r\n\t]\r\n}").ToString()
            });
            if (modelconfg.Temperature >= 0)
            {
                var requestContent = new
                {

                    model = modelconfg.Model,
                    messages = contents,
                    stream = false,
                    temperature = modelconfg.Temperature,

                };
                response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, apiEndpoint)
                {
                    Content = new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json")
                }, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            }
            else
            {
                var requestContent = new
                {

                    model = modelconfg.Model,
                    messages = contents,
                    stream = false,


                };
                response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, apiEndpoint)
                {
                    Content = new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json")
                }, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            }
            if (response.StatusCode != System.Net.HttpStatusCode.OK)
            {
                return await Task.FromResult("错误：StatusCode " + response.StatusCode.ToString());

            }
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);

            while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadToEndAsync(cancellationToken);
                if (string.IsNullOrEmpty(line)) continue;


                var chunk = JsonSerializer.Deserialize<OpenAIResponse>(line);
                var content = chunk?.choices?.FirstOrDefault()?.message?.content;
                if (!string.IsNullOrEmpty(content))
                {

                    int beging = content.IndexOf("```json");
                    if (beging >= 0)
                    {
                        int end = content.IndexOf("```", 7 + beging);
                        if (end > 0 && end > beging)
                        {
                            content = content.Substring(beging + 7, end - beging - 7);
                            //return await Task.FromResult(content);
                        }
                    }
                    return await Task.FromResult(content);
                }

            }
            return await Task.FromResult(string.Empty);
        }
        //OpenAI 提取相关信息
        public async Task<string> OpenAIExtractAsync(ChatModelConfig modelconfg, string searchterm, string info, [EnumeratorCancellation] CancellationToken cancellationToken)
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
            client.Timeout = TimeSpan.FromMinutes(30);
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

            string Search = string.Empty;


            HttpResponseMessage response = null;
            var contents = new List<object>();
            contents.Add(new
            {
                role = "user",
                content = ((new StringBuilder()).Append("请提取 ")
                .Append('\n')
                .Append('[')
                .Append(info)
                .Append(']')
                .Append('\n')
                .Append(" 中与 ")
                .Append('\n')
                .Append('[')
                .Append(searchterm)
                .Append(']')
                 .Append('\n')
                .Append("相关的关键信息，不做其他操作。")
                .ToString())
            });
            if (modelconfg.Temperature >= 0)
            {
                var requestContent = new
                {

                    model = modelconfg.Model,
                    messages = contents,
                    stream = true,
                    temperature = modelconfg.Temperature,

                };
                response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, apiEndpoint)
                {
                    Content = new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json")
                }, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            }
            else
            {
                var requestContent = new
                {

                    model = modelconfg.Model,
                    messages = contents,
                    stream = false,


                };
                response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, apiEndpoint)
                {
                    Content = new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json")
                }, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            if (response.StatusCode != System.Net.HttpStatusCode.OK)
            {
                return await Task.FromResult("错误：StatusCode " + response.StatusCode.ToString());

            }
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);

            while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadToEndAsync(cancellationToken);
                if (string.IsNullOrEmpty(line)) continue;


                var chunk = JsonSerializer.Deserialize<OpenAIResponse>(line);
                var content = chunk?.choices?.FirstOrDefault()?.message?.content;
                if (!string.IsNullOrEmpty(content))
                {

                    return await Task.FromResult(content);
                }

            }
            return await Task.FromResult(string.Empty);
        }
        //OpenAI 深度研究子项目
        public async Task<string> OpenAIDeepResearchSubAsync(ChatModelConfig modelconfg, string searchterm, string info, [EnumeratorCancellation] CancellationToken cancellationToken)
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
            client.Timeout = TimeSpan.FromMinutes(30);
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

            string Search = string.Empty;

            Search = generateSystemPrompt(info, searchterm);
            HttpResponseMessage response = null;
            var contents = new List<object>();
            contents.Add(new
            {
                role = "user",
                content = Search
            });
            if (modelconfg.Temperature >= 0)
            {
                var requestContent = new
                {

                    model = modelconfg.Model,
                    messages = contents,
                    stream = true,
                    temperature = modelconfg.Temperature,

                };
                response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, apiEndpoint)
                {
                    Content = new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json")
                }, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            }
            else
            {
                var requestContent = new
                {

                    model = modelconfg.Model,
                    messages = contents,
                    stream = false,


                };
                response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, apiEndpoint)
                {
                    Content = new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json")
                }, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            if (response.StatusCode != System.Net.HttpStatusCode.OK)
            {
                return await Task.FromResult("错误：StatusCode " + response.StatusCode.ToString());

            }
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);

            while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadToEndAsync(cancellationToken);
                if (string.IsNullOrEmpty(line)) continue;


                var chunk = JsonSerializer.Deserialize<OpenAIResponse>(line);
                var content = chunk?.choices?.FirstOrDefault()?.message?.content;
                if (!string.IsNullOrEmpty(content))
                {

                    return await Task.FromResult(content);
                }

            }
            return await Task.FromResult(string.Empty);
        }
        //OpenAI 深度研究
        public async IAsyncEnumerable<string> OpenAIDeepResearchAsync(ChatModelConfig modelconfg, ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            // 验证配置
            var apiKey = Environment.GetEnvironmentVariable(modelconfg.EnvironmentApikeyName);
            var apiEndpoint = modelconfg.ApiEndpoint;


            if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiEndpoint))
            {
                throw new InvalidOperationException("API配置缺失");
            }

            string Searchterm = await OpenAISearchtermAsync(modelconfg, request.History[request.History.Count - 1].Content, cancellationToken);
            SearchTermsResponse Searchtermlist = null;
            try
            {
                Searchtermlist = JsonSerializer.Deserialize<SearchTermsResponse>(Searchterm);
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "反序列化 Searchterm 失败");
                // 根据需要处理异常，例如返回默认值或重新抛出
            }
            string Search = string.Empty;
            List<JinaSearchResult> Searchlist = new List<JinaSearchResult>();
            JinaSearch js = new JinaSearch(_httpClientFactory);
            //if (Searchtermlist == null || Searchtermlist.SearchTerms.Count == 0)
            //{
            //    var list = await js.JinaAiSearch(request.History[request.History.Count - 1].Content);

            //    Searchlist.Add(list);
            //}
            //else
            //{

            //    for (int i = 0; i < Searchtermlist.SearchTerms.Count; i++)
            //    {
            //        var list = await js.JinaAiSearch(Searchtermlist.SearchTerms[i]);

            //        Searchlist.Add(list);
            //    }
            //}

            Search = JsonSerializer.Serialize<List<JinaSearchResult>>(Searchlist, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });

            Search = generateSystemPrompt(Search, request.History.Last().Content);
            int num1 = CalculateTokens(Search);

            // 创建HTTP客户端
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromMinutes(30);
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");



            HttpResponseMessage response = null;
            if (modelconfg.Temperature >= 0)
            {
                var requestContent = new
                {

                    model = modelconfg.Model,
                    messages = ToMessagesOpenAi(request, modelconfg, Search),
                    stream = modelconfg.Stream,
                    temperature = modelconfg.Temperature,

                };
                response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, apiEndpoint)
                {
                    Content = new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json")
                }, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            }
            else
            {
                var requestContent = new
                {

                    model = modelconfg.Model,
                    messages = ToMessagesOpenAi(request, modelconfg, Search),
                    stream = modelconfg.Stream,


                };
                response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, apiEndpoint)
                {
                    Content = new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json")
                }, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }

            if (response.StatusCode != System.Net.HttpStatusCode.OK)
            {
                yield return "OpenAi 深度研究失败 StatusCode= " + response.StatusCode.ToString();
                yield break;
            }
            response.EnsureSuccessStatusCode();

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var reader = new StreamReader(stream);

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

        //Gemini 获取聊天消息搜索词
        public async Task<string> GeminiSearchtermAsync(ChatModelConfig modelconfg, string searchterm, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            // 验证配置
            var apiKey = Environment.GetEnvironmentVariable(modelconfg.EnvironmentApikeyName);
            var apiEndpoint = modelconfg.ApiEndpoint;
            apiEndpoint = apiEndpoint + @"/models/" + modelconfg.Model;


            if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiEndpoint))
            {
                throw new InvalidOperationException("配置缺失");
            }

            apiEndpoint = apiEndpoint + $":generateContent?key={apiKey}";

            // 创建HTTP客户端
            var client = _httpClientFactory.CreateClient();

            HttpResponseMessage response = null;
            var contents = new List<object>();
            var contentlist = new List<object>();
            contentlist.Add(new
            {
                text = (new StringBuilder()).Append("要深度研究：")
                        .Append('\n')
                        .Append(searchterm)
                        .Append('\n')
                        .Append(" ，需要联网搜索权威可信的资料， 要求只输出精准的搜索词并按重要性排序\r\n以 JSON 的形式输出，输出的 JSON 需遵守以下的格式：\r\n\r\n{\r\n\tsearch_terms:[\r\n\t  <搜索关键词>,\r\n\t  <搜索关键词>,\r\n\t]\r\n}").ToString()
            });

            contents.Add(new
            {
                role = "user",
                parts = contentlist
            });

            if (modelconfg.Temperature >= 0)
            {

                var requestContent = new
                {
                    system_instruction = new
                    {
                        parts = new { text = "" }
                    },
                    contents = contents,
                    generationConfig = new { temperature = modelconfg.Temperature }

                };
                var str = JsonSerializer.Serialize(requestContent, _jsonOptions);
                response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, apiEndpoint)
                {
                    Content = new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json")
                }, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            }
            else
            {
                var requestContent = new
                {
                    system_instruction = new
                    {
                        parts = new { text = "" }
                    },
                    contents = contents,

                };
                var str = JsonSerializer.Serialize(requestContent, _jsonOptions);
                response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, apiEndpoint)
                {
                    Content = new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json")
                }, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            }

            if (response.StatusCode != System.Net.HttpStatusCode.OK)
            {
                return await Task.FromResult("错误：StatusCode " + response.StatusCode.ToString());

            }
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);

            while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
            {



                var line = await reader.ReadToEndAsync(cancellationToken);

                var chunk = JsonSerializer.Deserialize<GeminiChunkResponse>(line);
                var content = chunk?.candidates?.FirstOrDefault()?.content?.parts?.FirstOrDefault()?.text;

                if (!string.IsNullOrEmpty(content))
                {
                    int beging = content.IndexOf("```json");
                    if (beging >= 0)
                    {
                        int end = content.IndexOf("```", 7 + beging);
                        if (end > 0 && end > beging)
                        {
                            content = content.Substring(beging + 7, end - beging - 7);

                        }
                    }
                    return await Task.FromResult(content);


                }


            }
            return await Task.FromResult(string.Empty);
        }
        //Gemini 获取聊天消息搜索词
        public async Task<string> GeminiExtractAsync(ChatModelConfig modelconfg, string searchterm, string info, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            // 验证配置
            var apiKey = Environment.GetEnvironmentVariable(modelconfg.EnvironmentApikeyName);
            var apiEndpoint = modelconfg.ApiEndpoint;
            apiEndpoint = apiEndpoint + @"/models/" + modelconfg.Model;


            if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiEndpoint))
            {
                throw new InvalidOperationException("配置缺失");
            }

            apiEndpoint = apiEndpoint + $":generateContent?key={apiKey}";

            // 创建HTTP客户端
            var client = _httpClientFactory.CreateClient();

            HttpResponseMessage response = null;
            var contents = new List<object>();
            var contentlist = new List<object>();
            contentlist.Add(new
            {
                text = ((new StringBuilder()).Append("请提取 ")
                .Append('\n')
                .Append('[')
                .Append(info)
                .Append(']')
                .Append('\n')
                .Append(" 中与 ")
                .Append('\n')
                .Append('[')
                .Append(searchterm)
                .Append(']')
                 .Append('\n')
                .Append("相关的关键信息，不做其他操作。")
                .ToString())
            });

            contents.Add(new
            {
                role = "user",
                parts = contentlist
            });

            if (modelconfg.Temperature >= 0)
            {

                var requestContent = new
                {
                    system_instruction = new
                    {
                        parts = new { text = "" }
                    },
                    contents = contents,
                    generationConfig = new { temperature = modelconfg.Temperature }

                };
                var str = JsonSerializer.Serialize(requestContent, _jsonOptions);
                response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, apiEndpoint)
                {
                    Content = new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json")
                }, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            }
            else
            {
                var requestContent = new
                {
                    system_instruction = new
                    {
                        parts = new { text = "" }
                    },
                    contents = contents,

                };
                var str = JsonSerializer.Serialize(requestContent, _jsonOptions);
                response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, apiEndpoint)
                {
                    Content = new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json")
                }, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }

            if (response.StatusCode != System.Net.HttpStatusCode.OK)
            {
                return await Task.FromResult("错误：StatusCode " + response.StatusCode.ToString());

            }
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);

            while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadToEndAsync(cancellationToken);
                var chunk = JsonSerializer.Deserialize<GeminiChunkResponse>(line);
                var content = chunk?.candidates?.FirstOrDefault()?.content?.parts?.FirstOrDefault()?.text;

                if (!string.IsNullOrEmpty(content))
                {

                    return await Task.FromResult(content);

                }


            }
            return await Task.FromResult(string.Empty);
        }
        public async Task<string> GeminiDeepResearchSubAsync(ChatModelConfig modelconfg, string searchterm, string info, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            // 验证配置
            var apiKey = Environment.GetEnvironmentVariable(modelconfg.EnvironmentApikeyName);
            var apiEndpoint = modelconfg.ApiEndpoint;
            apiEndpoint = apiEndpoint + @"/models/" + modelconfg.Model;


            if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiEndpoint))
            {
                throw new InvalidOperationException("配置缺失");
            }

            apiEndpoint = apiEndpoint + $":generateContent?key={apiKey}";

            // 创建HTTP客户端
            var client = _httpClientFactory.CreateClient();
            string Search = string.Empty;

            Search = generateSystemPrompt(info, searchterm);

            HttpResponseMessage response = null;
            var contents = new List<object>();
            var contentlist = new List<object>();
            contentlist.Add(new
            {
                text = Search
            });

            contents.Add(new
            {
                role = "user",
                parts = contentlist
            });

            if (modelconfg.Temperature >= 0)
            {

                var requestContent = new
                {
                    system_instruction = new
                    {
                        parts = new { text = "" }
                    },
                    contents = contents,
                    generationConfig = new { temperature = modelconfg.Temperature }

                };
                var str = JsonSerializer.Serialize(requestContent, _jsonOptions);
                response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, apiEndpoint)
                {
                    Content = new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json")
                }, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            }
            else
            {
                var requestContent = new
                {
                    system_instruction = new
                    {
                        parts = new { text = "" }
                    },
                    contents = contents,

                };
                var str = JsonSerializer.Serialize(requestContent, _jsonOptions);
                response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, apiEndpoint)
                {
                    Content = new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json")
                }, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }

            if (response.StatusCode != System.Net.HttpStatusCode.OK)
            {
                return await Task.FromResult("错误：StatusCode " + response.StatusCode.ToString());

            }
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);

            while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadToEndAsync(cancellationToken);
                var chunk = JsonSerializer.Deserialize<GeminiChunkResponse>(line);
                var content = chunk?.candidates?.FirstOrDefault()?.content?.parts?.FirstOrDefault()?.text;

                if (!string.IsNullOrEmpty(content))
                {

                    return await Task.FromResult(content);

                }


            }
            return await Task.FromResult(string.Empty);
        }
        //Gemini 深度研究
        public async IAsyncEnumerable<string> GeminiDeepResearchAsync(ChatModelConfig modelconfg, ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
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

            string Searchterm = await GeminiSearchtermAsync(modelconfg, request.History[request.History.Count - 1].Content, cancellationToken);
            SearchTermsResponse Searchtermlist = null;
            try
            {
                Searchtermlist = JsonSerializer.Deserialize<SearchTermsResponse>(Searchterm);
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "反序列化 Searchterm 失败");
                // 根据需要处理异常，例如返回默认值或重新抛出
            }
            string Search = string.Empty;
            List<JinaSearchResult> Searchlist = new List<JinaSearchResult>();
            JinaSearch js = new JinaSearch(_httpClientFactory);
            //if (Searchtermlist == null || Searchtermlist.SearchTerms.Count == 0)
            //{
            //    var list = await js.JinaAiSearch(request.History[request.History.Count - 1].Content);

            //    Searchlist.Add(list);
            //}
            //else
            //{

            //    for (int i = 0; i < Searchtermlist.SearchTerms.Count; i++)
            //    {
            //        var list = await js.JinaAiSearch(Searchtermlist.SearchTerms[i]);

            //        Searchlist.Add(list);
            //    }
            //}
            StringBuilder sb = new StringBuilder();

            //await foreach (var item in SearchResults(Searchlist, modelconfg, request.History[request.History.Count - 1].Content, sb, cancellationToken))
            //{
            //    yield return item;
            //}
            Search = sb.ToString();
            Search = generateSystemPrompt(Search, request.History.Last().Content);
            int num1 = CalculateTokens(Search);




            // 创建HTTP客户端
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromMinutes(30);

            HttpResponseMessage response = null;
            if (modelconfg.Temperature >= 0)
            {
                var requestContent = new
                {
                    system_instruction = new
                    {
                        parts = new { text = modelconfg.Systemprompt }
                    },

                    contents = ToMessagesGemini(request, modelconfg, Search),
                    generationConfig = new { temperature = modelconfg.Temperature }


                };
                var str = JsonSerializer.Serialize(requestContent, _jsonOptions);
                response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, apiEndpoint)
                {
                    Content = new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json")
                }, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            }
            else
            {
                var requestContent = new
                {
                    system_instruction = new
                    {
                        parts = new { text = modelconfg.Systemprompt }
                    },

                    contents = ToMessagesGemini(request, modelconfg, Search),



                };
                var str = JsonSerializer.Serialize(requestContent, _jsonOptions);
                response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, apiEndpoint)
                {
                    Content = new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json")
                }, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            if (response.StatusCode != System.Net.HttpStatusCode.OK)
            {
                yield return "Gemini 深度研究失败 StatusCode= " + response.StatusCode.ToString();
                yield break;
            }
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
        #endregion

        #region 组装消息
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
        private static List<object> ToMessagesResponsesOpenAi(ChatRequest request, ChatModelConfig modelconfg, string generateSystemPrompt = "")
        {


            var messages = new List<object>();
            if (!string.IsNullOrWhiteSpace(generateSystemPrompt))
            {
                generateSystemPrompt = "\n\n以下是相关资料：\n\n" + generateSystemPrompt;
            }
            // 添加系统提示词
            if (!string.IsNullOrWhiteSpace(modelconfg.Systemprompt) || !string.IsNullOrWhiteSpace(generateSystemPrompt))
            {
                messages.Add(new
                {
                    role = "developer",
                    content = new List<object> {

                        new { type = "input_text", text = modelconfg.Systemprompt+generateSystemPrompt} }

                });
            }
            // 添加历史消息

            foreach (var msg in request.History)
            {
                if (msg.Images?.Any() == true && modelconfg.EnableImageUpload)
                {

                    var contentlist = new List<object>();


                    contentlist.Add(new { type = "input_text", text = (msg.Role == "assistant" ? delstr(msg.Content, "<think>", "</think>") : msg.Content) });

                    foreach (var image in msg.Images)
                    {

                        contentlist.Add(new { type = "input_image", image_url = $"data:image/jpeg;base64,{ConvertUrlToBase64(image)}" });


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
        private static List<object> ToMessagesOpenAi(ChatRequest request, ChatModelConfig modelconfg, string generateSystemPrompt = "")
        {


            var messages = new List<object>();
            if (!string.IsNullOrWhiteSpace(generateSystemPrompt))
            {
                generateSystemPrompt = "\n\n以下是相关资料：\n\n" + generateSystemPrompt;
            }
            // 添加系统提示词
            if (!string.IsNullOrWhiteSpace(modelconfg.Systemprompt) || !string.IsNullOrWhiteSpace(generateSystemPrompt))
            {
                messages.Add(new
                {
                    role = "system",
                    content = new List<object> {

                        new { type = "text", text = modelconfg.Systemprompt+generateSystemPrompt} }

                });
            }
            // 添加历史消息

            foreach (var msg in request.History)
            {
                if (msg.Images?.Any() == true && modelconfg.EnableImageUpload)
                {

                    var contentlist = new List<object>();
                    //if (msg == request.History.Last() && msg.Role == "user" && !string.IsNullOrEmpty(generateSystemPrompt))
                    //{
                    //    contentlist.Add(new { type = "text", text = generateSystemPrompt });
                    //}
                    //else
                    {
                        contentlist.Add(new { type = "text", text = (msg.Role == "assistant" ? delstr(msg.Content, "<think>", "</think>") : msg.Content) });
                    }
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

                    //if (msg == request.History.Last() && msg.Role == "user" && !string.IsNullOrEmpty(generateSystemPrompt))
                    //{
                    //    messages.Add(new
                    //    {
                    //        role = msg.Role,
                    //        content = generateSystemPrompt
                    //    });
                    //}
                    //else
                    {
                        messages.Add(new
                        {
                            role = msg.Role,
                            content = (msg.Role == "assistant" ? delstr(msg.Content, "<think>", "</think>") : msg.Content)
                        });
                    }
                }
            }


            return messages;
        }
        private static List<object> ToMessagesGemini(ChatRequest request, ChatModelConfig modelconfg, string generateSystemPrompt = "")
        {


            var contents = new List<object>();



            foreach (var msg in request.History)
            {
                if (msg.Images?.Any() == true && modelconfg.EnableImageUpload)
                {

                    var contentlist = new List<object>();
                    if (msg == request.History.Last() && msg.Role == "user" && !string.IsNullOrEmpty(generateSystemPrompt))
                    {
                        contentlist.Add(new { text = generateSystemPrompt });
                    }
                    else
                    {
                        contentlist.Add(new { text = (msg.Role == "assistant" ? delstr(msg.Content, "<think>", "</think>") : msg.Content) });
                    }
                    foreach (var image in msg.Images)
                    {

                        contentlist.Add(new { inline_data = new { mime_type = "image/jpeg", data = $"{ConvertUrlToBase64(image)}" } });
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
                    if (msg == request.History.Last() && msg.Role == "user" && !string.IsNullOrEmpty(generateSystemPrompt))
                    {
                        contentlist.Add(new { text = generateSystemPrompt });
                    }
                    else
                    {
                        contentlist.Add(new { text = (msg.Role == "assistant" ? delstr(msg.Content, "<think>", "</think>") : msg.Content) });
                    }
                    //contentlist.Add(new { text = (msg.Role == "assistant" ? delstr(msg.Content, "<think>", "</think>") : msg.Content) });
                    contents.Add(new
                    {
                        role = msg.Role == "assistant" ? "model" : msg.Role,
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
        #endregion



        #region 错误处理
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
        #endregion

        #endregion
        #region tools
        private async Task<string>? JinaAiSearch(string query)
        {
            var result = await _jinaSearch.Search(query);
            return await Task.FromResult(result);
        }

        private async Task<string>? GetWeather(string query)
        {
            var result = await _openWeather.GetWeatherAsync(query);
            return await Task.FromResult(result);
        }

        private async Task<string>? SearchTrainTicket(string startingplace, string arrivalplace, string date)
        {
            var result = await _jinaSearch.SearchTrainTicket(startingplace, arrivalplace, date);
            return await Task.FromResult(result);
        }

        private async Task<string>? GetCurrentDataTime()
        {
            
            var result= DateTime.Now.ToString(" 日期: yyyy年M月dd日 dddd 时间：HH:mm:ss ");
            
            return await Task.FromResult(result);
        }
        #endregion
        #region 图片处理
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

        #endregion
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
        private string generateSystemPrompt(string SystemPrompt, string userPrompt)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(@"You are a research assistant tasked with creating a comprehensive report based on multiple sources.
                        The report should specifically address this request: ");
            sb.Append(userPrompt);
            sb.Append("\n" + "\n");
            sb.Append(@"
                        Your report should:
                        1.Have a clear title that reflects the specific analysis requested

                        2.Begin with a concise executive summary

                        3.Be organized into relevant sections based on the analysis requested

                        4.Use markdown formatting for emphasis, lists, and structure

                        5.Integrate information from sources naturally without explicitly referencing them by number

                        6.Maintain objectivity while addressing the specific aspects requested in the prompt

                        7.Compare and contrast the information from each source, noting areas of consensus or points of contention. 

                        8.Showcase key insights, important data, or innovative ideas.

                        9.Output in Simplified Chinese. If there are formulas in LaTeX format, please wrap them with $ or $$.
                        
                        10.The referenced materials must include a link address and numeric identifiers.


           
                    ");
            sb.Append(SystemPrompt);
            sb.Append("\n" + "\n");
            //sb.Append(@" Format the report as a JSON object with the following structure:");


            //sb.Append("\n" + "\n");

            sb.Append(""""
            
            The content strictly follows the following format:
            -Use * *bold * * for emphasis
            - Use bullet points and numbered lists where appropriate
            - Use headings and subheadings with # syntax
- Include code blocks if relevant
                - Use > for quotations
- Use-- - for horizontal rules where appropriate

Important: Do not use phrases like "Source 1" or "According to Source 2".Instead, integrate the information naturally into the narrative or reference sources by their titles when necessary.

"""");
            //            sb.Append(""""

            //                        {
            //                          "title": "Report title",
            //                          "summary": "Executive summary (can include markdown)",
            //                          "sections": [
            //                            {
            //                              "title": "Section title",
            //                              "content": "Section content with markdown formatting"
            //                            }
            //                          ]
            //                        } 




            //            Use markdown formatting in the content to improve readability:
            //            -Use * *bold * * for emphasis
            //            - Use bullet points and numbered lists where appropriate
            //            - Use headings and subheadings with # syntax
            //- Include code blocks if relevant
            //                - Use > for quotations
            //- Use-- - for horizontal rules where appropriate

            //Important: Do not use phrases like "Source 1" or "According to Source 2".Instead, integrate the information naturally into the narrative or reference sources by their titles when necessary.

            //"""");
            return sb.ToString();
        }

        // ChatService.cs 中添加用户验证方法的实现
        public async Task<bool> ValidateUserIdAsync(string userId)
        {
            if (string.IsNullOrEmpty(userId))
                return false;

            try
            {
                using (NpgsqlConnection connection = new NpgsqlConnection("Host = localhost; Username = postgres; database = cloudserver; port = 5432; password =1234"))
                {
                    await connection.OpenAsync();

                    // 查询用户ID是否存在于数据库中
                    var command = new NpgsqlCommand(
                        "SELECT COUNT(1) FROM users WHERE uid=@user_uid or phone=@user_phone",
                        connection);
                    command.Parameters.AddWithValue("@user_uid", userId);
                    command.Parameters.AddWithValue("@user_phone", userId);
                    var result = await command.ExecuteScalarAsync();
                    return Convert.ToInt32(result) > 0;
                }
            }
            catch (Exception ex)
            {

                return false;
            }
        }

        /// <summary>
        /// 计算文本的大致token数量
        /// </summary>
        public int CalculateTokens(string text)
        {
            if (string.IsNullOrEmpty(text))
                return 0;

            // 简单估算:
            // - 每个英文单词/标点算1个token
            // - 每个中文字符算2个token
            // - 每个数字/标点符号算1个token
            int tokens = 0;
            bool isInWord = false;

            foreach (char c in text)
            {
                if (char.IsLetterOrDigit(c))
                {
                    if (!isInWord)
                    {
                        tokens++;
                        isInWord = true;
                    }
                }
                else if (c >= 0x4E00 && c <= 0x9FFF) // 中文字符范围
                {
                    tokens += 2;
                    isInWord = false;
                }
                else
                {
                    tokens++;
                    isInWord = false;
                }
            }

            return tokens;
        }


        public async Task<byte[]> ExportMessageToDocx(string content)
        {
            //content = Markdig.Markdown.ToPlainText(content);
            using (var ms = new MemoryStream())
            {
                using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
                {
                    var mainPart = doc.AddMainDocumentPart();
                    mainPart.Document = new Document();
                    var body = mainPart.Document.AppendChild(new Body());

                    // 添加标题样式
                    var titleStyle = new Style
                    {
                        StyleId = "Title",
                        Type = StyleValues.Paragraph
                    };
                    titleStyle.Append(new StyleName { Val = "Title" });
                    titleStyle.Append(new PrimaryStyle());

                    var docStyles = mainPart.StyleDefinitionsPart ?? mainPart.AddNewPart<StyleDefinitionsPart>();
                    docStyles.Styles = new Styles();
                    docStyles.Styles.Append(titleStyle);

                    // 添加标题
                    var titlePara = body.AppendChild(new Paragraph(
                        new ParagraphProperties(
                            new ParagraphStyleId { Val = "Title" }
                        ),
                        new Run(
                            new RunProperties(
                                new RunFonts { Ascii = "微软雅黑", EastAsia = "微软雅黑" },
                                new FontSize { Val = "32" },
                                new Bold()
                            ),
                            new Text("聊天记录")
                        )
                    ));

                    // 添加导出时间
                    var timePara = body.AppendChild(new Paragraph(
                        new Run(
                            new RunProperties(
                                new RunFonts { Ascii = "微软雅黑", EastAsia = "微软雅黑" },
                                new FontSize { Val = "24" }
                            ),
                            new Text($"导出时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}")
                        )
                    ));

                    // 按行处理内容
                    var lines = content.Split('\n');
                    foreach (var line in lines)
                    {
                        var para = body.AppendChild(new Paragraph());
                        var run = new Run();
                        var runProps = new RunProperties(
                            new RunFonts { Ascii = "微软雅黑", EastAsia = "微软雅黑" },
                            new FontSize { Val = "24" }
                        );

                        // 处理代码块
                        if (line.StartsWith("```"))
                        {
                            runProps = new RunProperties(
                                new RunFonts { Ascii = "Consolas", EastAsia = "Consolas" },
                                new FontSize { Val = "20" },
                                new Color { Val = "666666" }
                            );
                        }
                        // 处理标题
                        else if (line.StartsWith("#"))
                        {
                            runProps.Append(new Bold());
                            runProps.Append(new FontSize { Val = "28" });
                        }
                        // 处理粗体
                        else if (line.Contains("**"))
                        {
                            runProps.Append(new Bold());
                        }
                        // 处理斜体
                        else if (line.Contains("*"))
                        {
                            runProps.Append(new Italic());
                        }

                        run.Append(runProps);
                        run.Append(new Text(line));
                        para.Append(run);
                    }

                    doc.Save();
                }
                return ms.ToArray();
            }
        }


        public async Task<byte[]> ExportMessageToPdf(string content)
        {
            try
            {
                //content=Markdig.Markdown.ToPlainText(content);
                // 注册编码和字体
                System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
                if (GlobalFontSettings.FontResolver == null)
                {
                    GlobalFontSettings.FontResolver = new CustomFontResolver();
                }

                using (var ms = new MemoryStream())
                {
                    using (var document = new PdfDocument())
                    {
                        var page = document.AddPage();
                        var gfx = XGraphics.FromPdfPage(page);

                        // 尝试使用不同的中文字体
                        XFont titleFont, normalFont, codeFont;
                        try
                        {
                            titleFont = new XFont("SimHei", 16, XFontStyleEx.Regular);
                            normalFont = new XFont("KaiU", 12, XFontStyleEx.Regular);
                            codeFont = new XFont("SimHei", 11, XFontStyleEx.Regular);
                        }
                        catch
                        {
                            // 如果上述字体不可用，尝试其他字体
                            var defaultFont = GlobalFontSettings.FontResolver.ResolveTypeface(null, false, false).FaceName;
                            titleFont = new XFont(defaultFont, 16, XFontStyleEx.Regular);
                            normalFont = new XFont(defaultFont, 12, XFontStyleEx.Regular);
                            codeFont = new XFont(defaultFont, 11, XFontStyleEx.Regular);
                        }

                        // 绘制标题
                        gfx.DrawString("聊天记录", titleFont, XBrushes.Black, 50, 50);

                        // 绘制时间
                        gfx.DrawString($"导出时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                            normalFont, XBrushes.Black, 50, 80);

                        // 绘制分隔线
                        gfx.DrawLine(XPens.Gray, 50, 100, 550, 100);

                        // 处理内容
                        var yPosition = 120.0;
                        var lines = content.Split('\n');
                        var maxWidth = page.Width - 100;

                        foreach (var line in lines)
                        {
                            if (yPosition > page.Height - 50)
                            {
                                page = document.AddPage();
                                gfx = XGraphics.FromPdfPage(page);
                                yPosition = 50;
                            }

                            var font = line.StartsWith("```") ? codeFont : normalFont;
                            var size = gfx.MeasureString(line, font);

                            // 处理长行自动换行
                            if (size.Width > maxWidth)
                            {
                                var words = line.ToCharArray();
                                var currentLine = new StringBuilder();

                                foreach (var c in words)
                                {
                                    currentLine.Append(c);
                                    var testSize = gfx.MeasureString(currentLine.ToString(), font);

                                    if (testSize.Width >= maxWidth)
                                    {
                                        gfx.DrawString(currentLine.ToString(), font, XBrushes.Black,
                                            new XRect(50, yPosition, maxWidth, font.Height),
                                            XStringFormats.TopLeft);
                                        yPosition += font.Height + 2;
                                        currentLine.Clear();
                                    }
                                }

                                if (currentLine.Length > 0)
                                {
                                    gfx.DrawString(currentLine.ToString(), font, XBrushes.Black,
                                        new XRect(50, yPosition, maxWidth, font.Height),
                                        XStringFormats.TopLeft);
                                }
                            }
                            else
                            {
                                gfx.DrawString(line, font, XBrushes.Black,
                                    new XRect(50, yPosition, maxWidth, font.Height),
                                    XStringFormats.TopLeft);
                            }

                            yPosition += font.Height + 5;
                        }

                        document.Save(ms);
                    }
                    return ms.ToArray();
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"PDF生成失败: {ex.Message}", ex);
            }
        }
    }

    public class CustomFontResolver : IFontResolver
    {
        private static readonly Dictionary<string, string> FontFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        {"kaiu", "kaiu.ttf"},        // 标楷体
        {"simhei", "simhei.ttf"},    // 黑体
        {"msyh", "msyh.ttf"},        // 微软雅黑
        {"simsun", "simsun.ttc"},    // 宋体
        {"simkai", "simkai.ttf"}     // 楷体
    };

        private static readonly Dictionary<string, byte[]> FontData = new Dictionary<string, byte[]>();

        static CustomFontResolver()
        {
            var fontPath = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            foreach (var font in FontFiles)
            {
                var path = Path.Combine(fontPath, font.Value);
                if (File.Exists(path))
                {
                    FontData[font.Key] = File.ReadAllBytes(path);
                }
            }

            if (FontData.Count == 0)
            {
                throw new FileNotFoundException("未找到任何可用的中文字体");
            }
        }

        public byte[] GetFont(string faceName)
        {
            string key = faceName.ToLower();
            if (FontData.ContainsKey(key))
            {
                return FontData[key];
            }
            // 如果请求的字体不存在，返回第一个可用的字体
            return FontData.First().Value;
        }

        public FontResolverInfo ResolveTypeface(string familyName, bool isBold, bool isItalic)
        {
            // 依次尝试可用的字体
            foreach (var font in FontFiles.Keys)
            {
                if (FontData.ContainsKey(font))
                {
                    return new FontResolverInfo(font);
                }
            }
            throw new Exception("没有可用的中文字体");
        }
    }
}