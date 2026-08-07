
using ChatBot.Web.Services;
using System.Text.Json;
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
        public string SessionId { get; set; } = string.Empty;

        /// <summary>
        /// The previous Responses API response ID used to continue a conversation.
        /// </summary>
        [JsonPropertyName("previous_response_id")]
        public string? PreviousResponseId { get; set; }

        /// <summary>
        /// The response ID produced for the current request. It is returned through SSE
        /// metadata and is not part of the incoming request JSON contract.
        /// </summary>
        [JsonIgnore]
        public string? ResponseId { get; set; }
        /// <summary>
        /// 图片链接
        /// </summary>
        [JsonPropertyName("image")]
        public string[] Image { get; set; } = [];

        /// <summary>
        /// 是否启用流式输出
        /// </summary>
        [JsonPropertyName("EnableSearch")]
        public bool EnableSearch { get; set; } = false;

        /// <summary>
        /// 选择的技能名称
        /// </summary>
        [JsonPropertyName("skill")]
        public string? Skill { get; set; }

        /// <summary>
        /// HCSoft desktop context for this request only. It is never persisted as a chat message.
        /// </summary>
        [JsonPropertyName("hcsoft_context")]
        public JsonElement? HcsoftContext { get; set; }

        /// <summary>
        /// HCSoft 桌面端对全部工程数据执行分片扫描后形成的紧凑累计结果。
        /// 该字段只参与当前模型请求，不作为聊天消息持久化。
        /// </summary>
        [JsonPropertyName("hcsoft_analysis")]
        public JsonElement? HcsoftAnalysis { get; set; }
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
        public string[] Images { get; set; } = [];
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
        public choice[] choices { get; set; } = [];
        public string[] citations { get; set; } = [];
        public class choice
        {
            public delta delta { get; set; } = new delta();
            public int index { get; set; }
            public string finish_reason { get; set; } = string.Empty;
        }

        public class delta
        {
            public string content { get; set; } = string.Empty;
            public string reasoning_content { get; set; } = string.Empty;
            public string reasoning { get; set; } = string.Empty;
            public string role { get; set; } = string.Empty;
            public tool_call[] function_call { get; set; } = [];
            public tool_call[] tool_calls { get; set; } = [];


        }

    }
    public class tool_call
    {
        public int index { get; set; }
        public string id { get; set; }
        public string type { get; set; } = string.Empty;
        public string name { get; set; } = string.Empty;
        public Function function { get; set; }
        public class Function
        {
            public string name { get; set; } = string.Empty;
            public string arguments { get; set; } = string.Empty;
        }

    }
    public class tool_callnew
    {

        public string id { get; set; } = string.Empty;
        public string call_id { get; set; } = string.Empty;
        public string type { get; set; } = string.Empty;
        public string name { get; set; } = string.Empty;
        public string arguments { get; set; } = string.Empty;

    }

    public class GeminiToolCall
    {
        public string id { get; set; }

        public string name { get; set; } = string.Empty;
        //public string args  { get; set; } = string.Empty;

        public object args { get; set; }

    }
    // 响应类型
    public class GeminiChunkResponse
    {
        public candidate[] candidates { get; set; }

        public class candidate
        {
            public content content { get; set; }
            public string finishReason { get; set; }

        }
        public class content
        {
            public part[] parts { get; set; }


        }

        public class part
        {
            public string text { get; set; }
            public bool thought { get; set; }  // Gemini 思考内容标记
            public GeminiToolCall functionCall { get; set; } = new GeminiToolCall();
        }
    }

    // 响应类型
    public class ClaudeChunkResponse
    {
        public string type { get; set; }
        public int index { get; set; }

        public Delta content_block { get; set; }

        public Delta delta { get; set; }
        public class Delta
        {
            public string id { get; set; } = string.Empty;
            public string type { get; set; } = string.Empty;
            public string name { get; set; } = string.Empty;
            public string text { get; set; } = string.Empty;
            public string thinking { get; set; } = string.Empty;
            public string signature { get; set; } = string.Empty;
            public string partial_json { get; set; } = string.Empty;
            public string stop_reason { get; set; } = string.Empty;

        }

    }
    // Claude响应类型
    public class ClaudeResponse
    {
        public string id { get; set; } = string.Empty;
        public string type { get; set; } = string.Empty;
        public string model { get; set; } = string.Empty;
        public string role { get; set; } = string.Empty;
        public string stop_reason { get; set; } = string.Empty;
        public long created_at { get; set; }

        [JsonPropertyName("content")]
        public List<ClaudeResponseContent> Content { get; set; } = new List<ClaudeResponseContent>();

        [JsonPropertyName("usage")]
        public ClaudeResponseUsage Usage { get; set; } = new ClaudeResponseUsage();

        public class ClaudeResponseContent
        {
            public string type { get; set; } = string.Empty;

            public string text { get; set; } = string.Empty;

            public string id { get; set; } = string.Empty;
            public object input { get; set; }
            public string name { get; set; } = string.Empty;

            public string signature { get; set; } = string.Empty;
            public string thinking { get; set; } = string.Empty;

        }

        public class ClaudeResponseUsage
        {
            [JsonPropertyName("input_tokens")]
            public int InputTokens { get; set; }

            [JsonPropertyName("output_tokens")]
            public int OutputTokens { get; set; }
        }
    }
    // 响应类型
    public class OpenAIResponse
    {
        public choice[] choices { get; set; } = [];
        public string[] citations { get; set; } = [];
        public class choice
        {
            public message message { get; set; }
            public int choices { get; set; }
            public string finish_reason { get; set; } = string.Empty;

        }

        public class message
        {
            public string reasoning_content { get; set; }
            public string content { get; set; }
            public string role { get; set; }
            public tool_call[] function_call { get; set; }
            public tool_call[] tool_calls { get; set; }
        }
    }
    // 响应类型
    public class OpenAIResponsenew
    {
        public string id { get; set; } = string.Empty;
        [JsonPropertyName("object")]
        public string openaiobject { get; set; } = string.Empty;

        public string status { get; set; } = string.Empty;
        public ErrorDetails? error { get; set; }
        public IncompleteDetails? incomplete_details { get; set; }

        public string model { get; set; } = string.Empty;
        public OpenAioutput[] output { get; set; }

        public class IncompleteDetails
        {
            public string reason { get; set; } = string.Empty;
        }

        public class ErrorDetails
        {
            public string type { get; set; } = string.Empty;
            public string code { get; set; } = string.Empty;
            public string message { get; set; } = string.Empty;
            public string? param { get; set; }
        }

        public class OpenAioutput
        {


            public string type { get; set; } = string.Empty;
            public string id { get; set; } = string.Empty;
            public string status { get; set; } = string.Empty;

            public string call_id { get; set; } = string.Empty;
            public string name { get; set; } = string.Empty;
            public string arguments { get; set; } = string.Empty;

            public string role { get; set; } = string.Empty;
            public Content[] content { get; set; }
            public SummaryItem[] summary { get; set; }


        }

        public class Content
        {
            public string type { get; set; } = string.Empty;
            public string text { get; set; } = string.Empty;


        }

        public class SummaryItem
        {
            public string type { get; set; } = string.Empty;
            public string text { get; set; } = string.Empty;
        }

        public class usage
        {
            public int input_tokens { get; set; }
            public int output_tokens { get; set; }
            public int total_tokens { get; set; }
        }
    }

    // OpenAI Responses API 流式响应类型 (支持完整事件类型)
    // 事件类型包括:
    // - response.created, response.in_progress, response.completed, response.incomplete, response.failed
    // - response.output_item.added, response.output_item.done
    // - response.content_part.added, response.content_part.done
    // - response.output_text.delta, response.output_text.done
    // - response.reasoning_summary_part.added, response.reasoning_summary_part.done
    // - response.reasoning_summary_text.delta, response.reasoning_summary_text.done
    // - response.function_call_arguments.delta, response.function_call_arguments.done
    // - response.web_search_call.searching, response.web_search_call.completed
    // - response.file_search_call.searching, response.file_search_call.completed
    public class OpenAIChunkResponsenew
    {
        /// <summary>
        /// 事件类型
        /// </summary>
        public string type { get; set; } = string.Empty;

        /// <summary>
        /// 输出项索引
        /// </summary>
        public int output_index { get; set; }

        /// <summary>
        /// 内容索引
        /// </summary>
        public int content_index { get; set; }

        /// <summary>
        /// 响应ID
        /// </summary>
        public string response_id { get; set; } = string.Empty;

        /// <summary>
        /// 事件序列号，用于排序
        /// </summary>
        public int sequence_number { get; set; }

        /// <summary>
        /// 输出项ID
        /// </summary>
        public string item_id { get; set; } = string.Empty;

        /// <summary>
        /// 工具调用项 (用于 function_call 类型) 或 reasoning 项
        /// 使用 JsonElement 保留原始 JSON 数据，因为 reasoning 项有不同的结构
        /// </summary>
        public JsonElement? item { get; set; }

        /// <summary>
        /// 增量内容 (用于 delta 事件)
        /// </summary>
        public string delta { get; set; } = string.Empty;

        /// <summary>
        /// 完整文本内容 (用于 done 事件)
        /// </summary>
        public string text { get; set; } = string.Empty;

        /// <summary>
        /// 内容部分 (用于 content_part.added 事件)
        /// </summary>
        public ContentPart part { get; set; }

        /// <summary>
        /// 推理摘要部分 (用于 reasoning_summary_part.added 事件)
        /// </summary>
        public ReasoningSummaryPart summary { get; set; }

        /// <summary>
        /// 完整响应对象 (用于 response.completed 等事件)
        /// </summary>
        public OpenAIResponsenew response { get; set; }

        /// <summary>
        /// 顶层错误对象 (用于 error 事件)
        /// </summary>
        public OpenAIResponsenew.ErrorDetails? error { get; set; }

        /// <summary>
        /// 内容部分类型
        /// </summary>
        public class ContentPart
        {
            public string type { get; set; } = string.Empty;
            public string text { get; set; } = string.Empty;
            public Annotations[] annotations { get; set; }
        }

        /// <summary>
        /// 注释类型 (用于引用等)
        /// </summary>
        public class Annotations
        {
            public string type { get; set; } = string.Empty;
            public int start_index { get; set; }
            public int end_index { get; set; }
            public string url { get; set; } = string.Empty;
            public string title { get; set; } = string.Empty;
        }

        /// <summary>
        /// 推理摘要部分类型
        /// </summary>
        public class ReasoningSummaryPart
        {
            public string type { get; set; } = string.Empty;
            public string text { get; set; } = string.Empty;
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
        public Output output { get; set; }
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
    public class JinaSearchResult
    {
        [JsonPropertyName("code")]
        public int Code { get; set; }
        [JsonPropertyName("status")]
        public int Status { get; set; }
        [JsonPropertyName("data")]
        public List<JinaSearchResultData> Data { get; set; } = new List<JinaSearchResultData>();


    }
    public class JinaReaderResult
    {
        [JsonPropertyName("code")]
        public int Code { get; set; }
        [JsonPropertyName("status")]
        public int Status { get; set; }
        [JsonPropertyName("data")]
        public JinaSearchResultData Data { get; set; } = new JinaSearchResultData();


    }
    public class JinaSearchResultData
    {
        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;
        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;
        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;
        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;
        [JsonPropertyName("usage")]
        public JinaSearchResultDataUsage Usage { get; set; } = new();
        public int index { get; set; }
        public float Score { get; set; }

    }
    public class JinaSearchResultDataUsage
    {
        [JsonPropertyName("tokens")]
        public int tokens { get; set; }

    }

    public class JinaRerankResult
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;
        [JsonPropertyName("results")]
        public List<JinaRerankResultData> Results { get; set; } = new List<JinaRerankResultData>();

    }
    public class JinaRerankResultData
    {
        [JsonPropertyName("index")]
        public int Index { get; set; }
        [JsonPropertyName("document")]
        public JinaRerankResultDataDocument Document { get; set; } = new JinaRerankResultDataDocument();
        public float Score { get; set; }

    }
    public class JinaRerankResultDataDocument
    {
        [JsonPropertyName("text")]
        public string Text { get; set; } = string.Empty;

    }

    // 添加Dify响应的数据类
    public class DifyChunkResponse
    {
        public string event_id { get; set; }
        [JsonPropertyName("event")]
        public string Event { get; set; }
        public DifyMessageData data { get; set; }
        public string answer { get; set; }
        public class DifyMessageData
        {
            public string answer { get; set; }
            public string error { get; set; }
            public List<DifyMessageSource> sources { get; set; }
            public string conversation_id { get; set; }

            public class DifyMessageSource
            {
                public string id { get; set; }
                public string name { get; set; }
                public string content { get; set; }
                public string url { get; set; }
            }
        }
    }

    public class DifyBlockingResponse
    {
        public string answer { get; set; }
        public string error { get; set; }
        public string conversation_id { get; set; }
        public List<DifyMessageSource> sources { get; set; }

        public class DifyMessageSource
        {
            public string id { get; set; }
            public string name { get; set; }
            public string content { get; set; }
            public string url { get; set; }
        }
    }

    // 定义嵌入API响应的类
    public class JinaEmbeddingResponse
    {
        [JsonPropertyName("model")]
        public string Model { get; set; }

        [JsonPropertyName("data")]
        public List<JinaEmbeddingData> Data { get; set; } = new List<JinaEmbeddingData>();

        [JsonPropertyName("usage")]
        public JinaEmbeddingUsage Usage { get; set; }

        public class JinaEmbeddingData
        {
            [JsonPropertyName("embedding")]
            public float[] Embedding { get; set; }

            [JsonPropertyName("index")]
            public int Index { get; set; }
        }

        public class JinaEmbeddingUsage
        {
            [JsonPropertyName("prompt_tokens")]
            public int PromptTokens { get; set; }

            [JsonPropertyName("total_tokens")]
            public int TotalTokens { get; set; }
        }
    }
}
