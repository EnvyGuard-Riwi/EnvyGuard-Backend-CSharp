using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace EnvyGuard.Agent.Services;

public class ScreenSpyWorker : BackgroundService
{
    private readonly IConfiguration _config;
    private readonly ILogger<ScreenSpyWorker> _logger;
    private bool _isSpying = false; // Empieza dormido
    private string _pcId = Environment.MachineName;

    public ScreenSpyWorker(IConfiguration config, ILogger<ScreenSpyWorker> logger)
    {
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Leer configuración del appsettings o variables de entorno
        var factory = new ConnectionFactory
        {
            HostName = _config["RabbitMQ:HostName"] ?? "localhost",
            UserName = _config["RabbitMQ:UserName"] ?? "guest",
            Password = _config["RabbitMQ:Password"] ?? "guest",
            VirtualHost = _config["RabbitMQ:VirtualHost"] ?? "/"
        };

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation($"🕵️ [SPY] Iniciando módulo de vigilancia en {_pcId}...");
                
                using var connection = await factory.CreateConnectionAsync(stoppingToken);
                using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);

                // 1. Configurar Canal de Control (Escuchar órdenes START/STOP)
                // CORRECCIÓN: El servidor tiene este exchange como Durable=true. Debemos coincidir.
                await channel.ExchangeDeclareAsync(exchange: "spy.control", type: ExchangeType.Fanout, durable: true, autoDelete: false, arguments: null, cancellationToken: stoppingToken);
                var queueName = (await channel.QueueDeclareAsync(cancellationToken: stoppingToken)).QueueName;
                await channel.QueueBindAsync(queueName, "spy.control", "", cancellationToken: stoppingToken);

                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.ReceivedAsync += async (model, ea) =>
                {
                    var body = ea.Body.ToArray();
                    var message = Encoding.UTF8.GetString(body).Trim().Trim('"').ToUpper();
                    _logger.LogInformation($"📣 [SPY] Orden recibida: {message}");

                    if (message.Contains("START")) { _isSpying = true; _logger.LogInformation("🟢 [SPY] Modo ACTIVO - Iniciando capturas"); }
                    if (message.Contains("STOP")) { _isSpying = false; _logger.LogInformation("🔴 [SPY] Modo INACTIVO - Detenido"); }
                };
                await channel.BasicConsumeAsync(queueName, autoAck: true, consumer: consumer, cancellationToken: stoppingToken);

                // 2. Bucle de Vigilancia
                string lastHash = "";
                
