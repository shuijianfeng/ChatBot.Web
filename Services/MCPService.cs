using ChatBot.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace ChatBot.Web.Services
{
    /// <summary>
    /// MCP (Model Context Protocol) 服务实现
    /// </summary>
    public class MCPService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<MCPService> _logger;
        private readonly JsonSerializerOptions _jsonOptions;
        private int _requestId = 0;

        public MCPService(
            IHttpClientFactory httpClientFactory,
            ILogger<MCPService> logger)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
        }

        /// <summary>
        /// 初始化 MCP 连接
        /// </summary>
        public async Task<MCPResponse?> InitializeAsync(string endpoint, string apiKey)
        {
            var request = new MCPRequest
            {
                Method = "initialize",
                Params = new
                {
                    protocolVersion = "2024-11-05",
                    capabilities = new
                    {
                        roots = new { listChanged = true },
                        sampling = new { }
                    },
                    clientInfo = new
                    {
                        name = "ChatBot.Web",
                        version = "1.0.0"
                    }
                },
                Id = Interlocked.Increment(ref _requestId)
            };

            return await SendRequestAsync(endpoint, apiKey, request);
        }

        /// <summary>
        /// 发送聊天请求（支持流式输出）
        /// </summary>
        public async IAsyncEnumerable<string> SendChatStreamAsync(
            ChatModelConfig config,
            ChatRequest chatRequest,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var apiKey = Environment.GetEnvironmentVariable(config.EnvironmentApikeyName);
            if (string.IsNullOrEmpty(apiKey))
            {
                yield return "错误: MCP API密钥未配置";
                yield break;
            }

            // 构建 MCP 采样请求
            var samplingParams = new MCPSamplingParams
            {
                Messages = ConvertToMCPMessages(chatRequest.History),
                SystemPrompt = config.Systemprompt,
                Temperature = config.Temperature >= 0 ? config.Temperature : null,
                MaxTokens = config.MaxTokens > 0 ? config.MaxTokens : null,
                ModelPreferences = new MCPModelPreferences
                {
                    Hints = new List<MCPModelHint>
                    {
                        new MCPModelHint { Name = config.Model }
                    }
                }
            };

            var request = new MCPRequest
            {
                Method = "sampling/createMessage",
                Params = samplingParams,
                Id = Interlocked.Increment(ref _requestId)
            };

            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
            client.Timeout = TimeSpan.FromMinutes(10);

            // 使用辅助方法处理实际的HTTP请求和响应
            await foreach (var item in SendChatStreamInternalAsync(client, config, request, cancellationToken))
            {
                yield return item;
            }
        }

        /// <summary>
        /// 内部方法：实际执行HTTP请求和处理响应（不包含异常处理，让异常自然传播）
        /// </summary>
        private async IAsyncEnumerable<string> SendChatStreamInternalAsync(
            HttpClient client,
            ChatModelConfig config,
            MCPRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var jsonContent = JsonSerializer.Serialize(request, _jsonOptions);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
            
            using var response = await client.PostAsync(config.ApiEndpoint, content, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError($"MCP请求失败: {response.StatusCode}, 内容: {errorContent}");
                yield return $"错误: HTTP {response.StatusCode}";
                yield break;
            }

            // 处理流式响应
            if (config.Stream)
            {
                await foreach (var chunk in ReadStreamResponseAsync(response, cancellationToken))
                {
                    if (!string.IsNullOrEmpty(chunk))
                    {
                        yield return chunk;
                    }
                }
            }
            else
            {
                // 非流式响应
                var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
                var mcpResponse = JsonSerializer.Deserialize<MCPResponse>(responseContent, _jsonOptions);

                if (mcpResponse?.Error != null)
                {
                    yield return $"错误: {mcpResponse.Error.Message}";
                }
                else if (mcpResponse?.Result?.Content != null)
                {
                    foreach (var contentItem in mcpResponse.Result.Content)
                    {
                        if (!string.IsNullOrEmpty(contentItem.Text))
                        {
                            yield return contentItem.Text;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 读取流式响应
        /// </summary>
        private async IAsyncEnumerable<string> ReadStreamResponseAsync(
            HttpResponseMessage response,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);

            while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                // 处理 Server-Sent Events (SSE) 格式
                if (line.StartsWith("data: "))
                {
                    var data = line.Substring(6);
                    if (data == "[DONE]")
                        break;

                    // 尝试解析为 MCPStreamChunk - 如果失败则记录日志并继续
                    var chunk = TryDeserialize<MCPStreamChunk>(data);
                    
                    // 根据 MCP 协议处理流式数据
                    // 这里需要根据实际的 MCP 服务器实现来调整
                    if (chunk != null)
                    {
                        // 提取文本内容
                        yield return data; // 临时返回原始数据，需要根据实际格式调整
                    }
                }
                else
                {
                    // 可能是其他格式的响应
                    var mcpResponse = TryDeserialize<MCPResponse>(line);

                    if (mcpResponse?.Result?.Content != null)
                    {
                        foreach (var contentItem in mcpResponse.Result.Content)
                        {
                            if (!string.IsNullOrEmpty(contentItem.Text))
                            {
                                yield return contentItem.Text;
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 尝试反序列化 JSON，失败时返回 null
        /// </summary>
        private T? TryDeserialize<T>(string json) where T : class
        {
            try
            {
                return JsonSerializer.Deserialize<T>(json, _jsonOptions);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning($"解析 JSON 失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 发送 MCP 请求
        /// </summary>
        private async Task<MCPResponse?> SendRequestAsync(string endpoint, string apiKey, MCPRequest request)
        {
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

            var jsonContent = JsonSerializer.Serialize(request, _jsonOptions);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            try
            {
                var response = await client.PostAsync(endpoint, content);
                response.EnsureSuccessStatusCode();

                var responseContent = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<MCPResponse>(responseContent, _jsonOptions);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MCP请求失败");
                return null;
            }
        }

        /// <summary>
        /// 将聊天历史转换为 MCP 消息格式
        /// </summary>
        private List<MCPMessage> ConvertToMCPMessages(List<HistoryMessage> history)
        {
            var messages = new List<MCPMessage>();

            foreach (var msg in history)
            {
                var mcpMessage = new MCPMessage
                {
                    Role = msg.Role,
                    Content = new MCPMessageContent
                    {
                        Type = "text",
                        Text = msg.Content
                    }
                };

                messages.Add(mcpMessage);
            }

            return messages;
        }

        /// <summary>
        /// 列出可用的工具
        /// </summary>
        public async Task<List<MCPTool>?> ListToolsAsync(string endpoint, string apiKey)
        {
            var request = new MCPRequest
            {
                Method = "tools/list",
                Id = Interlocked.Increment(ref _requestId)
            };

            var response = await SendRequestAsync(endpoint, apiKey, request);
            
            if (response?.Result != null)
            {
                // 需要根据实际的 MCP 响应格式来解析工具列表
                // 这里返回 null，实际使用时需要实现
                return null;
            }

            return null;
        }

        /// <summary>
        /// 调用工具
        /// </summary>
        public async Task<string?> CallToolAsync(
            string endpoint,
            string apiKey,
            string toolName,
            Dictionary<string, object> arguments)
        {
            var request = new MCPRequest
            {
                Method = "tools/call",
                Params = new
                {
                    name = toolName,
                    arguments = arguments
                },
                Id = Interlocked.Increment(ref _requestId)
            };

            var response = await SendRequestAsync(endpoint, apiKey, request);

            if (response?.Error != null)
            {
                _logger.LogError($"工具调用失败: {response.Error.Message}");
                return null;
            }

            // 根据实际的 MCP 响应格式提取结果
            return response?.Result?.Content?.FirstOrDefault()?.Text;
        }
    }
}
