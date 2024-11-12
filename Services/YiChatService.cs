using ChatBot.Web.Models;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ChatBot.Web.Services
{
    public class YiChatService
    {
       

    }

    // 义千问API响应模型
    class YiChatResponse
    {
        public Choice[] Choices { get; set; } = Array.Empty<Choice>();
    }

    class Choice
    {
        public Delta Delta { get; set; } = new();
    }

    class Delta
    {
        public string Content { get; set; } = string.Empty;
    }
}