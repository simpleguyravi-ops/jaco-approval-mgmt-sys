using System.Net;
using System.Threading.RateLimiting;
using JACO.Unified.Infrastructure;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

// ContentRootPath must be pinned explicitly -- Windows' Service Control Manager launches
// services with C:\WINDOWS\system32 as the working directory (see jaco-dev-environment memory).
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

builder.Host.UseWindowsService();
if (Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService())
{
    builder.Logging.AddEventLog(settings => settings.SourceName = "JACO Unified");
}

builder.Services.AddControllersWithViews();

// Reachable through a reverse proxy in real deployments (mbjaco.com's front door is nginx,
// terminating TLS and forwarding plain HTTP to this app) -- without this, the app never
// finds out the original request was HTTPS, so UseHttpsRedirection loops and the auth
// cookie's SameAsRequest policy wrongly treats every request as insecure. Harmless locally:
// with no proxy in front, these headers simply never arrive.
// ReverseProxy:TrustedProxyIp -- the ONE address the forwarded headers are accepted from, so
// a request can't just claim "X-Forwarded-Proto: https" to itself. Leave unset locally.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    var trustedProxyIp = builder.Configuration["ReverseProxy:TrustedProxyIp"];
    if (!string.IsNullOrWhiteSpace(trustedProxyIp))
    {
        options.KnownProxies.Clear();
        options.KnownProxies.Add(IPAddress.Parse(trustedProxyIp));
    }
});
builder.Services.AddDbContext<UnifiedDbContext>(o =>
    o.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection("Email"));
builder.Services.AddScoped<RoutingService>();
builder.Services.AddSingleton<EmailPasswordProtector>();
builder.Services.AddSingleton<ApprovalActionLinkService>();
builder.Services.AddScoped<MailSender>();
builder.Services.AddScoped<TimelineService>();
builder.Services.AddScoped<PpfExecutor>();
builder.Services.AddScoped<RequestService>();
builder.Services.AddScoped<ReportsService>();
builder.Services.AddSingleton(sp =>
{
    var root = builder.Configuration["Attachments:RootPath"] ?? @"C:\JACO\_shared\unified-attachments";
    Directory.CreateDirectory(root);
    return new RequestAttachmentStorage(root);
});

// Shared SSO: trusts the login cookie issued by JACO Portal -- same key ring + same
// cookie name as every other JACO app, so a Portal login carries straight through here.
var keyRingPath = builder.Configuration["SharedAuth:KeyRingPath"] ?? @"C:\JACO\_shared\dpkeys";
Directory.CreateDirectory(keyRingPath);
var dataProtectionBuilder = builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keyRingPath))
    .SetApplicationName("JACO-Platform");

var keyRingCertThumbprint = builder.Configuration["SharedAuth:KeyRingCertThumbprint"];
if (!string.IsNullOrEmpty(keyRingCertThumbprint))
    dataProtectionBuilder.ProtectKeysWithCertificate(keyRingCertThumbprint);

// Unauthenticated requests now land on Unified's OWN login page -- standalone, no detour
// through Portal required. A visitor who already carries a valid shared cookie from a
// Portal (or any other JACO app) session is still recognized automatically; this path only
// fires when there's no valid cookie at all.
var cookieName = builder.Configuration["SharedAuth:CookieName"] ?? ".JACO.Auth";
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = cookieName;
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.LoginPath = "/Account/Login";
        options.AccessDeniedPath = "/Account/AccessDenied";
    });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("UnifiedAdmin", p => p.RequireRole("UNIFIED_ADMIN", "PORTAL_ADMIN", "SYSTEM_ADMIN"));
    // Reports live outside Administration on purpose -- an auditor can be granted exactly
    // this and nothing else. Any admin role also satisfies it, since an admin can already
    // see everything Reports shows.
    options.AddPolicy("UnifiedReports", p => p.RequireRole("UNIFIED_ADMIN", "PORTAL_ADMIN", "SYSTEM_ADMIN", "UNIFIED_AUDITOR"));
});

// Throttles the actions that change state or move a file -- partitioned per signed-in user
// (not per IP) since everyone shares the same reverse-proxy-less localhost origin today.
// Nudge already has its own 15-minute cooldown; this is a broader backstop for Decide and
// attachment upload/download.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("sensitive", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.User.Identity?.Name ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions { Window = TimeSpan.FromMinutes(1), PermitLimit = 30 }));
    // Tighter than "sensitive" and partitioned by IP specifically -- Login is anonymous
    // (no User.Identity.Name to key on), and the per-account lockout above only throttles
    // repeated guesses against ONE username; this is the backstop against spraying many
    // different usernames from the same source instead.
    options.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { Window = TimeSpan.FromMinutes(1), PermitLimit = 10 }));
    // One-click email Approve/Reject links are anonymous by design (that's the whole point --
    // no login needed) and the token is unguessable, but this is still a backstop against
    // scripted hammering of the endpoint, same reasoning as "login".
    options.AddPolicy("emailAction", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { Window = TimeSpan.FromMinutes(1), PermitLimit = 20 }));
    // Partitioned per API key (not IP) so one external system can't be starved by another,
    // and a misbehaving/looping caller can't exceed this regardless of key validity --
    // falls back to IP only for the (already-401ing) case of no key being presented at all.
    options.AddPolicy("api", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Request.Headers["X-Api-Key"].FirstOrDefault() ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { Window = TimeSpan.FromMinutes(1), PermitLimit = 60 }));
});

var app = builder.Build();

// Must run before anything that inspects Request.Scheme/Host (HTTPS redirection, HSTS, the
// auth cookie's Secure policy) -- otherwise those see the plain-HTTP hop from the proxy
// instead of the client's original HTTPS request.
app.UseForwardedHeaders();

// Hosted at a path (mbjaco.com/JAMS), not a domain root -- derived from AppBaseUrl so
// there's only one place that ever needs to know the path, instead of a second config key
// that could drift out of sync with it. Without this, every generated link (CSS/JS, form
// actions, redirects) comes out missing "/JAMS" and 404s once actually behind the proxy,
// even though it looks fine hitting the app directly. Requires nginx to forward the FULL
// path unmodified (no "proxy_pass .../;" prefix-stripping) -- see the deployment runbook.
var appBasePath = new Uri(builder.Configuration["AppBaseUrl"] ?? "http://localhost:5004").AbsolutePath.TrimEnd('/');
if (!string.IsNullOrEmpty(appBasePath))
    app.UsePathBase(appBasePath);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}
app.UseHttpsRedirection();

app.Use((ctx, next) =>
{
    ctx.Response.Headers.Append("X-Content-Type-Options", "nosniff");
    ctx.Response.Headers.Append("X-Frame-Options", "DENY");
    ctx.Response.Headers.Append("Referrer-Policy", "same-origin");
    ctx.Response.Headers.Append("Content-Security-Policy",
        "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; font-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'");
    return next();
});

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// Before MVC dispatch so it can short-circuit an /api/** call (disabled/unauthenticated)
// without ever reaching a controller, and after UseRouting/UseAuthorization so a normal
// browser request is completely unaffected by it (the check is a cheap path-prefix test).
app.UseMiddleware<JACO.Unified.Web.Middleware.ApiGatewayMiddleware>();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Requests}/{action=Index}/{id?}");

app.Run();
