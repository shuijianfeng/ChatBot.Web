namespace ChatBot.Web.Models
{
    // 聊天消息模型
    public class ChatMessage
    {
        public string Role { get; set; } = "user";        // 角色：user或assistant
        public string Content { get; set; } = string.Empty;  // 消息内容
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;  // 时间戳
    }

    // 聊天设置模型
    public class ChatSettings
    {
        public string Model { get; set; } = "qwen-turbo";  // 默认模型
        public float Temperature { get; set; } = 0.7f;     // 温度参数
        public bool StreamOutput { get; set; } = true;     // 是否启用流式输出
    }
}