namespace OSManager.Api.Core;

public sealed class AuditMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, Database db)
    {
        try { await next(context); }
        catch (Exception ex)
        {
            if (context.Response.HasStarted) throw;
            context.Response.StatusCode = ex switch { UnauthorizedAccessException => 403, FileNotFoundException or KeyNotFoundException => 404, ArgumentException or InvalidOperationException => 400, _ => 500 };
            await db.AuditAsync(context.User.Identity?.IsAuthenticated == true ? context.CurrentUser() : null, "request.error", context.Request.Path, false, new { error = ex.Message }, context.Connection.RemoteIpAddress?.ToString());
            await context.Response.WriteAsJsonAsync(new { message = context.Response.StatusCode == 500 ? "服务器内部错误" : ex.Message });
        }
    }
}
