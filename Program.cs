using garge_operator.Services;
using Serilog;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureAppConfiguration((context, config) =>
    {
        config.Sources.Clear();
        config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: false);
        config.AddJsonFile($"appsettings.{context.HostingEnvironment.EnvironmentName}.json", optional: true, reloadOnChange: false);
        config.AddEnvironmentVariables();
        config.AddUserSecrets<Program>(optional: true);
    })
    .UseSerilog((context, services, configuration) =>
    {
        configuration
            .WriteTo.Console()
            .Enrich.FromLogContext()
            .ReadFrom.Configuration(context.Configuration);
    })
    .ConfigureServices((context, services) =>
    {
        // Make the BackgroundService crash contract explicit: if a hosted service throws an
        // unhandled exception (for example MqttService.ConnectAsync when no JWT can be obtained),
        // stop the host. In Kubernetes the pod then exits non-zero and is rescheduled, which is the
        // desired fail-fast behavior rather than silently running with a dead MQTT connection.
        services.Configure<HostOptions>(options =>
        {
            options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost;
        });

        services.AddSingleton<IMqttService, MqttService>();
        services.AddHostedService<Worker>();
        services.AddHostedService<OperatorHubClient>();
        services.AddHttpClient();
    })
    .Build();

await host.RunAsync();
