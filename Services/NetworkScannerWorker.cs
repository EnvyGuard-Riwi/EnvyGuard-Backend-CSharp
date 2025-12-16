using System.Net.Http.Json; // Necesario para pedir la lista a Java
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using RabbitMQ.Client;

namespace EnvyGuard.Agent.Services;

public class NetworkScannerWorker : BackgroundService
{
    private readonly ILogger<NetworkScannerWorker> _logger;
    private readonly IConfiguration _config;
    private readonly ConnectionFactory _factory;
    private readonly HttpClient _httpClient;

    public NetworkScannerWorker(ILogger<NetworkScannerWorker> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;
        
        // Cliente HTTP para hablar con Java
        _httpClient = new HttpClient();

        // Configuración de RabbitMQ
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
        // Cola donde enviaremos los reportes de estado (ONLINE/OFFLINE)
        var queueName = _config["RabbitMQ:StatusQueueName"] ?? "pc_status_updates";
        
        // URL de tu Backend Java para obtener la lista de PCs
        // Puedes poner esto en appsettings, pero aquí dejo el default
        var apiUrl = _config["BackendApiUrl"] ?? "https://api.envyguard.crudzaso.com/api/computers";

        _logger.LogInformation("📡 [RADAR] Iniciando escáner de red...");
        
        // Esperar un poco al inicio para que RabbitMQ y Java estén listos
        await Task.Delay(5000, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // 1. OBTENER LISTA DE PCs DESDE JAVA (Dinámico)
                var pcsToScan = await ObtenerListaDeJava(apiUrl);

                if (pcsToScan.Count == 0)
                {
                    _logger.LogWarning("⚠️ La lista de PCs está vacía o no se pudo contactar a Java. Reintentando en 10s...");
                    await Task.Delay(10000, stoppingToken);
                    continue;
                }

                // 2. CONECTAR A RABBITMQ
                using var connection = await _factory.CreateConnectionAsync(stoppingToken);
                using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
                
                await channel.QueueDeclareAsync(
                    queue: queueName, 
                    durable: true, 
                    exclusive: false, 
                    autoDelete: false, 
                    arguments: null,
                    cancellationToken: stoppingToken);

                _logger.LogInformation($"📡 Escaneando {pcsToScan.Count} dispositivos...");

                // 3. ESCANEAR CADA PC (PING)
                foreach (var pc in pcsToScan)
                {
                    // Hacemos el Ping
                    bool isOnline = await PingHost(pc.Ip); // Asegúrate que tu DTO de Java tenga el campo "Ip" o "IpAddress"

                    // Preparamos el mensaje para RabbitMQ
                    var report = new 
                    {
                        PcId = pc.Id,         // ID único (ej: P5M9-0646)
                        IpAddress = pc.Ip,    // IP (ej: 10.0.120.10)
                        Status = isOnline ? "ONLINE" : "OFFLINE",
                        Timestamp = DateTime.UtcNow
                    };

                    var json = JsonSerializer.Serialize(report);
                    var body = Encoding.UTF8.GetBytes(json);

                    // Enviamos el reporte
                    await channel.BasicPublishAsync(
                        exchange: "", 
                        routingKey: queueName, 
                        mandatory: false, 
                        body: body, 
                        cancellationToken: stoppingToken);
                    
                    // Log visual (Opcional, comentar en producción si hace mucho ruido)
                    if (isOnline)
                        _logger.LogInformation($"✅ {pc.Id} ({pc.Ip}) está ONLINE");
                    else
                        _logger.LogWarning($"❌ {pc.Id} ({pc.Ip}) no responde.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"🔥 Error en el ciclo de radar: {ex.Message}");
            }

            // Esperar 10 segundos antes del siguiente barrido completo
            await Task.Delay(10000, stoppingToken);
        }
    }

    // --- MÉTODOS AUXILIARES ---

    private async Task<List<MonitoredPc>> ObtenerListaDeJava(string url)
    {
        try 
        {
            // Pide la lista al Backend Java
            var lista = await _httpClient.GetFromJsonAsync<List<MonitoredPc>>(url);
            return lista ?? new List<MonitoredPc>();
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error obteniendo lista de PCs desde Java: {ex.Message}");
            return new List<MonitoredPc>(); // Retorna lista vacía para no romper el programa
        }
    }

    private async Task<bool> PingHost(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return false;
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(ip, 1000); // Timeout 1 seg
            return reply.Status == IPStatus.Success;
        }
        catch
        {
            return false;
        }
    }
}

// Modelo simple para mapear lo que responde Java
// Java debe devolver un JSON array: [{"id": "...", "ip": "..."}]
public class MonitoredPc 
{ 
    public string Id { get; set; } = ""; // Nombre del PC o ID de BD
    public string Ip { get; set; } = ""; // Dirección IP
}