using garge_operator;

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddUserSecrets<Program>();
builder.Services.AddHostedService<Worker>();
builder.Services.AddSingleton<MqttService>();
builder.Services.AddHttpClient();

var host = builder.Build();
host.Run();
