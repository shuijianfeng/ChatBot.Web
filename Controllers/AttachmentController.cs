using ChatBot.Models;
using ChatBot.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using System.Security.Cryptography;
using System.Text;

namespace ChatBot.Controllers;

public sealed class AttachmentExceptionFilter(ILogger<AttachmentExceptionFilter> logger) : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        logger.LogWarning(context.Exception, "Attachment request failed.");
        var status = context.Exception is UnauthorizedAccessException ? 403 : context.Exception is InvalidDataException or ArgumentException ? 400 : 503;
        context.Result = new ObjectResult(new { error = status == 503 ? "附件服务暂不可用，请检查数据库迁移及转换组件。" : context.Exception.Message }) { StatusCode = status };
        context.ExceptionHandled = true;
    }
}

public sealed record StartAttachmentJobRequest(List<HistoryMessage> History, string Model, string Question);

[ApiController]
[ServiceFilter(typeof(AttachmentExceptionFilter))]
public sealed class AttachmentController(AttachmentStore store, AttachmentExtractor extractor,
    AttachmentIdentity identity, IChatService chat, IConfiguration configuration) : ControllerBase
{
    private string Owner => identity.GetOwner(HttpContext) ?? throw new UnauthorizedAccessException("请重新打开聊天页面后上传附件。");

    [HttpPost("/api/chat/upload-file")]
    [RequestSizeLimit(21 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 21 * 1024 * 1024)]
    public async Task<IActionResult> UploadAsync([FromForm] IFormFile file, CancellationToken ct)
    {
        var owner = Owner;
        if (file is null || file.Length == 0 || file.Length > 20L * 1024 * 1024)
            throw new InvalidDataException("单个附件应大于 0 且不超过 20MB。");
        var name = Path.GetFileName(file.FileName.Replace('\\', '/'));
        if (name.Length > 255) throw new InvalidDataException("文件名过长。");
        var type = AttachmentExtractor.ValidateExtension(name);
        var metadata = new ChatAttachment(Guid.NewGuid().ToString("N"), name, type, file.Length);
        var directory = extractor.DirectoryFor(metadata.Id);
        Directory.CreateDirectory(directory);
        try
        {
            await using (var output = System.IO.File.Create(extractor.Source(metadata))) await file.CopyToAsync(output, ct);
            try { AttachmentExtractor.ValidateFile(extractor.Source(metadata), type); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { throw new InvalidDataException("文件内容无效、已加密或超出解析容量，请检查后重新上传。", ex); }
            await store.AddAsync(owner, metadata, ct);
            return Ok(metadata);
        }
        catch
        {
            // This directory is a freshly generated, validated ID, never a client path.
            Directory.Delete(directory, recursive: true);
            throw;
        }
    }

    [HttpGet("/api/chat/attachments/{id}/download")]
    public async Task<IActionResult> DownloadAsync(string id, CancellationToken ct)
    {
        var file = await store.GetAsync(Owner, id, ct);
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers.CacheControl = "private, no-store";
        return PhysicalFile(extractor.Source(file), file.ContentType, file.Name, enableRangeProcessing: true);
    }

    [HttpGet("/api/chat/attachments/{id}/open")]
    public async Task<IActionResult> OpenAsync(string id, CancellationToken ct)
    {
        var file = await store.GetAsync(Owner, id, ct);
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers.CacheControl = "private, no-store";
        // Never serve uploaded HTML/XML as an executable page on the application origin.
        if (file.ContentType.StartsWith("text/", StringComparison.Ordinal) || file.ContentType is "application/xml" or "application/json")
            return Content(AttachmentExtractor.ReadTextAttachment(extractor.Source(file), file.ContentType), "text/plain; charset=utf-8");
        if (file.ContentType == "application/pdf" || file.ContentType.StartsWith("image/", StringComparison.Ordinal))
            return PhysicalFile(extractor.Source(file), file.ContentType, enableRangeProcessing: true);
        return PhysicalFile(extractor.Source(file), file.ContentType, file.Name, enableRangeProcessing: true);
    }

    [HttpPost("/api/chat/attachment-jobs")]
    public async Task<IActionResult> StartAsync(StartAttachmentJobRequest request, CancellationToken ct)
    {
        var owner = Owner;
        var config = chat.GetModelConfig(request.Model);
        if (!AttachmentWorker.Supports(config)) throw new InvalidDataException("请选择支持视觉的普通对话模型。");
        if (request.Question is null || request.Question.Length > 32000) throw new InvalidDataException("问题过长。");
        var ids = new HashSet<string>();
        foreach (var message in request.History)
        {
            if (message.Attachments.Count > 0 && message.Role != "user") throw new InvalidDataException("只能在用户消息中附加文件。");
            foreach (var file in await store.ValidateAsync(owner, message.Attachments, ct)) ids.Add(file.Id);
        }
        if (ids.Count == 0) throw new InvalidDataException("请选择附件。");
        string? isolation = null;
        if (config.EnableUserIsolation)
        {
            var key = Convert.FromBase64String(configuration["ChatBotUserIsolationKey"] ?? "");
            try
            {
                if (key.Length < 32) throw new InvalidOperationException("用户隔离密钥未配置。");
                isolation = Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(owner)).AsSpan(0, 12))
                    .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
        var id = await store.CreateJobAsync(owner, new(ids.Order().ToArray(), request.Model, request.Question, isolation), ct);
        return Accepted(new { id });
    }

    [HttpGet("/api/chat/attachment-jobs/{id}")]
    public async Task<IActionResult> StatusAsync(string id, CancellationToken ct)
    {
        var job = await store.JobAsync(Owner, id, ct);
        return job is null ? NotFound() : Ok(new { job.Id, job.Status, job.Completed, job.Total, job.Stage, job.Error });
    }

    [HttpPost("/api/chat/attachment-jobs/{id}/cancel")]
    public async Task<IActionResult> CancelAsync(string id, CancellationToken ct) =>
        Ok(new { changed = await store.ChangeAsync(Owner, id, false, ct) });

    [HttpPost("/api/chat/attachment-jobs/{id}/retry")]
    public async Task<IActionResult> RetryAsync(string id, CancellationToken ct) =>
        Ok(new { changed = await store.ChangeAsync(Owner, id, true, ct) });
}
