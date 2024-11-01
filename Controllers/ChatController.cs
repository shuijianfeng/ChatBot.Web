using Microsoft.AspNetCore.Mvc;
using ChatBot.Web.Models;
using ChatBot.Web.Services;
using System.Text.Json;

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

        //[HttpPost("stream")]
        //public async IActionResult StreamChat([FromBody] ChatRequest request)
        //{
        //    var messages = request.Messages;
        //    var settings = request.Settings;

        //    // 设置SSE响应头
        //    Response.Headers.Add("Content-Type", "text/event-stream");
        //    Response.Headers.Add("Cache-Control", "no-cache");
        //    Response.Headers.Add("Connection", "keep-alive");
             
        //    return Ok(_chatService.StreamChatAsync(messages, settings));
        //} 
    }

    //[HttpPost("stream")]
    //public async Task StreamChat([FromBody] ChatRequest request)
    //{
    //    Response.Headers.Add("Content-Type", "text/event-stream");
    //    Response.Headers.Add("Cache-Control", "no-cache");
    //    Response.Headers.Add("Connection", "keep-alive");

    //    try
    //    {
    //        IAsyncEnumerable<string> stream;
    //        if (request.Model.StartsWith("qwen-"))
    //        {
    //            stream = _qwenService.GenerateStreamViaDashScopeAsync(request.Message, request.Model);
    //        }
    //        else
    //        {
    //            stream = _qwenService.GenerateStreamViaOpenAIAsync(request.Message, request.Model);
    //        }

    //        await foreach (var chunk in stream)
    //        {
    //            var data = new { content = chunk };
    //            await Response.WriteAsync($"data: {JsonSerializer.Serialize(data)}\n\n");
    //            await Response.Body.FlushAsync();
    //        }
    //    }
    //    catch (Exception ex)
    //    {
    //        _logger.LogError(ex, "Error processing stream chat request");
    //        var errorData = new { error = "An error occurred while processing your request." };
    //        await Response.WriteAsync($"data: {JsonSerializer.Serialize(errorData)}\n\n");
    //    }
    //    finally
    //    {
    //        await Response.WriteAsync("data: [DONE]\n\n");
    //        await Response.Body.FlushAsync();
    //    }
    //}

    public class ChatRequest
    {
        public List<ChatMessage> Messages { get; set; } = new();
        public ChatSettings Settings { get; set; } = new();
    }
}