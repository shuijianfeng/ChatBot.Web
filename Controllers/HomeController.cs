using System.Buffers;
using System.Collections.Frozen;
using System.Text;
using ChatBot.Models;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChatBot.Web.Services;
using Microsoft.AspNetCore.Mvc;
using System.Web;

using AngleSharp;
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
        private readonly ChatSessionRepository _sessionRepository;
        private readonly StreamCacheService _streamCache;

        /// <summary>
        /// 文件扩展名 → Content-Type 映射（不可变冻结字典，O(1) 查找）。
        /// </summary>
        private static readonly FrozenDictionary<string, string> _extensionToContentType =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [".mp3"] = "audio/mpeg",
                [".wav"] = "audio/wav",
                [".ogg"] = "audio/ogg",
                [".opus"] = "audio/opus",
                [".flac"] = "audio/flac",
                [".aac"] = "audio/aac",
                [".m4a"] = "audio/mp4",
            }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Content-Type → 文件扩展名映射（不可变冻结字典，O(1) 查找）。
        /// </summary>
        private static readonly FrozenDictionary<string, string> _contentTypeToExtension =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["audio/wav"] = "wav",
                ["audio/x-wav"] = "wav",
                ["audio/ogg"] = "ogg",
                ["audio/opus"] = "opus",
                ["audio/flac"] = "flac",
                ["audio/aac"] = "aac",
                ["audio/mp4"] = "m4a",
            }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 缓存已消费的 TTS 流式音频数据，防止 waveform-player 被流式渲染重建时重复 fetch 返回 404。
        /// key: streamId, value: (audioBytes, contentType)
        /// </summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (byte[] AudioBytes, string ContentType)>
            _consumedTtsStreams = new();

        /// <summary>
        /// 防止同一 streamId 的并发请求重复调用上游 TTS 工厂（避免 429 限流）。
        /// </summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim>
            _streamLocks = new();

        private string BuildAbsoluteUrl(string relativePath)
        {
            var pathBase = Request.PathBase.HasValue ? Request.PathBase.Value : string.Empty;
            relativePath = relativePath.StartsWith('/') ? relativePath : $"/{relativePath}";
            return $"{Request.Scheme}://{Request.Host}{pathBase}{relativePath}";
        }

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
                    description = description[..197] + "...";
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
                hostname = hostname[4..];
                }

                // 只保留域名的主要部分
                var domainParts = hostname.Split('.');
                if (domainParts.Length >= 2)
                {
                    string mainPart = domainParts[^2];
                    // 首字母大写以提升可读性
                    return char.ToUpper(mainPart[0]) + mainPart[1..];
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

        /// <summary>
        /// 保存 HTML 内容到服务器并返回可分享链接
        /// </summary>
        [HttpPost]
        [Route("/api/chat/save-html")]
        public async Task<IActionResult> SaveHtml([FromBody] SaveHtmlRequest request)
        {
            if (string.IsNullOrEmpty(request?.HtmlContent))
            {
                return BadRequest(new { error = "HTML内容不能为空" });
            }

            try
            {
                // 生成唯一文件名
                var fileName = $"page-{Guid.NewGuid():N}.html";
                // 修改保存路径到 Data/SharedHtml，避免 wwwroot 变动触发 dotnet watch 刷新
                var dataFolder = Path.Combine(Directory.GetCurrentDirectory(), "Data", "SharedHtml");

                // 确保目录存在
                if (!Directory.Exists(dataFolder))
                {
                    Directory.CreateDirectory(dataFolder);
                }

                var filePath = Path.Combine(dataFolder, fileName);

                // 保存 HTML 文件
                await System.IO.File.WriteAllTextAsync(filePath, request.HtmlContent, Encoding.UTF8);

                // 返回动态访问链接
                var shareUrl = BuildAbsoluteUrl($"/share/view/{fileName}");
                return Ok(new { success = true, url = shareUrl, fileName });
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = $"保存失败: {ex.Message}" });
            }
        }

        /// <summary>
        /// 获取分享的 HTML 页面
        /// </summary>
        [HttpGet]
        [Route("/share/view/{fileName}")]
        public async Task<IActionResult> GetHtml(string fileName)
        {
            try
            {
                if (string.IsNullOrEmpty(fileName) || !fileName.EndsWith(".html"))
                {
                    return BadRequest("无效的文件名");
                }

                // 安全检查：只允许访问文件名，不允许路径遍历
                fileName = Path.GetFileName(fileName);
                var filePath = Path.Combine(Directory.GetCurrentDirectory(), "Data", "SharedHtml", fileName);

                if (!System.IO.File.Exists(filePath))
                {
                    return NotFound("页面不存在或已过期");
                }

                var content = await System.IO.File.ReadAllTextAsync(filePath, Encoding.UTF8);
                return Content(content, "text/html");
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"获取页面失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 获取分享的音频文件。
        /// </summary>
        [HttpGet]
        [Route("/share/media/{fileName}")]
        public IActionResult GetSharedMedia(string fileName)
        {
            try
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

                fileName = Path.GetFileName(fileName);
                var filePath = Path.Combine(Directory.GetCurrentDirectory(), "Data", "Sharedmedia", fileName);

                if (!System.IO.File.Exists(filePath))
                {
                    return NotFound("音频不存在或已过期");
                }

                var extension = Path.GetExtension(fileName);
                var contentType = _extensionToContentType.GetValueOrDefault(extension, "application/octet-stream");

                Response.Headers.CacheControl = "public, max-age=86400";
                return PhysicalFile(filePath, contentType, enableRangeProcessing: true);
            }
            catch (ArgumentException)
            {
                return BadRequest("无效的文件名");
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"获取音频失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 代理输出文本转语音的流式音频响应。
        /// 首次请求时从上游 TTS 逐块转发音频数据到客户端（边收边播），同时缓存完整数据。
        /// 后续请求直接返回缓存数据。音频同时持久化到磁盘，应用重启后仍可访问。
        /// <summary>
        /// 检查流式音频是否已缓存。waveform-player 用此接口判断是否可安全预加载。
        /// 仅检查内存缓存和磁盘持久化，不触发上游 TTS 调用。
        /// 返回 204 表示已缓存，404 表示未缓存（流尚未消费）。
        /// </summary>
        [HttpHead]
        [Route("/share/media/stream/{streamId}")]
        public IActionResult HeadSharedMediaStream(string streamId)
        {
            if (string.IsNullOrWhiteSpace(streamId))
                return BadRequest();

            if (_consumedTtsStreams.ContainsKey(streamId))
                return NoContent();

            if (FindPersistedStreamFile(streamId) != null)
                return NoContent();

            return NotFound();
        }

        /// <summary>
        /// 代理输出文本转语音的流式音频响应。
        /// 首次请求时从上游 TTS 逐块转发音频数据到客户端（边收边播），同时缓存完整数据。
        /// 后续请求直接返回缓存数据。音频同时持久化到磁盘，应用重启后仍可访问。
        /// 使用 SemaphoreSlim 防止并发请求重复调用上游 TTS（避免 429 限流）。
        /// </summary>
        [HttpGet]
        [Route("/share/media/stream/{streamId}")]
        public async Task<IActionResult> GetSharedMediaStream(string streamId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(streamId))
            {
                return BadRequest("无效的流标识");
            }

            // 1. 如果已缓存（之前已消费过），直接返回缓存的音频数据
            if (_consumedTtsStreams.TryGetValue(streamId, out var cached))
            {
                return File(cached.AudioBytes, cached.ContentType);
            }

            // 2. 检查磁盘上是否已持久化（应用重启后内存缓存丢失，但文件仍在）
            var persistedFile = FindPersistedStreamFile(streamId);
            if (persistedFile != null)
            {
                return PhysicalFile(persistedFile.Value.FilePath, persistedFile.Value.ContentType, enableRangeProcessing: true);
            }

            // 3. 尝试获取流工厂（TryGetValue，不移除）
            if (!_chatService.TryTakeTextToSpeechStream(streamId, out var streamFactory) || streamFactory == null)
            {
                return NotFound("音频流不存在或已过期");
            }

            // 4. 使用 SemaphoreSlim 确保同一 streamId 只有一个请求调用上游 TTS
            var streamLock = _streamLocks.GetOrAdd(streamId, _ => new SemaphoreSlim(1, 1));
            await streamLock.WaitAsync(CancellationToken.None);
            try
            {
                // 4a. 双重检查：等待期间可能其他请求已完成缓存
                if (_consumedTtsStreams.TryGetValue(streamId, out cached))
                {
                    return File(cached.AudioBytes, cached.ContentType);
                }

                var persistedFile2 = FindPersistedStreamFile(streamId);
                if (persistedFile2 != null)
                {
                    return PhysicalFile(persistedFile2.Value.FilePath, persistedFile2.Value.ContentType, enableRangeProcessing: true);
                }

                // 5. 使用独立超时令牌发起上游 TTS 请求
                using var upstreamCts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                HttpResponseMessage upstreamResponse;
                try
                {
                    upstreamResponse = await streamFactory(upstreamCts.Token);
                }
                catch (OperationCanceledException)
                {
                    return StatusCode(503, "TTS 服务请求超时");
                }
                catch (Exception ex)
                {
                    return StatusCode(502, $"TTS 上游请求失败: {ex.Message}");
                }

                using (upstreamResponse)
                {
                    if (!upstreamResponse.IsSuccessStatusCode)
                    {
                        var errorContent = await upstreamResponse.Content.ReadAsStringAsync(CancellationToken.None);
                        return StatusCode((int)upstreamResponse.StatusCode, errorContent);
                    }

                    var contentType = upstreamResponse.Content.Headers.ContentType?.ToString() ?? "audio/mpeg";

                    // 6. 逐块转发上游音频数据到客户端，实现边收边播；同时缓存完整数据供后续请求复用
                    Response.StatusCode = 200;
                    Response.ContentType = contentType;
                    Response.Headers.CacheControl = "no-cache";

                    // 禁用输出缓冲，确保每个分块立即发送到客户端
                    var bodyFeature = HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>();
                    bodyFeature?.DisableBuffering();

                    // 使用 ArrayPool 租借缓冲区，避免每次请求在堆上分配 64KB
                    var chunk = ArrayPool<byte>.Shared.Rent(64 * 1024);
                    try
                    {
                        await using var upstreamStream = await upstreamResponse.Content.ReadAsStreamAsync(CancellationToken.None);
                        using var buffer = new MemoryStream(64 * 1024);
                        bool clientWriteFailed = false;
                        int bytesRead;
                        while ((bytesRead = await upstreamStream.ReadAsync(chunk.AsMemory(), CancellationToken.None)) > 0)
                        {
                            // 始终先写入缓冲区，确保数据完整性
                            buffer.Write(chunk, 0, bytesRead);

                            // 尝试转发到客户端；即使客户端断开也继续读取上游数据以保证缓存
                            if (!clientWriteFailed)
                            {
                                try
                                {
                                    var slice = chunk.AsMemory(0, bytesRead);
                                    await Response.Body.WriteAsync(slice, CancellationToken.None);
                                    await Response.Body.FlushAsync(CancellationToken.None);
                                }
                                catch
                                {
                                    // 客户端已断开连接，继续读取上游数据以确保缓存完成
                                    clientWriteFailed = true;
                                }
                            }
                        }

                        var audioBytes = buffer.ToArray();

                        // 7. 缓存完整音频数据，供后续重复请求使用
                        _consumedTtsStreams.TryAdd(streamId, (audioBytes, contentType));

                        // 8. 5 分钟后自动清理内存缓存和锁，防止内存泄漏（磁盘文件保留）
                        _ = Task.Delay(TimeSpan.FromMinutes(5)).ContinueWith(__ =>
                        {
                            _consumedTtsStreams.TryRemove(streamId, out _);
                            _streamLocks.TryRemove(streamId, out _);
                        });

                        // 9. 持久化到磁盘，应用重启后仍可通过 streamId 访问
                        _ = PersistStreamAudioToDiskAsync(streamId, audioBytes, contentType);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(chunk);
                    }

                    // 响应体已逐块写入完毕，返回空结果
                    return new EmptyResult();
                }
            }
            finally
            {
                streamLock.Release();
            }
        }

        /// <summary>
        /// 将流式音频持久化到 Sharedmedia 目录，文件名包含 streamId 以便重启后检索。
        /// </summary>
        private static async Task PersistStreamAudioToDiskAsync(string streamId, byte[] audioBytes, string contentType)
        {
            try
            {
                var mediaDirectory = Path.Combine(Directory.GetCurrentDirectory(), "Data", "Sharedmedia");
                Directory.CreateDirectory(mediaDirectory);

                var extension = _contentTypeToExtension.GetValueOrDefault(contentType ?? string.Empty, "mp3");

                var fileName = $"tts-stream-{streamId}.{extension}";
                var filePath = Path.Combine(mediaDirectory, fileName);

                await System.IO.File.WriteAllBytesAsync(filePath, audioBytes);
            }
            catch
            {
                // 持久化失败不影响当前请求
            }
        }

        /// <summary>
        /// 在 Sharedmedia 目录中查找已持久化的流式音频文件。
        /// </summary>
        private static (string FilePath, string ContentType)? FindPersistedStreamFile(string streamId)
        {
            var mediaDirectory = Path.Combine(Directory.GetCurrentDirectory(), "Data", "Sharedmedia");
            if (!Directory.Exists(mediaDirectory)) return null;

            // 惰性枚举：找到第一个匹配文件即停止遍历目录
            var filePath = Directory.EnumerateFiles(mediaDirectory, $"tts-stream-{streamId}.*").FirstOrDefault();
            if (filePath is null) return null;

            var extension = Path.GetExtension(filePath);
            var ct = _extensionToContentType.GetValueOrDefault(extension, "application/octet-stream");

            return (filePath, ct);
        }


        public class SaveHtmlRequest
        {
            public string HtmlContent { get; set; } = string.Empty;
        }

        [HttpGet]
        [Route("/api/chat/GetChatModels")]
        public IActionResult GetChatModels()
        {
            List<ChatModelConfig> chatModels = _chatService.GetModels();
            return Ok(chatModels);
        }

        /// <summary>
        /// 获取可用的技能列表
        /// </summary>
        [HttpGet]
        [Route("/api/chat/GetSkills")]
        public IActionResult GetSkills()
        {
            var skills = _chatService.GetSkills();
            return Ok(skills);
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
            var imageUrl = BuildAbsoluteUrl($"/uploads/{fileName}");
            return Ok(new { url = imageUrl });
        }


        [HttpPost]
        [Route("/api/chat/stream")]
        public async Task StreamChat([FromBody] ChatRequest request)
        {
            // 创建 streamId 用于断线重连
            var streamId = _streamCache.CreateStream();

            // 使用独立的取消令牌，防止客户端断开连接（如锁屏）导致后端停止生成
            // 设置 5 分钟超时作为安全限制，防止无限挂起
            using var generationCts = new CancellationTokenSource(TimeSpan.FromMinutes(40));
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

                

                // 关键点：传递 generationCts.Token 而不是 requestToken 给生成服务
                // 这样即使 requestToken 取消（客户端断开），生成也会继续
                IAsyncEnumerable<string> stream = _chatService.GenerateStreamAsync(request, generationCts.Token);

                int count = 0;
                await foreach (var chunk in stream)
                {
                    string str = chunk;
                   

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

            // 自动工程检索标签是网页与桌面桥之间的瞬时控制消息。
            // 即使旧网页或页面关闭竞态把它提交到保存接口，也不能写入会话数据库。
            request.Messages = request.Messages?
                .Where(message =>
                    !string.Equals(
                        message.Role,
                        "assistant",
                        StringComparison.OrdinalIgnoreCase) ||
                    !(message.Content?.Contains(
                        "<hcsoft_search>",
                        StringComparison.Ordinal) ?? false))
                .ToList() ?? new List<SaveMessageRequest>();

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
