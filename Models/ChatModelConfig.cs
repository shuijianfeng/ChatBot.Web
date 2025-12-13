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

    public class ChatModelSettings: List<ChatModelConfig> 
    {
        
    }

    public class ErrorViewModel
    {
        public string ErrorCode { get; set; }
        public string Message { get; set; }
        public string RequestId { get; set; }
        public bool ShowRequestId => !string.IsNullOrEmpty(RequestId);
    }
}
