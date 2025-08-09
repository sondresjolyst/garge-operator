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
        services.AddSingleton<MqttService>();
        services.AddHostedService<Worker>();
        services.AddHttpClient();
    })
    .ConfigureWebHostDefaults(webBuilder =>
    {
        webBuilder.Configure(app =>
        {
            var mqttService = app.ApplicationServices.GetRequiredService<MqttService>();

            app.UseRouting();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapPost("/webhook", async context =>
                {
                    var mqttService = context.RequestServices.GetRequiredService<MqttService>();

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
