using Microsoft.AspNetCore.DataProtection;

namespace ChatBot.Web.Services;

/// <summary>Only the validated chat landing page may issue an attachment identity.</summary>
public sealed class AttachmentIdentity(IDataProtectionProvider provider)
{
    private readonly IDataProtector protector = provider.CreateProtector("ChatBot.Attachments.User.v1");
    private const string CookieName = "ChatBot.AttachmentUser";

    public void SignIn(HttpContext context, string uid)
    {
        context.Response.Cookies.Append(CookieName, protector.Protect(uid), new CookieOptions
        {
            HttpOnly = true, Secure = context.Request.IsHttps, SameSite = SameSiteMode.Strict,
            Path = context.Request.PathBase.HasValue ? context.Request.PathBase.Value : "/",
            IsEssential = true
        });
    }

    public string? GetOwner(HttpContext context)
    {
        if (!context.Request.Cookies.TryGetValue(CookieName, out var value)) return null;
        try { return protector.Unprotect(value); }
        catch (System.Security.Cryptography.CryptographicException) { return null; }
    }
}
