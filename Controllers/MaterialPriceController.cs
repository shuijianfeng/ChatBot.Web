using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChatBot.Models;
using ChatBot.Web.Services;
using Hcsoft.Knowledge;
using Microsoft.AspNetCore.Mvc;

namespace ChatBot.Controllers;

[ApiController]
[Route("internal/material-prices/analyze")]
public sealed class MaterialPriceController(IChatService chat, IConfiguration configuration, ILogger<MaterialPriceController> log) : ControllerBase
{
    private static readonly SemaphoreSlim Capacity = new(4);

    [HttpPost, RequestSizeLimit(65536)]
    public async Task<IActionResult> Analyze(MaterialPriceEvidence evidence, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        if (!(configuration.GetValue<bool?>("MaterialPrices:AiEnabled") ?? configuration.GetValue<bool>("Knowledge:Enabled"))) return NotFound();
        string auth = Request.Headers.Authorization.ToString();
        int user = KnowledgeToken.Validate(auth.StartsWith("Bearer ", StringComparison.Ordinal) ? auth[7..] : null,
            configuration["MaterialPrices:SigningKey"] ?? configuration["Knowledge:SigningKey"] ?? "", "material-price-ai");
        if (user <= 0) return Unauthorized();
        if (!MaterialPriceEvidencePolicy.Valid(evidence)) return BadRequest();
        if (!await Capacity.WaitAsync(0, ct)) return StatusCode(429);
        try
        {
            var model = configuration["MaterialPrices:Model"] ?? configuration["Knowledge:Model"];
            if (string.IsNullOrWhiteSpace(model)) return Ok(new MaterialPriceAiResponse(false, "材料价格 AI 分析尚未配置，统计图仍可使用。"));
            string message = JsonSerializer.Serialize(evidence, KnowledgeJson.Options);
            var input = new ChatRequest { Model = model, Message = message, History = [new HistoryMessage { Role = "user", Content = message }],
                KnowledgeSystemPrompt = MaterialPriceEvidencePolicy.Rules + " 当前 UTC 日期：" + DateTime.UtcNow.ToString("yyyy-MM-dd"), EnableSearch = false };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue<int?>("MaterialPrices:AiTimeoutSeconds") ?? 120, 10, 600)));
            var text = new StringBuilder();
            string isolation = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("material-prices:" + user)))[..16];
            await foreach (var part in chat.GenerateStreamAsync(input, isolation, timeout.Token))
            {
                text.Append(part);
                if (text.Length > 128000) throw new JsonException();
            }
            string answer = Regex.Replace(text.ToString(), @"<think>.*?</think>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)).Trim();
            if (answer.Contains("<think>", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(answer) || answer.Length > 12000) throw new JsonException();
            return Ok(new MaterialPriceAiResponse(true, answer));
        }
        catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log.LogWarning("Material price AI unavailable: {Type}", error.GetType().Name);
            return Ok(new MaterialPriceAiResponse(false, error is OperationCanceledException
                ? "AI 分析超时，请稍后重试。统计图仍可使用。" : "AI 分析暂不可用，请稍后重试。统计图仍可使用。"));
        }
        finally { Capacity.Release(); }
    }
}
