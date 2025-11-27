using EnvyGuard.Agent;
using EnvyGuard.Agent.Messaging;
using EnvyGuard.Agent.Services;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<RabbitMqConnection>();
builder.Services.AddSingleton<CommandExecutor>();
builder.Services.AddSingleton<CommandConsumer>();

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();