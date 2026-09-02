using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace OSManager.Api.Core;

public sealed class TokenAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, Database database)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return AuthenticateResult.NoResult();
        var user = await database.FindSessionAsync(header[7..].Trim());
        if (user is null) return AuthenticateResult.Fail("会话已过期");
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Name, user.UserName), new Claim("display_name", user.DisplayName), new Claim(ClaimTypes.Role, user.Role) };
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name));
    }
}

public static class HttpUserExtensions
{
    public static AuthUser CurrentUser(this HttpContext ctx) => new(long.Parse(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)!), ctx.User.Identity!.Name!, ctx.User.FindFirstValue("display_name")!, ctx.User.FindFirstValue(ClaimTypes.Role)!);
    public static IResult Forbidden(string message = "权限不足") => Results.Json(new { message }, statusCode: 403);
}
