using ChatBot.Models;
using ChatBot.Web.Services;
using Hcsoft.Knowledge;
using Microsoft.AspNetCore.Mvc;

namespace ChatBot.Controllers;

[ApiController]
[Route("api/embeddings")]
public sealed class EmbeddingController(EmbeddingService service, IConfiguration configuration,
    ILogger<EmbeddingController> log) : ControllerBase
{
    private IActionResult? CheckAccess()
    {
        Response.Headers.CacheControl = "no-store";
        if (!service.Enabled) return NotFound();
        string authorization = Request.Headers.Authorization.ToString();
        string? token = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? authorization[7..] : null;
        // 复用现有云登录签发的 user 令牌，不信任客户端传入的用户 ID。
        return KnowledgeToken.Validate(token, configuration["Knowledge:SigningKey"] ?? "") > 0 ? null : Unauthorized();
    }

    [HttpGet("config")]
    public IActionResult GetConfiguration()
    {
        if (CheckAccess() is { } error) return error;
        try { return Ok(service.GetConfiguration()); }
        catch (EmbeddingServiceException ex) { return Failure(ex); }
    }

    [HttpPost, RequestSizeLimit(1048576)]
    public async Task<IActionResult> Create(EmbeddingRequest request, CancellationToken ct)
    {
        if (CheckAccess() is { } error) return error;
        try { return Ok(await service.GenerateAsync(request, ct)); }
        catch (EmbeddingServiceException ex) { return Failure(ex); }
    }

    private ObjectResult Failure(EmbeddingServiceException ex)
    {
        log.LogWarning("Embedding request failed: {Code}; HTTP {Status}; provider HTTP {ProviderStatus}",
            ex.Code, ex.StatusCode, ex.ProviderStatusCode);
        return StatusCode(ex.StatusCode, new { error = new { code = ex.Code, message = ex.Message } });
    }
}
