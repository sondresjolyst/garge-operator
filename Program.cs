using Microsoft.AspNetCore.Builder;
using garge_operator.Services;
using Microsoft.AspNetCore.Hosting;
using Serilog;
using Microsoft.AspNetCore.Http;
using System.Text.Json;

var builder = Host.CreateDefaultBuilder(args)
    .ConfigureAppConfiguration((context, config) =>
    {
        config.AddUserSecrets<Program>();
    });

var host = builder
    .UseSerilog((context, services, configuration) =>
    {
        configuration
            .WriteTo.Console()
            .Enrich.FromLogContext()
            .ReadFrom.Configuration(context.Configuration);
    })
    .ConfigureServices((context, services) =>
    {
        services.AddSingleton<IMqttService, MqttService>();
        services.AddHostedService<Worker>();
        services.AddHttpClient();
    })
    .ConfigureWebHostDefaults(webBuilder =>
    {
        webBuilder.Configure(app =>
        {
            app.UseRouting();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapPost("/webhook", async context =>
                {
                    if (context.Request.ContentLength > 65536)
                    {
                        context.Response.StatusCode = StatusCodes.Status413RequestEntityTooLarge;
                        await context.Response.WriteAsync("Payload too large.");
                        return;
                    }

                    var config = context.RequestServices.GetRequiredService<IConfiguration>();
                    var webhookSecret = config["Webhook:Secret"];
                    if (!string.IsNullOrEmpty(webhookSecret))
                    {
                        var providedSecret = context.Request.Headers["X-Webhook-Secret"].FirstOrDefault();
                        if (providedSecret != webhookSecret)
                        {
                            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                            await context.Response.WriteAsync("Unauthorized.");
                            return;
                        }
                    }

                    var mqttService = context.RequestServices.GetRequiredService<IMqttService>();
                    var payload = await JsonSerializer.DeserializeAsync<WebhookPayload>(context.Request.Body);

                    if (payload == null)
                    {
                        context.Response.StatusCode = StatusCodes.Status400BadRequest;
                        await context.Response.WriteAsync("Payload is null.");
                        return;
                    }

                    await mqttService.HandleWebhookDataAsync(payload);

                    context.Response.StatusCode = StatusCodes.Status200OK;
                    await context.Response.WriteAsync("Webhook data processed.");
                });
            });

        });
    })
    .Build();

await host.RunAsync();
