using garge_operator.Models;
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

        // Bind strongly-typed configuration. DataAnnotations replace the former hand-rolled
        // constructor validation; ValidateOnStart surfaces misconfiguration at startup so the
        // pod fails fast instead of throwing lazily on first use. The section names and keys are
        // unchanged (Mqtt:Broker, Mqtt:Port, Api:BaseUrl, ...) so existing appsettings and
        // environment variables continue to bind.
        services.AddOptions<MqttOptions>()
            .Bind(context.Configuration.GetSection(MqttOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<ApiOptions>()
            .Bind(context.Configuration.GetSection(ApiOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Token provider owns JWT fetch/caching and uses an unauthenticated login client.
        services.AddSingleton<ITokenProvider, JwtTokenProvider>();
        services.AddTransient<BearerTokenHandler>();

        // Authenticated garge-api client: BearerTokenHandler attaches the JWT automatically.
        services.AddHttpClient(GargeApiClient.Authorized)
            .AddHttpMessageHandler<BearerTokenHandler>();
        // Unauthenticated client used only for the login call (no bearer handler, no recursion).
        services.AddHttpClient(JwtTokenProvider.LoginClientName);

        services.AddSingleton<IMqttService, MqttService>();
        services.AddHostedService<Worker>();
        services.AddHostedService<OperatorHubClient>();
        services.AddHttpClient();
    })
    .Build();

await host.RunAsync();
