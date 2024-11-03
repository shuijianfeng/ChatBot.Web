using System.Text;
using ChatBot.Models;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChatBot.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace ChatBot.Controllers
{
    /// <summary>
    /// 首页控制器
    /// </summary>
    public class HomeController : Controller
    {
        private readonly ILogger<HomeController> _logger;
        private readonly IChatService _chatService;
        private readonly IConfiguration _configuration;

        public HomeController(
            ILogger<HomeController> logger,
            IChatService chatService,
            IConfiguration configuration)
        {
            _logger = logger;
            _chatService = chatService;
            _configuration = configuration;
        }

        /// <summary>
        /// 首页视图
        /// </summary>
        public IActionResult Index()
        {
            return View();
        }

        /// <summary>
        /// 关于页面
        /// </summary>
        public IActionResult About()
        {
            return View();
        }

        /// <summary>
        /// 隐私政策页面
        /// </summary>
        public IActionResult Privacy()
        {
            return View();
        }


        [HttpPost]
        [Route("/api/chat/stream")]
        public async Task StreamChat([FromBody] ChatRequest request)
        {
            
            Response.Headers.Append("Content-Type", "text/event-stream");
            Response.Headers.Append("Cache-Control", "no-cache");
            Response.Headers.Append("Connection", "keep-alive");
            Response.Headers.Append("X-Accel-Buffering", "no");
            var cancellationToken = HttpContext.RequestAborted;
            try
            {
                IAsyncEnumerable<string> stream;
                if (request.Model== "软件及业务问答")
                {
                    stream = _chatService.GenerateStreamViaDashScopeAsync(request, "cb3fb45aeaf347b8bf51373d4ded12b2", cancellationToken);
                }
                else
                {
                    stream = _chatService.GenerateStreamViaOpenAIAsync( request, cancellationToken);
                }

                await foreach (var chunk in stream)
                {
                    var data = new { content = chunk };
                    await Response.WriteAsync($"data: {JsonSerializer.Serialize(data)}\n\n");
                    await Response.Body.FlushAsync();
                }
            }
            catch (Exception ex)
            {
               
                var errorData = new { error = "在处理您的请求时发生了错误。" };
                await Response.WriteAsync($"data: {JsonSerializer.Serialize(errorData)}\n\n");
            }
            finally
            {
                await Response.WriteAsync("data: [DONE]\n\n");
                await Response.Body.FlushAsync();
            }
        }

        
        /// <summary>
        /// 写入SSE事件
        /// </summary>
        private static async Task WriteEventAsync<T>(StreamWriter writer, string eventType, T data)
        {
            await writer.WriteAsync($"event: {eventType}\n");
            await writer.WriteAsync($"data: {System.Text.Json.JsonSerializer.Serialize(data)}\n\n");
            await writer.FlushAsync();
        }

        /// <summary>
        /// 错误页面
        /// </summary>
        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            return View();
        }
    }
}