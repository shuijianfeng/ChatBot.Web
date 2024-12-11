// Models/ChatModelConfig.cs
using System.Collections.Generic;

namespace ChatBot.Models
{
    public class ChatModelConfig
    {
        public string Name { get; set; } = string.Empty; // Ä£ÐÍÃû³Æ
        public string ApiEndpoint { get; set; } = string.Empty;
        public float Temperature { get; set; }
        public int MaxTokens { get; set; }
        public bool EnableSearch { get; set; }
        public bool Stream { get; set; }
        public string Model { get; set; } = string.Empty;
        public bool Include_usage { get; set; }
        public bool Isprompt { get; set; }
        public string Promptid { get; set; } = string.Empty;
        public bool EnableImageUpload { get; set; }
        public bool Incremental_output { get; set; }
    }

    public class ChatModelSettings: List<ChatModelConfig> 
    {
        
    }
}
