// Models/ChatModelConfig.cs
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ChatBot.Models
{
    public class SearchTermsResponse
    {
        [JsonPropertyName("search_terms")]
        public List<string> SearchTerms { get; set; }
    }
    public enum ChatModelType
    {
        OPenAi,
        Claude,
        Gemini,
        DeepSeek,
        Qwen,
        QwenVl,
        Llama,
        Deepbricks,
        OpenAiDeepResearch,
        GeminiFileSearch,
        Dify,  // 新增的 Dify 类型
        OpenAiResponses
    }

    public class ChatModelConfig
    {
        public string Name { get; set; } = string.Empty; // 模型名称
        public string ApiEndpoint { get; set; } = string.Empty;
        public string EnvironmentApikeyName { get; set; } = string.Empty;
        public string Systemprompt { get; set; } = string.Empty;
        public float Temperature { get; set; }
        public int MaxTokens { get; set; }
        public bool EnableSearch { get; set; }
        public bool Stream { get; set; }
        public string Model { get; set; } = string.Empty;
        public ChatModelType ChatModelType { get; set; } = ChatModelType.OPenAi;
        public bool Include_usage { get; set; }
        public bool Isprompt { get; set; }
        public string Promptid { get; set; } = string.Empty;
        public bool EnableImageUpload { get; set; }
        public bool Incremental_output { get; set; }
        public int ThinkingTokens { get; set; }

        public string File_search_store_names { get; set; } = string.Empty;

        public string ThinkingLevel { get; set; } = string.Empty;
    }

    public class ChatModelSettings : List<ChatModelConfig>
    {

    }

    public class ErrorViewModel
    {
        public string ErrorCode { get; set; }
        public string Message { get; set; }
        public string RequestId { get; set; }
        public bool ShowRequestId => !string.IsNullOrEmpty(RequestId);
    }

    /// <summary>
    /// MCP 传输类型
    /// </summary>
    public enum McpTransportType
    {
        /// <summary>
        /// 标准输入输出（本地进程）
        /// </summary>
        Stdio,

        /// <summary>
        /// SSE（Server-Sent Events）HTTP 传输
        /// </summary>
        Sse
    }

    /// <summary>
    /// MCP 服务器配置
    /// </summary>
    public class McpServerConfig
    {
        /// <summary>
        /// 服务器名称
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// 传输类型（Stdio 或 Sse）
        /// </summary>
        public McpTransportType TransportType { get; set; } = McpTransportType.Stdio;

        /// <summary>
        /// [Stdio] 启动命令 (如 npx, python, node)
        /// </summary>
        public string Command { get; set; } = string.Empty;

        /// <summary>
        /// [Stdio] 命令参数
        /// </summary>
        public string[] Arguments { get; set; } = Array.Empty<string>();

        /// <summary>
        /// [Stdio] 工作目录（可选）
        /// </summary>
        public string? WorkingDirectory { get; set; }

        /// <summary>
        /// [Stdio] 环境变量（可选）
        /// </summary>
        public Dictionary<string, string>? EnvironmentVariables { get; set; }

        /// <summary>
        /// [Sse] MCP 服务器 URL（如 http://localhost:3000/sse）
        /// </summary>
        public string? Url { get; set; }

        /// <summary>
        /// [Sse] HTTP 请求头（如 Authorization）
        /// </summary>
        public Dictionary<string, string>? Headers { get; set; }
    }

    /// <summary>
    /// MCP 设置
    /// </summary>
    public class McpSettings
    {
        /// <summary>
        /// 是否启用 MCP
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// MCP 服务器列表
        /// </summary>
        public List<McpServerConfig> Servers { get; set; } = new List<McpServerConfig>();
    }
}
