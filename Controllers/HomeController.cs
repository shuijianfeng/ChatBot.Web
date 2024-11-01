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
            Response.Headers.Add("Content-Type", "text/event-stream");
            Response.Headers.Add("Cache-Control", "no-cache");
            Response.Headers.Add("Connection", "keep-alive");

            try
            {
                IAsyncEnumerable<string> stream;
                if (request.Model== "软件及业务问答")
                {
                    stream = _chatService.GenerateStreamViaDashScopeAsync(request, "cb3fb45aeaf347b8bf51373d4ded12b2");
                }
                else
                {
                    stream = _chatService.GenerateStreamViaOpenAIAsync( request);
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
                _logger.LogError(ex, "Error processing stream chat request");
                var errorData = new { error = "An error occurred while processing your request." };
                await Response.WriteAsync($"data: {JsonSerializer.Serialize(errorData)}\n\n");
            }
            finally
            {
                await Response.WriteAsync("data: [DONE]\n\n");
                await Response.Body.FlushAsync();
            }
        }

        /// <summary>
        /// 聊天API端点
        /// </summary>
        [HttpPost]
        [Route("/api/chat/stream1")]
        public async Task ChatAsync([FromBody] ChatRequest request)
        {
            // 验证请求
            if (string.IsNullOrEmpty(request.Message))
            {
                Response.StatusCode = 400;
                await Response.WriteAsJsonAsync(new ErrorResponse
                {
                    Code = "InvalidRequest",
                    Message = "消息不能为空"
                });
                return;
            }

            try
            {
                // 设置响应头
                Response.Headers.Add("Content-Type", "text/event-stream");
                Response.Headers.Add("Cache-Control", "no-cache");
                Response.Headers.Add("Connection", "keep-alive");

                // 获取响应流
                var response = await _chatService.GetChatResponseAsync(request);
                var streamWriter = new StreamWriter(Response.Body);

                // 处理流式响应
                await foreach (var chunk in response)
                {
                    if (chunk.Event == StreamEventType.Error)
                    {
                        _logger.LogError("Chat API error: {Message}", chunk.Data?.Content);
                        await WriteEventAsync(streamWriter, StreamEventType.Error, new ErrorResponse
                        {
                            Code = "ApiError",
                            Message = "API调用出错"
                        });
                        break;
                    }

                    // 发送数据块
                    await WriteEventAsync(streamWriter, chunk.Event, chunk.Data);

                    // 如果是结束信号，结束流式输出
                    if (chunk.Event == StreamEventType.End)
                    {
                        break;
                    }
                }

                await streamWriter.FlushAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "处理聊天请求时发生错误");
                Response.StatusCode = 500;
                await Response.WriteAsJsonAsync(new ErrorResponse
                {
                    Code = "InternalError",
                    Message = "服务器内部错误"
                });
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