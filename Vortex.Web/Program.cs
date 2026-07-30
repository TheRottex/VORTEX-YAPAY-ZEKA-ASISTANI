using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);
var serverBaseUrl = builder.Configuration["Vortex:ServerBaseUrl"];
if (!Uri.TryCreate(serverBaseUrl, UriKind.Absolute, out var serverBaseUri) ||
    (serverBaseUri.Scheme != Uri.UriSchemeHttp && serverBaseUri.Scheme != Uri.UriSchemeHttps))
{
    throw new InvalidOperationException("Vortex:ServerBaseUrl must be an absolute HTTP(S) URL.");
}

builder.Services.AddRazorPages();
builder.Services.AddHttpClient("vortex-server", client =>
{
    client.BaseAddress = serverBaseUri;
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddHttpClient("vortex-server-no-redirect", client =>
{
    client.BaseAddress = serverBaseUri;
    client.Timeout = TimeSpan.FromSeconds(30);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

var app = builder.Build();
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    ForwardLimit = 1,
    KnownProxies = { IPAddress.Loopback, IPAddress.IPv6Loopback }
});

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseStaticFiles();
app.UseRouting();
app.UseAuthorization();
app.MapRazorPages();
app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "Vortex.Web" }));
app.Run();
