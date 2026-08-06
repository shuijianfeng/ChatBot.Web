// Models/ChatModelConfig.cs
using System.Text.Json.Serialization;

namespace ChatBot.Models
{
    public class SearchTermsResponse
    {
        [JsonPropertyName("search_terms")]
        public List<string> SearchTerms { get; set; } = [];
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

        /// <summary>
        /// 是否使用 Responses API 的 WebSocket 传输模式。
        /// 仅在 <see cref="ChatModelType.OpenAiResponses"/> 且 <see cref="Stream"/> 为
        /// <see langword="true"/> 时生效；连接失败时会自动回退到 HTTP/SSE。
        /// </summary>
        public bool UseWebSocket { get; set; }

        /// <summary>
        /// 是否为 Responses API 请求启用快速模式（service_tier: fast）。
        /// </summary>
        public bool UseFastMode { get; set; }

        public string Model { get; set; } = string.Empty;
        public ChatModelType ChatModelType { get; set; } = ChatModelType.OPenAi;
        
       
        public bool EnableImageUpload { get; set; }
        
        public int ThinkingTokens { get; set; }

        public string File_search_store_names { get; set; } = string.Empty;

        public string ThinkingLevel { get; set; } = string.Empty;

        /// <summary>
        /// 该模型关联的技能名称列表（为空或 null 表示不加载任何技能）
        /// </summary>
        public List<string>? Skills { get; set; }
    }

    public class ChatModelSettings : List<ChatModelConfig>
    {

    }

    public class ErrorViewModel
    {
        public string ErrorCode { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string RequestId { get; set; } = string.Empty;
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
        /// SSE（Server-Sent Events）HTTP 传输（旧版）
        /// </summary>
        Sse,

        /// <summary>
        /// Streamable HTTP 传输（MCP 2025-03-26 规范推荐）
        /// 使用单一 HTTP 端点，支持 POST 请求和 SSE 响应流
        /// </summary>
        StreamableHttp
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
        public string[] Arguments { get; set; } = [];

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
        public List<McpServerConfig> Servers { get; set; } = [];
    }

    /// <summary>
    /// 技能配置
    /// </summary>
    public class SkillConfig
    {
        /// <summary>
        /// 技能名称（来自 SKILL.md 的 name 字段）
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// 技能在界面中的显示名称（优先来自 agents/openai.yaml 的 display_name）。
        /// 没有界面元数据时回退到 Name。
        /// </summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>
        /// 技能文件夹名称
        /// </summary>
        public string FolderName { get; set; } = string.Empty;

        /// <summary>
        /// 技能文件夹全路径（绝对路径，方便后续读取 SKILL.md 和相关资源）
        /// </summary>
        public string FullPath { get; set; } = string.Empty;
        
        /// <summary>
        /// 技能图标（Emoji）
        /// </summary>
        public string Icon { get; set; } = string.Empty;

        /// <summary>
        /// 技能描述（来自 SKILL.md 的 description 字段）
        /// </summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// 技能的系统提示词（SKILL.md 的 markdown 正文）
        /// </summary>
        public string SystemPrompt { get; set; } = string.Empty;
    }

    /// <summary>
    /// 技能设置列表
    /// </summary>
    public class SkillSettings : List<SkillConfig>
    {
    }
}
