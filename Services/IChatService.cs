using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChatBot.Models;
using System.Net.Http.Headers;
using System.Net.Http;
using System;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Options;
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

        IAsyncEnumerable<string> GenerateStreamAsync( ChatRequest request, CancellationToken cancellationToken);
        List<string> GetAvailableModels();
        ChatModelConfig GetModelConfig(string modelName);
    }

    /// <summary>
    /// 通义千问API聊天服务实现
    /// </summary>
    public class QianWenChatService : IChatService
    {
        static string SessionId = string.Empty;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _configuration;
        private readonly ILogger<QianWenChatService> _logger;
        private readonly JsonSerializerOptions _jsonOptions;
        private readonly ChatModelSettings _modelSettings;

        public QianWenChatService(
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            ILogger<QianWenChatService> logger, 
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
                await foreach (var item in GenerateStreamViaOpenAIAsync(config, request, cancellationToken))
                {
                    yield return item;
                }
            }
        }


        // 流式输出 - OpenAI 兼容方式
        public async IAsyncEnumerable<string> GenerateStreamViaOpenAIAsync(ChatModelConfig modelconfg ,ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken )
        {
            // 验证配置
            var apiKey = Environment.GetEnvironmentVariable("AiApiKey");
            var apiEndpoint = modelconfg.ApiEndpoint;

            if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiEndpoint))
            {
                throw new InvalidOperationException("通义千问API配置缺失");
            }

            // 创建HTTP客户端
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

            // 准备请求内容
            var requestContent = new
            {

                model = modelconfg.Model,
                messages = ToMessages(request),
                stream = modelconfg.Stream,
                temperature = modelconfg.Temperature,
                max_tokens = modelconfg.MaxTokens,
                enable_search = modelconfg.EnableSearch,
                stream_options = new
                {
                    include_usage = modelconfg.Include_usage
                }
            };
           var str= JsonSerializer.Serialize(requestContent, _jsonOptions);
            var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, apiEndpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json")
            }, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);

            while (!reader.EndOfStream&& !cancellationToken.IsCancellationRequested)
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

        // 流式输出 - DashScope 百练应用调用方式
        public async IAsyncEnumerable<string> GenerateStreamViaDashScopeAsync(ChatModelConfig modelconfg, ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken )
        {
            // 验证配置
            string baseUrl = modelconfg.ApiEndpoint;
            string endpoint = "completion";
            var apiEndpoint = $"{baseUrl}/{modelconfg.Promptid}/{endpoint}";

            var apiKey = Environment.GetEnvironmentVariable("AiApiKey");

            if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiEndpoint))
            {
                throw new InvalidOperationException("通义千问API配置缺失");
            }

            // 创建HTTP客户端
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
            client.DefaultRequestHeaders.TryAddWithoutValidation("X-DashScope-SSE", "enable");
            string s_id = SessionId;
            
            
            // 准备请求内容
            var requestContent = new
            {
                input = new { prompt = ToMessage(request), session_id= s_id },
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

            while (!reader.EndOfStream&& !cancellationToken.IsCancellationRequested)
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
       

        /// <summary>
        /// 转换聊天历史为API消息格式
        /// </summary>
        private static List<object> ToMessages(ChatRequest request)
        {
            var messages = new List<object>();

            // 添加系统提示词
            messages.Add(new
            {
                role = "system",
                content = "你是一个得力的助手,请用用简体中文回答。公式输出时用$和或$$包裹。"
                //content = "你是一个得力的助手,请用用简体中文回答。"
            });

            // 添加历史消息
            foreach (var msg in request.History)
            {
                messages.Add(new
                {
                    role = msg.Role,
                    content = msg.Content
                });
            }

            return messages;
        }
        private static string ToMessage(ChatRequest request)
        {
            var messages = string.Empty;

            if (request.History.Count > 0&& request.History[request.History.Count - 1].Role=="user")
            {
                messages = request.History[request.History.Count - 1].Content;
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
    }
}