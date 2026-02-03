using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Net;
using System.Net.Sockets;

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

                                // Enviar al Topic (Para que Java/React lo vean)
                                var props = new BasicProperties 
                                { 
                                    Priority = 0,
                                    ContentType = "application/json",
                                    DeliveryMode = DeliveryModes.Persistent
                                };
                                
                                await channel.BasicPublishAsync(exchange: "amq.topic", routingKey: "spy.screens", mandatory: false, basicProperties: props, body: body, cancellationToken: stoppingToken);
                                
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

# Función para buscar variables en procesos del usuario
function find_session_vars() {
    # Buscar procesos de interfaz gráfica (gnome-shell, Xorg, kwin, etc)
    for pid in $(pgrep -u $1 'gnome-shell|Xorg|kwin|xfwm4|lxsession'); do
        # Intentar leer el environment del proceso
        if [ -f /proc/$pid/environ ]; then
            # Leer environ reemplazando nulls por newlines para poder usar grep bien
            ENV_CONTENT=$(cat /proc/$pid/environ | tr '\0' '\n')
            
            # Extraer variables exactas
            DBUS=$(echo ""$ENV_CONTENT"" | grep '^DBUS_SESSION_BUS_ADDRESS=' | cut -d= -f2-)
            DISP=$(echo ""$ENV_CONTENT"" | grep '^DISPLAY=' | cut -d= -f2-)
            XAUTH=$(echo ""$ENV_CONTENT"" | grep '^XAUTHORITY=' | cut -d= -f2-)
            
            # Priorizar si encontramos DISPLAY
            if [ -n ""$DISP"" ]; then
                export DISPLAY=$DISP
                [ -n ""$XAUTH"" ] && export XAUTHORITY=$XAUTH
                [ -n ""$DBUS"" ] && export DBUS_SESSION_BUS_ADDRESS=$DBUS
                return 0
            fi
        fi
    done
    return 1
}

# 1. Identificar al usuario real (que no sea root ni messagebus)
# Buscamos usuarios con sesiones activas en /run/user/
REAL_USER=""""
for uid_dir in /run/user/[0-9]*; do
    uid=$(basename $uid_dir)
    user_name=$(id -nu $uid 2>/dev/null)
    if [ -n ""$user_name"" ]; then
        REAL_USER=$user_name
        # Intentar extraer variables de esa sesión
        find_session_vars $REAL_USER
        if [ -n ""$DISPLAY"" ]; then
            break # Encontramos un usuario con DISPLAY válido
        fi
    fi
done

# Si falló lo anterior, fallback clásico
if [ -z ""$DISPLAY"" ]; then
    export DISPLAY=:0
    [ -n ""$REAL_USER"" ] && export XAUTHORITY=/home/$REAL_USER/.Xauthority
fi

echo ""INFO:USER=$REAL_USER""
echo ""INFO:DISPLAY=$DISPLAY""
echo ""INFO:XAUTH=$XAUTHORITY""

# 3. Intentar capturar

# Opción A: gnome-screenshot (Prioridad 1 - Mejor compatibilidad Wayland/X11)
# Requiere: sudo apt install gnome-screenshot
gnome-screenshot -f ""$OUTPUT"" 2>/dev/null && echo ""METHOD:gnome-screenshot"" && exit 0

# Opción B: import (ImageMagick - X11)
import -window root ""$OUTPUT"" 2>/dev/null && echo ""METHOD:import"" && exit 0

# Opción C: xwd (X11 - Muy robusto)
xwd -root -silent -out ""$OUTPUT.xwd"" 2>/dev/null && convert ""$OUTPUT.xwd"" ""$OUTPUT"" 2>/dev/null && rm ""$OUTPUT.xwd"" && echo ""METHOD:xwd"" && exit 0

# Opción D: scrot (X11)
scrot -z -o ""$OUTPUT"" 2>/dev/null && echo ""METHOD:scrot"" && exit 0

# Opción E: Wayland específico (grim)
grim ""$OUTPUT"" 2>/dev/null && echo ""METHOD:grim"" && exit 0

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
