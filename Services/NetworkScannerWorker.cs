using System.Collections.Concurrent;
using System.Net.Http.Json;
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
    
    // Diccionario para mantener el último estado conocido de cada PC
    // Clave: ID del PC, Valor: true = ONLINE, false = OFFLINE
    private readonly ConcurrentDictionary<long, bool> _lastKnownStatus = new();

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
        var apiUrl = _config["BackendApiUrl"] ?? "https://api.andrescortes.dev/api/computers";
        
        // Intervalo de escaneo configurable (default: 30 segundos)
        var scanIntervalMs = _config.GetValue<int>("NetworkScanner:IntervalSeconds", 30) * 1000;

        _logger.LogInformation("📡 [RADAR] Iniciando escáner de red... (intervalo: {Interval}s)", scanIntervalMs / 1000);
        
        // Esperar un poco al inicio para que RabbitMQ y Java estén listos
        await Task.Delay(5000, stoppingToken);

        _logger.LogInformation("📡 [RADAR] Escáner de red DESACTIVADO por solicitud del usuario.");
        
        /* Comentado para desactivar el envío de estados a RabbitMQ
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

                int statusChanges = 0;

                // 3. ESCANEAR CADA PC (PING)
                foreach (var pc in pcsToScan)
                {
                    // Hacemos el Ping
                    bool isOnline = await PingHost(pc.Ip);
                    
                    // Verificar si el estado cambió respecto al último conocido
                    bool statusChanged = false;
                    if (_lastKnownStatus.TryGetValue(pc.Id, out bool previousStatus))
                    {
                        // Si teníamos un estado previo, verificar si cambió
                        statusChanged = previousStatus != isOnline;
                    }
                    else
                    {
                        // Primera vez que escaneamos este PC - siempre reportar
                        statusChanged = true;
                    }
                    
                    // Actualizar el estado conocido
                    _lastKnownStatus[pc.Id] = isOnline;
                    
                    // SOLO enviar mensaje si el estado CAMBIÓ
                    if (statusChanged)
                    {
                        statusChanges++;
                        
                        var report = new 
                        {
                            PcId = pc.Id,
                            PcName = pc.Name ?? pc.Id.ToString(),
                            IpAddress = pc.Ip,
                            MacAddress = pc.Mac,
                            Status = isOnline ? "ONLINE" : "OFFLINE",
                            Timestamp = DateTime.UtcNow
                        };

                        var json = JsonSerializer.Serialize(report);
                        var body = Encoding.UTF8.GetBytes(json);

                        /*
                        await channel.BasicPublishAsync(
                            exchange: "", 
                            routingKey: queueName, 
                            mandatory: false, 
                            body: body, 
                            cancellationToken: stoppingToken);
                        
                        // Log cuando hay cambio de estado
                        if (isOnline)
                            _logger.LogInformation($"🔄 {pc.Name ?? pc.Id.ToString()} ({pc.Ip}) cambió a ONLINE");
                        else
                            _logger.LogWarning($"🔄 {pc.Name ?? pc.Id.ToString()} ({pc.Ip}) cambió a OFFLINE");
                        */
                    }
                }
                
                // Log resumen del ciclo
                if (statusChanges > 0)
                {
                    // _logger.LogInformation($"📊 Ciclo completado: {statusChanges} cambios de estado detectados");
                }
                else
                {
                    // _logger.LogDebug("📊 Ciclo completado: sin cambios de estado");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"🔥 Error en el ciclo de radar: {ex.Message}");
            }

            // Esperar antes del siguiente barrido (configurable, default 30 seg)
            await Task.Delay(scanIntervalMs, stoppingToken);
        }
        */
        
        await Task.CompletedTask;
    }
    }

    // --- MÉTODOS AUXILIARES ---

    private async Task<List<MonitoredPc>> ObtenerListaDeJava(string url)
    {
        try 
        {
            var lista = await _httpClient.GetFromJsonAsync<List<MonitoredPc>>(url);
            return lista ?? new List<MonitoredPc>();
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error obteniendo lista de PCs desde Java: {ex.Message}");
            return new List<MonitoredPc>();
        }
    }

    private async Task<bool> PingHost(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return false;
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

// Modelo simple para mapear lo que responde Java
// Java devuelve: [{"id": 1, "name": "PC 1", "ipAddress": "...", "macAddress": "..."}]
public class MonitoredPc 
{ 
    [System.Text.Json.Serialization.JsonPropertyName("id")]
    public long Id { get; set; }
    
    [System.Text.Json.Serialization.JsonPropertyName("name")]
    public string? Name { get; set; }
    
    [System.Text.Json.Serialization.JsonPropertyName("ipAddress")]
    public string Ip { get; set; } = "";
    
    [System.Text.Json.Serialization.JsonPropertyName("macAddress")]
    public string? Mac { get; set; }
}