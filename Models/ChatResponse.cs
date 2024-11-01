// ChatResponse.cs
using System.Text.Json.Serialization;

namespace ChatBot.Models
{
    /// <summary>
    /// 聊天响应模型类
    /// </summary>
    public class ChatResponse
    {
        /// <summary>
        /// 响应ID
        /// </summary>
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        /// <summary>
        /// 回复内容
        /// </summary>
        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;

        /// <summary>
        /// 消息角色（system/user/assistant）
        /// </summary>
        [JsonPropertyName("role")]
        public string Role { get; set; } = "assistant";

        /// <summary>
        /// 会话ID
        /// </summary>
        [JsonPropertyName("conversation_id")]
        public string ConversationId { get; set; } = string.Empty;

        /// <summary>
        /// 是否为流式响应的最后一条消息
        /// </summary>
        [JsonPropertyName("is_end")]
        public bool IsEnd { get; set; }

        /// <summary>
        /// 使用的模型名称
        /// </summary>
        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        /// <summary>
        /// 令牌使用统计
        /// </summary>
        [JsonPropertyName("usage")]
        public TokenUsage? Usage { get; set; }

        /// <summary>
        /// 创建时间戳
        /// </summary>
        [JsonPropertyName("created")]
        public long Created { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    /// <summary>
    /// 令牌使用统计
    /// </summary>
    public class TokenUsage
    {
        /// <summary>
        /// 提示令牌数
        /// </summary>
        [JsonPropertyName("prompt_tokens")]
        public int PromptTokens { get; set; }

        /// <summary>
        /// 补全令牌数
        /// </summary>
        [JsonPropertyName("completion_tokens")]
        public int CompletionTokens { get; set; }

        /// <summary>
        /// 总令牌数
        /// </summary>
        [JsonPropertyName("total_tokens")]
        public int TotalTokens { get; set; }
    }

    /// <summary>
    /// 错误响应
    /// </summary>
    public class ErrorResponse
    {
        /// <summary>
        /// 错误代码
        /// </summary>
        [JsonPropertyName("code")]
        public string Code { get; set; } = string.Empty;

        /// <summary>
        /// 错误消息
        /// </summary>
        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>
    /// 聊天请求模型类
    /// </summary>
    public class ChatRequest
    {
        /// <summary>
        /// 用户输入的消息
        /// </summary>
        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// 选择的模型
        /// </summary>
        [JsonPropertyName("model")]
        public string Model { get; set; } = "qwen-turbo";

        /// <summary>
        /// 历史消息记录
        /// </summary>
        [JsonPropertyName("history")]
        public List<HistoryMessage> History { get; set; } = new();

        /// <summary>
        /// 是否启用流式输出
        /// </summary>
        [JsonPropertyName("stream")]
        public bool Stream { get; set; } = true;

        /// <summary>
        /// 温度参数 (0-1)
        /// </summary>
        [JsonPropertyName("temperature")]
        public float Temperature { get; set; } = 0.7f;

        /// <summary>
        /// 返回结果的最大tokens
        /// </summary>
        [JsonPropertyName("max_tokens")]
        public int MaxTokens { get; set; } = 1500;

        /// <summary>
        /// 返回的结果数量
        /// </summary>
        [JsonPropertyName("n")]
        public int N { get; set; } = 1;

        /// <summary>
        /// 停止生成的标记
        /// </summary>
        [JsonPropertyName("stop")]
        public List<string>? Stop { get; set; }
    }

    /// <summary>
    /// 历史消息记录
    /// </summary>
    public class HistoryMessage
    {
        /// <summary>
        /// 消息角色
        /// </summary>
        [JsonPropertyName("role")]
        public string Role { get; set; } = string.Empty;

        /// <summary>
        /// 消息内容
        /// </summary>
        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;
    }

    /// <summary>
    /// 流式响应事件类型
    /// </summary>
    public static class StreamEventType
    {
        /// <summary>
        /// 添加文本
        /// </summary>
        public const string Add = "add";

        /// <summary>
        /// 结束标记
        /// </summary>
        public const string End = "end";

        /// <summary>
        /// 错误标记
        /// </summary>
        public const string Error = "error";

        /// <summary>
        /// 心跳包
        /// </summary>
        public const string Ping = "ping";
    }

    /// <summary>
    /// 流式响应事件
    /// </summary>
    public class StreamEvent
    {
        /// <summary>
        /// 事件类型
        /// </summary>
        [JsonPropertyName("event")]
        public string Event { get; set; } = string.Empty;

        /// <summary>
        /// 数据内容
        /// </summary>
        [JsonPropertyName("data")]
        public ChatResponse? Data { get; set; }
    }
}