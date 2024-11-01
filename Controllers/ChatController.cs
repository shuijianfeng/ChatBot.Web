using Microsoft.AspNetCore.Mvc;
using ChatBot.Web.Models;
using ChatBot.Web.Services;

namespace ChatBot.Web.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ChatController : ControllerBase
    {
        private readonly YiChatService _chatService;

        public ChatController(YiChatService chatService)
        {
            _chatService = chatService;
        }

        [HttpPost("stream")]
        public IActionResult StreamChat([FromBody] ChatRequest request)
        {
            var messages = request.Messages;
            var settings = request.Settings;

            // 设置SSE响应头
            Response.Headers.Add("Content-Type", "text/event-stream");
            Response.Headers.Add("Cache-Control", "no-cache");
            Response.Headers.Add("Connection", "keep-alive");

            return Ok(_chatService.StreamChatAsync(messages, settings));
        }
    }

    public class ChatRequest
    {
        public List<ChatMessage> Messages { get; set; } = new();
        public ChatSettings Settings { get; set; } = new();
    }
}