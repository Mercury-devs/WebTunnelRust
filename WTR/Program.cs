using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Primitives;

namespace RustWebTunnel;

public class Program
{
    private static String SharedSecret = "super-secret-token";

    private static readonly HttpClient HttpClient = new HttpClient();

    public static void Main(String[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        
        String? secretFromConfig = builder.Configuration["Tunnel:SharedSecret"];
        if (!String.IsNullOrWhiteSpace(secretFromConfig))
            SharedSecret = secretFromConfig;

        Console.WriteLine($"[Tunnel] Используется секретный ключ: {SharedSecret}");

        WebApplication app = builder.Build();

        app.MapGet("/", () => "Туннель запущен");

        app.MapPost("/tunnel/forward", async (HttpRequest request) =>
        {
            if (!request.Headers.TryGetValue("X-Tunnel-Secret", out StringValues secret) ||
                secret != SharedSecret)
            {
                Console.WriteLine("[Tunnel] Invalid secret from " +
                                  request.HttpContext.Connection.RemoteIpAddress);
                return Results.StatusCode((Int32)HttpStatusCode.Forbidden);
            }

            using StreamReader reader = new StreamReader(request.Body, Encoding.UTF8);
            String json = await reader.ReadToEndAsync();

            Console.WriteLine("[Tunnel] Получен JSON:");
            Console.WriteLine(json);

            TunnelRequest? tunnelReq;
            try { tunnelReq = JsonSerializer.Deserialize<TunnelRequest>(json); }
            catch (Exception ex)
            {
                Console.WriteLine("[Tunnel] Ошибка чтения JSON: " + ex);
                return Results.BadRequest("Invalid JSON");
            }

            if (tunnelReq == null || String.IsNullOrWhiteSpace(tunnelReq.target))
            {
                Console.WriteLine("[Tunnel] Не найден URL");
                return Results.BadRequest("No target URL");
            }

            Console.WriteLine($"[Tunnel] Получен запрос из плагина: {tunnelReq.plugin}");
            Console.WriteLine($"[Tunnel] Перенаправление: {tunnelReq.method} {tunnelReq.target}");

            HttpMethod httpMethod = new HttpMethod(tunnelReq.method?.ToUpperInvariant() ?? "POST");
            HttpRequestMessage msg = new HttpRequestMessage(httpMethod, tunnelReq.target);

            if (!String.IsNullOrEmpty(tunnelReq.body) &&
                httpMethod != HttpMethod.Get &&
                httpMethod != HttpMethod.Head)
                msg.Content = new StringContent(tunnelReq.body, Encoding.UTF8, "application/json");

            msg.Headers.Add("X-From-Tunnel-Plugin", tunnelReq.plugin ?? "unknown");

            HttpResponseMessage targetResp;
            try
            {
                targetResp = await HttpClient.SendAsync(msg);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Tunnel] Критическая ошибка: " + ex);
                return Results.StatusCode(502);
            }

            String respBody = await targetResp.Content.ReadAsStringAsync();

            Console.WriteLine($"[Tunnel] Сервер-назначение ответил: {(Int32)targetResp.StatusCode} ({targetResp.StatusCode})");
            Console.WriteLine("[Tunnel] Тело ответа (первые 200 символов):");
            Console.WriteLine(respBody.Length > 200 ? respBody[..200] + "..." : respBody);

            Console.WriteLine("========================================");

            if (targetResp.StatusCode == HttpStatusCode.NoContent)
                return Results.StatusCode((Int32)targetResp.StatusCode);

            return Results.Content(
                String.IsNullOrEmpty(respBody) ? String.Empty : respBody,
                "application/json",
                Encoding.UTF8,
                (Int32)targetResp.StatusCode
            );
        });

        app.Run("http://0.0.0.0:5000");
    }

    public record TunnelRequest(String target, String method, String plugin, String body);
}