                while (connection.IsOpen && !stoppingToken.IsCancellationRequested)
                {
                    if (_isSpying)
                    {
                        byte[]? imageBytes = await CapturarYOptimizarLinux();
                        
                        if (imageBytes != null && imageBytes.Length > 0)
                        {
                            string currentHash = CalcularHash(imageBytes);
                            if (currentHash != lastHash)
                            {
                                string base64Image = Convert.ToBase64String(imageBytes);
                                
                                var payload = new { 
                                    PcId = _pcId, 
                                    ImageBase64 = base64Image, 
                                    Timestamp = DateTime.UtcNow 
                                };
                                
                                var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));

                                // FIX: Java Backend requiere Priority o crashea (NPE)
                                var props = new BasicProperties
                                {
                                    Priority = 0,
                                    DeliveryMode = DeliveryModes.Transient // Fotos pueden perderse, no pasa nada
                                };

                                // Enviar al Topic (Para que Java/React lo vean)
                                await channel.BasicPublishAsync(
                                    exchange: "amq.topic", 
                                    routingKey: "spy.screens", 
                                    mandatory: false,
                                    basicProperties: props,
                                    body: body, 
                                    cancellationToken: stoppingToken);
                                
                                _logger.LogInformation($"📸 [SPY] Foto enviada: {imageBytes.Length / 1024} KB");
                                lastHash = currentHash;
                            }
                        }
                        await Task.Delay(4000, stoppingToken); // Frecuencia: 4 segundos
                    }
                    else
                    {
                        await Task.Delay(2000, stoppingToken); // Dormido
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"⚠️ [SPY] Error: {ex.Message}. Reintentando en 5s...");
                await Task.Delay(5000, stoppingToken);
            }
        }
    }

    // --- MÉTODOS AUXILIARES (Copiados de tu versión exitosa) ---

    private async Task<byte[]?> CapturarYOptimizarLinux()
    {
        string tempFile = $"/tmp/spy_{Guid.NewGuid()}.jpg";
        try
        {
            _logger.LogInformation($"📷 [SPY] Capturando pantalla a {tempFile}...");
            
            // Crear script de captura robusto
            string scriptPath = "/tmp/capture_helper.sh";
            if (!File.Exists(scriptPath))
            {
                string scriptContent = @"#!/bin/bash
OUTPUT=$1

# 1. Detectar usuario y display activo
LINE=$(w -h | grep -E ' :[0-9]' | head -n 1)
if [ -n ""$LINE"" ]; then
    USER=$(echo ""$LINE"" | awk '{print $1}')
    DISPLAY=$(echo ""$LINE"" | awk '{print $3}')
else
    USER=$(whoami)
    DISPLAY=:0
fi

export DISPLAY=$DISPLAY

# 2. Buscar XAUTHORITY
# Intento A: Home del usuario
XAUTH=/home/$USER/.Xauthority
# Intento B: Runtime dir
if [ ! -f ""$XAUTH"" ]; then
    XAUTH=$(find /run/user -name 'Xauthority' 2>/dev/null | grep $(id -u $USER) | head -n 1)
fi

if [ -f ""$XAUTH"" ]; then
    export XAUTHORITY=$XAUTH
fi

echo ""INFO:SESSION_TYPE=$XDG_SESSION_TYPE""
echo ""INFO:DESKTOP=$XDG_CURRENT_DESKTOP""

# 3. Intentar capturar
# Opción A: import (ImageMagick - Prioridad 1)
# Quitamos 2>/dev/null para ver errores
import -display $DISPLAY -window root ""$OUTPUT"" && echo ""METHOD:import"" && exit 0

# Opción B: xwd (X Window Dump - Muy robusto y silencioso - Prioridad 2)
# Requiere: sudo apt-get install x11-apps imagemagick
xwd -display $DISPLAY -root -silent -out ""$OUTPUT.xwd"" 2>/dev/null && convert ""$OUTPUT.xwd"" ""$OUTPUT"" 2>/dev/null && rm ""$OUTPUT.xwd"" && echo ""METHOD:xwd"" && exit 0

# Opción C: scrot (Silencioso - Prioridad 3)
scrot -z -o -q 50 ""$OUTPUT"" 2>/dev/null && echo ""METHOD:scrot"" && exit 0

# Opción D: gnome-screenshot (Flash visible - Último recurso)
gnome-screenshot -f ""$OUTPUT"" 2>/dev/null && echo ""METHOD:gnome-screenshot"" && exit 0

exit 1
";
                await File.WriteAllTextAsync(scriptPath, scriptContent);
                // Dar permisos de ejecución
                var chmod = new ProcessStartInfo { FileName = "chmod", Arguments = $"+x {scriptPath}" };
                Process.Start(chmod)?.WaitForExit();
            }

            // Ejecutar el script helper
            var psiCapture = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = $"-c \"{scriptPath} '{tempFile}'\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true // Capturar stdout para ver el método
            };
            
            using (var p = Process.Start(psiCapture)) { 
                if (p != null) 
                {
                    string output = await p.StandardOutput.ReadToEndAsync();
                    string stderr = await p.StandardError.ReadToEndAsync();
                    await p.WaitForExitAsync();
                    
                    if (!string.IsNullOrWhiteSpace(output))
                         _logger.LogInformation($"ℹ️ [SPY] Captura exitosa usando: {output.Trim()}");

                    // Loguear stderr SIEMPRE si hay algo, para saber por qué falló 'import' aunque 'scrot' funcionara después
                    if (!string.IsNullOrWhiteSpace(stderr))
                         _logger.LogWarning($"⚠️ [SPY] Errores internos durante captura: {stderr}");

                    if (p.ExitCode != 0)
                        _logger.LogWarning($"⚠️ [SPY] Fallaron todos los metodos de captura.");
                }
            }
            
            if (!File.Exists(tempFile))
            {
                _logger.LogWarning($"⚠️ [SPY] Archivo no creado: {tempFile}");
                return null;
            }

            // Leer bytes
            byte[] imageBytes = await File.ReadAllBytesAsync(tempFile);
            
            // Borrar temp
            try { File.Delete(tempFile); } catch { }
            
            return imageBytes;
        }
        catch (Exception ex)
        {
            _logger.LogError($"❌ [SPY] Error capturando: {ex.Message}");
            return null; 
        }
    }

    private string CalcularHash(byte[] data)
    {
        using var md5 = MD5.Create();
        return Convert.ToBase64String(md5.ComputeHash(data));
    }
}
