
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
        public int MaxTokens { get; set; } = 0;

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

        /// <summary>
        /// 用户输入的消息
        /// </summary>
        [JsonPropertyName("session_id")]
        public string SessionId { get; set; }= string.Empty;
        /// <summary>
        /// 图片链接
        /// </summary>
        [JsonPropertyName("image")]
        public string[] Image { get; set; } = Array.Empty<string>();

        /// <summary>
        /// 是否启用流式输出
        /// </summary>
        [JsonPropertyName("EnableSearch")]
        public bool EnableSearch { get; set; } = false;
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
        /// <summary>
        /// 图片链接
        /// </summary>
        [JsonPropertyName("images")]
        public string[] Images { get; set; } = Array.Empty<string>();
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

    // 响应类型
    public class OpenAIChunkResponse
    {
        public choice[] choices { get; set; }
        
        public class choice
        {
            public delta delta { get; set; }
            public int index { get; set; }

        }
       
        public class delta
        {
            public string content { get; set; }
            public string reasoning_content { get; set; }
            public string role { get; set; }
        }
    }

    // 响应类型
    public class GeminiChunkResponse
    {
        public candidate[] candidates { get; set; }

        public class candidate
        {
            public content content { get; set; }


        }
        public class content
        {
            public parts[] parts { get; set; }
           

        }

        public class parts
        {
            public string text { get; set; }
           
        }
    }

    // 响应类型
    public class ClaudeChunkResponse
    {
        public string type { get; set; }
        public int index { get; set; }
        [JsonPropertyName("delta")]
        public delta deltaitem { get; set; }
        public class delta
        {
            public string type { get; set; }
            public string text { get; set; }
        }
    }

    // 响应类型
    public class OpenAIResponse
    {
        public choice[] choices { get; set; }

        public class choice
        {
            public message message { get; set; }
            public int index { get; set; }

        }

        public class message
        {
            public string reasoning_content { get; set; }
            public string content { get; set; }
            public string role { get; set; }
            
        }
    }
    // 响应类型
    public class llama32ChunkResponse
    {
        public outputitem output { get; set; }
        public class outputitem
        {
            public choice[] choices { get; set; }

            public class choice
            {
                public message message { get; set; }
                public int index { get; set; }

            }

            public class message
            {

                public string role { get; set; }
                public contentitem[] content { get; set; }
                public class contentitem
                {
                    public string text { get; set; }

                }
            }
        }
    }
    public class DashScopeChunkResponse
    {
        [JsonPropertyName("output")]
        public Output output { get;set;}
        public class Output
        {
            [JsonPropertyName("text")]
            public string Text { get; set; }
            [JsonPropertyName("session_id")]
            public string SessionId { get; set; }
            [JsonPropertyName("finish_reason")]
            public string FinishReason { get; set; }
        }

    }

    public class ChatSessionRequest
    {
        public string UserId { get; set; } = string.Empty;
        public List<ChatMessage> Messages { get; set; } = new();
        public string? Title { get; set; }
    }

    public class ChatSession
    {
        public string Id { get; set; } = string.Empty;
        public string UserId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public List<ChatMessage> Messages { get; set; } = new();
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    public class ChatMessage
    {
        public string Role { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
    }

    public class SearchResult
    {
        public string Title { get; set; }
        public string Snippet { get; set; }
        public string Link { get; set; }
        public DateTime PublishedDate { get; set; }
        public int ClickRate { get; set; }

        // 综合评分
        public double GetRelevanceScore()
        {
            // 时间衰减因子（越新的内容分数越高）
            double timeDecay = Math.Exp((PublishedDate - DateTime.Now).TotalDays / 365.0);

            // 点击率权重
            double clickWeight = Math.Log(ClickRate + 1);

            return timeDecay * clickWeight;
        }
    }
    
}