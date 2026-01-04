using System.Text;
using ChatBot.Models;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChatBot.Web.Services;
using Microsoft.AspNetCore.Mvc;
using System.Web;

using Microsoft.Extensions.Configuration;
using AngleSharp;

namespace ChatBot.Controllers
{
    /// <summary>
    /// 首页控制器
    /// </summary>
    public class HomeController : Controller
    {

        private readonly IChatService _chatService;
        private readonly IWebHostEnvironment _env;
        private readonly ChatSessionRepository _sessionRepository;
        private readonly StreamCacheService _streamCache;

        public HomeController(
            ILogger<HomeController> logger,
            IChatService chatService,
            IWebHostEnvironment webHostEnvironment,
            ChatSessionRepository sessionRepository,
            StreamCacheService streamCache,
            Microsoft.Extensions.Configuration.IConfiguration configuration)
        {

            _chatService = chatService;
            _env = webHostEnvironment;
            _sessionRepository = sessionRepository;
            _streamCache = streamCache;
        }

        /// <summary>
        /// 首页视图，支持通过参数设置默认主题和用户ID验证
        /// </summary>
        /// <param name="theme">主题设置：light 或 dark，默认跟随系统</param>
        /// <param name="uid">用户唯一标识，用于访问权限验证</param>
        /// <returns>首页视图或未授权页面</returns>
        public async Task<IActionResult> Index(string theme = null, string uid = null)
        {
            // 验证用户ID
            if (!string.IsNullOrEmpty(uid))
            {
                bool isValidUser = await _chatService.ValidateUserIdAsync(uid);
                if (!isValidUser)
                {
                    // 用户ID无效，返回未授权页面
                    return View("Unauthorized", new ErrorViewModel
                    {
                        Message = "您没有访问权限，请联系管理员获取有效的用户ID。",
                        ErrorCode = "401"
                    });
                }

                // 用户有效，可以在会话中保存用户ID
                //HttpContext.Session.SetString("UserId", uid);
            }
            // 如果没有提供UID但系统配置要求UID验证，检查会话中是否已存在验证过的UID
            else
            {
                // 用户ID无效，返回未授权页面
                return View("Unauthorized", new ErrorViewModel
                {
                    Message = "您没有访问权限，请联系管理员获取有效的用户ID。",
                    ErrorCode = "401"
                });
            }

            // 如果提供了有效的主题参数
            if (!string.IsNullOrEmpty(theme))
            {
                // 将主题参数传递给视图以便前端处理
                ViewBag.DefaultTheme = theme.ToLower() == "dark" ? "dark" :
                                       theme.ToLower() == "light" ? "light" : null;
            }
            else
            {
                ViewBag.DefaultTheme = "light";
            }

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

        /// <summary>
        /// 获取链接预览信息，返回包含标题、描述、图片、网站图标等信息
        /// </summary>
        /// <param name="request">包含URL的请求</param>
        /// <returns>链接预览信息</returns>
        [HttpPost]
        [Route("/api/chat/link-preview")]
        public async Task<IActionResult> GetLinkPreview([FromBody] LinkPreviewRequest request)
        {
            if (string.IsNullOrEmpty(request.Url))
            {
                return BadRequest(new { error = "URL不能为空" });
            }

            try
            {
                // 处理特殊URL格式
                if (!request.Url.StartsWith("http://") && !request.Url.StartsWith("https://"))
                {
                    request.Url = "https://" + request.Url;
                }

                // 创建 HttpClient
                using var httpClient = _chatService.HttpClientFactory.CreateClient();
                httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");

                // 设置超时，避免长时间等待
                httpClient.Timeout = TimeSpan.FromSeconds(8);

                // 使用 HttpCompletionOption.ResponseHeadersRead 只读取头部
                var response = await httpClient.GetAsync(request.Url, HttpCompletionOption.ResponseHeadersRead);

                // 检查内容类型
                var contentType = response.Content.Headers.ContentType?.MediaType;
                if (contentType != null && !contentType.StartsWith("text/"))
                {
                    // 处理非HTML内容，如PDF、图片等
                    var uri = new Uri(request.Url);
                    string filename = Path.GetFileName(uri.AbsolutePath);

                    return Ok(new
                    {
                        url = request.Url,
                        title = string.IsNullOrEmpty(filename) ? "文件" : filename,
                        description = $"{contentType} 类型文件",
                        favicon = $"https://www.google.com/s2/favicons?domain={uri.Host}&sz=64",
                        siteName = GetSimpleDomain(uri.Host)
                    });
                }

                response.EnsureSuccessStatusCode();

                // 设置最大内容长度限制，避免处理过大的页面
                const int maxLength = 500 * 1024; // 500KB
                var content = await response.Content.ReadAsStringAsync();
                if (content.Length > maxLength)
                {
                    content = content.Substring(0, maxLength);
                }

                // 使用 AngleSharp 解析 HTML
                var context = new BrowsingContext(Configuration.Default);
                var document = await context.OpenAsync(req => req.Content(content));

                // 获取标题 - 增强兼容性
                var title = document.QuerySelector("meta[property='og:title']")?.GetAttribute("content")
                         ?? document.QuerySelector("meta[name='twitter:title']")?.GetAttribute("content")
                         ?? document.QuerySelector("meta[itemprop='name']")?.GetAttribute("content")
                         ?? document.QuerySelector("title")?.TextContent?.Trim()
                         ?? document.QuerySelector("h1")?.TextContent?.Trim() // 尝试获取第一个H1标签内容
                         ?? "无标题";

                // 获取描述 - 增强兼容性
                var description = document.QuerySelector("meta[property='og:description']")?.GetAttribute("content")
                                ?? document.QuerySelector("meta[name='description']")?.GetAttribute("content")
                                ?? document.QuerySelector("meta[name='twitter:description']")?.GetAttribute("content")
                                ?? document.QuerySelector("meta[itemprop='description']")?.GetAttribute("content")
                                ?? document.QuerySelector("meta[name='summary']")?.GetAttribute("content");

                // 如果没有找到描述，尝试从首段文本提取
                if (string.IsNullOrEmpty(description))
                {
                    // 尝试获取第一段有意义的文本作为描述（排除短文本和菜单项等）
                    var paragraphs = document.QuerySelectorAll("p, div.content, article > div")
                        .Where(p => !string.IsNullOrWhiteSpace(p.TextContent) && p.TextContent.Trim().Length > 40)
                        .Select(p => p.TextContent.Trim())
                        .Take(1);

                    description = paragraphs.FirstOrDefault();
                }

                // 获取图片 - 增强兼容性
                var image = document.QuerySelector("meta[property='og:image']")?.GetAttribute("content")
                         ?? document.QuerySelector("meta[name='twitter:image']")?.GetAttribute("content")
                         ?? document.QuerySelector("meta[itemprop='image']")?.GetAttribute("content")
                         ?? document.QuerySelector("link[rel='image_src']")?.GetAttribute("href");

                // 尝试找到大图片（如果元数据中没有图片）
                if (string.IsNullOrEmpty(image))
                {
                    // 查找页面中可能的大图片（至少200x150像素大小）
                    // 先检查有明确width/height属性的图片
                    var imgWithSize = document.QuerySelectorAll("img[width][height]")
                        .FirstOrDefault(img =>
                        {
                            int.TryParse(img.GetAttribute("width"), out int width);
                            int.TryParse(img.GetAttribute("height"), out int height);
                            return width >= 200 && height >= 150;
                        });

                    if (imgWithSize != null)
                    {
                        image = imgWithSize.GetAttribute("src");
                    }
                    else
                    {
                        // 尝试获取第一个非图标大小的图片
                        image = document.QuerySelectorAll("img")
                            .Where(img => !img.GetAttribute("src")?.Contains("icon", StringComparison.OrdinalIgnoreCase) ?? false)
                            .Where(img => !img.GetAttribute("src")?.Contains("logo", StringComparison.OrdinalIgnoreCase) ?? false)
                            .Where(img =>
                            {
                                var src = img.GetAttribute("src");
                                return !string.IsNullOrEmpty(src) && !src.EndsWith(".svg") && !src.EndsWith(".ico");
                            })
                            .Select(img => img.GetAttribute("src"))
                            .FirstOrDefault();
                    }
                }

                // 尝试获取网站图标 - 增强兼容性
                var favicon = document.QuerySelector("link[rel='icon']")?.GetAttribute("href")
                          ?? document.QuerySelector("link[rel='shortcut icon']")?.GetAttribute("href")
                          ?? document.QuerySelector("link[rel='apple-touch-icon']")?.GetAttribute("href")
                          ?? document.QuerySelector("link[rel='apple-touch-icon-precomposed']")?.GetAttribute("href");

                // 构建绝对URL - 处理相对路径
                if (!string.IsNullOrEmpty(image) && !image.StartsWith("http"))
                {
                    try
                    {
                        Uri baseUri = new Uri(request.Url);
                        image = new Uri(baseUri, image).ToString();
                    }
                    catch
                    {
                        image = null;
                    }
                }

                // 同样处理网站图标URL - 处理相对路径
                if (!string.IsNullOrEmpty(favicon) && !favicon.StartsWith("http"))
                {
                    try
                    {
                        Uri baseUri = new Uri(request.Url);
                        favicon = new Uri(baseUri, favicon).ToString();
                    }
                    catch
                    {
                        favicon = null;
                    }
                }

                // 如果找不到图标，回退到网站根目录的默认图标位置
                if (string.IsNullOrEmpty(favicon))
                {
                    try
                    {
                        Uri baseUri = new Uri(request.Url);
                        var domainFavicon = new Uri(baseUri, "/favicon.ico").ToString();

                        // 尝试验证favicon是否存在（可选，但可能导致额外请求）
                        try
                        {
                            var faviconResponse = await httpClient.GetAsync(domainFavicon, HttpCompletionOption.ResponseHeadersRead);
                            if (faviconResponse.IsSuccessStatusCode)
                            {
                                favicon = domainFavicon;
                            }
                            else
                            {
                                // 使用Google的favicon服务作为备选
                                favicon = $"https://www.google.com/s2/favicons?domain={baseUri.Host}&sz=64";
                            }
                        }
                        catch
                        {
                            // 如果请求失败，使用Google的favicon服务
                            favicon = $"https://www.google.com/s2/favicons?domain={baseUri.Host}&sz=64";
                        }
                    }
                    catch
                    {
                        // URI处理失败，使用备选值
                        favicon = null;
                    }
                }

                // 获取网站名称 - 增强兼容性
                var siteName = document.QuerySelector("meta[property='og:site_name']")?.GetAttribute("content")
                            ?? document.QuerySelector("meta[name='application-name']")?.GetAttribute("content")
                            ?? document.QuerySelector("meta[name='twitter:site']")?.GetAttribute("content");

                // 如果没有明确的网站名称，尝试从URL获取可读性更好的名称
                if (string.IsNullOrEmpty(siteName))
                {
                    var uri = new Uri(request.Url);
                    siteName = GetSimpleDomain(uri.Host);
                }

                // 截断过长的描述
                if (!string.IsNullOrEmpty(description) && description.Length > 200)
                {
                    description = description.Substring(0, 197) + "...";
                }

                // 返回结果
                return Ok(new
                {
                    url = request.Url,
                    favicon = favicon,
                    siteName = siteName,
                    title = title,
                    description = description,
                    image = image
                });
            }
            catch (TaskCanceledException)
            {
                // 处理超时情况
                var uri = new Uri(request.Url);
                return Ok(new
                {
                    url = request.Url,
                    title = uri.Host,
                    description = "网页加载超时，无法获取完整预览。",
                    favicon = $"https://www.google.com/s2/favicons?domain={uri.Host}&sz=64",
                    siteName = GetSimpleDomain(uri.Host),
                    error = "timeout"
                });
            }
            catch (HttpRequestException ex)
            {
                // 处理HTTP错误
                try
                {
                    var uri = new Uri(request.Url);
                    return Ok(new
                    {
                        url = request.Url,
                        title = uri.Host,
                        description = $"无法访问此页面: {(ex.StatusCode.HasValue ? $"HTTP错误 {(int)ex.StatusCode}" : ex.Message)}",
                        favicon = $"https://www.google.com/s2/favicons?domain={uri.Host}&sz=64",
                        siteName = GetSimpleDomain(uri.Host),
                        error = ex.Message
                    });
                }
                catch
                {
                    return Ok(new
                    {
                        url = request.Url,
                        title = "无效URL",
                        description = "无法解析此URL",
                        error = ex.Message
                    });
                }
            }
            catch (UriFormatException)
            {
                // 处理无效URL
                return Ok(new
                {
                    url = request.Url,
                    title = "无效URL",
                    description = "提供的URL格式无效或无法解析",
                    error = "invalid_uri"
                });
            }
            catch (Exception ex)
            {
                // 处理其他异常
                try
                {
                    var uri = new Uri(request.Url);
                    return Ok(new
                    {
                        url = request.Url,
                        title = uri.Host,
                        description = "无法获取网页预览信息。",
                        favicon = $"https://www.google.com/s2/favicons?domain={uri.Host}&sz=64",
                        siteName = GetSimpleDomain(uri.Host),
                        error = ex.Message
                    });
                }
                catch
                {
                    return Ok(new
                    {
                        url = request.Url,
                        title = "预览不可用",
                        description = "无法获取网页预览信息",
                        error = ex.Message
                    });
                }
            }
        }

        /// <summary>
        /// 从主机名提取简化的域名作为网站名称
        /// </summary>
        /// <param name="hostname">主机名</param>
        /// <returns>简化后的域名作为网站名称</returns>
        private string GetSimpleDomain(string hostname)
        {
            try
            {
                // 移除www前缀
                if (hostname.StartsWith("www."))
                {
                    hostname = hostname.Substring(4);
                }

                // 只保留域名的主要部分
                var domainParts = hostname.Split('.');
                if (domainParts.Length >= 2)
                {
                    string mainPart = domainParts[domainParts.Length - 2];
                    // 首字母大写以提升可读性
                    return char.ToUpper(mainPart[0]) + mainPart.Substring(1);
                }
                return hostname;
            }
            catch
            {
                // 解析失败时返回原始主机名
                return hostname;
            }
        }
        //// 在 ChatController.cs 中添加
        //[HttpPost]
        //[Route("/api/chat/link-preview")]
        //public async Task<IActionResult> GetLinkPreview([FromBody] LinkPreviewRequest request)
        //{
        //    if (string.IsNullOrEmpty(request.Url))
        //    {
        //        return BadRequest(new { error = "URL不能为空" });
        //    }

        //    try
        //    {
        //        // 创建 HttpClient
        //        using var httpClient = _chatService.HttpClientFactory.CreateClient();
        //        //httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");

        //        // 设置超时，避免长时间等待
        //        httpClient.Timeout = TimeSpan.FromSeconds(10);

        //        // 获取网页内容
        //        var response = await httpClient.GetAsync(request.Url);
        //        response.EnsureSuccessStatusCode();
        //        var content = await response.Content.ReadAsStringAsync();

        //        // 使用 AngleSharp 解析 HTML
        //        var context = new BrowsingContext(Configuration.Default);
        //        var document = await context.OpenAsync(req => req.Content(content));

        //        // 获取标题
        //        var title = document.QuerySelector("meta[property='og:title']")?.GetAttribute("content")
        //                    ?? document.QuerySelector("title")?.TextContent?.Trim();

        //        // 获取描述
        //        var description = document.QuerySelector("meta[property='og:description']")?.GetAttribute("content")
        //                        ?? document.QuerySelector("meta[name='description']")?.GetAttribute("content");

        //        // 获取图片
        //        var image = document.QuerySelector("meta[property='og:image']")?.GetAttribute("content");

        //        // 尝试获取网站图标
        //        var favicon = document.QuerySelector("link[rel='icon']")?.GetAttribute("href")
        //                    ?? document.QuerySelector("link[rel='shortcut icon']")?.GetAttribute("href");


        //        // 构建绝对URL
        //        if (!string.IsNullOrEmpty(image) && !image.StartsWith("http"))
        //        {
        //            try
        //            {
        //                Uri baseUri = new Uri(request.Url);
        //                image = new Uri(baseUri, image).ToString();
        //            }
        //            catch
        //            {
        //                image = null;
        //            }
        //        }

        //        // 同样处理网站图标URL
        //        if (!string.IsNullOrEmpty(favicon) && !favicon.StartsWith("http"))
        //        {
        //            try
        //            {
        //                Uri baseUri = new Uri(request.Url);
        //                favicon = new Uri(baseUri, favicon).ToString();
        //            }
        //            catch
        //            {
        //                favicon = null;
        //            }
        //        }

        //        // 截断过长的描述
        //        if (!string.IsNullOrEmpty(description) && description.Length > 200)
        //        {
        //            description = description.Substring(0, 197) + "...";
        //        }

        //        // 返回结果，增加favicon和siteName字段
        //        var siteName = document.QuerySelector("meta[property='og:site_name']")?.GetAttribute("content");
        //        var hostname = new Uri(request.Url).Host;

        //        return Ok(new
        //        {
        //            url = request.Url,
        //            favicon = favicon,
        //            siteName = siteName ?? hostname,
        //            title = title ?? "无标题",
        //            description = description,
        //            image = image

        //        });
        //    }
        //    catch (TaskCanceledException)
        //    {
        //        return Ok(new
        //        {
        //            url = request.Url,
        //            title = "加载超时",
        //            description = "网页加载超时，无法获取预览",
        //            image = (string)null

        //        });
        //    }
        //    catch (Exception ex)
        //    {
        //        // 返回基本信息，避免整个预览功能失败
        //        return Ok(new
        //        {
        //            url = request.Url,
        //            title = "预览不可用",
        //            description = "无法获取网页预览信息",
        //            error = ex.Message
        //        });
        //    }
        //}

        public class LinkPreviewRequest
        {
            public string Url { get; set; }
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
        [HttpPost]
        [Route("/api/chat/stream")]
        public async Task StreamChat([FromBody] ChatRequest request)
        {
            // 创建 streamId 用于断线重连
            var streamId = _streamCache.CreateStream();

            // 使用独立的取消令牌，防止客户端断开连接（如锁屏）导致后端停止生成
            // 设置 5 分钟超时作为安全限制，防止无限挂起
            using var generationCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var requestToken = HttpContext.RequestAborted;

            try
            {
                // 先尽力发送 streamId 给前端
                try
                {
                    var initData = new { streamId = streamId };
                    await Response.WriteAsync($"data: {JsonSerializer.Serialize(initData)}\n\n", requestToken);
                    await Response.Body.FlushAsync(requestToken);
                }
                catch
                {
                    // 忽略初始写入错误（如已断开），继续执行后端生成以便缓存
                }

                bool Incremental_output = _chatService.GetModelConfig(request.Model).Incremental_output;

                // 关键点：传递 generationCts.Token 而不是 requestToken 给生成服务
                // 这样即使 requestToken 取消（客户端断开），生成也会继续
                IAsyncEnumerable<string> stream = _chatService.GenerateStreamAsync(request, generationCts.Token);

                int count = 0;
                await foreach (var chunk in stream)
                {
                    string str = chunk;
                    if (!Incremental_output)
                    {
                        str = chunk.Substring(count);
                        count = chunk.Length;
                    }

                    // 写入缓存 (这是最重要的，供 Resume 使用)
                    _streamCache.AppendContent(streamId, str);

                    // 尝试推送到当前连接
                    // 如果客户端还在连接，就推送；如果断开了，catch 住错误但继续循环
                    if (!requestToken.IsCancellationRequested)
                    {
                        try
                        {
                            var data = new { content = str };
                            await Response.WriteAsync($"data: {JsonSerializer.Serialize(data)}\n\n", requestToken);
                            await Response.Body.FlushAsync(requestToken);
                        }
                        catch
                        {
                            // 网络写入失败，仅仅忽略，不中断生成循环
                        }
                    }
                }

                // 只有在生成循环完整结束后，才标记完成
                _streamCache.MarkCompleted(streamId);
            }
            catch (Exception)
            {
                // 生成过程发生内部错误，或者 generationCts 超时
                if (!requestToken.IsCancellationRequested)
                {
                    try
                    {
                        var errorData = new { error = "在处理您的请求时发生了错误。" };
                        await Response.WriteAsync($"data: {JsonSerializer.Serialize(errorData)}\n\n", requestToken);
                    }
                    catch { }
                }
                _streamCache.MarkCompleted(streamId);
            }
            finally
            {
                if (!requestToken.IsCancellationRequested)
                {
                    try
                    {
                        await Response.WriteAsync("data: [DONE]\n\n", requestToken);
                        await Response.Body.FlushAsync(requestToken);
                    }
                    catch { }
                }
            }
        }

        /// <summary>
        /// 恢复断线的流式传输，获取从指定偏移量开始的缓存内容
        /// </summary>
        [HttpGet]
        [Route("/api/chat/stream/{streamId}/resume")]
        public IActionResult ResumeStream(string streamId, [FromQuery] int offset = 0)
        {
            if (string.IsNullOrEmpty(streamId))
            {
                return BadRequest(new { error = "streamId 不能为空" });
            }

            var result = _streamCache.GetContentFromOffset(streamId, offset);
            if (result == null)
            {
                return NotFound(new { error = "流不存在或已过期" });
            }

            return Ok(new
            {
                content = result.Content,
                totalLength = result.TotalLength,
                isCompleted = result.IsCompleted
            });
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

        #region 会话管理API

        /// <summary>
        /// 获取用户的所有会话列表
        /// </summary>
        [HttpGet]
        [Route("/api/sessions/{uid}")]
        public async Task<IActionResult> GetUserSessions(string uid)
        {
            if (string.IsNullOrEmpty(uid))
            {
                return BadRequest(new { error = "用户ID不能为空" });
            }

            var sessions = await _sessionRepository.GetSessionsByUserAsync(uid);
            return Ok(sessions.Select(s => new
            {
                id = s.Id,
                title = s.Title,
                modelName = s.ModelName,
                createdAt = s.CreatedAt,
                updatedAt = s.UpdatedAt
            }));
        }

        /// <summary>
        /// 获取会话详情（包含消息）
        /// </summary>
        [HttpGet]
        [Route("/api/sessions/{uid}/{sessionId}")]
        public async Task<IActionResult> GetSessionDetail(string uid, string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                return BadRequest(new { error = "会话ID不能为空" });
            }

            var session = await _sessionRepository.GetSessionWithMessagesAsync(sessionId);
            if (session == null)
            {
                return NotFound(new { error = "会话不存在" });
            }

            // 验证会话归属
            if (session.Uid != uid)
            {
                return Forbid();
            }

            return Ok(new
            {
                id = session.Id,
                title = session.Title,
                modelName = session.ModelName,
                createdAt = session.CreatedAt,
                updatedAt = session.UpdatedAt,
                messages = session.Messages.Select(m => new
                {
                    role = m.Role,
                    content = m.Content,
                    imageUrls = m.ImageUrls,
                    createdAt = m.CreatedAt
                })
            });
        }

        /// <summary>
        /// 保存会话
        /// </summary>
        [HttpPost]
        [Route("/api/sessions/save")]
        public async Task<IActionResult> SaveSession([FromBody] SaveSessionRequest request)
        {
            if (string.IsNullOrEmpty(request?.SessionId) || string.IsNullOrEmpty(request?.Uid))
            {
                return BadRequest(new { error = "会话ID和用户ID不能为空" });
            }

            var success = await _sessionRepository.SaveSessionAsync(request);
            if (success)
            {
                return Ok(new { success = true, sessionId = request.SessionId });
            }

            return BadRequest(new { error = "保存会话失败" });
        }

        /// <summary>
        /// 删除会话
        /// </summary>
        [HttpDelete]
        [Route("/api/sessions/{sessionId}")]
        public async Task<IActionResult> DeleteSession(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                return BadRequest(new { error = "会话ID不能为空" });
            }

            var success = await _sessionRepository.DeleteSessionAsync(sessionId);
            if (success)
            {
                return Ok(new { success = true });
            }

            return NotFound(new { error = "会话不存在或删除失败" });
        }

        #endregion
    }

}