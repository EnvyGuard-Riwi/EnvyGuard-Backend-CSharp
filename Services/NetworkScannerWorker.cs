using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using EnvyGuard.Agent.Models;
using RabbitMQ.Client;

namespace EnvyGuard.Agent.Services;

public class NetworkScannerWorker : BackgroundService
{
    private readonly ILogger<NetworkScannerWorker> _logger;
    private readonly IConfiguration _config;
    private readonly ConnectionFactory _factory;

    public NetworkScannerWorker(ILogger<NetworkScannerWorker> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;
        
        // AQUÍ ESTABA EL ERROR: Faltaba leer usuario y contraseña
        _factory = new ConnectionFactory
        {
            HostName = _config["RabbitMQ:HostName"] ?? "localhost",
            UserName = _config["RabbitMQ:UserName"] ?? "guest",
            Password = _config["RabbitMQ:Password"] ?? "guest",
            VirtualHost = _config["RabbitMQ:VirtualHost"] ?? "/"
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pcsToScan = _config.GetSection("MonitoredPcs").Get<List<MonitoredPc>>() ?? new List<MonitoredPc>();
        var queueName = _config["RabbitMQ:StatusQueueName"] ?? "pc_status_updates";

        _logger.LogInformation("📡 [DIEGO] Iniciando Radar. PCs a vigilar: {Count}", pcsToScan.Count);

        await Task.Delay(3000, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var connection = await _factory.CreateConnectionAsync(stoppingToken);
                
                // CORRECCIÓN AQUÍ: Agregamos "cancellationToken:"
                using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
                
                await channel.QueueDeclareAsync(
                    queue: queueName, 
                    durable: true, 
                    exclusive: false, 
                    autoDelete: false, 
                    arguments: null, // RabbitMQ 7 requiere argumentos explícitos a veces
                    cancellationToken: stoppingToken);

                foreach (var pc in pcsToScan)
                {
                    bool isOnline = await PingHost(pc.Ip);

                    var report = new 
                    {
                        PcId = pc.Id,
                        IpAddress = pc.Ip,
                        Status = isOnline ? "ONLINE" : "OFFLINE",
                        Timestamp = DateTime.UtcNow
                    };

                    var json = JsonSerializer.Serialize(report);
                    var body = Encoding.UTF8.GetBytes(json);

                    await channel.BasicPublishAsync(
                        exchange: "", 
                        routingKey: queueName, 
                        mandatory: false, 
                        body: body, 
                        cancellationToken: stoppingToken);
                    
                    if(isOnline) 
                        _logger.LogInformation("✅ {Id} ({Ip}) responde al Ping.", pc.Id, pc.Ip);
                    else
                        _logger.LogWarning("❌ {Id} ({Ip}) NO responde.", pc.Id, pc.Ip);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Error en el radar de Diego: {Message}", ex.Message);
            }

            await Task.Delay(10000, stoppingToken);
        }
    }

    private async Task<bool> PingHost(string ip)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(ip, 1000);
            return reply.Status == IPStatus.Success;
        }
        catch
        {
            return false;
        }
    }
}

public class MonitoredPc { public string Id { get; set; } = ""; public string Ip { get; set; } = ""; }