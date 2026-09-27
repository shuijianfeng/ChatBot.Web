using ChatBot.Models;
using ChatBot.Web.Services;
using Hcsoft.Knowledge;
using Microsoft.AspNetCore.Mvc;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ChatBot.Controllers;

[ApiController]
[Route("internal/knowledge/explain")]
public sealed class KnowledgeController(IChatService chat,IConfiguration configuration,ILogger<KnowledgeController> log):ControllerBase
{
    private static readonly SemaphoreSlim Capacity=new(4);
    [HttpPost, RequestSizeLimit(65536)]
    public async Task<IActionResult> Explain(KnowledgeAiRequest request,CancellationToken ct)
    {
        Response.Headers.CacheControl="no-store";
        if(!configuration.GetValue<bool>("Knowledge:Enabled")) return NotFound();
        string auth=Request.Headers.Authorization.ToString();
        int user=KnowledgeToken.Validate(auth.StartsWith("Bearer ",StringComparison.Ordinal)?auth[7..]:null,configuration["Knowledge:SigningKey"] ?? "","knowledge-ai");
        if(user<=0) return Unauthorized();
        if(!KnowledgeEvidencePolicy.Valid(request)) return BadRequest();
        if(!await Capacity.WaitAsync(0,ct)) return StatusCode(429);
        try
        {
            var model=configuration["Knowledge:Model"];
            if(string.IsNullOrWhiteSpace(model)) return Ok(new KnowledgeAiResponse(false,"AI 解释尚未配置，仍可检索、比较和引用。",[],[]));
            string instructions=KnowledgeEvidencePolicy.Rules + (request.Mode=="query" ? " 将用户查询拆为用于造价清单检索的核心词项，只返回 JSON 字符串数组，最多八项；不得补造用户没有给出的地区、时期或定额体系。" : " 简明说明匹配理由、适用条件和待核实差异。引用候选时使用 [id]。不判断是否可以直接套用。");
            var message=JsonSerializer.Serialize(request,KnowledgeJson.Options);
            var input=new ChatRequest { Model=model, Message=message, History=[new HistoryMessage {Role="user",Content=message}], KnowledgeSystemPrompt=instructions,EnableSearch=false };
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue<int?>("Knowledge:AiTimeoutSeconds") ?? 120,10,600)));
            var text=new StringBuilder();
            string isolation=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("knowledge:"+user)))[..16];
            await foreach(var part in chat.GenerateStreamAsync(input,isolation,timeout.Token))
            {
                text.Append(part);
                if(text.Length>128000) throw new KnowledgeException("AI_OUTPUT_LIMIT","AI 回答超过容量限制，请缩短查询或减少候选。",502);
            }
            // ChatService emits <think> blocks for reasoning models. They may contain JSON
            // examples or [candidate ids]; only the final answer is evidence for this endpoint.
            string answer=Regex.Replace(text.ToString(),@"<think>.*?</think>","",RegexOptions.Singleline|RegexOptions.IgnoreCase,TimeSpan.FromSeconds(1)).Trim();
            if(answer.Contains("<think>",StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(answer) || answer.Length>12000) throw new JsonException();
            if(request.Mode=="query")
            {
                string raw=answer; int begin=raw.IndexOf('['),end=raw.LastIndexOf(']');
                var terms=begin>=0 && end>begin ? JsonSerializer.Deserialize<List<string>>(raw[begin..(end+1)]) : null;
                if(terms is null || terms.Count>8 || terms.Any(t=>string.IsNullOrWhiteSpace(t) || t.Length>60)) throw new JsonException();
                return Ok(new KnowledgeAiResponse(true,"可修改这些词项后再查找。",terms,[]));
            }
            return Ok(new KnowledgeAiResponse(true,answer,[],request.Candidates.Select(c=>c.Id).ToList()));
        }
        catch(Exception ex) when(ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var (code,reason)=ex switch
            {
                KnowledgeException known => (known.Code,known.Message),
                OperationCanceledException => ("AI_TIMEOUT","AI 响应超时，请稍后重试。"),
                HttpRequestException {StatusCode:System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden} => ("AI_PROVIDER_AUTH","AI 服务未接受模型凭据，请检查 ChatBot 服务的密钥配置。"),
                HttpRequestException {StatusCode:System.Net.HttpStatusCode.TooManyRequests} => ("AI_RATE_LIMIT","AI 服务繁忙或额度受限，请稍后重试。"),
                HttpRequestException {StatusCode:System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.NotFound} => ("AI_MODEL_REQUEST","AI 服务未接受模型或请求参数，请检查 ChatBot 模型配置。"),
                HttpRequestException => ("AI_CONNECTION","ChatBot 暂时无法连接模型服务。"),
                JsonException => ("AI_OUTPUT_FORMAT","AI 未返回有效的最终回答，请重试。"),
                ArgumentException => ("AI_MODEL_NOT_FOUND","Knowledge:Model 与 ChatModels 中的名称不匹配。"),
                _ => ("AI_UNAVAILABLE","AI 解释暂不可用。")
            };
            var diagnostic=Guid.NewGuid().ToString("N")[..12];
            log.LogWarning("Knowledge explanation unavailable: {Code}; diagnostic {DiagnosticId}; type {Type}; HTTP {Status}",code,diagnostic,ex.GetType().Name,(ex as HttpRequestException)?.StatusCode);
            return Ok(new KnowledgeAiResponse(false,reason+" 检索、比较和桌面引用仍可使用。",[],[]){ErrorCode=code,DiagnosticId=diagnostic});
        }
        finally {Capacity.Release();}
    }
}
