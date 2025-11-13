using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ChatBot.Models
{
    /// <summary>
    /// MCP (Model Context Protocol) 请求模型
    /// </summary>
    public class MCPRequest
    {
        [JsonPropertyName("jsonrpc")]
        public string JsonRpc { get; set; } = "2.0";

        [JsonPropertyName("method")]
        public string Method { get; set; } = string.Empty;

        [JsonPropertyName("params")]
        public object? Params { get; set; }

        [JsonPropertyName("id")]
        public int Id { get; set; }
    }

    /// <summary>
    /// MCP 响应模型
    /// </summary>
    public class MCPResponse
    {
        [JsonPropertyName("jsonrpc")]
        public string? JsonRpc { get; set; }

        [JsonPropertyName("result")]
        public MCPResult? Result { get; set; }

        [JsonPropertyName("error")]
        public MCPError? Error { get; set; }

        [JsonPropertyName("id")]
        public int Id { get; set; }
    }

    /// <summary>
    /// MCP 结果
    /// </summary>
    public class MCPResult
    {
        [JsonPropertyName("content")]
        public List<MCPContent>? Content { get; set; }

        [JsonPropertyName("model")]
        public string? Model { get; set; }

        [JsonPropertyName("role")]
        public string? Role { get; set; }

        [JsonPropertyName("stopReason")]
        public string? StopReason { get; set; }
    }

    /// <summary>
    /// MCP 内容
    /// </summary>
    public class MCPContent
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "text";

        [JsonPropertyName("text")]
        public string? Text { get; set; }
    }

    /// <summary>
    /// MCP 错误
    /// </summary>
    public class MCPError
    {
        [JsonPropertyName("code")]
        public int Code { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("data")]
        public object? Data { get; set; }
    }

    /// <summary>
    /// MCP 工具定义
    /// </summary>
    public class MCPTool
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("inputSchema")]
        public MCPToolInputSchema? InputSchema { get; set; }
    }

    /// <summary>
    /// MCP 工具输入模式
    /// </summary>
    public class MCPToolInputSchema
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "object";

        [JsonPropertyName("properties")]
        public Dictionary<string, object>? Properties { get; set; }

        [JsonPropertyName("required")]
        public List<string>? Required { get; set; }
    }

    /// <summary>
    /// MCP 采样请求参数
    /// </summary>
    public class MCPSamplingParams
    {
        [JsonPropertyName("messages")]
        public List<MCPMessage> Messages { get; set; } = new();

        [JsonPropertyName("modelPreferences")]
        public MCPModelPreferences? ModelPreferences { get; set; }

        [JsonPropertyName("systemPrompt")]
        public string? SystemPrompt { get; set; }

        [JsonPropertyName("includeContext")]
        public string? IncludeContext { get; set; }

        [JsonPropertyName("temperature")]
        public float? Temperature { get; set; }

        [JsonPropertyName("maxTokens")]
        public int? MaxTokens { get; set; }

        [JsonPropertyName("stopSequences")]
        public List<string>? StopSequences { get; set; }

        [JsonPropertyName("metadata")]
        public Dictionary<string, object>? Metadata { get; set; }
    }

    /// <summary>
    /// MCP 消息
    /// </summary>
    public class MCPMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = string.Empty;

        [JsonPropertyName("content")]
        public MCPMessageContent Content { get; set; } = new();
    }

    /// <summary>
    /// MCP 消息内容
    /// </summary>
    public class MCPMessageContent
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "text";

        [JsonPropertyName("text")]
        public string? Text { get; set; }
    }

    /// <summary>
    /// MCP 模型偏好
    /// </summary>
    public class MCPModelPreferences
    {
        [JsonPropertyName("hints")]
        public List<MCPModelHint>? Hints { get; set; }

        [JsonPropertyName("costPriority")]
        public float? CostPriority { get; set; }

        [JsonPropertyName("speedPriority")]
        public float? SpeedPriority { get; set; }

        [JsonPropertyName("intelligencePriority")]
        public float? IntelligencePriority { get; set; }
    }

    /// <summary>
    /// MCP 模型提示
    /// </summary>
    public class MCPModelHint
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    /// <summary>
    /// MCP 流式响应块
    /// </summary>
    public class MCPStreamChunk
    {
        [JsonPropertyName("jsonrpc")]
        public string? JsonRpc { get; set; }

        [JsonPropertyName("method")]
        public string? Method { get; set; }

        [JsonPropertyName("params")]
        public MCPStreamParams? Params { get; set; }
    }

    /// <summary>
    /// MCP 流式参数
    /// </summary>
    public class MCPStreamParams
    {
        [JsonPropertyName("progressToken")]
        public int ProgressToken { get; set; }

        [JsonPropertyName("progress")]
        public float Progress { get; set; }

        [JsonPropertyName("total")]
        public float Total { get; set; }
    }
}
