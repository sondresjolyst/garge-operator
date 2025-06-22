using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using garge_operator.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

var builder = Host.CreateDefaultBuilder(args)
    .ConfigureAppConfiguration((context, config) =>
    {
        config.AddUserSecrets<Program>();
    })
    .ConfigureServices((context, services) =>
    {
        services.AddSingleton<MqttService>();
        services.AddHostedService<Worker>();
        services.AddHttpClient();
    })
    .ConfigureWebHostDefaults(webBuilder =>
    {
        webBuilder.Configure(app =>
        {
            var mqttService = app.ApplicationServices.GetRequiredService<MqttService>();

            // Add routing middleware
            app.UseRouting();

            // Map endpoints
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapPost("/webhook", async (WebhookPayload payload) =>
                {
                    if (payload == null)
                    {
                        return Results.BadRequest("Payload is null.");
                    }

                    // Process the webhook data using MqttService
                    await mqttService.HandleWebhookDataAsync(payload);
                    return Results.Ok("Webhook data processed.");
                });
            });
        });
    });

var host = builder.Build();
await host.RunAsync();
