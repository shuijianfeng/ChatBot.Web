using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChatBot.Models;
using System.Net.Http.Headers;
using System.Net.Http;
namespace ChatBot.Web.Services
{
    /// <summary>
    /// 聊天服务接口
    /// </summary>
    public interface IChatService
    {
        /// <summary>
        /// 获取聊天响应流
        /// </summary>
        /// <param name="request">聊天请求</param>
        /// <returns>响应事件流</returns>
        Task<IAsyncEnumerable<StreamEvent>> GetChatResponseAsync(ChatRequest request);
        IAsyncEnumerable<string> GenerateStreamViaOpenAIAsync(ChatRequest request);
        IAsyncEnumerable<string> GenerateStreamViaDashScopeAsync(ChatRequest request, string appId);
    }

    /// <summary>
    /// 通义千问API聊天服务实现
    /// </summary>
    public class QianWenChatService : IChatService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _configuration;
        private readonly ILogger<QianWenChatService> _logger;
        private readonly JsonSerializerOptions _jsonOptions;

        public QianWenChatService(
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            ILogger<QianWenChatService> logger)
        {
            _httpClientFactory = httpClientFactory;
            _configuration = configuration;
            _logger = logger;

            // 配置JSON序列化选项
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                //Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
        }
        // 流式输出 - OpenAI 兼容方式
        public async IAsyncEnumerable<string> GenerateStreamViaOpenAIAsync(ChatRequest request)
        {
            // 验证配置
            var apiKey = "sk-f9c4450d10604891a9d912bb398a397b";
            var apiEndpoint = @"https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions";

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

                model = request.Model,
                messages = ConvertToApiMessages(request),
                stream = true,
                temperature = request.Temperature,
                max_tokens = request.MaxTokens,
                enable_search = true,
                stream_options = new
                {
                    include_usage = true
                }
            };
            var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, apiEndpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json")
            }, HttpCompletionOption.ResponseHeadersRead);

            //var response = await client.PostAsJsonAsync(apiEndpoint, requestContent);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream);

            while (!reader.EndOfStream)
            {
                var line = await reader.ReadLineAsync();
                if (string.IsNullOrEmpty(line)) continue;
                if (line.StartsWith("data: "))
                {
                    line = line.Substring(6);
                    if (line == "[DONE]") break;

                    var chunk = JsonSerializer.Deserialize<OpenAIChunkResponse>(line);
                    if (!string.IsNullOrEmpty(chunk?.choices?[0]?.delta?.content))
                    {
                        yield return chunk.choices[0].delta.content;
                    }
                }
            }
        }

        // 流式输出 - DashScope 百练应用调用方式
        public async IAsyncEnumerable<string> GenerateStreamViaDashScopeAsync(ChatRequest request,string appId)
        {
            // 验证配置
            string baseUrl = "https://dashscope.aliyuncs.com/api/v1/apps";
            string endpoint = "completion";
            var apiEndpoint = $"{baseUrl}/{appId}/{endpoint}";

            var apiKey = "sk-f9c4450d10604891a9d912bb398a397b";

            if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiEndpoint))
            {
                throw new InvalidOperationException("通义千问API配置缺失");
            }

            // 创建HTTP客户端
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
            client.DefaultRequestHeaders.TryAddWithoutValidation("X-DashScope-SSE", "enable");
            string s_id = request.SessionId;
            
            
            // 准备请求内容
            var requestContent = new
            {
                input = new { prompt = ConvertToApiMessage(request), session_id= s_id },
                parameters = new { enable_search = true, incremental_output = true }

            };
            var str = JsonSerializer.Serialize(requestContent, _jsonOptions);
            var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, apiEndpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(requestContent, _jsonOptions), Encoding.UTF8, "application/json")
            }, HttpCompletionOption.ResponseHeadersRead);

            //var response = await client.PostAsJsonAsync(apiEndpoint, requestContent);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream);

            while (!reader.EndOfStream)
            {
                var line = await reader.ReadLineAsync();
                if (string.IsNullOrEmpty(line)) continue;
                if (line.StartsWith("data:"))
                {
                    line = line.Substring(5);
                    if (line == "[DONE]") break;

                    var chunk = JsonSerializer.Deserialize<DashScopeChunkResponse>(line);
                    if (!string.IsNullOrEmpty(chunk?.output.Text))
                    {
                        //request.SessionId = chunk.SessionId;
                        yield return chunk.output.Text;
                    }
                }
            }
        }
        /// <summary>
        /// 获取聊天响应流
        /// </summary>
        public async Task<IAsyncEnumerable<StreamEvent>> GetChatResponseAsync(ChatRequest request)
        {
            // 验证配置
            var apiKey = "sk-f9c4450d10604891a9d912bb398a397b";
            var apiEndpoint = @"https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions";

            if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiEndpoint))
            {
                throw new InvalidOperationException("通义千问API配置缺失");
            }

            // 创建HTTP客户端
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
            //client.DefaultRequestHeaders.TryAddWithoutValidation("X-DashScope-SSE", "enable");
            //client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            try
            {
                // 准备请求内容
                var requestContent = new
                {
                    
                    model = request.Model,
                    messages = ConvertToApiMessages(request),
                    stream = true,
                    temperature = request.Temperature,
                    max_tokens = request.MaxTokens
                };
                var str = JsonSerializer.Serialize(requestContent, _jsonOptions);
                // 发送请求
                var response = await client.PostAsync(
                       apiEndpoint,
                       new StringContent(
                           JsonSerializer.Serialize(requestContent, _jsonOptions),
                           System.Text.Encoding.UTF8,
                           "application/json"
                       )
                   );
                //var response = await client.PostAsync(
                //    apiEndpoint,
                //    JsonContent.Create(requestContent)
                //);
                // 检查响应状态
                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync();
                    _logger.LogError("API请求失败: {StatusCode} {Error}", response.StatusCode, error);
                    return GetErrorStream("API请求失败");
                }

                // 返回响应流
                return ProcessResponseStream(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "处理聊天请求时发生错误");
                return GetErrorStreamFromException(ex);
            }
        }

        /// <summary>
        /// 转换聊天历史为API消息格式
        /// </summary>
        private static List<object> ConvertToApiMessages(ChatRequest request)
        {
            var messages = new List<object>();

            // 添加系统提示词
            messages.Add(new
            {
                role = "system",
                content = "你是一个得力的助手,请用用简体中文回答"
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
        private static string ConvertToApiMessage(ChatRequest request)
        {
            var messages = string.Empty;

            if (request.History.Count > 0&& request.History[request.History.Count - 1].Role=="user")
            {
                messages = request.History[request.History.Count - 1].Content;
            }

            

            return messages;
        }

        /// <summary>
        /// 处理API响应流
        /// </summary>
        private async IAsyncEnumerable<StreamEvent> ProcessResponseStream(HttpResponseMessage response)
        {
            using var stream = await response.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream);

            var buffer = new StringBuilder();
            var currentResponse = new ChatResponse();

            while (!reader.EndOfStream)
            {
                var line = await reader.ReadLineAsync();
                if (string.IsNullOrEmpty(line)) continue;

                if (line.StartsWith("data: "))
                {
                    var data = line.Substring(6);

                    // 处理结束标记
                    if (data == "[DONE]")
                    {
                        currentResponse.IsEnd = true;
                        yield return new StreamEvent
                        {
                            Event = StreamEventType.End,
                            Data = currentResponse
                        };
                        yield break;
                    }

                   
                        var chunk = JsonSerializer.Deserialize<ChatResponse>(data, _jsonOptions);
                        if (chunk != null)
                        {
                            // 更新当前响应
                            currentResponse.Content += chunk.Content;
                            currentResponse.Id = chunk.Id;
                            currentResponse.Created = chunk.Created;
                            currentResponse.Model = chunk.Model;

                            // 发送数据块
                            yield return new StreamEvent
                            {
                                Event = StreamEventType.Add,
                                Data = new ChatResponse
                                {
                                    Content = chunk.Content,
                                    Role = "assistant"
                                }
                            };
                        }
                    
                    
                }
            }
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