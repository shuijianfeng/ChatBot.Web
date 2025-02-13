using System.Text;
using ChatBot.Models;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChatBot.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace ChatBot.Controllers
{
    /// <summary>
    /// 首页控制器
    /// </summary>
    public class HomeController : Controller
    {

        private readonly IChatService _chatService;
        private readonly IWebHostEnvironment _env;

        public HomeController(
            ILogger<HomeController> logger,
            IChatService chatService,
            IWebHostEnvironment webHostEnvironment,
            IConfiguration configuration)
        {

            _chatService = chatService;
            _env = webHostEnvironment;
        }

        /// <summary>
        /// 首页视图
        /// </summary>
        public IActionResult Index()
        {
            //ViewBag.AvailableModels = _chatService.GetAvailableModels();
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

        [HttpGet]
        [Route("/api/chat/GetChatModels")]
        public IActionResult GetChatModels()
        {
            List<ChatModelConfig> chatModels = _chatService.GetModels();
            return Ok(chatModels);
        }
        [HttpPost]
        [Route("/api/chat/upload-image")]
        public async Task<IActionResult> UploadImage([FromForm] IFormFile image)
        {
            if (image == null || image.Length == 0)
                return BadRequest(new { error = "未选择任何文件。" });

            // 验证文件类型
            if (!image.ContentType.StartsWith("image/"))
                return BadRequest(new { error = "仅支持图片文件。" });

            // 可选：限制文件大小
            const long maxSize = 5 * 1024 * 1024; // 5MB
            if (image.Length > maxSize)
                return BadRequest(new { error = "文件大小超过限制（5MB）。" });

            // 生成唯一文件名
            var fileExtension = Path.GetExtension(image.FileName);
            var fileName = $"{Path.GetFileNameWithoutExtension(image.FileName)}-{System.Guid.NewGuid()}{fileExtension}";
            var uploadsFolder = Path.Combine(_env.WebRootPath, "uploads");

            if (!Directory.Exists(uploadsFolder))
            {
                Directory.CreateDirectory(uploadsFolder);
            }

            var filePath = Path.Combine(uploadsFolder, fileName);

            using (var stream = new FileStream(filePath, FileMode.Create))
            {
                await image.CopyToAsync(stream);
            }

            // 返回图片的URL
            var imageUrl = $"{Request.Scheme}://{Request.Host}/uploads/{fileName}";
            return Ok(new { url = imageUrl });
        }


        [HttpPost]
        [Route("/api/chat/stream")]
        public async Task StreamChat([FromBody] ChatRequest request)
        {

            //Response.Headers.Append("Content-Type", "text/event-stream");
            //Response.Headers.Append("Cache-Control", "no-cache");
            Response.Headers.Append("Connection", "keep-alive");
            //Response.Headers.Append("X-Accel-Buffering", "no");
            var cancellationToken = HttpContext.RequestAborted;
            try
            {
                bool Incremental_output= _chatService.GetModelConfig(request.Model).Incremental_output;
                IAsyncEnumerable<string> stream;
                stream = _chatService.GenerateStreamAsync(request, cancellationToken);
                int count = 0;
                await foreach (var chunk in stream)  
                {
                    string str = chunk;
                    if (!Incremental_output)
                    {
                        str = chunk.Substring(count);
                        count = chunk.Length;
                    }
                    var data = new { content = str };

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
        [HttpPost("/api/chat/export-message-docx")]
        public async Task<IActionResult> ExportMessageToDocx([FromBody] ExportMessageRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request?.Content))
                {
                    return BadRequest(new { error = "导出内容不能为空" });
                }

                var fileName = $"聊天消息_{DateTime.Now:yyyy-MM-dd-HHmmss}.docx";
                var bytes = await _chatService.ExportMessageToDocx(request.Content);

                if (bytes == null || bytes.Length == 0)
                {
                    return BadRequest(new { error = "生成DOCX文件失败" });
                }

                // 直接返回文件流
                return File(bytes, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", fileName);
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = $"导出失败: {ex.Message}" });
            }
        }
        //[HttpPost("/api/chat/export-message-docx")]
        //public async Task<IActionResult> ExportMessageToDocx([FromBody] ExportMessageRequest request)
        //{
        //    try
        //    {
        //        var fileName = $"聊天消息_{DateTime.Now:yyyy-MM-dd-HHmmss}.docx";
        //        var filesFolder = Path.Combine(_env.WebRootPath, "files");

        //        // 确保文件夹存在
        //        if (!Directory.Exists(filesFolder))
        //        {
        //            Directory.CreateDirectory(filesFolder);
        //        }

        //        var filePath = Path.Combine(filesFolder, fileName);
        //        var bytes = await _chatService.ExportMessageToDocx(request.Content);

        //        // 保存文件
        //        await System.IO.File.WriteAllBytesAsync(filePath, bytes);

        //        // 返回文件URL
        //        var fileUrl = $"{Request.Scheme}://{Request.Host}/files/{fileName}";
        //        return Ok(new { url = fileUrl });
        //    }
        //    catch (Exception ex)
        //    {
        //        return BadRequest("导出失败");
        //    }
        //}

        [HttpPost("/api/chat/export-message-pdf")]
        public async Task<IActionResult> ExportMessageToPdf([FromBody] ExportMessageRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request?.Content))
                {
                    //_logger.LogWarning("导出PDF失败: 内容为空");
                    return BadRequest(new { error = "导出内容不能为空" });
                }

                var fileName = $"聊天消息_{DateTime.Now:yyyy-MM-dd-HHmmss}.pdf";
                var bytes = await _chatService.ExportMessageToPdf(request.Content);

                if (bytes == null || bytes.Length == 0)
                {
                    //_logger.LogError("导出PDF失败: 生成的文件为空");
                    return BadRequest(new { error = "生成PDF文件失败" });
                }

                // 直接返回文件流
                return File(bytes, "application/pdf", fileName);
            }
            catch (Exception ex)
            {
                //_logger.LogError(ex, "导出PDF时发生异常: {Message}", ex.Message);
                return BadRequest(new { error = $"导出失败: {ex.Message}" });
            }
        }
        //[HttpPost("/api/chat/export-message-pdf")]
        //public async Task<IActionResult> ExportMessageToPdf([FromBody] ExportMessageRequest request)
        //{
        //    try
        //    {
        //        var fileName = $"聊天消息_{DateTime.Now:yyyy-MM-dd-HHmmss}.pdf";
        //        var filesFolder = Path.Combine(_env.WebRootPath, "files");

        //        // 确保文件夹存在
        //        if (!Directory.Exists(filesFolder))
        //        {
        //            Directory.CreateDirectory(filesFolder);
        //        }

        //        var filePath = Path.Combine(filesFolder, fileName);
        //        var bytes = await _chatService.ExportMessageToPdf(request.Content);

        //        // 保存文件
        //        await System.IO.File.WriteAllBytesAsync(filePath, bytes);

        //        // 返回文件URL
        //        var fileUrl = $"{Request.Scheme}://{Request.Host}/files/{fileName}";
        //        return Ok(new { url = fileUrl });
        //    }
        //    catch (Exception ex)
        //    {
        //        return BadRequest("导出失败");
        //    }
        //}

        //[HttpPost("/api/chat/export-message-pdf")]
        //public async Task<IActionResult> ExportMessageToPdf([FromBody] ExportMessageRequest request)
        //{
        //    try
        //    {
        //        if (string.IsNullOrEmpty(request?.Content))
        //        {

        //            return BadRequest(new { error = "导出内容不能为空" });
        //        }

        //        var bytes = await _chatService.ExportMessageToPdf(request.Content);
        //        if (bytes == null || bytes.Length == 0)
        //        {

        //            return BadRequest(new { error = "生成PDF文件失败" });
        //        }

        //        var fileName = $"聊天消息_{DateTime.Now:yyyy-MM-dd-HHmmss}.pdf";
        //        // 直接返回文件流
        //        return File(bytes, "application/pdf", fileName);
        //    }
        //    catch (Exception ex)
        //    {

        //        return BadRequest(new { error = $"导出失败: {ex.Message}" });
        //    }
        //}

        public class ExportMessageRequest
        {
            public string Content { get; set; }
        }
    }
}