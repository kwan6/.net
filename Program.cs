using Microsoft.Extensions.Options;

// Plug and play: kalau dijalankan dari hasil publish (tidak ada file .csproj di folder kerja),
// semua path relatif (model, alerts, wwwroot, appsettings) dihitung dari folder program ini,
// jadi aman diklik dari mana saja.
if (!Directory.GetFiles(Directory.GetCurrentDirectory(), "*.csproj").Any())
    Directory.SetCurrentDirectory(AppContext.BaseDirectory);

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<NotifyOptions>(builder.Configuration.GetSection("Notify"));
builder.Services.AddSingleton<AlertNotifier>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AlertNotifier>());
builder.Services.Configure<VisionOptions>(builder.Configuration.GetSection("Vision"));
builder.Services.AddSingleton<VisionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<VisionService>());

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();   // melayani wwwroot/index.html

var api = app.MapGroup("/api");

api.MapGet("/status", (VisionService v) => v.GetState());

api.MapGet("/alerts", (VisionService v) =>
    Directory.Exists(v.AlertDir)
        ? Directory.GetFiles(v.AlertDir, "*.jpg")
              .Select(f => Path.GetFileName(f)).OrderByDescending(x => x).ToArray()
        : Array.Empty<string>());

api.MapGet("/alerts/{nama}", (string nama, VisionService v) =>
{
    var path = Path.GetFullPath(Path.Combine(v.AlertDir, Path.GetFileName(nama)));  // cegah akses folder lain
    return File.Exists(path) ? Results.File(path, "image/jpeg") : Results.NotFound();
});

api.MapGet("/video", async (HttpContext ctx, VisionService v, CancellationToken ct) =>
{
    ctx.Response.ContentType = "multipart/x-mixed-replace; boundary=frame";
    ctx.Response.Headers.CacheControl = "no-store";
    byte[]? lastSent = null;
    try
    {
        while (!ct.IsCancellationRequested)
        {
            var jpg = v.GetJpeg();
            if (jpg != null && !ReferenceEquals(jpg, lastSent))
            {
                await ctx.Response.WriteAsync(
                    $"--frame\r\nContent-Type: image/jpeg\r\nContent-Length: {jpg.Length}\r\n\r\n", ct);
                await ctx.Response.Body.WriteAsync(jpg, ct);
                await ctx.Response.WriteAsync("\r\n", ct);
                await ctx.Response.Body.FlushAsync(ct);
                lastSent = jpg;
            }
            await Task.Delay(20, ct);
        }
    }
    catch (OperationCanceledException) { /* penonton menutup halaman, normal */ }
});

api.MapGet("/test-wa", async (AlertNotifier n, CancellationToken ct) =>
{
    var (ok, info) = await n.SendTestAsync(ct);
    return Results.Json(new { ok, info }, statusCode: ok ? 200 : 500);
});

app.Run();