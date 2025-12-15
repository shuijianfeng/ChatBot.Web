using ChatBot.Models;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Office2013.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using iText.Commons.Utils;
using iText.Html2pdf;
using iText.Layout.Font;
using Markdig;
using Microsoft.Extensions.Options;
using Npgsql;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;
using System.Data;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Color = DocumentFormat.OpenXml.Wordprocessing.Color;
using HtmlConverter = HtmlToOpenXml.HtmlConverter;



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
        public string CreateJavaScriptCommand(string functionName, params object[] args);
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
        private readonly CtripSearch _ctripSearch;
        private readonly IMcpClientManager _mcpClientManager;

        public IHttpClientFactory HttpClientFactory => _httpClientFactory;

        public ChatService(
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            ILogger<ChatService> logger,
            IOptions<ChatModelSettings> modelOptions,
            IMcpClientManager mcpClientManager)
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
            _ctripSearch = new CtripSearch(_httpClientFactory, _logger);
            _mcpClientManager = mcpClientManager;

            // 初始化 MCP 客户端（异步启动）
            _ = _mcpClientManager.InitializeAsync();
        }

        #region 搜索相关
        // 在 ChatService 类中实现
        public string CreateJavaScriptCommand(string functionName, params object[] args)
        {
            // 创建一个特殊格式的命令字符串，前端可以识别并执行
            var command = new
            {
                type = "js_command",
                function = functionName,
                arguments = args
            };

            return $"<js_command>{JsonSerializer.Serialize(command)}</js_command>";
        }


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
                    //case ChatModelType.DeepSeek:
                    //    {
                    //        await foreach (var item in DeepseekOpenAIAsync(config, request, cancellationToken))
                    //        {
                    //            yield return item;
                    //        }
                    //        break;
                    //    }
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
                    case ChatModelType.GeminiFileSearch:
                        {
                            await foreach (var item in GeminiFileSearchAsync(config, request, cancellationToken))
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

        /// <summary>
        /// OpenAI Responses API 流式响应处理方法
        /// 支持完整的 SSE 事件类型，包括:
        /// - response.created/in_progress/completed/failed: 响应生命周期事件
        /// - response.output_item.added/done: 输出项事件
        /// - response.output_text.delta/done: 文本输出事件
        /// - response.reasoning_summary_part.added/done: 推理摘要部分事件
        /// - response.reasoning_summary_text.delta/done: 推理摘要文本事件  
        /// - response.function_call_arguments.delta/done: 函数调用事件
        /// - response.web_search_call.searching/completed: Web搜索事件
        /// - response.file_search_call.searching/completed: 文件搜索事件
        /// </summary>
        public async IAsyncEnumerable<string> OpenAIResponsesAsync(
            ChatModelConfig modelconfg,
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken,
            HttpClient inputclient = null,
            List<object> toolsmessages = null)
        {
            // 验证API配置
            var apiKey = Environment.GetEnvironmentVariable(modelconfg.EnvironmentApikeyName);
            var apiEndpoint = modelconfg.ApiEndpoint;

            if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiEndpoint))
            {
                throw new InvalidOperationException("API配置缺失");
            }

            // 创建或复用HTTP客户端
            HttpClient client = inputclient ?? _httpClientFactory.CreateClient();
            if (inputclient == null)
            {
                client.Timeout = TimeSpan.FromMinutes(30);
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
            }

            // 准备消息列表
            var messages = ToMessagesResponsesOpenAi(request, modelconfg);
            toolsmessages ??= new List<object>();
            messages.AddRange(toolsmessages);

            // 准备工具列表 (如果启用搜索)
            List<object>? tools = await PrepareOpenAiResponsesToolsAsync(request.EnableSearch, cancellationToken);

            // 构建请求内容
            var requestContent = new
            {
                model = modelconfg.Model,
                input = messages,
                stream = modelconfg.Stream,
                temperature = modelconfg.Temperature >= 0 ? (float?)modelconfg.Temperature : null,
                reasoning = OpenAiThinkingLevel(modelconfg),
                tools = tools,
            };

            var str = JsonSerializer.Serialize(requestContent, _jsonOptions);

            using (var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, modelconfg.ApiEndpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json")
            }, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                // 检查HTTP响应状态
                if (response.StatusCode != System.Net.HttpStatusCode.OK)
                {
                    // 尝试读取错误详情
                    var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                    yield return $"失败: StatusCode {response.StatusCode}\n{errorContent}";
                    yield break;
                }
                response.EnsureSuccessStatusCode();

                using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var reader = new StreamReader(stream);

                // 状态变量
                bool isReasoningStarted = false;      // 推理摘要是否已开始
                bool isReasoningEnded = false;        // 推理摘要是否已结束
                bool isThinkTagStarted = false;       // <think>标签是否已开始 (对于某些模型)
                bool isThinkTagEnded = false;         // </think>标签是否已结束
                bool isWebSearching = false;          // 是否正在执行Web搜索
                bool isFileSearching = false;         // 是否正在执行文件搜索

                List<tool_callnew> tool_calls = new();      // 工具调用列表
                List<object> reasoning_items = new();        // 推理项列表 (用于关联 function_call)
                var contentBuilder = new StringBuilder();    // 内容构建器

                while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
                {
                    if (modelconfg.Stream)
                    {
                        // 流式模式：处理SSE事件
                        var line = await reader.ReadLineAsync(cancellationToken);
                        if (string.IsNullOrEmpty(line)) continue;

                        // 处理SSE格式 "data: {...}"
                        if (line.StartsWith("data: "))
                        {
                            line = line.Substring(6);

                            // 尝试解析JSON
                            OpenAIChunkResponsenew chunk;
                            try
                            {
                                chunk = JsonSerializer.Deserialize<OpenAIChunkResponsenew>(line);
                            }
                            catch (JsonException)
                            {
                                // JSON解析失败，跳过此行
                                continue;
                            }

                            if (chunk == null) continue;

                            // 根据事件类型分发处理
                            switch (chunk.type)
                            {
                                // ========== 响应生命周期事件 ==========
                                case "response.created":
                                    // 响应创建，可用于初始化
                                    break;

                                case "response.in_progress":
                                    // 响应进行中
                                    break;

                                case "response.completed":
                                    // 响应完成
                                    break;

                                case "response.failed":
                                    // 响应失败
                                    yield return "\n\n⚠️ **响应失败**";
                                    yield break;

                                // ========== 推理摘要事件 (OpenAI o1等推理模型) ==========
                                case "response.reasoning_summary_part.added":
                                    // 推理摘要部分开始
                                    if (!isReasoningStarted)
                                    {
                                        yield return "<think>\n\n~~~Thoughts\n\n";
                                        isReasoningStarted = true;
                                    }
                                    break;

                                case "response.reasoning_summary_text.delta":
                                    // 推理摘要文本增量
                                    if (!string.IsNullOrEmpty(chunk.delta))
                                    {
                                        if (!isReasoningStarted)
                                        {
                                            yield return "<think>\n\n~~~Thoughts\n\n";
                                            isReasoningStarted = true;
                                        }
                                        yield return chunk.delta;
                                    }
                                    break;

                                case "response.reasoning_summary_text.done":
                                    // 推理摘要文本完成
                                    break;

                                case "response.reasoning_summary_part.done":
                                    // 推理摘要部分完成
                                    if (isReasoningStarted && !isReasoningEnded)
                                    {
                                        yield return "\n\n~~~\n\n</think>\n\n";
                                        isReasoningEnded = true;
                                    }
                                    break;

                                // ========== 输出项事件 ==========
                                case "response.output_item.added":
                                    // 输出项添加 (可能是文本、函数调用、推理等)
                                    if (chunk?.item?.ValueKind == JsonValueKind.Object)
                                    {
                                        var itemElement = chunk.item.Value;
                                        if (itemElement.TryGetProperty("type", out var typeProperty))
                                        {
                                            var itemType = typeProperty.GetString();
                                            if (itemType == "function_call")
                                            {
                                                // 将 JsonElement 反序列化为 tool_callnew
                                                var toolCall = JsonSerializer.Deserialize<tool_callnew>(itemElement.GetRawText(), _jsonOptions);
                                                if (toolCall != null)
                                                {
                                                    tool_calls.Add(toolCall);
                                                }
                                            }
                                            else if (itemType == "reasoning")
                                            {
                                                // 保存原始 JsonElement 以保留完整结构
                                                // OpenAI 推理模型 (o1/o3等) 要求 function_call 必须包含其关联的 reasoning 项
                                                // 需要使用 Clone() 因为原始 JsonElement 是 stream 的一部分，可能会被释放
                                                reasoning_items.Add(JsonSerializer.Deserialize<object>(itemElement.GetRawText(), _jsonOptions)!);
                                            }
                                        }
                                    }
                                    break;

                                // ========== 内容部分事件 ==========
                                case "response.content_part.added":
                                    // 内容部分添加
                                    break;

                                case "response.content_part.done":
                                    // 内容部分完成
                                    break;

                                // ========== 文本输出事件 ==========
                                case "response.output_text.delta":
                                    {
                                        var content = chunk?.delta;
                                        if (!string.IsNullOrEmpty(content))
                                        {
                                            // 处理连续引用标记，添加空格分隔
                                            content = Regex.Replace(content, @"(\[\^?\d+\])(?=\[\^?\d+\])", "$1 ");
                                            contentBuilder.Append(content);
                                        }

                                        if (!string.IsNullOrEmpty(content))
                                        {
                                            // 如果推理刚结束，先输出推理结束标记
                                            if (isReasoningStarted && !isReasoningEnded)
                                            {
                                                yield return "\n\n~~~\n\n</think>\n\n" + content;
                                                isReasoningEnded = true;
                                            }
                                            // 处理某些模型的 <think> 标签
                                            else if (content.Contains("<think>") && !isThinkTagStarted && !isThinkTagEnded)
                                            {
                                                yield return content.Replace("<think>", "<think>\n\n~~~Thoughts\n\n");
                                                isThinkTagStarted = true;
                                            }
                                            else if (content.Contains("</think>") && isThinkTagStarted && !isThinkTagEnded)
                                            {
                                                yield return content.Replace("</think>", "\n\n~~~\n\n</think>\n\n");
                                                isThinkTagEnded = true;
                                            }
                                            else
                                            {
                                                yield return content;
                                            }
                                        }
                                        break;
                                    }

                                case "response.output_text.done":
                                    // 文本输出完成
                                    break;

                                // ========== 函数调用事件 ==========
                                case "response.function_call_arguments.delta":
                                    // 函数调用参数增量
                                    if (chunk?.output_index - 1 >= 0 && chunk?.output_index - 1 < tool_calls.Count)
                                    {
                                        tool_calls[(int)chunk.output_index - 1].arguments += chunk.delta ?? string.Empty;
                                    }
                                    break;

                                case "response.function_call_arguments.done":
                                    // 函数调用参数完成
                                    break;

                                // ========== Web搜索事件 ==========
                                case "response.web_search_call.searching":
                                    // Web搜索开始
                                    if (!isWebSearching)
                                    {
                                        yield return "\n\n🔍 *正在搜索网络...*\n\n";
                                        isWebSearching = true;
                                    }
                                    break;

                                case "response.web_search_call.completed":
                                    // Web搜索完成
                                    if (isWebSearching)
                                    {
                                        yield return "\n\n✅ *搜索完成*\n\n";
                                        isWebSearching = false;
                                    }
                                    break;

                                case "response.web_search_call.failed":
                                    // Web搜索失败
                                    yield return "\n\n❌ *搜索失败*\n\n";
                                    isWebSearching = false;
                                    break;

                                // ========== 文件搜索事件 ==========
                                case "response.file_search_call.searching":
                                    // 文件搜索开始
                                    if (!isFileSearching)
                                    {
                                        yield return "\n\n📁 *正在搜索文件...*\n\n";
                                        isFileSearching = true;
                                    }
                                    break;

                                case "response.file_search_call.completed":
                                    // 文件搜索完成
                                    if (isFileSearching)
                                    {
                                        yield return "\n\n✅ *文件搜索完成*\n\n";
                                        isFileSearching = false;
                                    }
                                    break;

                                // ========== 输出项完成事件 ==========
                                case "response.output_item.done":
                                    {
                                        // 处理函数调用完成
                                        if (tool_calls.Count > 0)
                                        {
                                            // 先添加 reasoning 项到消息列表
                                            // OpenAI 推理模型 (o1/o3等) 要求 function_call 必须包含其关联的 reasoning 项
                                            foreach (var reasoningItem in reasoning_items)
                                            {
                                                toolsmessages.Add(reasoningItem);
                                            }
                                            reasoning_items.Clear();

                                            foreach (var pair in tool_calls)
                                            {
                                                string toolResult = string.Empty;
                                                switch (pair.name)
                                                {
                                                    case nameof(GetCurrentDataTime):
                                                        {
                                                            // 该函数不需要参数
                                                            toolResult = await GetCurrentDataTime();
                                                            break;
                                                        }
                                                    case nameof(JinaAiSearch):
                                                        {
                                                            if (string.IsNullOrWhiteSpace(pair.arguments) || pair.arguments == "{}")
                                                            {
                                                                toolResult = "错误：搜索查询参数不能为空";
                                                                break;
                                                            }
                                                            using (JsonDocument argumentsJson = JsonDocument.Parse(pair.arguments))
                                                            {
                                                                if (!argumentsJson.RootElement.TryGetProperty("query", out JsonElement outquery))
                                                                {
                                                                    toolResult = "错误：缺少 query 参数";
                                                                    break;
                                                                }
                                                                var queryStr = outquery.GetString();
                                                                if (string.IsNullOrEmpty(queryStr))
                                                                {
                                                                    toolResult = "错误：query 参数不能为空";
                                                                    break;
                                                                }
                                                                toolResult = await JinaAiSearch(queryStr);
                                                            }
                                                            break;
                                                        }
                                                    case nameof(SearchTrainTicket):
                                                        {
                                                            if (string.IsNullOrWhiteSpace(pair.arguments) || pair.arguments == "{}")
                                                            {
                                                                toolResult = "错误：搜索火车票参数不能为空";
                                                                break;
                                                            }
                                                            using (JsonDocument argumentsJson = JsonDocument.Parse(pair.arguments))
                                                            {
                                                                argumentsJson.RootElement.TryGetProperty("startingplace", out JsonElement startingplace);
                                                                argumentsJson.RootElement.TryGetProperty("arrivalplace", out JsonElement arrivalplace);
                                                                if (!argumentsJson.RootElement.TryGetProperty("date", out JsonElement date))
                                                                {
                                                                    toolResult = "错误：缺少 date 参数";
                                                                    break;
                                                                }
                                                                toolResult = await SearchTrainTicket(startingplace.GetString(), arrivalplace.GetString(), date.GetString());
                                                            }
                                                            break;
                                                        }
                                                    case nameof(GetWeather):
                                                        {
                                                            if (string.IsNullOrWhiteSpace(pair.arguments) || pair.arguments == "{}")
                                                            {
                                                                toolResult = "错误：天气查询参数不能为空";
                                                                break;
                                                            }
                                                            using (JsonDocument argumentsJson = JsonDocument.Parse(pair.arguments))
                                                            {
                                                                if (!argumentsJson.RootElement.TryGetProperty("city", out JsonElement outquery))
                                                                {
                                                                    toolResult = "错误：缺少 city 参数";
                                                                    break;
                                                                }
                                                                var cityStr = outquery.GetString();
                                                                if (string.IsNullOrEmpty(cityStr))
                                                                {
                                                                    toolResult = "错误：city 参数不能为空";
                                                                    break;
                                                                }
                                                                toolResult = await GetWeather(cityStr);
                                                            }
                                                            break;
                                                        }
                                                    default:
                                                        {
                                                            // 尝试调用 MCP 工具
                                                            if (_mcpClientManager.IsEnabled && _mcpClientManager.IsMcpTool(pair.name))
                                                            {
                                                                toolResult = await _mcpClientManager.CallToolAsync(pair.name, pair.arguments ?? "{}", cancellationToken);
                                                            }
                                                            else
                                                            {
                                                                toolResult = $"未知工具调用: {pair.name}";
                                                            }
                                                            break;
                                                        }
                                                }

                                                // 添加工具调用和结果到消息列表
                                                // 注意: OpenAI 推理模型要求 function_call 必须包含其关联的 reasoning 项
                                                toolsmessages.Add(pair);
                                                toolsmessages.Add(new
                                                {
                                                    type = "function_call_output",
                                                    call_id = pair.call_id,
                                                    output = toolResult
                                                });
                                            }

                                            // 清理状态，递归调用以继续对话
                                            contentBuilder.Clear();
                                            tool_calls.Clear();
                                            response.Content.Dispose();
                                            await foreach (var item in OpenAIResponsesAsync(modelconfg, request, cancellationToken, client, toolsmessages))
                                            {
                                                yield return item;
                                            }
                                            yield break;
                                        }
                                        break;
                                    }

                                // ========== 其他事件 ==========
                                default:
                                    // 未知事件类型，记录日志（可选）
                                    // _logger.LogDebug("Unknown event type: {EventType}", chunk.type);
                                    break;
                            }
                        }
                    }
                    else
                    {
                        // 非流式模式：一次性读取完整响应
                        var line = await reader.ReadToEndAsync(cancellationToken);
                        if (string.IsNullOrEmpty(line)) continue;

                        var chunk = JsonSerializer.Deserialize<OpenAIResponsenew>(line);

                        var output = chunk?.output;
                        if (output == null || output.Length == 0) continue;

                        foreach (var item in output)
                        {
                            if (item.type == "function_call")
                            {
                                // 处理函数调用类型
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
                                            // 该函数不需要参数
                                            toolResult = await GetCurrentDataTime();
                                            break;
                                        }
                                    case nameof(SearchTrainTicket):
                                        {
                                            if (string.IsNullOrWhiteSpace(item.arguments) || item.arguments == "{}")
                                            {
                                                toolResult = "错误：搜索火车票参数不能为空";
                                                break;
                                            }
                                            using (JsonDocument argumentsJson = JsonDocument.Parse(item.arguments))
                                            {
                                                argumentsJson.RootElement.TryGetProperty("startingplace", out JsonElement startingplace);
                                                argumentsJson.RootElement.TryGetProperty("arrivalplace", out JsonElement arrivalplace);
                                                if (!argumentsJson.RootElement.TryGetProperty("date", out JsonElement date))
                                                {
                                                    toolResult = "错误：缺少 date 参数";
                                                    break;
                                                }
                                                toolResult = await SearchTrainTicket(startingplace.GetString(), arrivalplace.GetString(), date.GetString());
                                            }
                                            break;
                                        }
                                    case nameof(JinaAiSearch):
                                        {
                                            if (string.IsNullOrWhiteSpace(item.arguments) || item.arguments == "{}")
                                            {
                                                toolResult = "错误：搜索查询参数不能为空";
                                                break;
                                            }
                                            using (JsonDocument argumentsJson = JsonDocument.Parse(item.arguments))
                                            {
                                                if (!argumentsJson.RootElement.TryGetProperty("query", out JsonElement outquery))
                                                {
                                                    toolResult = "错误：缺少 query 参数";
                                                    break;
                                                }
                                                var queryStr = outquery.GetString();
                                                if (string.IsNullOrEmpty(queryStr))
                                                {
                                                    toolResult = "错误：query 参数不能为空";
                                                    break;
                                                }
                                                toolResult = await JinaAiSearch(queryStr);
                                            }
                                            break;
                                        }
                                    case nameof(GetWeather):
                                        {
                                            if (string.IsNullOrWhiteSpace(item.arguments) || item.arguments == "{}")
                                            {
                                                toolResult = "错误：天气查询参数不能为空";
                                                break;
                                            }
                                            using (JsonDocument argumentsJson = JsonDocument.Parse(item.arguments))
                                            {
                                                if (!argumentsJson.RootElement.TryGetProperty("city", out JsonElement outquery))
                                                {
                                                    toolResult = "错误：缺少 city 参数";
                                                    break;
                                                }
                                                var cityStr = outquery.GetString();
                                                if (string.IsNullOrEmpty(cityStr))
                                                {
                                                    toolResult = "错误：city 参数不能为空";
                                                    break;
                                                }
                                                toolResult = await GetWeather(cityStr);
                                            }
                                            break;
                                        }
                                    default:
                                        {
                                            // 尝试调用 MCP 工具
                                            if (_mcpClientManager.IsEnabled && _mcpClientManager.IsMcpTool(item.name))
                                            {
                                                toolResult = await _mcpClientManager.CallToolAsync(item.name, item.arguments ?? "{}", cancellationToken);
                                            }
                                            else
                                            {
                                                toolResult = $"未知工具调用: {item.name}";
                                            }
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
                                // 处理普通文本内容
                                var content1 = item?.content?.FirstOrDefault()?.text;
                                if (!string.IsNullOrEmpty(content1))
                                {
                                    content1 = Regex.Replace(content1, @"(\[\^?\d+\])(?=\[\^?\d+\])", "$1 ");
                                    contentBuilder.Append(content1);
                                }
                            }
                        }

                        // 输出内容
                        var content = contentBuilder.ToString();
                        if (!string.IsNullOrEmpty(content))
                        {
                            // 处理 <think> 标签
                            content = content.Replace("<think>", "<think>\n\n~~~Thoughts\n\n");
                            content = content.Replace("</think>", "\n\n~~~\n\n</think>\n\n");
                            yield return content;
                        }

                        // 如果有工具调用，递归处理
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
            List<object> tools = await PrepareClaudeTools(request.EnableSearch, cancellationToken);


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
                string textsignature = string.Empty;
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
                                            yield return "\n" + "\n" + "~~~" + "\n" + "\n" + "</think>" + "\n" + "\n" + Regex.Replace(chunk.delta.text, @"(\[\^?\d+\])(?=\[\^?\d+\])", "$1 ");
                                            end = true;
                                        }
                                        else
                                        {
                                            yield return Regex.Replace(chunk.delta.text, @"(\[\d+\])(?=\[\d+\])", "$1 ");
                                        }
                                    }
                                    if (chunk.delta.type == "thinking_delta")
                                    {
                                        textthinking += chunk.delta.thinking;
                                        if (!beging)
                                        {
                                            yield return "<think>" + "\n" + "\n" + "~~~Thoughts" + "\n" + "\n" + chunk.delta.thinking;
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
                                                            input = new { startingplace = startingplace.GetString(), arrivalplace = arrivalplace.GetString(), date = date.GetString() }
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
                                                        // 尝试调用 MCP 工具
                                                        if (_mcpClientManager.IsEnabled && _mcpClientManager.IsMcpTool(pair.name))
                                                        {
                                                            // 处理 input：将 partial_json 反序列化为 object
                                                            object inputValue = !string.IsNullOrEmpty(pair.partial_json)
                                                                ? JsonSerializer.Deserialize<object>(pair.partial_json, _jsonOptions)
                                                                : new { };

                                                            content.Add(new
                                                            {
                                                                type = "tool_use",
                                                                id = pair.id,
                                                                name = pair.name,
                                                                input = inputValue
                                                            });
                                                            toolsmessages.Add(new
                                                            {
                                                                role = "assistant",
                                                                content = content


                                                            });
                                                            toolResult = await _mcpClientManager.CallToolAsync(pair.name, pair.partial_json ?? "{}", cancellationToken);
                                                        }
                                                        else
                                                        {
                                                            yield return "未知工具调用";
                                                        }
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
                                            textthinking = item.thinking ?? string.Empty;
                                            textsignature = item.signature ?? string.Empty;

                                            var textthinking1 = "<think>" + textthinking + "</think>";
                                            textthinking1 = textthinking1.Replace("<think>", "<think>" + "\n" + "\n" + "~~~Thoughts" + "\n" + "\n");
                                            textthinking1 = textthinking1.Replace("</think>", "\n" + "\n" + "~~~" + "\n" + "\n" + "</think>" + "\n" + "\n");
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
                                            text += item.text ?? string.Empty;
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
                                            // 尝试调用 MCP 工具
                                            if (_mcpClientManager.IsEnabled && _mcpClientManager.IsMcpTool(pair.name))
                                            {
                                                // 处理 input：如果是 JsonElement 则反序列化为 object，否则直接使用
                                                object inputValue = pair.input is JsonElement jsonElement
                                                    ? JsonSerializer.Deserialize<object>(jsonElement.GetRawText(), _jsonOptions)
                                                    : pair.input;

                                                content1.Add(new
                                                {
                                                    type = "tool_use",
                                                    id = pair.id,
                                                    name = pair.name,
                                                    input = inputValue
                                                });
                                                toolsmessages.Add(new
                                                {
                                                    role = "assistant",
                                                    content = content1


                                                });
                                                text = string.Empty;

                                                // 获取正确的 JSON 字符串用于 MCP 调用
                                                string inputJson = pair.input is JsonElement je
                                                    ? je.GetRawText()
                                                    : JsonSerializer.Serialize(pair.input, _jsonOptions);
                                                toolResult = await _mcpClientManager.CallToolAsync(pair.name, inputJson ?? "{}", cancellationToken);
                                            }
                                            else
                                            {
                                                yield return "未知工具调用";
                                            }
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
        public async IAsyncEnumerable<string> GeminiAsync(ChatModelConfig modelconfg, ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken, HttpClient inputclient = null, List<object> toolsmessages = null)
        {
            // 验证配置AQ.Ab8RN6LQoXO75Ty1A9x4EEogc0XS97bVZPZwz-ytddNBxMvvrg
            var apiKey = Environment.GetEnvironmentVariable(modelconfg.EnvironmentApikeyName);
            //var apiKey = "AQ.Ab8RN6LQoXO75Ty1A9x4EEogc0XS97bVZPZwz-ytddNBxMvvrg";
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
            HttpClient client = inputclient ?? _httpClientFactory.CreateClient();

            var messages = ToMessagesGemini(request, modelconfg);
            toolsmessages ??= new List<object>();
            messages.AddRange(toolsmessages);

            // 准备工具定义
            List<object> tools = await PrepareGeminiTools(request.EnableSearch, cancellationToken);

            // 获取思考配置
            var thinkingConfig = GeminiThinkingConfig(modelconfg);

            HttpResponseMessage response = null;
            if (modelconfg.Temperature >= 0)
            {
                var requestContent = new
                {
                    system_instruction = new
                    {
                        parts = new { text = modelconfg.Systemprompt }
                    },
                    contents = messages,
                    generationConfig = new { temperature = modelconfg.Temperature, thinkingConfig = thinkingConfig },
                    tools = tools
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
                    system_instruction = new
                    {
                        parts = new { text = modelconfg.Systemprompt }
                    },
                    contents = messages,
                    generationConfig = thinkingConfig != null ? new { thinkingConfig = thinkingConfig } : null,
                    tools = tools
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
            var contentBuilder = new StringBuilder();
            //List<GeminiFunctionCall> functionCalls = new();
            List<GeminiToolCall> tool_calls = new();
            bool isThinkingStarted = false;  // 思考内容是否已开始
            bool isThinkingEnded = false;    // 思考内容是否已结束
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
                        var candidate = chunk?.candidates?.FirstOrDefault();

                        if (candidate?.content?.parts != null)
                        {
                            foreach (var part in candidate.content.parts)
                            {

                                if (part.functionCall != null)
                                {
                                    if (!string.IsNullOrEmpty(part.functionCall.name))
                                    {
                                        tool_calls.Add(part.functionCall);
                                    }

                                    //continue;
                                }

                                // 处理文本内容
                                if (!string.IsNullOrEmpty(part.text))
                                {
                                    // 检查是否是思考内容 (Gemini 思考模型返回 thought=true)
                                    if (part.thought)
                                    {
                                        // 思考内容开始标记
                                        if (!isThinkingStarted)
                                        {
                                            yield return "<think>\n\n~~~Thoughts\n\n" + part.text;
                                            isThinkingStarted = true;
                                        }
                                        else
                                        {
                                            yield return part.text;
                                        }
                                    }
                                    else
                                    {
                                        // 非思考内容
                                        if (isThinkingStarted && !isThinkingEnded)
                                        {
                                            // 思考结束，输出结束标记
                                            yield return "\n\n~~~\n\n</think>\n\n" + part.text;
                                            isThinkingEnded = true;
                                        }
                                        else
                                        {
                                            yield return part.text;
                                        }
                                    }
                                    contentBuilder.Append(part.text);
                                }

                            }
                        }

                        // 检查是否完成且有函数调用
                        if (candidate?.finishReason == "STOP" && tool_calls.Count > 0)
                        {
                            // 执行函数调用
                            var functionResults = new List<object>();

                            foreach (var funcCall in tool_calls)
                            {
                                string toolResult = await ExecuteFunctionCall(funcCall);

                                functionResults.Add(new
                                {
                                    role = "function",
                                    parts = new[]
                                    {
                                new
                                {
                                    functionResponse = new
                                    {
                                        name = funcCall.name,
                                        response = new { result = toolResult }
                                    }
                                }
                            }
                                });
                            }

                            toolsmessages.AddRange(functionResults);
                            tool_calls.Clear();
                            contentBuilder.Clear();
                            response.Content.Dispose();

                            // 递归调用以获取最终响应
                            await foreach (var item in GeminiAsync(modelconfg, request, cancellationToken, client, toolsmessages))
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

                    var chunk = JsonSerializer.Deserialize<GeminiChunkResponse>(line);
                    var candidate = chunk?.candidates?.FirstOrDefault();

                    if (candidate?.content?.parts != null)
                    {
                        foreach (var part in candidate.content.parts)
                        {
                            if (part.functionCall != null)
                            {
                                if (!string.IsNullOrEmpty(part.functionCall.name))
                                {
                                    tool_calls.Add(part.functionCall);
                                }

                                //continue;
                            }

                            // 处理文本内容
                            if (!string.IsNullOrEmpty(part.text))
                            {
                                contentBuilder.Append(part.text);
                                yield return part.text;
                            }
                        }
                    }

                    // 如果有函数调用，执行并重新请求
                    // 检查是否完成且有函数调用
                    if (candidate?.finishReason == "STOP" && tool_calls.Count > 0)
                    {
                        // 执行函数调用
                        var functionResults = new List<object>();

                        foreach (var funcCall in tool_calls)
                        {
                            string toolResult = await ExecuteFunctionCall(funcCall);

                            functionResults.Add(new
                            {
                                role = "function",
                                parts = new[]
                                {
                                new
                                {
                                    functionResponse = new
                                    {
                                        name = funcCall.name,
                                        response = new { result = toolResult }
                                    }
                                }
                            }
                            });
                        }

                        toolsmessages.AddRange(functionResults);
                        tool_calls.Clear();
                        contentBuilder.Clear();
                        response.Content.Dispose();

                        // 递归调用以获取最终响应
                        await foreach (var item in GeminiAsync(modelconfg, request, cancellationToken, client, toolsmessages))
                        {
                            yield return item;
                        }
                    }
                }
            }
        }

        //Gemini
        public async IAsyncEnumerable<string> GeminiFileSearchAsync(ChatModelConfig modelconfg, ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken, HttpClient inputclient = null, List<object> toolsmessages = null)
        {
            // 验证配置AQ.Ab8RN6LQoXO75Ty1A9x4EEogc0XS97bVZPZwz-ytddNBxMvvrg
            var apiKey = Environment.GetEnvironmentVariable(modelconfg.EnvironmentApikeyName);
            //var apiKey = "AQ.Ab8RN6LQoXO75Ty1A9x4EEogc0XS97bVZPZwz-ytddNBxMvvrg";
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
            HttpClient client = inputclient ?? _httpClientFactory.CreateClient();

            var messages = ToMessagesGemini(request, modelconfg);
            toolsmessages ??= new List<object>();
            messages.AddRange(toolsmessages);

            // 准备工具定义
            List<object> tools = await PrepareGeminiFileSearchTools(request.EnableSearch, modelconfg, cancellationToken);

            // 获取思考配置
            var thinkingConfig = GeminiThinkingConfig(modelconfg);

            HttpResponseMessage response = null;
            if (modelconfg.Temperature >= 0)
            {
                var requestContent = new
                {
                    system_instruction = new
                    {
                        parts = new { text = modelconfg.Systemprompt }
                    },
                    contents = messages,
                    generationConfig = new { temperature = modelconfg.Temperature, thinkingConfig = thinkingConfig },
                    tools = tools
                };
                string str = JsonSerializer.Serialize(requestContent, _jsonOptions);
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
                    contents = messages,
                    generationConfig = thinkingConfig != null ? new { thinkingConfig = thinkingConfig } : null,
                    tools = tools
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
            var contentBuilder = new StringBuilder();
            //List<GeminiFunctionCall> functionCalls = new();
            List<GeminiToolCall> tool_calls = new();
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
                        var candidate = chunk?.candidates?.FirstOrDefault();

                        if (candidate?.content?.parts != null)
                        {
                            foreach (var part in candidate.content.parts)
                            {

                                if (part.functionCall != null)
                                {
                                    if (!string.IsNullOrEmpty(part.functionCall.name))
                                    {
                                        tool_calls.Add(part.functionCall);
                                    }

                                    //continue;
                                }

                                // 处理文本内容
                                if (!string.IsNullOrEmpty(part.text))
                                {
                                    contentBuilder.Append(part.text);
                                    yield return part.text;
                                }

                            }
                        }

                        // 检查是否完成且有函数调用
                        if (candidate?.finishReason == "STOP" && tool_calls.Count > 0)
                        {
                            // 执行函数调用
                            var functionResults = new List<object>();

                            foreach (var funcCall in tool_calls)
                            {
                                string toolResult = await ExecuteFunctionCall(funcCall);

                                functionResults.Add(new
                                {
                                    role = "function",
                                    parts = new[]
                                    {
                                new
                                {
                                    functionResponse = new
                                    {
                                        name = funcCall.name,
                                        response = new { result = toolResult }
                                    }
                                }
                            }
                                });
                            }

                            toolsmessages.AddRange(functionResults);
                            tool_calls.Clear();
                            contentBuilder.Clear();
                            response.Content.Dispose();

                            // 递归调用以获取最终响应
                            await foreach (var item in GeminiFileSearchAsync(modelconfg, request, cancellationToken, client, toolsmessages))
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

                    var chunk = JsonSerializer.Deserialize<GeminiChunkResponse>(line);
                    var candidate = chunk?.candidates?.FirstOrDefault();

                    if (candidate?.content?.parts != null)
                    {
                        foreach (var part in candidate.content.parts)
                        {
                            if (part.functionCall != null)
                            {
                                if (!string.IsNullOrEmpty(part.functionCall.name))
                                {
                                    tool_calls.Add(part.functionCall);
                                }

                                //continue;
                            }

                            // 处理文本内容
                            if (!string.IsNullOrEmpty(part.text))
                            {
                                contentBuilder.Append(part.text);
                                yield return part.text;
                            }
                        }
                    }

                    // 如果有函数调用，执行并重新请求
                    // 检查是否完成且有函数调用
                    if (candidate?.finishReason == "STOP" && tool_calls.Count > 0)
                    {
                        // 执行函数调用
                        var functionResults = new List<object>();

                        foreach (var funcCall in tool_calls)
                        {
                            string toolResult = await ExecuteFunctionCall(funcCall);

                            functionResults.Add(new
                            {
                                role = "function",
                                parts = new[]
                                {
                                new
                                {
                                    functionResponse = new
                                    {
                                        name = funcCall.name,
                                        response = new { result = toolResult }
                                    }
                                }
                            }
                            });
                        }

                        toolsmessages.AddRange(functionResults);
                        tool_calls.Clear();
                        contentBuilder.Clear();
                        response.Content.Dispose();

                        // 递归调用以获取最终响应
                        await foreach (var item in GeminiFileSearchAsync(modelconfg, request, cancellationToken, client, toolsmessages))
                        {
                            yield return item;
                        }
                    }
                }
            }
        }

        // 添加辅助方法：执行函数调用
        private async Task<string> ExecuteFunctionCall(GeminiToolCall funcCall)
        {
            string toolResult = string.Empty;

            // 将 args 转换为 JsonElement 以便访问属性
            JsonElement argsJson;

            // 如果 args 已经是 JsonElement
            if (funcCall.args is JsonElement element)
            {
                argsJson = element;
            }
            // 如果 args 是字符串,需要解析它
            else if (funcCall.args is string argsStr)
            {
                using var doc = JsonDocument.Parse(argsStr);
                argsJson = doc.RootElement.Clone();
            }
            // 如果是其他类型,尝试序列化再解析
            else
            {
                var argsStr1 = JsonSerializer.Serialize(funcCall.args);
                using var doc = JsonDocument.Parse(argsStr1);
                argsJson = doc.RootElement.Clone();
            }

            switch (funcCall.name)
            {
                case nameof(GetCurrentDataTime):
                    toolResult = await GetCurrentDataTime();
                    break;

                case nameof(JinaAiSearch):
                    if (argsJson.TryGetProperty("query", out var queryValue))
                    {
                        string query = queryValue.GetString() ?? throw new ArgumentNullException(nameof(queryValue), "Query cannot be null.");
                        toolResult = await JinaAiSearch(query);
                    }
                    break;

                case nameof(SearchTrainTicket):
                    if (argsJson.TryGetProperty("startingplace", out var startValue) &&
                        argsJson.TryGetProperty("arrivalplace", out var arrivalValue) &&
                        argsJson.TryGetProperty("date", out var dateValue))
                    {
                        toolResult = await SearchTrainTicket(
                            startValue.GetString(),
                            arrivalValue.GetString(),
                            dateValue.GetString()
                        );
                    }
                    break;

                case nameof(GetWeather):
                    if (argsJson.TryGetProperty("city", out var cityValue))
                    {
                        string city = cityValue.GetString() ?? throw new ArgumentNullException(nameof(cityValue), "City cannot be null.");
                        toolResult = await GetWeather(city);
                    }
                    break;

                case nameof(SearchCtripHotel):
                    {
                        argsJson.TryGetProperty("city", out var city);
                        argsJson.TryGetProperty("checkInDate", out var checkInDate);
                        argsJson.TryGetProperty("checkOutDate", out var checkOutDate);
                        argsJson.TryGetProperty("keyword", out var keyword);
                        toolResult = await SearchCtripHotel(
                            city.GetString() ?? "",
                            checkInDate.GetString() ?? "",
                            checkOutDate.GetString() ?? "",
                            keyword.ValueKind != JsonValueKind.Undefined ? keyword.GetString() : null);
                    }
                    break;

                case nameof(SearchCtripFlight):
                    {
                        argsJson.TryGetProperty("departure", out var departure);
                        argsJson.TryGetProperty("arrival", out var arrival);
                        argsJson.TryGetProperty("date", out var date);
                        argsJson.TryGetProperty("isRoundTrip", out var isRoundTrip);
                        toolResult = await SearchCtripFlight(
                            departure.GetString() ?? "",
                            arrival.GetString() ?? "",
                            date.GetString() ?? "",
                            isRoundTrip.ValueKind == JsonValueKind.True);
                    }
                    break;

                case nameof(SearchCtripAttraction):
                    {
                        argsJson.TryGetProperty("city", out var city);
                        argsJson.TryGetProperty("keyword", out var keyword);
                        toolResult = await SearchCtripAttraction(
                            city.GetString() ?? "",
                            keyword.ValueKind != JsonValueKind.Undefined ? keyword.GetString() : null);
                    }
                    break;

                case nameof(SearchCtripTour):
                    {
                        argsJson.TryGetProperty("destination", out var destination);
                        argsJson.TryGetProperty("keyword", out var keyword);
                        toolResult = await SearchCtripTour(
                            destination.GetString() ?? "",
                            keyword.ValueKind != JsonValueKind.Undefined ? keyword.GetString() : null);
                    }
                    break;

                default:
                    // 尝试调用 MCP 工具
                    if (_mcpClientManager.IsEnabled && _mcpClientManager.IsMcpTool(funcCall.name))
                    {
                        var argsStr = argsJson.ValueKind == JsonValueKind.Undefined ? "{}" : argsJson.GetRawText();
                        toolResult = await _mcpClientManager.CallToolAsync(funcCall.name, argsStr);
                    }
                    else
                    {
                        toolResult = "未知工具调用";
                    }
                    break;
            }

            return toolResult;
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
                    content = msg.Role == "assistant" ? DelAllString(msg.Content, "<think>", "</think>") : msg.Content,
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

            List<object> tools = await PrepareOpenAiToolsAsync(request.EnableSearch, cancellationToken);

            var requestContent = new
            {
                model = modelconfg.Model,
                messages = messages,
                stream = modelconfg.Stream,
                temperature = modelconfg.Temperature >= 0 ? (float?)modelconfg.Temperature : null,
                reasoning = OpenAiThinkingLevel(modelconfg),
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

                // 判断是否是工具调用后的继续（已有之前的推理内容）
                bool isToolCallContinuation = toolsmessages.Count > 0;
                bool thinkingStarted = false;
                bool thinkingEnded = false;
                bool inlineThinkingStarted = false;
                bool inlineThinkingEnded = false;
                List<tool_call> tool_calls = new();
                var contentBuilder = new StringBuilder();
                var reasoningContentBuilder = new StringBuilder();
                bool hasCitations = false;
                string citationsString = string.Empty;

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

                            if (!string.IsNullOrEmpty(content))
                            {
                                contentBuilder.Append(content);
                            }
                            if (!string.IsNullOrEmpty(reasoning_content))
                            {
                                reasoningContentBuilder.Append(reasoning_content);
                            }

                            // 处理工具调用
                            var toolCallDelta = chunk?.choices?.FirstOrDefault()?.delta?.tool_calls?.FirstOrDefault();
                            if (toolCallDelta != null)
                            {
                                if (!string.IsNullOrEmpty(toolCallDelta.function?.name))
                                {
                                    tool_calls.Add(toolCallDelta);
                                }
                                else if (tool_calls.Count > 0)
                                {
                                    int index = toolCallDelta.index;
                                    if (index >= 0 && index < tool_calls.Count)
                                    {
                                        tool_calls[index].function.arguments += toolCallDelta.function?.arguments;
                                    }
                                }
                            }

                            // 输出推理内容
                            if (!string.IsNullOrEmpty(reasoning_content))
                            {
                                if (!thinkingStarted)
                                {
                                    // 如果是工具调用后的继续，需要先闭合之前的内容块再开始新的思考块
                                    if (isToolCallContinuation)
                                    {
                                        yield return "\n\n<think>\n\n~~~Thoughts\n\n" + reasoning_content;
                                    }
                                    else
                                    {
                                        yield return "<think>\n\n~~~Thoughts\n\n" + reasoning_content;
                                    }
                                    thinkingStarted = true;
                                }
                                else
                                {
                                    yield return reasoning_content;
                                }
                            }

                            // 输出内容
                            if (!string.IsNullOrEmpty(content))
                            {
                                if (thinkingStarted && !thinkingEnded)
                                {
                                    yield return "\n\n~~~\n\n</think>\n\n" + content;
                                    thinkingEnded = true;
                                }
                                else if (content.Contains("<think>") && !inlineThinkingStarted && !inlineThinkingEnded)
                                {
                                    yield return content.Replace("<think>", "<think>\n\n~~~Thoughts\n\n");
                                    inlineThinkingStarted = true;
                                }
                                else if (content.Contains("</think>") && inlineThinkingStarted && !inlineThinkingEnded)
                                {
                                    yield return content.Replace("</think>", "\n\n~~~\n\n</think>\n\n");
                                    inlineThinkingEnded = true;
                                }
                                else
                                {
                                    yield return content;
                                }
                            }

                            // 处理工具调用完成
                            var finishReason = chunk?.choices?.FirstOrDefault()?.finish_reason;
                            if (tool_calls.Count > 0 && (finishReason == "tool_calls" || finishReason == "stop"))
                            {
                                // 如果有未闭合的思考块，先闭合它
                                if (thinkingStarted && !thinkingEnded)
                                {
                                    yield return "\n\n~~~\n\n</think>\n\n";
                                    thinkingEnded = true;
                                }

                                await foreach (var item in ExecuteToolCallsAndContinueAsync(
                                    modelconfg, request, cancellationToken, client, toolsmessages,
                                    tool_calls, contentBuilder, reasoningContentBuilder, response))
                                {
                                    yield return item;
                                }
                                yield break;
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

                        if (!string.IsNullOrEmpty(content))
                        {
                            contentBuilder.Append(content);
                        }

                        if (!string.IsNullOrEmpty(reasoning_content))
                        {
                            reasoningContentBuilder.Append(reasoning_content);
                        }
                        // 处理工具调用
                        var toolCalls = chunk?.choices?.FirstOrDefault()?.message?.tool_calls;
                        if (toolCalls != null && toolCalls.Length > 0)
                        {
                            tool_calls.AddRange(toolCalls.Cast<tool_call>());

                            // 如果有推理内容，先输出它（带闭合标签）
                            if (!string.IsNullOrEmpty(reasoning_content))
                            {
                                var formattedReasoning = isToolCallContinuation
                                    ? $"\n\n<think>\n\n~~~Thoughts\n\n{reasoning_content}\n\n~~~\n\n</think>\n\n"
                                    : $"<think>\n\n~~~Thoughts\n\n{reasoning_content}\n\n~~~\n\n</think>\n\n";
                                yield return formattedReasoning;
                            }

                            await foreach (var item in ExecuteToolCallsAndContinueAsync(
                                modelconfg, request, cancellationToken, client, toolsmessages,
                                tool_calls, contentBuilder, reasoningContentBuilder, response))
                            {
                                yield return item;
                            }
                            yield break;
                        }

                        // 输出推理和内容
                        if (!string.IsNullOrEmpty(reasoning_content))
                        {
                            var formattedReasoning = isToolCallContinuation
                                ? $"\n\n<think>\n\n~~~Thoughts\n\n{reasoning_content}\n\n~~~\n\n</think>\n\n"
                                : $"<think>\n\n~~~Thoughts\n\n{reasoning_content}\n\n~~~\n\n</think>\n\n";
                            yield return formattedReasoning + content;
                        }
                        else if (!string.IsNullOrEmpty(content))
                        {
                            content = content.Replace("<think>", "<think>\n\n~~~Thoughts\n\n");
                            content = content.Replace("</think>", "\n\n~~~\n\n</think>\n\n");
                            yield return content;
                        }
                    }
                }

                if (!string.IsNullOrEmpty(citationsString))
                {
                    yield return "\n\n" + citationsString;
                }
            }
        }

        // 提取工具调用执行逻辑为独立方法，减少代码重复
        private async IAsyncEnumerable<string> ExecuteToolCallsAndContinueAsync(
            ChatModelConfig modelconfg, ChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken,
            HttpClient client, List<object> toolsmessages,
            List<tool_call> tool_calls, StringBuilder contentBuilder,
            StringBuilder reasoningContentBuilder, HttpResponseMessage response)
        {
            List<object> tool_calls1 = new List<object>();
            tool_calls1.AddRange(tool_calls);

            var assistantMessage = reasoningContentBuilder != null
                ? new { role = "assistant", content = contentBuilder.ToString(), reasoning_content = reasoningContentBuilder.ToString(), tool_calls = tool_calls1 }
                : (object)new { role = "assistant", content = contentBuilder.ToString(), tool_calls = tool_calls1 };

            toolsmessages.Add(assistantMessage);

            foreach (var pair in tool_calls)
            {
                string toolResult = await ExecuteToolCallAsync(pair);

                toolsmessages.Add(new
                {
                    role = "tool",
                    tool_call_id = pair.id,
                    content = toolResult
                });
            }

            contentBuilder.Clear();
            reasoningContentBuilder?.Clear();
            tool_calls.Clear();
            response.Content.Dispose();

            await foreach (var item in OpenAIAsync(modelconfg, request, cancellationToken, client, toolsmessages))
            {
                yield return item;
            }
        }

        // 提取工具执行逻辑
        private async Task<string> ExecuteToolCallAsync(tool_call pair)
        {
            switch (pair.function.name)
            {
                case nameof(GetCurrentDataTime):
                    return await GetCurrentDataTime();

                case nameof(JinaAiSearch):
                    {
                        using JsonDocument argumentsJson = JsonDocument.Parse(pair.function.arguments);
                        if (!argumentsJson.RootElement.TryGetProperty("query", out JsonElement outquery))
                        {
                            throw new ArgumentNullException("query", "The query argument is required.");
                        }
                        return await JinaAiSearch(outquery.GetString() ?? throw new ArgumentNullException("query", "Query cannot be null."));
                    }

                case nameof(SearchTrainTicket):
                    {
                        using JsonDocument argumentsJson = JsonDocument.Parse(pair.function.arguments);
                        argumentsJson.RootElement.TryGetProperty("startingplace", out JsonElement startingplace);
                        argumentsJson.RootElement.TryGetProperty("arrivalplace", out JsonElement arrivalplace);
                        if (!argumentsJson.RootElement.TryGetProperty("date", out JsonElement date))
                        {
                            throw new ArgumentNullException("date", "The date argument is required.");
                        }
                        return await SearchTrainTicket(startingplace.GetString(), arrivalplace.GetString(), date.GetString());
                    }

                case nameof(GetWeather):
                    {
                        using JsonDocument argumentsJson = JsonDocument.Parse(pair.function.arguments);
                        if (!argumentsJson.RootElement.TryGetProperty("city", out JsonElement outquery))
                        {
                            throw new ArgumentNullException("city", "The city argument is required.");
                        }
                        return await GetWeather(outquery.GetString() ?? throw new ArgumentNullException("city", "City cannot be null."));
                    }

                case nameof(SearchCtripHotel):
                    {
                        using JsonDocument argumentsJson = JsonDocument.Parse(pair.function.arguments ?? "{}");
                        argumentsJson.RootElement.TryGetProperty("city", out JsonElement city);
                        argumentsJson.RootElement.TryGetProperty("checkInDate", out JsonElement checkInDate);
                        argumentsJson.RootElement.TryGetProperty("checkOutDate", out JsonElement checkOutDate);
                        argumentsJson.RootElement.TryGetProperty("keyword", out JsonElement keyword);
                        return await SearchCtripHotel(
                            city.GetString() ?? "",
                            checkInDate.GetString() ?? "",
                            checkOutDate.GetString() ?? "",
                            keyword.ValueKind != JsonValueKind.Undefined ? keyword.GetString() : null);
                    }

                case nameof(SearchCtripFlight):
                    {
                        using JsonDocument argumentsJson = JsonDocument.Parse(pair.function.arguments ?? "{}");
                        argumentsJson.RootElement.TryGetProperty("departure", out JsonElement departure);
                        argumentsJson.RootElement.TryGetProperty("arrival", out JsonElement arrival);
                        argumentsJson.RootElement.TryGetProperty("date", out JsonElement date);
                        argumentsJson.RootElement.TryGetProperty("isRoundTrip", out JsonElement isRoundTrip);
                        return await SearchCtripFlight(
                            departure.GetString() ?? "",
                            arrival.GetString() ?? "",
                            date.GetString() ?? "",
                            isRoundTrip.ValueKind == JsonValueKind.True);
                    }

                case nameof(SearchCtripAttraction):
                    {
                        using JsonDocument argumentsJson = JsonDocument.Parse(pair.function.arguments ?? "{}");
                        argumentsJson.RootElement.TryGetProperty("city", out JsonElement city);
                        argumentsJson.RootElement.TryGetProperty("keyword", out JsonElement keyword);
                        return await SearchCtripAttraction(
                            city.GetString() ?? "",
                            keyword.ValueKind != JsonValueKind.Undefined ? keyword.GetString() : null);
                    }

                case nameof(SearchCtripTour):
                    {
                        using JsonDocument argumentsJson = JsonDocument.Parse(pair.function.arguments ?? "{}");
                        argumentsJson.RootElement.TryGetProperty("destination", out JsonElement destination);
                        argumentsJson.RootElement.TryGetProperty("keyword", out JsonElement keyword);
                        return await SearchCtripTour(
                            destination.GetString() ?? "",
                            keyword.ValueKind != JsonValueKind.Undefined ? keyword.GetString() : null);
                    }

                default:
                    // 尝试调用 MCP 工具
                    if (_mcpClientManager.IsEnabled && _mcpClientManager.IsMcpTool(pair.function.name))
                    {
                        return await _mcpClientManager.CallToolAsync(pair.function.name, pair.function.arguments ?? "{}");
                    }
                    return "未知工具调用";
            }
        }
        #region 工具方法
        // 创建工具列表（包含内置工具和 MCP 工具）
        private async Task<List<object>> PrepareOpenAiResponsesToolsAsync(bool search, CancellationToken cancellationToken = default)
        {
            var tools = new List<object>();
            if (search)
            {
                tools.Add
                    (
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

                    });
            }

            tools.Add(
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

                 });
            tools.Add(
                new
                {
                    type = "function",
                    name = nameof(GetCurrentDataTime),
                    description = "获取当前日期和时间",
                    parameters = new
                    {
                        type = "object",
                        properties = new { },
                        required = Array.Empty<string>()
                    }
                });
            tools.Add(
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
                               description = "出发日期(格式:YYYY-MM-DD)"
                           }
                       }
                   },
                   required = new[] { "startingplace", "arrivalplace", "date" }
               });

            // 携程酒店搜索
            tools.Add(
               new
               {
                   type = "function",
                   name = nameof(SearchCtripHotel),
                   description = "搜索携程酒店信息，获取指定城市的酒店列表、价格和评分",
                   parameters = new
                   {
                       type = "object",
                       properties = new
                       {
                           city = new
                           {
                               type = "string",
                               description = "城市名称（如：上海、北京）"
                           },
                           checkInDate = new
                           {
                               type = "string",
                               description = "入住日期(格式:YYYY-MM-DD)"
                           },
                           checkOutDate = new
                           {
                               type = "string",
                               description = "离店日期(格式:YYYY-MM-DD)"
                           },
                           keyword = new
                           {
                               type = "string",
                               description = "搜索关键词（可选，如酒店名称、地标）"
                           }
                       },
                       required = new[] { "city", "checkInDate", "checkOutDate" }
                   }
               });

            // 携程机票搜索
            tools.Add(
               new
               {
                   type = "function",
                   name = nameof(SearchCtripFlight),
                   description = "搜索携程机票信息，获取指定航线的航班列表和票价",
                   parameters = new
                   {
                       type = "object",
                       properties = new
                       {
                           departure = new
                           {
                               type = "string",
                               description = "出发城市（如：北京、上海）"
                           },
                           arrival = new
                           {
                               type = "string",
                               description = "到达城市（如：广州、深圳）"
                           },
                           date = new
                           {
                               type = "string",
                               description = "出发日期(格式:YYYY-MM-DD)"
                           },
                           isRoundTrip = new
                           {
                               type = "boolean",
                               description = "是否往返（可选，默认单程）"
                           }
                       },
                       required = new[] { "departure", "arrival", "date" }
                   }
               });

            // 携程景点门票搜索
            tools.Add(
               new
               {
                   type = "function",
                   name = nameof(SearchCtripAttraction),
                   description = "搜索携程景点门票信息，获取指定城市的景点列表和门票价格",
                   parameters = new
                   {
                       type = "object",
                       properties = new
                       {
                           city = new
                           {
                               type = "string",
                               description = "城市名称（如：杭州、西安）"
                           },
                           keyword = new
                           {
                               type = "string",
                               description = "景点关键词（可选，如西湖、兵马俑）"
                           }
                       },
                       required = new[] { "city" }
                   }
               });

            // 携程旅游产品搜索
            tools.Add(
               new
               {
                   type = "function",
                   name = nameof(SearchCtripTour),
                   description = "搜索携程旅游产品，获取跟团游、自由行等旅游线路信息",
                   parameters = new
                   {
                       type = "object",
                       properties = new
                       {
                           destination = new
                           {
                               type = "string",
                               description = "目的地（如：三亚、丽江）"
                           },
                           keyword = new
                           {
                               type = "string",
                               description = "关键词（可选，如亲子游、蜜月）"
                           }
                       },
                       required = new[] { "destination" }
                   }
               });


            // 加载 MCP 工具
            if (_mcpClientManager.IsEnabled)
            {
                try
                {
                    var mcpTools = await _mcpClientManager.GetAllToolsAsync(cancellationToken);
                    foreach (var mcpTool in mcpTools)
                    {
                        // 使用 JsonNode 正确解析 inputSchema
                        //object? inputSchema = mcpTool.InputSchema.HasValue
                        //    ? System.Text.Json.Nodes.JsonNode.Parse(mcpTool.InputSchema.Value.GetRawText())
                        //    : null;
                        tools.Add(new
                        {
                            type = "function",
                            name = mcpTool.Name,
                            description = mcpTool.Description,
                            parameters = mcpTool.InputSchema
                        });
                    }
                    _logger.LogInformation("加载了 {Count} 个 MCP 工具", mcpTools.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "加载 MCP 工具失败");
                }
            }

            return tools.Count == 0 ? null : tools;

        }

        private async Task<List<object>> PrepareOpenAiToolsAsync(bool search, CancellationToken cancellationToken = default)
        {
            var tools = new List<object>();
            if (search)
            {
                tools.Add
                    (
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
                    });
            }

            tools.Add(
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
                 });
            tools.Add(
                new
                {
                    type = "function",
                    function = new
                    {
                        name = nameof(GetCurrentDataTime),
                        description = "获取当前日期和时间",
                        parameters = new
                        {
                            type = "object",
                            properties = new { },
                            required = Array.Empty<string>()
                        }
                    }
                });
            tools.Add(
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
                                   description = "出发日期(格式:YYYY-MM-DD)"
                               }
                           }
                       },
                       required = new[] { "startingplace", "arrivalplace", "date" }
                   }
               });

            //// 携程酒店搜索
            //tools.Add(
            //   new
            //   {
            //       type = "function",
            //       function = new
            //       {
            //           name = nameof(SearchCtripHotel),
            //           description = "搜索携程酒店信息，获取指定城市的酒店列表、价格和评分",
            //           parameters = new
            //           {
            //               type = "object",
            //               properties = new
            //               {
            //                   city = new { type = "string", description = "城市名称（如：上海、北京）" },
            //                   checkInDate = new { type = "string", description = "入住日期(格式:YYYY-MM-DD)" },
            //                   checkOutDate = new { type = "string", description = "离店日期(格式:YYYY-MM-DD)" },
            //                   keyword = new { type = "string", description = "搜索关键词（可选，如酒店名称、地标）" }
            //               },
            //               required = new[] { "city", "checkInDate", "checkOutDate" }
            //           }
            //       }
            //   });

            //// 携程机票搜索
            //tools.Add(
            //   new
            //   {
            //       type = "function",
            //       function = new
            //       {
            //           name = nameof(SearchCtripFlight),
            //           description = "搜索携程机票信息，获取指定航线的航班列表和票价",
            //           parameters = new
            //           {
            //               type = "object",
            //               properties = new
            //               {
            //                   departure = new { type = "string", description = "出发城市（如：北京、上海）" },
            //                   arrival = new { type = "string", description = "到达城市（如：广州、深圳）" },
            //                   date = new { type = "string", description = "出发日期(格式:YYYY-MM-DD)" },
            //                   isRoundTrip = new { type = "boolean", description = "是否往返（可选，默认单程）" }
            //               },
            //               required = new[] { "departure", "arrival", "date" }
            //           }
            //       }
            //   });

            //// 携程景点门票搜索
            //tools.Add(
            //   new
            //   {
            //       type = "function",
            //       function = new
            //       {
            //           name = nameof(SearchCtripAttraction),
            //           description = "搜索携程景点门票信息，获取指定城市的景点列表和门票价格",
            //           parameters = new
            //           {
            //               type = "object",
            //               properties = new
            //               {
            //                   city = new { type = "string", description = "城市名称（如：杭州、西安）" },
            //                   keyword = new { type = "string", description = "景点关键词（可选，如西湖、兵马俑）" }
            //               },
            //               required = new[] { "city" }
            //           }
            //       }
            //   });

            //// 携程旅游产品搜索
            //tools.Add(
            //   new
            //   {
            //       type = "function",
            //       function = new
            //       {
            //           name = nameof(SearchCtripTour),
            //           description = "搜索携程旅游产品，获取跟团游、自由行等旅游线路信息",
            //           parameters = new
            //           {
            //               type = "object",
            //               properties = new
            //               {
            //                   destination = new { type = "string", description = "目的地（如：三亚、丽江）" },
            //                   keyword = new { type = "string", description = "关键词（可选，如亲子游、蜜月）" }
            //               },
            //               required = new[] { "destination" }
            //           }
            //       }
            //   });


            // 加载 MCP 工具
            if (_mcpClientManager.IsEnabled)
            {
                try
                {
                    var mcpTools = await _mcpClientManager.GetAllToolsAsync(cancellationToken);
                    foreach (var mcpTool in mcpTools)
                    {
                        //// 使用 JsonNode 正确解析 inputSchema
                        //object? inputSchema = mcpTool.InputSchema.HasValue
                        //    ? System.Text.Json.Nodes.JsonNode.Parse(mcpTool.InputSchema.Value.GetRawText())
                        //    : null;
                        tools.Add(new
                        {
                            type = "function",
                            function = new
                            {
                                name = mcpTool.Name,
                                description = mcpTool.Description,
                                parameters = mcpTool.InputSchema
                            }
                        });
                    }
                    _logger.LogInformation("加载了 {Count} 个 MCP 工具", mcpTools.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "加载 MCP 工具失败");
                }
            }

            return tools.Count == 0 ? null : tools;

        }

        // 添加辅助方法：准备 Claude 格式的工具定义
        private async Task<List<object>> PrepareClaudeTools(bool search, CancellationToken cancellationToken = default)
        {
            var tools = new List<object>();
            if (search)
            {
                tools.Add
                    (
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
                   });
            }
            tools.Add
                    (
                   new
                   {
                       name = nameof(GetWeather),
                       description = "获取指定城市未来8天天气预报",
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
                   });

            tools.Add
                (
                new
                {
                    name = nameof(GetCurrentDataTime),
                    description = "获取当前日期和时间",
                    input_schema = new
                    {
                        type = "object",
                        properties = new { },
                        required = Array.Empty<string>()
                    }
                });

            tools.Add
            (
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
                                description = "日期(查询日期需要大于或等于今天日期,格式:YYYY-MM-DD)"
                            }
                        },
                        required = new[] { "startingplace", "arrivalplace", "date" }
                    }
                });

            //// 携程酒店搜索
            //tools.Add(
            //    new
            //    {
            //        name = nameof(SearchCtripHotel),
            //        description = "搜索携程酒店信息，获取指定城市的酒店列表、价格和评分",
            //        input_schema = new
            //        {
            //            type = "object",
            //            properties = new
            //            {
            //                city = new { type = "string", description = "城市名称（如：上海、北京）" },
            //                checkInDate = new { type = "string", description = "入住日期(格式:YYYY-MM-DD)" },
            //                checkOutDate = new { type = "string", description = "离店日期(格式:YYYY-MM-DD)" },
            //                keyword = new { type = "string", description = "搜索关键词（可选，如酒店名称、地标）" }
            //            },
            //            required = new[] { "city", "checkInDate", "checkOutDate" }
            //        }
            //    });

            //// 携程机票搜索
            //tools.Add(
            //    new
            //    {
            //        name = nameof(SearchCtripFlight),
            //        description = "搜索携程机票信息，获取指定航线的航班列表和票价",
            //        input_schema = new
            //        {
            //            type = "object",
            //            properties = new
            //            {
            //                departure = new { type = "string", description = "出发城市（如：北京、上海）" },
            //                arrival = new { type = "string", description = "到达城市（如：广州、深圳）" },
            //                date = new { type = "string", description = "出发日期(格式:YYYY-MM-DD)" },
            //                isRoundTrip = new { type = "boolean", description = "是否往返（可选，默认单程）" }
            //            },
            //            required = new[] { "departure", "arrival", "date" }
            //        }
            //    });

            //// 携程景点门票搜索
            //tools.Add(
            //    new
            //    {
            //        name = nameof(SearchCtripAttraction),
            //        description = "搜索携程景点门票信息，获取指定城市的景点列表和门票价格",
            //        input_schema = new
            //        {
            //            type = "object",
            //            properties = new
            //            {
            //                city = new { type = "string", description = "城市名称（如：杭州、西安）" },
            //                keyword = new { type = "string", description = "景点关键词（可选，如西湖、兵马俑）" }
            //            },
            //            required = new[] { "city" }
            //        }
            //    });

            //// 携程旅游产品搜索
            //tools.Add(
            //    new
            //    {
            //        name = nameof(SearchCtripTour),
            //        description = "搜索携程旅游产品，获取跟团游、自由行等旅游线路信息",
            //        input_schema = new
            //        {
            //            type = "object",
            //            properties = new
            //            {
            //                destination = new { type = "string", description = "目的地（如：三亚、丽江）" },
            //                keyword = new { type = "string", description = "关键词（可选，如亲子游、蜜月）" }
            //            },
            //            required = new[] { "destination" }
            //        }
            //    });

            // 加载 MCP 工具
            if (_mcpClientManager.IsEnabled)
            {
                try
                {
                    var mcpTools = await _mcpClientManager.GetAllToolsAsync(cancellationToken);
                    foreach (var mcpTool in mcpTools)
                    {

                        tools.Add(new
                        {
                            //type = "function",
                            name = mcpTool.Name,
                            description = mcpTool.Description,
                            input_schema = mcpTool.InputSchema
                        });
                    }
                    _logger.LogInformation("加载了 {Count} 个 MCP 工具", mcpTools.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "加载 MCP 工具失败");
                }
            }

            return tools.Count == 0 ? null : tools;

        }
        /// <summary>
        /// 递归过滤 Gemini API 不支持的 JSON Schema 字段
        /// </summary>
        /// <param name="element">要处理的 JsonElement</param>
        /// <returns>过滤后的 JsonElement</returns>
        private static JsonElement FilterGeminiUnsupportedSchemaFields(JsonElement element)
        {
            // Gemini API 不支持的字段列表
            var excludedTopLevelFields = new HashSet<string> { "$schema", "$id", "$ref", "$defs", "definitions", "additionalProperties", "title", "default", "examples" };
            // 属性定义层只允许的字段（白名单）
            var allowedPropertyFields = new HashSet<string> { "type", "description", "enum", "items", "properties", "required" };

            var filtered = FilterElement(element, excludedTopLevelFields, allowedPropertyFields, isPropertyLevel: false);

            // 将过滤后的对象序列化为 JsonElement
            var json = JsonSerializer.Serialize(filtered, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });
            return JsonDocument.Parse(json).RootElement;

            static object? FilterElement(JsonElement elem, HashSet<string> topLevelExcluded, HashSet<string> allowedPropFields, bool isPropertyLevel)
            {
                switch (elem.ValueKind)
                {
                    case JsonValueKind.Object:
                        var dict = new Dictionary<string, object?>();
                        foreach (var prop in elem.EnumerateObject())
                        {
                            // 在顶层过滤 $schema, additionalProperties 等
                            if (!isPropertyLevel && topLevelExcluded.Contains(prop.Name))
                                continue;

                            // 在属性定义层使用白名单，只保留 type, description 等
                            if (isPropertyLevel && !allowedPropFields.Contains(prop.Name))
                                continue;

                            // 递归处理 properties 对象中的每个属性定义
                            if (prop.Name == "properties" && prop.Value.ValueKind == JsonValueKind.Object)
                            {
                                // properties 内部的每个键值对代表一个属性定义
                                var propsDict = new Dictionary<string, object?>();
                                foreach (var propDef in prop.Value.EnumerateObject())
                                {
                                    propsDict[propDef.Name] = FilterElement(propDef.Value, topLevelExcluded, allowedPropFields, isPropertyLevel: true);
                                }
                                dict[prop.Name] = propsDict;
                            }
                            else if (prop.Name == "items" && prop.Value.ValueKind == JsonValueKind.Object)
                            {
                                // items 也是一个 schema 定义，需要递归过滤
                                dict[prop.Name] = FilterElement(prop.Value, topLevelExcluded, allowedPropFields, isPropertyLevel: true);
                            }
                            else
                            {
                                dict[prop.Name] = FilterElement(prop.Value, topLevelExcluded, allowedPropFields, isPropertyLevel);
                            }
                        }
                        return dict;

                    case JsonValueKind.Array:
                        var list = new List<object?>();
                        foreach (var item in elem.EnumerateArray())
                        {
                            list.Add(FilterElement(item, topLevelExcluded, allowedPropFields, isPropertyLevel));
                        }
                        return list;

                    case JsonValueKind.String:
                        return elem.GetString();

                    case JsonValueKind.Number:
                        if (elem.TryGetInt64(out var longVal))
                            return longVal;
                        return elem.GetDouble();

                    case JsonValueKind.True:
                        return true;

                    case JsonValueKind.False:
                        return false;

                    case JsonValueKind.Null:
                    default:
                        return null;
                }
            }
        }

        // 添加辅助方法：准备 Gemini 格式的工具定义
        private async Task<List<object>> PrepareGeminiTools(bool search, CancellationToken cancellationToken = default)
        {
            var tools = new List<object>();

            if (search)
            {
                tools.Add(new
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
                });
            }

            tools.Add(new
            {
                name = nameof(GetWeather),
                description = "获取指定城市未来8天天气预报",
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
            });

            tools.Add(new
            {
                name = nameof(GetCurrentDataTime),
                description = "获取当前日期和时间",
                parameters = new
                {
                    type = "object",
                    properties = new { }
                }
            });

            tools.Add(new
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
                            description = "日期(查询日期需要大于或等于今天日期,格式:YYYY-MM-DD)"
                        }
                    },
                    required = new[] { "startingplace", "arrivalplace", "date" }
                }
            });

            //// 携程酒店搜索
            //tools.Add(new
            //{
            //    name = nameof(SearchCtripHotel),
            //    description = "搜索携程酒店信息，获取指定城市的酒店列表、价格和评分",
            //    parameters = new
            //    {
            //        type = "object",
            //        properties = new
            //        {
            //            city = new { type = "string", description = "城市名称（如：上海、北京）" },
            //            checkInDate = new { type = "string", description = "入住日期(格式:YYYY-MM-DD)" },
            //            checkOutDate = new { type = "string", description = "离店日期(格式:YYYY-MM-DD)" },
            //            keyword = new { type = "string", description = "搜索关键词（可选，如酒店名称、地标）" }
            //        },
            //        required = new[] { "city", "checkInDate", "checkOutDate" }
            //    }
            //});

            //// 携程机票搜索
            //tools.Add(new
            //{
            //    name = nameof(SearchCtripFlight),
            //    description = "搜索携程机票信息，获取指定航线的航班列表和票价",
            //    parameters = new
            //    {
            //        type = "object",
            //        properties = new
            //        {
            //            departure = new { type = "string", description = "出发城市（如：北京、上海）" },
            //            arrival = new { type = "string", description = "到达城市（如：广州、深圳）" },
            //            date = new { type = "string", description = "出发日期(格式:YYYY-MM-DD)" },
            //            isRoundTrip = new { type = "boolean", description = "是否往返（可选，默认单程）" }
            //        },
            //        required = new[] { "departure", "arrival", "date" }
            //    }
            //});

            //// 携程景点门票搜索
            //tools.Add(new
            //{
            //    name = nameof(SearchCtripAttraction),
            //    description = "搜索携程景点门票信息，获取指定城市的景点列表和门票价格",
            //    parameters = new
            //    {
            //        type = "object",
            //        properties = new
            //        {
            //            city = new { type = "string", description = "城市名称（如：杭州、西安）" },
            //            keyword = new { type = "string", description = "景点关键词（可选，如西湖、兵马俑）" }
            //        },
            //        required = new[] { "city" }
            //    }
            //});

            //// 携程旅游产品搜索
            //tools.Add(new
            //{
            //    name = nameof(SearchCtripTour),
            //    description = "搜索携程旅游产品，获取跟团游、自由行等旅游线路信息",
            //    parameters = new
            //    {
            //        type = "object",
            //        properties = new
            //        {
            //            destination = new { type = "string", description = "目的地（如：三亚、丽江）" },
            //            keyword = new { type = "string", description = "关键词（可选，如亲子游、蜜月）" }
            //        },
            //        required = new[] { "destination" }
            //    }
            //});


            // 加载 MCP 工具
            if (_mcpClientManager.IsEnabled)
            {
                try
                {
                    var mcpTools = await _mcpClientManager.GetAllToolsAsync(cancellationToken);
                    foreach (var mcpTool in mcpTools)
                    {

                        // 从 MCP InputSchema 中动态提取字段，过滤掉 Gemini 不支持的字段
                        object? inputSchema;
                        if (mcpTool.InputSchema.HasValue)
                        {
                            // 使用辅助方法递归过滤不支持的字段（如 format, $schema, additionalProperties, default, examples, title 等）
                            inputSchema = FilterGeminiUnsupportedSchemaFields(mcpTool.InputSchema.Value);


                        }
                        else
                        {
                            inputSchema = new { type = "object", properties = new { } };
                        }
                        tools.Add(new
                        {
                            name = mcpTool.Name,
                            description = mcpTool.Description,
                            parameters = inputSchema
                        });
                    }
                    _logger.LogInformation("加载了 {Count} 个 MCP 工具", mcpTools.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "加载 MCP 工具失败");
                }
            }

            return tools.Count == 0 ? null : new List<object> { new { functionDeclarations = tools } };


        }
        // 添加辅助方法：准备 Gemini 格式的工具定义
        private async Task<List<object>> PrepareGeminiFileSearchTools(bool search, ChatModelConfig config, CancellationToken cancellationToken = default)
        {

            var tools = new List<object>();

            if (search)
            {
                tools.Add(new
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
                });
            }

            tools.Add(new
            {
                name = nameof(GetWeather),
                description = "获取指定城市未来8天天气预报",
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
            });

            tools.Add(new
            {
                name = nameof(GetCurrentDataTime),
                description = "获取当前日期和时间",
                parameters = new
                {
                    type = "object",
                    properties = new { }
                }
            });

            tools.Add(new
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
                            description = "日期(查询日期需要大于或等于今天日期,格式:YYYY-MM-DD)"
                        }
                    },
                    required = new[] { "startingplace", "arrivalplace", "date" }
                }
            });


            // 加载 MCP 工具
            if (_mcpClientManager.IsEnabled)
            {
                try
                {
                    var mcpTools = await _mcpClientManager.GetAllToolsAsync(cancellationToken);
                    foreach (var mcpTool in mcpTools)
                    {
                        // 从 MCP InputSchema 中动态提取字段，过滤掉 Gemini 不支持的字段
                        object? inputSchema;
                        if (mcpTool.InputSchema.HasValue)
                        {
                            // 使用辅助方法递归过滤不支持的字段（如 format, $schema, additionalProperties, default, examples, title 等）
                            inputSchema = FilterGeminiUnsupportedSchemaFields(mcpTool.InputSchema.Value);


                        }
                        else
                        {
                            inputSchema = new { type = "object", properties = new { } };
                        }
                        tools.Add(new
                        {
                            name = mcpTool.Name,
                            description = mcpTool.Description,
                            parameters = inputSchema
                        });
                    }
                    _logger.LogInformation("加载了 {Count} 个 MCP 工具", mcpTools.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "加载 MCP 工具失败");
                }
            }

            object File_search = null;
            if (!string.IsNullOrWhiteSpace(config.File_search_store_names))
            {
                File_search =
                    new
                    {
                        file_search = new
                        {
                            file_search_store_names = new List<object>
                            {
                                config.File_search_store_names
                            }
                        }
                    }
                ;
            }

            if (tools.Count == 0 && File_search == null) return null;

            return new List<object> { new { functionDeclarations = tools }, File_search };

        }

        /// <summary>
        /// 根据配置返回 OpenAI Responses API 的 reasoning 配置
        /// OpenAI API 使用 effort 来控制推理强度: "low", "medium", "high"
        /// 可选 summary 参数: "auto", "concise", "detailed"
        /// </summary>
        private object OpenAiThinkingLevel(ChatModelConfig config)
        {
            // 如果没有设置 ThinkingLevel，返回 null（不启用推理）
            if (string.IsNullOrEmpty(config.ThinkingLevel))
            {
                return null;
            }

            // 根据 ThinkingLevel 返回对应的 effort 配置
            return config.ThinkingLevel.ToUpperInvariant() switch
            {
                "HIGH" => new { effort = "high" },
                "MEDIUM" => new { effort = "medium" },
                "LOW" => new { effort = "low" },
                "OFF" => null,  // 关闭推理
                _ => null       // 默认不设置
            };
        }

        /// <summary>
        /// 根据配置返回 Gemini API 的 thinkingConfig 配置
        /// Gemini API 使用 thinkingBudget 来控制思考的 token 预算
        /// </summary>
        private object GeminiThinkingConfig(ChatModelConfig config)
        {
            // 如果 ThinkingTokens 大于 0，使用具体的 token 预算
            if (config.ThinkingTokens > 0)
            {
                return new { thinkingBudget = config.ThinkingTokens, includeThoughts = true };
            }

            // 根据 ThinkingLevel 返回预设的 token 预算
            return config.ThinkingLevel switch
            {
                "HIGH" => new { thinkingBudget = 24576, includeThoughts = true },    // 高强度思考
                "MEDIUM" => new { thinkingBudget = 8192, includeThoughts = true },   // 中等强度思考
                "LOW" => new { thinkingBudget = 1024, includeThoughts = true },      // 低强度思考
                "OFF" => new { thinkingBudget = 0, includeThoughts = false },         // 关闭思考
                _ => null                                     // 默认不设置，使用 API 默认值
            };
        }
        #endregion

        #region 组装消息

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


                    contentlist.Add(new { type = "input_text", text = (msg.Role == "assistant" ? DelAllString(msg.Content, "<think>", "</think>") : msg.Content) });

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
                        content = (msg.Role == "assistant" ? DelAllString(msg.Content, "<think>", "</think>") : msg.Content)
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


                    contentlist.Add(new { type = "text", text = (msg.Role == "assistant" ? DelAllString(msg.Content, "<think>", "</think>") : msg.Content) });


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


                    {
                        messages.Add(new
                        {
                            role = msg.Role,
                            content = (msg.Role == "assistant" ? DelAllString(msg.Content, "<think>", "</think>") : msg.Content)

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
                        contentlist.Add(new { text = (msg.Role == "assistant" ? DelAllString(msg.Content, "<think>", "</think>") : msg.Content) });
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
                        contentlist.Add(new { text = (msg.Role == "assistant" ? DelAllString(msg.Content, "<think>", "</think>") : msg.Content) });
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
                            text = (msg.Role == "assistant" ? DelAllString(msg.Content, "<think>", "</think>") : msg.Content)
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
                        content = (msg.Role == "assistant" ? DelAllString(msg.Content, "<think>", "</think>") : msg.Content)
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
                messages = (request.History[request.History.Count - 1].Role == "assistant" ? DelAllString(request.History[request.History.Count - 1].Content, "<think>", "</think>") : request.History[request.History.Count - 1].Content);
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

            var result = DateTime.Now.ToString(" 日期: yyyy年M月dd日 dddd 时间：HH:mm:ss ");

            return await Task.FromResult(result);
        }

        // 携程酒店搜索
        private async Task<string> SearchCtripHotel(string city, string checkInDate, string checkOutDate, string? keyword = null)
        {
            var result = await _ctripSearch.SearchHotel(city, checkInDate, checkOutDate, keyword);
            return result;
        }

        // 携程机票搜索
        private async Task<string> SearchCtripFlight(string departure, string arrival, string date, bool isRoundTrip = false)
        {
            var result = await _ctripSearch.SearchFlight(departure, arrival, date, isRoundTrip);
            return result;
        }

        // 携程景点门票搜索
        private async Task<string> SearchCtripAttraction(string city, string? keyword = null)
        {
            var result = await _ctripSearch.SearchAttraction(city, keyword);
            return result;
        }

        // 携程旅游产品搜索
        private async Task<string> SearchCtripTour(string destination, string? keyword = null)
        {
            var result = await _ctripSearch.SearchTour(destination, keyword);
            return result;
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
                            Quality = 100 // 压缩质量，范围0-100
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
            try
            {
                content = DelAllString(content, "<think>", "</think>");
                // 预处理内容，确保表头前有空行
                content = PreprocessLatex(EnsureTableHeaderHasEmptyLine(content));
                using (var ms = new MemoryStream())
                {
                    using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
                    {
                        // 添加主文档部分
                        var mainPart = doc.AddMainDocumentPart();
                        mainPart.Document = new Document();
                        var body = mainPart.Document.AppendChild(new Body());

                        // 配置表格样式
                        var stylePart = mainPart.AddNewPart<StyleDefinitionsPart>();
                        GenerateCompleteStyles(stylePart);

                        // 使用加强的Markdown流水线，特别是表格支持
                        var pipeline = new MarkdownPipelineBuilder()
                                       .UseAdvancedExtensions()
                                       .UseBootstrap() // 使用Bootstrap扩展改善表格渲染
                                       .UsePipeTables() // 确保支持管道表格
                                       .UseGridTables() // 支持网格表格
                                       .UseEmphasisExtras() // 支持更多强调语法
                                       .UseTaskLists() // 支持任务列表
                                       .UseAutoIdentifiers() // 自动添加表格ID
                                       .UseCustomContainers() // 支持自定义容器
                                       .UseDefinitionLists() // 支持定义列表
                                       .UseFootnotes() // 支持脚注
                                       .UseAutoLinks() // 自动检测链接
                                       .UseListExtras() // 增强列表功能
                                       .UseMediaLinks() // 支持媒体链接
                                       .UseFigures() // 支持图表
                                       .UseGenericAttributes() // 支持通用属性
                                       .UseYamlFrontMatter() // 支持YAML前置元数据
                                       .Build();

                        // 将Markdown转换为HTML
                        var htmlContent = Markdig.Markdown.ToHtml(content, pipeline);

                        // 构建完整的HTML文档，优化表格样式
                        htmlContent = $@"
        <!DOCTYPE html>
        <html lang=""zh-CN"">
        <head>
            <meta charset=""utf-8""/>

            <style>
                body {{ 
                    font-family: 'SimSun', 'Microsoft YaHei', 'Arial Unicode MS', Arial, sans-serif; 
                    padding: 20px;

                }}
                table {{ 
                    border-collapse: collapse; 
                    width: 100%;
                    margin-bottom: 1em;
                    max-width: 100%;
                    table-layout: fixed;
                }}
                table, th, td {{ 
                    border: 1px solid #000; 
                }}
                th, td {{ 
                    padding: 8px; 
                    text-align: left;
                    word-wrap: break-word;
                    overflow-wrap: break-word;
                }}
                th {{ 
                    background-color: #f2f2f2; 
                    font-weight: bold;
                }}
                tr:nth-child(even) {{ 
                    background-color: #f9f9f9; 
                }}
                code {{ 
                    font-family: Consolas, Monaco, 'Courier New', monospace;
                    background-color: #f5f5f5;
                    padding: 2px 4px;
                    border-radius: 4px;
                }}
                pre {{ 
                    background-color: #f5f5f5;
                    padding: 10px;
                    border-radius: 4px;
                    overflow-x: auto;
                    white-space: pre-wrap;
                }}
                blockquote {{
                    border-left: 4px solid #ddd;
                    padding-left: 15px;
                    margin-left: 0;
                    color: #666;
                }}
            </style>
        </head>
        <body>{htmlContent}</body>
        </html>";

                        try
                        {
                            // Update the line causing the error by awaiting the Task<string> result.
                            //htmlContent = await ExportMessageTo(htmlContent);
                            //htmlContent = PreprocessHtmlForWordExport(htmlContent);
                            // 创建HTML转换器 - 注意这里不使用不存在的HtmlConverterSettings
                            var converter = new HtmlConverter(mainPart);

                            // 解析HTML并添加到文档
                            var paragraphs = converter.Parse(htmlContent);

                            // 将段落添加到文档
                            foreach (var para in paragraphs)
                            {
                                body.AppendChild(para.CloneNode(true));
                            }
                        }
                        catch (Exception ex)
                        {
                            // 如果表格处理失败，尝试替代方案
                            _logger.LogError($"表格处理错误: {ex.Message}");

                            // 预处理HTML以修复表格
                            var processedHtml = ProcessTablesForWordExport(htmlContent);
                            var converter = new HtmlConverter(mainPart);
                            var paragraphs = converter.Parse(processedHtml);

                            foreach (var para in paragraphs)
                            {
                                body.AppendChild(para.CloneNode(true));
                            }
                        }

                        // 保存文档
                        doc.Save();
                    }
                    return ms.ToArray();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"DOCX生成失败: {ex.Message}, 堆栈: {ex.StackTrace}");
                throw new Exception($"DOCX生成失败: {ex.Message}", ex);
            }
        }

        public async Task<byte[]> ExportMessageToDocx1(string content)
        {
            try
            {
                // 预处理内容，确保表头前有空行
                content = PreprocessLatex(EnsureTableHeaderHasEmptyLine(content));
                using (var ms = new MemoryStream())
                {
                    using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
                    {
                        // 添加主文档部分
                        var mainPart = doc.AddMainDocumentPart();
                        mainPart.Document = new Document();
                        var body = mainPart.Document.AppendChild(new Body());

                        // 配置表格样式
                        var stylePart = mainPart.AddNewPart<StyleDefinitionsPart>();
                        GenerateCompleteStyles(stylePart);

                        // 使用加强的Markdown流水线，特别是表格支持
                        var pipeline = new MarkdownPipelineBuilder()
                                       .UseAdvancedExtensions()
                                       .UseBootstrap() // 使用Bootstrap扩展改善表格渲染
                                       .UsePipeTables() // 确保支持管道表格
                                       .UseGridTables() // 支持网格表格
                                       .UseEmphasisExtras() // 支持更多强调语法
                                       .UseTaskLists() // 支持任务列表
                                       .UseAutoIdentifiers() // 自动添加表格ID
                                       .UseCustomContainers() // 支持自定义容器
                                       .UseDefinitionLists() // 支持定义列表
                                       .UseFootnotes() // 支持脚注
                                       .UseAutoLinks() // 自动检测链接
                                       .UseListExtras() // 增强列表功能
                                       .UseMediaLinks() // 支持媒体链接
                                       .UseFigures() // 支持图表
                                       .UseGenericAttributes() // 支持通用属性
                                       .UseYamlFrontMatter() // 支持YAML前置元数据
                                       .Build();

                        // 将Markdown转换为HTML
                        var htmlContent = Markdig.Markdown.ToHtml(content, pipeline);

                        // 构建完整的HTML文档，优化表格样式
                        htmlContent = $@"
<!DOCTYPE html>
<html lang=""zh-CN"">
<head>
    <meta charset=""utf-8""/>
<link rel=""stylesheet"" href=""https://cdn.jsdelivr.net/npm/katex@0.16.22/dist/katex.min.css"" integrity=""sha384-5TcZemv2l/9On385z///+d7MSYlvIEw9FuZTIdZ14vJLqWphw7e7ZPuOiCHJcFCP"" crossorigin=""anonymous"">
<script defer src=""https://cdn.jsdelivr.net/npm/katex@0.16.22/dist/katex.min.js"" integrity=""sha384-cMkvdD8LoxVzGF/RPUKAcvmm49FQ0oxwDF3BGKtDXcEc+T1b2N+teh/OJfpU0jr6"" crossorigin=""anonymous""></script>
<script defer src=""https://cdn.jsdelivr.net/npm/katex@0.16.22/dist/contrib/auto-render.min.js"" integrity=""sha384-hCXGrW6PitJEwbkoStFjeJxv+fSOOQKOPbJxSfM6G5sWZjAyWhXiTIIAmQqnlLlh"" crossorigin=""anonymous""></script>


<script>
            document.addEventListener('DOMContentLoaded', function() {{
                renderMathInElement(document.body, {{
                    delimiters: [
                        {{left: '$$', right: '$$', display: true}},
                        {{left: '$', right: '$', display: false}},
                        {{left: '\\(', right: '\\)', display: false}},
                        {{left: '\\[', right: '\\]', display: true}},
                        {{left: '$$\\begin{{align}}', right: '\\end{{align}}$$', display: true}},
                        {{left: '\\begin{{equation}}', right: '\\end{{equation}}', display: true}},
                        {{left: '\\begin{{align}}', right: '\\end{{align}}', display: true}},
                        {{left: '\\begin{{alignat}}', right: '\\end{{alignat}}', display: true}},
                        {{left: '\\begin{{gather}}', right: '\\end{{gather}}', display: true}},
                        {{left: '\\begin{{CD}}', right: '\\end{{CD}}', display: true}},
                        {{left: '\\begin{{matrix}}', right: '\\end{{matrix}}', display: true}},
                        {{left: '\\begin{{pmatrix}}', right: '\\end{{pmatrix}}', display: true}},
                        {{left: '\\begin{{bmatrix}}', right: '\\end{{bmatrix}}', display: true}},
                        {{left: '\\begin{{vmatrix}}', right: '\\end{{vmatrix}}', display: true}}
                    ],
                    throwOnError: false,
                    errorColor: '#cc0000',
                    macros: {{
                        ""\\\\coloneqq"": ""≔"",
                        ""\\\\implies"": ""⟹"",
                        ""\\\\iff"": ""⟺"",
                        ""\\\\varnothing"": ""∅"",
                        ""\\\\Reals"": ""\\mathbb{{R}}"",
                        ""\\\\Naturals"": ""\\mathbb{{N}}"",
                        ""\\\\Complex"": ""\\mathbb{{C}}"",
                        ""\\\\Integers"": ""\\mathbb{{Z}}""
                    }},
                    strict: false,
                    trust: true,
                    fleqn: false,
                    output: 'html',         // 使用HTML输出以获得更好的显示效果
                    minRuleThickness: 0.08, // 调整分数线等元素的最小厚度
                    maxSize: 10,            // 设置最大大小限制
                    maxExpand: 1000,        // 提高展开宏的限制
                    displayMode: true,      // 启用显示模式
                    leqno: false            // 禁用左侧等式编号
                }});
            }});
        </script>
<style>
/* 优化KaTeX显示 */
.katex-display {{
    overflow-x: auto;
    overflow-y: visible; /* 改为visible允许内容超出 */
    padding: 18px 0;     /* 增加上下内边距 */
    margin: 1.2em 0;     /* 增加上下外边距 */
    text-align: center;
    min-height: 2.5em;   /* 设置最小高度 */
}}

.katex {{
    font-size: 1.15em;
    line-height: 1.8;    /* 增加行高 */
    text-rendering: auto;
}}

/* 行内公式样式优化 */
.katex-inline {{
    padding: 2px 3px;
    margin: 0 1px;
    border-radius: 4px;
    background-color: var(--math-bg);
    border: none;
    vertical-align: middle; /* 改善行内公式垂直对齐 */
}}

.katex-error {{
    color: #cc0000;
    background-color: #ffecec;
    padding: 2px 4px;
    border-radius: 3px;
    border: 1px solid #ffbaba;
}}

/* 隐藏公式序号 */
.katex .tag {{
    display: none !important;
}}

/* 矩阵样式优化 */
.katex .mord.mathnormal {{
    font-style: normal;
    font-weight: normal;
    font-family: 'KaTeX_Math', serif;
}}

/* 改善矩阵显示 */
.katex .mtable {{
    margin: 0.8em 0;     /* 增加矩阵上下间距 */
}}

.katex .mtable .arraycolsep {{
    width: 0.8em; /* 增加列间距 */
}}

/* 矩阵括号优化 */
.katex .delimsizing.size3 .delim-size3 {{
    font-size: 2.5em;
}}

.katex .vlist > span {{
    font-weight: normal;
}}

/* 分数样式优化 */
.katex .mfrac {{
    margin: 0 0.25em;
}}

.katex .mfrac .frac-line {{
    border-bottom-width: 0.08em;
    min-height: 0.08em;
}}

.katex .mfrac .frac-line::after {{
    border-bottom-width: 0.08em;
}}

/* 提高分式的可读性 */
.katex .mfrac .msubsup {{
    font-size: 0.9em;
}}

/* 调整上下标大小和位置 */
.katex .msupsub {{
    font-size: 0.85em;
    vertical-align: baseline;
}}

/* 处理长公式换行问题 */
.katex-display > .katex {{
    display: inline-block;
    max-width: 100%;
    text-align: center;
}}

.katex-display > .katex > .katex-html {{
    display: block;
    max-width: 100%;
    overflow-x: auto;
    overflow-y: visible; /* 改为visible允许内容超出 */
    padding: 6px 5px;    /* 增加上下内边距 */
    text-align: center;
    white-space: nowrap;
}}

/* 对齐环境优化 */
.katex .align {{
    display: flex;
    flex-direction: column;
    align-items: center;
    margin-top: 0.8em;   /* 增加上下间距 */
    margin-bottom: 0.8em;
}}

.katex .align-inner {{
    display: flex;
    flex-direction: column;
    align-items: flex-start;
    width: auto;
}}

/* 隐藏公式编号 */
.katex .eqn-num {{
    display: none !important;
}}

/* 特别处理align环境中的编号 */
.katex-display .katex .align .tag {{
    display: none !important;
}}

/* 增加公式容器防止裁剪 */
.katex-html {{
    padding-top: 8px;    /* 增加顶部内边距 */
    padding-bottom: 8px; /* 增加底部内边距 */
}}

/* 修复大型公式分割问题 */
.katex .vlist-t {{
    display: inline-table !important;
    table-layout: fixed;
}}

/* 优化大型矩阵和多行公式 */
.katex .vlist-s {{
    height: 2px; /* 增加行间距 */
}}
</style>
    <style>
        body {{ 
            font-family: 'SimSun', 'SimHei', 'Microsoft YaHei', 'Arial Unicode MS', Arial, sans-serif; 
            padding: 20px;
            font-size: 11pt;
            line-height: 1.8;
        }}
        table {{ 
            border-collapse: collapse; 
            width: 100%;
            margin-bottom: 1em;
            max-width: 100%;
            table-layout: fixed;
        }}
        table, th, td {{ 
            border: 1px solid #000; 
        }}
        th, td {{ 
            padding: 8px; 
            text-align: left;
            word-wrap: break-word;
            overflow-wrap: break-word;
        }}
        th {{ 
            background-color: #f2f2f2; 
            font-weight: bold;
        }}
        tr:nth-child(even) {{ 
            background-color: #f9f9f9; 
        }}
        code {{ 
            font-family: Consolas, Monaco, 'Courier New', monospace;
            background-color: #f5f5f5;
            padding: 2px 4px;
            border-radius: 4px;
        }}
        pre {{ 
            background-color: #f5f5f5;
            padding: 10px;
            border-radius: 4px;
            overflow-x: auto;
            white-space: pre-wrap;
        }}
        blockquote {{
            border-left: 4px solid #ddd;
            padding-left: 15px;
            margin-left: 0;
            color: #666;
        }}
    </style>
</head>
<body>{htmlContent}</body>
</html>";

                        try
                        {
                            // 创建HTML转换器 - 注意这里不使用不存在的HtmlConverterSettings
                            var converter = new HtmlConverter(mainPart);

                            // 解析HTML并添加到文档
                            var paragraphs = converter.Parse(htmlContent);

                            // 将段落添加到文档
                            foreach (var para in paragraphs)
                            {
                                body.AppendChild(para.CloneNode(true));
                            }
                        }
                        catch (Exception ex)
                        {
                            // 如果表格处理失败，尝试替代方案
                            _logger.LogError($"表格处理错误: {ex.Message}");

                            // 预处理HTML以修复表格
                            var processedHtml = ProcessTablesForWordExport(htmlContent);
                            var converter = new HtmlConverter(mainPart);
                            var paragraphs = converter.Parse(processedHtml);

                            foreach (var para in paragraphs)
                            {
                                body.AppendChild(para.CloneNode(true));
                            }
                        }

                        // 保存文档
                        doc.Save();
                    }
                    return ms.ToArray();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"DOCX生成失败: {ex.Message}, 堆栈: {ex.StackTrace}");
                throw new Exception($"DOCX生成失败: {ex.Message}", ex);
            }
        }
        // 为文档添加更完整的样式
        private void GenerateCompleteStyles(StyleDefinitionsPart styleDefinitionsPart)
        {
            var styles = new Styles();

            // 添加默认段落样式
            var normalStyle = new Style
            {
                Type = StyleValues.Paragraph,
                StyleId = "Normal",
                Default = true
            };
            normalStyle.Append(new StyleName { Val = "Normal" });
            normalStyle.Append(new PrimaryStyle());
            normalStyle.Append(
                new StyleRunProperties(
                    new RunFonts { Ascii = "SimSun", HighAnsi = "SimSun", EastAsia = "SimSun", ComplexScript = "SimSun" },
                    new FontSize { Val = "22" } // 11pt
                )
            );
            styles.Append(normalStyle);

            // 添加标题样式
            var titleStyle = new Style
            {
                Type = StyleValues.Paragraph,
                StyleId = "Title"
            };
            titleStyle.Append(new StyleName { Val = "Title" });
            titleStyle.Append(
                new StyleParagraphProperties(
                    new SpacingBetweenLines { Before = "480", After = "240" }
                )
            );
            titleStyle.Append(
                new StyleRunProperties(
                    new RunFonts { Ascii = "SimHei", HighAnsi = "SimHei", EastAsia = "SimHei" },
                    new Bold(),
                    new FontSize { Val = "36" }, // 18pt
                    new Color { Val = "2F5496" }
                )
            );
            styles.Append(titleStyle);

            // 添加副标题样式
            var subtitleStyle = new Style
            {
                Type = StyleValues.Paragraph,
                StyleId = "Subtitle"
            };
            subtitleStyle.Append(new StyleName { Val = "Subtitle" });
            subtitleStyle.Append(
                new StyleParagraphProperties(
                    new SpacingBetweenLines { Before = "240", After = "480" }
                )
            );
            subtitleStyle.Append(
                new StyleRunProperties(
                    new RunFonts { Ascii = "SimHei", HighAnsi = "SimHei", EastAsia = "SimHei" },
                    new Italic(),
                    new FontSize { Val = "24" }, // 12pt
                    new Color { Val = "595959" }
                )
            );
            styles.Append(subtitleStyle);

            // 添加Heading 1-6样式
            for (int i = 1; i <= 6; i++)
            {
                var headingStyle = new Style
                {
                    Type = StyleValues.Paragraph,
                    StyleId = $"Heading{i}"
                };
                headingStyle.Append(new StyleName { Val = $"Heading {i}" });

                int fontSize = 32 - (i * 2); // 从16pt逐渐减小
                string colorVal = i <= 3 ? "2F5496" : "333333";

                headingStyle.Append(
                    new StyleParagraphProperties(
                        new SpacingBetweenLines { Before = $"{240}", After = $"{120}" },
                        new OutlineLevel { Val = i - 1 }
                    )
                );
                headingStyle.Append(
                    new StyleRunProperties(
                        new RunFonts { Ascii = "SimHei", HighAnsi = "SimHei", EastAsia = "SimHei" },
                        new Bold(),
                        new FontSize { Val = $"{fontSize}" },
                        new Color { Val = colorVal }
                    )
                );
                styles.Append(headingStyle);
            }

            // 添加表格样式
            var tableStyle = new Style
            {
                Type = StyleValues.Table,
                StyleId = "TableStyle",
                Default = true
            };
            tableStyle.Append(new StyleName { Val = "Table Style" });

            // 添加表格边框样式
            var tableBorders = new TableBorders(
                new TopBorder { Val = BorderValues.Single, Size = 2, Color = "000000" },
                new BottomBorder { Val = BorderValues.Single, Size = 2, Color = "000000" },
                new LeftBorder { Val = BorderValues.Single, Size = 2, Color = "000000" },
                new RightBorder { Val = BorderValues.Single, Size = 2, Color = "000000" },
                new InsideHorizontalBorder { Val = BorderValues.Single, Size = 1, Color = "000000" },
                new InsideVerticalBorder { Val = BorderValues.Single, Size = 1, Color = "000000" }
            );

            tableStyle.Append(tableBorders);

            // 添加单元格边距
            var tableCellMarginDefault = new TableCellMarginDefault(
                new TopMargin { Width = "60" },
                new BottomMargin { Width = "60" },
                new LeftMargin { Width = "60" },
                new RightMargin { Width = "60" }
            );

            tableStyle.Append(tableCellMarginDefault);
            styles.Append(tableStyle);

            // 添加页眉样式
            var headerStyle = new Style
            {
                Type = StyleValues.Paragraph,
                StyleId = "Header"
            };
            headerStyle.Append(new StyleName { Val = "Header" });
            headerStyle.Append(
                new StyleParagraphProperties(
                    new Justification { Val = JustificationValues.Right },
                    new SpacingBetweenLines { After = "0" }
                )
            );
            headerStyle.Append(
                new StyleRunProperties(
                    new RunFonts { Ascii = "SimSun", HighAnsi = "SimSun", EastAsia = "SimSun" },
                    new FontSize { Val = "18" }, // 9pt
                    new Color { Val = "666666" }
                )
            );
            styles.Append(headerStyle);

            // 添加页脚样式
            var footerStyle = new Style
            {
                Type = StyleValues.Paragraph,
                StyleId = "Footer"
            };
            footerStyle.Append(new StyleName { Val = "Footer" });
            footerStyle.Append(
                new StyleParagraphProperties(
                    new Justification { Val = JustificationValues.Center },
                    new SpacingBetweenLines { After = "0" }
                )
            );
            footerStyle.Append(
                new StyleRunProperties(
                    new RunFonts { Ascii = "SimSun", HighAnsi = "SimSun", EastAsia = "SimSun" },
                    new FontSize { Val = "18" }, // 9pt
                    new Color { Val = "666666" }
                )
            );
            styles.Append(footerStyle);

            // 添加超链接样式
            var hyperlinkStyle = new Style
            {
                Type = StyleValues.Character,
                StyleId = "Hyperlink"
            };
            hyperlinkStyle.Append(new StyleName { Val = "Hyperlink" });
            hyperlinkStyle.Append(
                new StyleRunProperties(
                    new Color { Val = "0066CC" },
                    new Underline { Val = UnderlineValues.Single }
                )
            );
            styles.Append(hyperlinkStyle);

            // 添加代码块样式
            var codeStyle = new Style
            {
                Type = StyleValues.Paragraph,
                StyleId = "CodeBlock"
            };
            codeStyle.Append(new StyleName { Val = "Code Block" });
            codeStyle.Append(
                new StyleParagraphProperties(
                    new SpacingBetweenLines { Before = "240", After = "240" },
                    new Indentation { Left = "720" },
                    new ParagraphBorders(
                        new TopBorder { Val = BorderValues.Single, Size = 2, Color = "AAAAAA" },
                        new BottomBorder { Val = BorderValues.Single, Size = 2, Color = "AAAAAA" },
                        new LeftBorder { Val = BorderValues.Single, Size = 8, Color = "AAAAAA" },
                        new RightBorder { Val = BorderValues.Single, Size = 2, Color = "AAAAAA" }
                    ),
                    new Shading { Val = ShadingPatternValues.Clear, Color = "auto", Fill = "F5F5F5" }
                )
            );
            codeStyle.Append(
                new StyleRunProperties(
                    new RunFonts { Ascii = "Consolas", HighAnsi = "Consolas" },
                    new FontSize { Val = "20" } // 10pt
                )
            );
            styles.Append(codeStyle);

            styleDefinitionsPart.Styles = styles;
        }

        // 预处理HTML中的表格，确保它们能正确转换
        private string ProcessTablesForWordExport(string htmlContent)
        {
            // 简化表格结构，确保每个单元格都有明确的宽度和标准结构
            var processedHtml = Regex.Replace(
                htmlContent,
                @"<table[^>]*>",
                "<table style='width:100%; border-collapse:collapse;'>"
            );

            // 确保所有单元格有标准边框
            processedHtml = Regex.Replace(
                processedHtml,
                @"<(td|th)[^>]*>",
                match =>
                {
                    var tag = match.Value;
                    if (!tag.Contains("style"))
                        return tag.Insert(tag.Length - 1, " style='border:1px solid black; padding:4px;'");
                    else if (!tag.Contains("border"))
                        return tag.Insert(tag.Length - 1, " border='1'");
                    return tag;
                }
            );

            // 如果存在复杂表格，可能需要添加额外的表格标记来帮助转换
            processedHtml = Regex.Replace(
                processedHtml,
                @"<tr[^>]*>",
                "<tr style='page-break-inside:avoid'>"
            );

            return processedHtml;
        }

        // 添加新的帮助方法处理表头前无空行的情况
        private string EnsureTableHeaderHasEmptyLine(string content)
        {
            // 使用正则表达式匹配表格开始的地方（文本后紧跟着一个表格的起始）
            // 匹配模式解释:
            // 1. 查找一个非空行后面紧跟着一个以 | 开头的行（可能是表头）
            // 2. 确保这个 | 开头的行不是表格内部的行（前面的行不是表格行）
            return Regex.Replace(
                content,
                @"(\S[^\r\n]*(?<!\|))(\r?\n)((?:\|[^\r\n]+\|[^\r\n]*)+)",
                m =>
                {
                    // 检查前一行是否已经是空行（应该是一个非空行才需要添加空行）
                    if (string.IsNullOrWhiteSpace(m.Groups[1].Value))
                        return m.Value; // 已有空行，不做改变

                    // 在表头前插入空行
                    return m.Groups[1].Value + m.Groups[2].Value + Environment.NewLine + m.Groups[3].Value;
                }
            );
        }

        private string PreprocessLatex(string markdown)
        {

            // 兼容 \[ ... \] 公式为 $$ ... $$
            string pattern = @"\\\[(.*?)\\\]";
            string replacement = @"$$1$$";
            string result = System.Text.RegularExpressions.Regex.Replace(markdown, pattern, replacement, System.Text.RegularExpressions.RegexOptions.Singleline);

            // 在 PreprocessLatex 方法中添加如下替换
            pattern = @"\\begin\{split\}";
            replacement = "\\begin{aligned}";
            result = System.Text.RegularExpressions.Regex.Replace(result, pattern, replacement);

            pattern = @"\\end\{split\}";
            replacement = "\\end{aligned}";
            result = System.Text.RegularExpressions.Regex.Replace(result, pattern, replacement);


            // 处理紧凑格式的align环境
            pattern = @"(\$\$)\\begin\{align\}";
            replacement = "\n$1\n\\begin{align}";
            result = System.Text.RegularExpressions.Regex.Replace(result, pattern, replacement);

            // 处理align环境的结束标记
            pattern = @"\\end\{align\}(\$\$)";
            replacement = "\\end{align}\n$1";
            result = System.Text.RegularExpressions.Regex.Replace(result, pattern, replacement);

            // 处理aligned环境
            pattern = @"(\$\$)\\begin\{aligned\}";
            replacement = "\n$1\n\\begin{aligned}";
            result = System.Text.RegularExpressions.Regex.Replace(result, pattern, replacement);

            pattern = @"\\end\{aligned\}(\$\$)";
            replacement = "\\end{aligned}\n$1";
            result = System.Text.RegularExpressions.Regex.Replace(result, pattern, replacement);

            // 处理array环境
            pattern = @"(\$\$)\\begin\{array\}";
            replacement = "\n$1\n\\begin{array}";
            result = System.Text.RegularExpressions.Regex.Replace(result, pattern, replacement);

            pattern = @"\\end\{array\}(\$\$)";
            replacement = "\\end{array}\n$1";
            result = System.Text.RegularExpressions.Regex.Replace(result, pattern, replacement);

            // 处理matrix环境
            pattern = @"(\$\$)\\begin\{(b|p|v|B|V|P)?matrix\}";
            replacement = "\n$1\n\\begin{$2matrix}";
            result = System.Text.RegularExpressions.Regex.Replace(result, pattern, replacement);

            pattern = @"\\end\{(b|p|v|B|V|P)?matrix\}(\$\$)";
            replacement = "\\end{$1matrix}\n$2";
            result = System.Text.RegularExpressions.Regex.Replace(result, pattern, replacement);

            // 确保公式周围有足够的空间
            pattern = @"(\$\$)(.*?)(\$\$)";
            replacement = "\n\n$1$2$3\n\n";
            result = System.Text.RegularExpressions.Regex.Replace(result, pattern, replacement,
                                                                System.Text.RegularExpressions.RegexOptions.Singleline);

            // 优化行内公式的间距
            pattern = @"([^\$])\$([^\$]+?)\$([^\$])";
            replacement = "$1 $$$2$$ $3";
            result = System.Text.RegularExpressions.Regex.Replace(result, pattern, replacement);

            return result;
        }
        public static string DelAllString(string input, string beginDelimiter, string endDelimiter)
        {
            if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(beginDelimiter) || string.IsNullOrEmpty(endDelimiter) || !input.Contains(beginDelimiter) || !input.Contains(endDelimiter))
                return input;

            // 使用 Span<char> 进行更高效的字符串操作
            var span = input.AsSpan();

            var result = new System.Text.StringBuilder(input.Length);  // 使用 StringBuilder 来避免多次字符串拼接

            while (true)
            {
                int Index = span.IndexOf(beginDelimiter);
                if (Index >= 0)
                {
                    result.Append(span.Slice(0, Index));
                    span = span.Slice(Index + beginDelimiter.Length);

                    Index = span.IndexOf(endDelimiter);
                    if (Index >= 0)
                    {
                        // 跳过分隔符之间的文本，并移动到结束分隔符之后
                        span = span.Slice(Index + endDelimiter.Length);
                    }
                    else
                    {
                        result.Append(beginDelimiter);
                        result.Append(span);
                        break;
                    }
                }
                else
                {
                    result.Append(span);
                    break;
                }

            }

            return result.ToString();
        }
        public async Task<byte[]> ExportMessageToPdf(string content)
        {
            try
            {
                content = DelAllString(content, "<think>", "</think>");
                // 预处理内容，确保表头前有空行
                content = PreprocessLatex(EnsureTableHeaderHasEmptyLine(content));
                // 使用加强的Markdown流水线，特别是表格支持
                var pipeline = new MarkdownPipelineBuilder()
                               .UseAdvancedExtensions()
                               .UseBootstrap() // 使用Bootstrap扩展改善表格渲染
                               .UsePipeTables() // 确保支持管道表格
                               .UseGridTables() // 支持网格表格
                               .UseEmphasisExtras() // 支持更多强调语法
                               .UseTaskLists() // 支持任务列表
                               .UseAutoIdentifiers() // 自动添加表格ID
                               .UseCustomContainers() // 支持自定义容器
                               .UseDefinitionLists() // 支持定义列表
                               .UseFootnotes() // 支持脚注
                               .UseAutoLinks() // 自动检测链接
                               .UseListExtras() // 增强列表功能
                               .UseMediaLinks() // 支持媒体链接
                               .UseFigures() // 支持图表
                               .UseGenericAttributes() // 支持通用属性
                               .UseYamlFrontMatter() // 支持YAML前置元数据
                               .Build();

                // 将Markdown转换为HTML
                var htmlContent = Markdig.Markdown.ToHtml(content, pipeline);

                // 注册编码和字体，增强表格样式
                htmlContent = $@"
<!DOCTYPE html>
<html lang=""zh-CN"">
<head>
    <meta charset=""utf-8""/>

    <style>
        @page {{ 
            margin: 2.5cm 1.5cm;
            size: A4;
        }}
        body {{ 
            font-family: 'SimSun',  'Microsoft YaHei', 'Arial Unicode MS', Arial, sans-serif; 
            padding: 20px;
           
            
        }}
        /* 增强表格样式，提高表格稳定性和兼容性 */
        table {{ 
            border-collapse: collapse; 
            width: 100%;
            margin-bottom: 1.5em;
            page-break-inside: auto;
            max-width: 100%;
            table-layout: fixed;
        }}
        table, th, td {{ 
            border: 1px solid #000; 
        }}
        th, td {{ 
            padding: 6px; 
            text-align: left;
            word-wrap: break-word;
            overflow-wrap: break-word;
            max-width: 100%;
        }}
        th {{ 
            background-color: #f2f2f2; 
            font-weight: bold;
        }}
        tr {{ 
            page-break-inside: avoid;
        }}
        /* 确保表格内容不会溢出 */
        table, tr, td, th, tbody, thead, tfoot {{
            page-break-inside: avoid !important;
        }}
        /* 代码样式 */
        code {{ 
            font-family: Consolas, Monaco, 'Courier New', monospace;
            background-color: #f5f5f5;
            padding: 2px 4px;
            border-radius: 4px;
            font-size: 90%;
        }}
        pre {{ 
            background-color: #f5f5f5;
            padding: 10px;
            border-radius: 4px;
            overflow-x: auto;
            white-space: pre-wrap;
        }}
        blockquote {{
            border-left: 4px solid #ddd;
            padding-left: 15px;
            margin-left: 0;
            color: #666;
        }}
        /* 使内容适应页面 */
        img {{
            max-width: 100%;
        }}
    </style>
</head>
<body>{htmlContent}</body>
</html>";
                //htmlContent = await ExportMessageTo(htmlContent);
                //htmlContent = PreprocessHtmlForWordExport(htmlContent);
                using (var ms = new MemoryStream())
                {
                    using (var htmlms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(htmlContent)))
                    {
                        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
                        var properties = new ConverterProperties();
                        var fontProvider = new FontProvider();

                        // 添加默认字体
                        fontProvider.AddStandardPdfFonts();

                        // 添加中文字体 - 尝试添加系统中常见的中文字体
                        try
                        {
                            var fontPaths = new[]
                            {
                        "simsun.ttc",    // 宋体
                        "Seguiemj.ttf", // Segoe UI Emoji
                        "msyh.ttc",      // 微软雅黑
                        "simkai.ttf",    // 楷体
                        "simfang.ttf",   // 仿宋，增加一种字体支持
                        "SIMLI.TTF",     // 隶书
                        "STKAITI.TTF",   // 楷体
                        "STFANGSO.TTF"   // 仿宋
                    };

                            var fontDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
                            foreach (var font in fontPaths)
                            {
                                var path = Path.Combine(fontDir, font);
                                if (File.Exists(path))
                                {
                                    fontProvider.AddFont(path);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            // 如果加载系统字体失败，记录错误但继续使用标准字体
                            _logger.LogWarning($"加载系统字体失败: {ex.Message}");
                        }

                        properties.SetFontProvider(fontProvider);

                        // 设置PDF版本和兼容性
                        //properties.SetPdfVersion(iText.Kernel.Pdf.PdfVersion.PDF_1_7);

                        // 增加转换时的内存限制
                        //properties.SetTagWorkerFactory(new iText.Html2pdf.AttachmentTagWorkerFactory());

                        // 优化表格处理
                        properties.SetBaseUri("");

                        // 提高文档处理能力 - 增加设备宽度，提高复杂布局处理能力
                        iText.StyledXmlParser.Css.Media.MediaDeviceDescription mediaDeviceDescription =
                            new iText.StyledXmlParser.Css.Media.MediaDeviceDescription(
                                iText.StyledXmlParser.Css.Media.MediaType.SCREEN);
                        mediaDeviceDescription.SetWidth(1600); // 增加宽度以适应复杂表格
                        properties.SetMediaDeviceDescription(mediaDeviceDescription);

                        // 转换HTML到PDF，使用异常处理捕获详细错误信息
                        try
                        {
                            iText.Html2pdf.HtmlConverter.ConvertToPdf(htmlms, ms, properties);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError($"HTML转PDF失败，详细错误: {ex.Message}, 堆栈: {ex.StackTrace}");
                            throw new Exception($"转换PDF时发生错误: {ex.Message}", ex);
                        }

                        return ms.ToArray();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"PDF生成失败: {ex.Message}, 堆栈: {ex.StackTrace}");
                throw new Exception($"PDF生成失败: {ex.Message}", ex);
            }
        }
        public async Task<byte[]> ExportMessageToPdf1(string content)
        {
            try
            {
                // 预处理内容，确保表头前有空行
                content = PreprocessLatex(EnsureTableHeaderHasEmptyLine(content));
                // 使用加强的Markdown流水线，特别是表格支持
                var pipeline = new MarkdownPipelineBuilder()
                               .UseAdvancedExtensions()
                               .UseBootstrap() // 使用Bootstrap扩展改善表格渲染
                               .UsePipeTables() // 确保支持管道表格
                               .UseGridTables() // 支持网格表格
                               .UseEmphasisExtras() // 支持更多强调语法
                               .UseTaskLists() // 支持任务列表
                               .UseAutoIdentifiers() // 自动添加表格ID
                               .UseCustomContainers() // 支持自定义容器
                               .UseDefinitionLists() // 支持定义列表
                               .UseFootnotes() // 支持脚注
                               .UseAutoLinks() // 自动检测链接
                               .UseListExtras() // 增强列表功能
                               .UseMediaLinks() // 支持媒体链接
                               .UseFigures() // 支持图表
                               .UseGenericAttributes() // 支持通用属性
                               .UseYamlFrontMatter() // 支持YAML前置元数据
                               .Build();

                // 将Markdown转换为HTML
                var htmlContent = Markdig.Markdown.ToHtml(content, pipeline);

                // 注册编码和字体，增强表格样式
                htmlContent = $@"
<!DOCTYPE html>
<html lang=""zh-CN"">
<head>
    <meta charset=""utf-8""/>
<link rel=""stylesheet"" href=""https://cdn.jsdelivr.net/npm/katex@0.16.22/dist/katex.min.css"" integrity=""sha384-5TcZemv2l/9On385z///+d7MSYlvIEw9FuZTIdZ14vJLqWphw7e7ZPuOiCHJcFCP"" crossorigin=""anonymous"">
<script defer src=""https://cdn.jsdelivr.net/npm/katex@0.16.22/dist/katex.min.js"" integrity=""sha384-cMkvdD8LoxVzGF/RPUKAcvmm49FQ0oxwDF3BGKtDXcEc+T1b2N+teh/OJfpU0jr6"" crossorigin=""anonymous""></script>
<script defer src=""https://cdn.jsdelivr.net/npm/katex@0.16.22/dist/contrib/auto-render.min.js"" integrity=""sha384-hCXGrW6PitJEwbkoStFjeJxv+fSOOQKOPbJxSfM6G5sWZjAyWhXiTIIAmQqnlLlh"" crossorigin=""anonymous""></script>


<script>
            document.addEventListener('DOMContentLoaded', function() {{
                renderMathInElement(document.body, {{
                    delimiters: [
                        {{left: '$$', right: '$$', display: true}},
                        {{left: '$', right: '$', display: false}},
                        {{left: '\\(', right: '\\)', display: false}},
                        {{left: '\\[', right: '\\]', display: true}},
                        {{left: '$$\\begin{{align}}', right: '\\end{{align}}$$', display: true}},
                        {{left: '\\begin{{equation}}', right: '\\end{{equation}}', display: true}},
                        {{left: '\\begin{{align}}', right: '\\end{{align}}', display: true}},
                        {{left: '\\begin{{alignat}}', right: '\\end{{alignat}}', display: true}},
                        {{left: '\\begin{{gather}}', right: '\\end{{gather}}', display: true}},
                        {{left: '\\begin{{CD}}', right: '\\end{{CD}}', display: true}},
                        {{left: '\\begin{{matrix}}', right: '\\end{{matrix}}', display: true}},
                        {{left: '\\begin{{pmatrix}}', right: '\\end{{pmatrix}}', display: true}},
                        {{left: '\\begin{{bmatrix}}', right: '\\end{{bmatrix}}', display: true}},
                        {{left: '\\begin{{vmatrix}}', right: '\\end{{vmatrix}}', display: true}}
                    ],
                    throwOnError: false,
                    errorColor: '#cc0000',
                    macros: {{
                        ""\\\\coloneqq"": ""≔"",
                        ""\\\\implies"": ""⟹"",
                        ""\\\\iff"": ""⟺"",
                        ""\\\\varnothing"": ""∅"",
                        ""\\\\Reals"": ""\\mathbb{{R}}"",
                        ""\\\\Naturals"": ""\\mathbb{{N}}"",
                        ""\\\\Complex"": ""\\mathbb{{C}}"",
                        ""\\\\Integers"": ""\\mathbb{{Z}}""
                    }},
                    strict: false,
                    trust: true,
                    fleqn: false,
                    output: 'html',         // 使用HTML输出以获得更好的显示效果
                    minRuleThickness: 0.08, // 调整分数线等元素的最小厚度
                    maxSize: 10,            // 设置最大大小限制
                    maxExpand: 1000,        // 提高展开宏的限制
                    displayMode: true,      // 启用显示模式
                    leqno: false            // 禁用左侧等式编号
                }});
            }});
        </script>
<style>
/* 优化KaTeX显示 */
.katex-display {{
    overflow-x: auto;
    overflow-y: visible; /* 改为visible允许内容超出 */
    padding: 18px 0;     /* 增加上下内边距 */
    margin: 1.2em 0;     /* 增加上下外边距 */
    text-align: center;
    min-height: 2.5em;   /* 设置最小高度 */
}}

.katex {{
    font-size: 1.15em;
    line-height: 1.8;    /* 增加行高 */
    text-rendering: auto;
}}

/* 行内公式样式优化 */
.katex-inline {{
    padding: 2px 3px;
    margin: 0 1px;
    border-radius: 4px;
    background-color: var(--math-bg);
    border: none;
    vertical-align: middle; /* 改善行内公式垂直对齐 */
}}

.katex-error {{
    color: #cc0000;
    background-color: #ffecec;
    padding: 2px 4px;
    border-radius: 3px;
    border: 1px solid #ffbaba;
}}

/* 隐藏公式序号 */
.katex .tag {{
    display: none !important;
}}

/* 矩阵样式优化 */
.katex .mord.mathnormal {{
    font-style: normal;
    font-weight: normal;
    font-family: 'KaTeX_Math', serif;
}}

/* 改善矩阵显示 */
.katex .mtable {{
    margin: 0.8em 0;     /* 增加矩阵上下间距 */
}}

.katex .mtable .arraycolsep {{
    width: 0.8em; /* 增加列间距 */
}}

/* 矩阵括号优化 */
.katex .delimsizing.size3 .delim-size3 {{
    font-size: 2.5em;
}}

.katex .vlist > span {{
    font-weight: normal;
}}

/* 分数样式优化 */
.katex .mfrac {{
    margin: 0 0.25em;
}}

.katex .mfrac .frac-line {{
    border-bottom-width: 0.08em;
    min-height: 0.08em;
}}

.katex .mfrac .frac-line::after {{
    border-bottom-width: 0.08em;
}}

/* 提高分式的可读性 */
.katex .mfrac .msubsup {{
    font-size: 0.9em;
}}

/* 调整上下标大小和位置 */
.katex .msupsub {{
    font-size: 0.85em;
    vertical-align: baseline;
}}

/* 处理长公式换行问题 */
.katex-display > .katex {{
    display: inline-block;
    max-width: 100%;
    text-align: center;
}}

.katex-display > .katex > .katex-html {{
    display: block;
    max-width: 100%;
    overflow-x: auto;
    overflow-y: visible; /* 改为visible允许内容超出 */
    padding: 6px 5px;    /* 增加上下内边距 */
    text-align: center;
    white-space: nowrap;
}}

/* 对齐环境优化 */
.katex .align {{
    display: flex;
    flex-direction: column;
    align-items: center;
    margin-top: 0.8em;   /* 增加上下间距 */
    margin-bottom: 0.8em;
}}

.katex .align-inner {{
    display: flex;
    flex-direction: column;
    align-items: flex-start;
    width: auto;
}}

/* 隐藏公式编号 */
.katex .eqn-num {{
    display: none !important;
}}

/* 特别处理align环境中的编号 */
.katex-display .katex .align .tag {{
    display: none !important;
}}

/* 增加公式容器防止裁剪 */
.katex-html {{
    padding-top: 8px;    /* 增加顶部内边距 */
    padding-bottom: 8px; /* 增加底部内边距 */
}}

/* 修复大型公式分割问题 */
.katex .vlist-t {{
    display: inline-table !important;
    table-layout: fixed;
}}

/* 优化大型矩阵和多行公式 */
.katex .vlist-s {{
    height: 2px; /* 增加行间距 */
}}
</style>
    <style>
        @page {{ 
            margin: 2.5cm 1.5cm;
            size: A4;
        }}
        body {{ 
            font-family: 'SimSun', 'SimHei', 'Microsoft YaHei', 'Arial Unicode MS', Arial, sans-serif; 
            padding: 20px;
            font-size: 11pt;
            line-height: 1.8;
        }}
        /* 增强表格样式，提高表格稳定性和兼容性 */
        table {{ 
            border-collapse: collapse; 
            width: 100%;
            margin-bottom: 1.5em;
            page-break-inside: auto;
            max-width: 100%;
            table-layout: fixed;
        }}
        table, th, td {{ 
            border: 1px solid #000; 
        }}
        th, td {{ 
            padding: 6px; 
            text-align: left;
            word-wrap: break-word;
            overflow-wrap: break-word;
            max-width: 100%;
        }}
        th {{ 
            background-color: #f2f2f2; 
            font-weight: bold;
        }}
        tr {{ 
            page-break-inside: avoid;
        }}
        /* 确保表格内容不会溢出 */
        table, tr, td, th, tbody, thead, tfoot {{
            page-break-inside: avoid !important;
        }}
        /* 代码样式 */
        code {{ 
            font-family: Consolas, Monaco, 'Courier New', monospace;
            background-color: #f5f5f5;
            padding: 2px 4px;
            border-radius: 4px;
            font-size: 90%;
        }}
        pre {{ 
            background-color: #f5f5f5;
            padding: 10px;
            border-radius: 4px;
            overflow-x: auto;
            white-space: pre-wrap;
        }}
        blockquote {{
            border-left: 4px solid #ddd;
            padding-left: 15px;
            margin-left: 0;
            color: #666;
        }}
        /* 使内容适应页面 */
        img {{
            max-width: 100%;
        }}
    </style>
</head>
<body>{htmlContent}</body>
</html>";

                using (var ms = new MemoryStream())
                {
                    using (var htmlms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(htmlContent)))
                    {
                        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
                        var properties = new ConverterProperties();
                        var fontProvider = new FontProvider();

                        // 添加默认字体
                        fontProvider.AddStandardPdfFonts();

                        // 添加中文字体 - 尝试添加系统中常见的中文字体
                        try
                        {
                            var fontPaths = new[]
                            {
                        "simsun.ttc",    // 宋体
                        "simhei.ttf",    // 黑体
                        "msyh.ttc",      // 微软雅黑
                        "simkai.ttf",    // 楷体
                        "simfang.ttf",   // 仿宋，增加一种字体支持
                        "SIMLI.TTF",     // 隶书
                        "STKAITI.TTF",   // 楷体
                        "STFANGSO.TTF"   // 仿宋
                    };

                            var fontDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
                            foreach (var font in fontPaths)
                            {
                                var path = Path.Combine(fontDir, font);
                                if (File.Exists(path))
                                {
                                    fontProvider.AddFont(path);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            // 如果加载系统字体失败，记录错误但继续使用标准字体
                            _logger.LogWarning($"加载系统字体失败: {ex.Message}");
                        }

                        properties.SetFontProvider(fontProvider);

                        // 设置PDF版本和兼容性
                        //properties.SetPdfVersion(iText.Kernel.Pdf.PdfVersion.PDF_1_7);

                        // 增加转换时的内存限制
                        //properties.SetTagWorkerFactory(new iText.Html2pdf.AttachmentTagWorkerFactory());

                        // 优化表格处理
                        properties.SetBaseUri("");

                        // 提高文档处理能力 - 增加设备宽度，提高复杂布局处理能力
                        iText.StyledXmlParser.Css.Media.MediaDeviceDescription mediaDeviceDescription =
                            new iText.StyledXmlParser.Css.Media.MediaDeviceDescription(
                                iText.StyledXmlParser.Css.Media.MediaType.SCREEN);
                        mediaDeviceDescription.SetWidth(1600); // 增加宽度以适应复杂表格
                        properties.SetMediaDeviceDescription(mediaDeviceDescription);

                        // 转换HTML到PDF，使用异常处理捕获详细错误信息
                        try
                        {
                            iText.Html2pdf.HtmlConverter.ConvertToPdf(htmlms, ms, properties);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError($"HTML转PDF失败，详细错误: {ex.Message}, 堆栈: {ex.StackTrace}");
                            throw new Exception($"转换PDF时发生错误: {ex.Message}", ex);
                        }

                        return ms.ToArray();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"PDF生成失败: {ex.Message}, 堆栈: {ex.StackTrace}");
                throw new Exception($"PDF生成失败: {ex.Message}", ex);
            }
        }
        public async Task<byte[]> ExportMessageToPdf2(string content)
        {
            try
            {
                content = DelAllString(content, "<think>", "</think>");
                // 预处理内容，确保表头前有空行
                content = PreprocessLatex(EnsureTableHeaderHasEmptyLine(content));
                // 使用加强的Markdown流水线，特别是表格支持
                var pipeline = new MarkdownPipelineBuilder()
                               .UseAdvancedExtensions()
                               .UseBootstrap() // 使用Bootstrap扩展改善表格渲染
                               .UsePipeTables() // 确保支持管道表格
                               .UseGridTables() // 支持网格表格
                               .UseEmphasisExtras() // 支持更多强调语法
                               .UseTaskLists() // 支持任务列表
                               .UseAutoIdentifiers() // 自动添加表格ID
                               .UseCustomContainers() // 支持自定义容器
                               .UseDefinitionLists() // 支持定义列表
                               .UseFootnotes() // 支持脚注
                               .UseAutoLinks() // 自动检测链接
                               .UseListExtras() // 增强列表功能
                               .UseMediaLinks() // 支持媒体链接
                               .UseFigures() // 支持图表
                               .UseGenericAttributes() // 支持通用属性
                               .UseYamlFrontMatter() // 支持YAML前置元数据
                               .Build();

                // 将Markdown转换为HTML
                var htmlContent = Markdig.Markdown.ToHtml(content, pipeline);

                // 注册编码和字体，增强表格样式，添加Emoji支持
                htmlContent = $@"
<!DOCTYPE html>
<html lang=""zh-CN"">
<head>
    <meta charset=""utf-8""/>
    <style>
        @page {{ 
            margin: 2.5cm 1.5cm;
            size: A4;
        }}
        body {{ 
            font-family: 'Segoe UI Emoji', 'Apple Color Emoji', 'Noto Color Emoji', 'SimSun', 'Microsoft YaHei', 'Arial Unicode MS', Arial, sans-serif; 
            padding: 20px;
        }}
        /* Emoji特定样式 */
        .emoji {{
            font-family: 'Segoe UI Emoji', 'Apple Color Emoji', 'Noto Color Emoji', sans-serif;
            color: inherit;
            font-style: normal;
        }}
        /* 增强表格样式，提高表格稳定性和兼容性 */
        table {{ 
            border-collapse: collapse; 
            width: 100%;
            margin-bottom: 1.5em;
            page-break-inside: auto;
            max-width: 100%;
            table-layout: fixed;
        }}
        table, th, td {{ 
            border: 1px solid #000; 
        }}
        th, td {{ 
            padding: 6px; 
            text-align: left;
            word-wrap: break-word;
            overflow-wrap: break-word;
            max-width: 100%;
        }}
        th {{ 
            background-color: #f2f2f2; 
            font-weight: bold;
        }}
        tr {{ 
            page-break-inside: avoid;
        }}
        /* 确保表格内容不会溢出 */
        table, tr, td, th, tbody, thead, tfoot {{
            page-break-inside: avoid !important;
        }}
        /* 代码样式 */
        code {{ 
            font-family: Consolas, Monaco, 'Courier New', monospace;
            background-color: #f5f5f5;
            padding: 2px 4px;
            border-radius: 4px;
            font-size: 90%;
        }}
        pre {{ 
            background-color: #f5f5f5;
            padding: 10px;
            border-radius: 4px;
            overflow-x: auto;
            white-space: pre-wrap;
        }}
        blockquote {{
            border-left: 4px solid #ddd;
            padding-left: 15px;
            margin-left: 0;
            color: #666;
        }}
        /* 使内容适应页面 */
        img {{
            max-width: 100%;
        }}
    </style>
    <!-- 添加基本Emoji支持的脚本 -->
    
</head>
<body>{htmlContent}</body>
</html>";

                using (var ms = new MemoryStream())
                {
                    using (var htmlms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(htmlContent)))
                    {
                        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
                        var properties = new ConverterProperties();
                        var fontProvider = new FontProvider();

                        // 添加默认字体
                        fontProvider.AddStandardPdfFonts();

                        // 添加支持Emoji的字体
                        try
                        {
                            var fontPaths = new[]
                            {
                        "simsun.ttc",     // 宋体
                        "Seguiemj.ttf",   // Segoe UI Emoji - 关键的Emoji字体
                        "segoeui.ttf",    // Segoe UI - 基本Unicode支持
                        "msjh.ttc",       // 微软正黑体 - 更好的CJK支持
                        "msyh.ttc",       // 微软雅黑
                        "simkai.ttf",     // 楷体
                        "simfang.ttf",    // 仿宋
                        "SIMLI.TTF",      // 隶书
                        "STKAITI.TTF",    // 楷体
                        "STFANGSO.TTF",   // 仿宋
                        "seguisym.ttf",   // Segoe UI Symbol - 支持更多符号
                        "NotoColorEmoji.ttf" // 如果系统有Noto Color Emoji
                    };

                            var fontDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
                            foreach (var font in fontPaths)
                            {
                                var path = Path.Combine(fontDir, font);
                                if (File.Exists(path))
                                {
                                    fontProvider.AddFont(path);
                                    _logger.LogInformation($"已加载字体: {font}");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            // 如果加载系统字体失败，记录错误但继续使用标准字体
                            _logger.LogWarning($"加载系统字体失败: {ex.Message}");
                        }

                        // 设置字体提供程序使用更广泛的字体选择
                        properties.SetFontProvider(fontProvider);

                        // 配置字体解析器以支持彩色Emoji
                        properties.SetFontProvider(fontProvider);

                        // 优化表格处理
                        properties.SetBaseUri("");

                        // 提高文档处理能力 - 增加设备宽度，提高复杂布局处理能力
                        iText.StyledXmlParser.Css.Media.MediaDeviceDescription mediaDeviceDescription =
                            new iText.StyledXmlParser.Css.Media.MediaDeviceDescription(
                                iText.StyledXmlParser.Css.Media.MediaType.SCREEN);
                        mediaDeviceDescription.SetWidth(1600); // 增加宽度以适应复杂表格
                        properties.SetMediaDeviceDescription(mediaDeviceDescription);

                        // 转换HTML到PDF，使用异常处理捕获详细错误信息
                        try
                        {
                            iText.Html2pdf.HtmlConverter.ConvertToPdf(htmlms, ms, properties);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError($"HTML转PDF失败，详细错误: {ex.Message}, 堆栈: {ex.StackTrace}");
                            throw new Exception($"转换PDF时发生错误: {ex.Message}", ex);
                        }

                        return ms.ToArray();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"PDF生成失败: {ex.Message}, 堆栈: {ex.StackTrace}");
                throw new Exception($"PDF生成失败: {ex.Message}", ex);
            }
        }


    }

    // 在 ChatService 类中添加此扩展方法
    public static class TypeExtensions
    {
        public static bool IsAnonymousType(this Type type)
        {
            return type.Name.StartsWith("<>")
                && type.GetCustomAttributes(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false).Length > 0;
        }
    }
}