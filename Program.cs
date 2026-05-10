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
        services.AddSingleton<IMqttService, MqttService>();
        services.AddHostedService<Worker>();
        services.AddHostedService<OperatorHubClient>();
        services.AddHttpClient();
    })
    .Build();

await host.RunAsync();
