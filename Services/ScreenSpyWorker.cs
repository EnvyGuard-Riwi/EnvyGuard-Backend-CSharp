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
            
            // Crear script de captura robusto (SIEMPRE SOBREESCRIBIR)
            string scriptPath = "/tmp/capture_helper.sh";
            string scriptContent = @"#!/bin/bash
OUTPUT=$1

# Función para buscar variables en procesos del usuario
function find_session_vars() {
    # Buscar procesos de interfaz gráfica
    for pid in $(pgrep -u $1 'gnome-shell|Xorg|kwin|xfwm4|lxsession'); do
        if [ -f /proc/$pid/environ ]; then
            ENV_CONTENT=$(cat /proc/$pid/environ | tr '\0' '\n')
            DISP=$(echo ""$ENV_CONTENT"" | grep '^DISPLAY=' | cut -d= -f2-)
            WDISP=$(echo ""$ENV_CONTENT"" | grep '^WAYLAND_DISPLAY=' | cut -d= -f2-)
            XAUTH=$(echo ""$ENV_CONTENT"" | grep '^XAUTHORITY=' | cut -d= -f2-)
            DBUS=$(echo ""$ENV_CONTENT"" | grep '^DBUS_SESSION_BUS_ADDRESS=' | cut -d= -f2-)
            RUNDIR=$(echo ""$ENV_CONTENT"" | grep '^XDG_RUNTIME_DIR=' | cut -d= -f2-)
            
            # Fallback corregido para XAUTH
            if [ -z ""$XAUTH"" ] && [ -f ""/home/$1/.Xauthority"" ]; then
                XAUTH=""/home/$1/.Xauthority""
            fi

            if [ -n ""$DISP"" ] || [ -n ""$WDISP"" ]; then
                export DISPLAY=$DISP
                export WAYLAND_DISPLAY=$WDISP
                export XAUTHORITY=$XAUTH
                export DBUS_SESSION_BUS_ADDRESS=$DBUS
                export XDG_RUNTIME_DIR=$RUNDIR
                return 0
            fi
        fi
    done
    return 1
}

# 1. Identificar al usuario real
REAL_USER=""""
for uid_dir in /run/user/[0-9]*; do
    uid=$(basename $uid_dir)
    user_name=$(id -nu $uid 2>/dev/null)
    if [ -n ""$user_name"" ] && [ ""$user_name"" != ""messagebus"" ] && [ ""$user_name"" != ""root"" ]; then
        REAL_USER=$user_name
        find_session_vars $REAL_USER && break
    fi
done

echo ""INFO:USER=$REAL_USER DISPLAY=$DISPLAY WAYLAND=$WAYLAND_DISPLAY XAUTH=$XAUTHORITY""

# 3. Intentar capturar (Priorizando Silencio y Wayland)

# Opción A: D-Bus GNOME (Silent)
if [ -n ""$DBUS_SESSION_BUS_ADDRESS"" ]; then
    sudo -u $REAL_USER env DISPLAY=$DISPLAY WAYLAND_DISPLAY=$WAYLAND_DISPLAY \
    XDG_RUNTIME_DIR=$XDG_RUNTIME_DIR DBUS_SESSION_BUS_ADDRESS=$DBUS_SESSION_BUS_ADDRESS \
    gdbus call --session --dest org.gnome.Shell.Screenshot --object-path /org/gnome/Shell/Screenshot \
    --method org.gnome.Shell.Screenshot.Screenshot true false ""$OUTPUT"" >/dev/null 2>&1
    if [ -s ""$OUTPUT"" ]; then echo ""METHOD:gnome-dbus-silent"" && exit 0; fi
fi

# Opción B: grim (Wayland Puro - Silencioso) - ALTA PRIORIDAD EN 24.04
if command -v grim >/dev/null && [ -n ""$WAYLAND_DISPLAY"" ]; then
    sudo -u $REAL_USER env WAYLAND_DISPLAY=$WAYLAND_DISPLAY XDG_RUNTIME_DIR=$XDG_RUNTIME_DIR \
    grim ""$OUTPUT"" >/dev/null 2>&1
    if [ -s ""$OUTPUT"" ]; then echo ""METHOD:grim-silent"" && exit 0; fi
fi

# Opción C: import (ImageMagick - Silencioso)
sudo -u $REAL_USER env DISPLAY=$DISPLAY XAUTHORITY=$XAUTHORITY \
import -window root ""$OUTPUT"" >/dev/null 2>&1
if [ -s ""$OUTPUT"" ]; then echo ""METHOD:import-silent"" && exit 0; fi

# Opción D: scrot (Silencioso)
sudo -u $REAL_USER env DISPLAY=$DISPLAY XAUTHORITY=$XAUTHORITY \
scrot -z -o ""$OUTPUT"" >/dev/null 2>&1
if [ -s ""$OUTPUT"" ]; then echo ""METHOD:scrot-silent"" && exit 0; fi

# Opción E: gnome-screenshot (X11/Wayland - TIENE FLASH - Último recurso)
sudo -u $REAL_USER env DISPLAY=$DISPLAY WAYLAND_DISPLAY=$WAYLAND_DISPLAY \
XDG_RUNTIME_DIR=$XDG_RUNTIME_DIR DBUS_SESSION_BUS_ADDRESS=$DBUS_SESSION_BUS_ADDRESS \
gnome-screenshot -f ""$OUTPUT"" >/dev/null 2>&1
if [ -s ""$OUTPUT"" ]; then echo ""METHOD:gnome-screenshot-flash"" && exit 0; fi

exit 1
";
            await File.WriteAllTextAsync(scriptPath, scriptContent);
            Process.Start("chmod", $"+x {scriptPath}")?.WaitForExit();

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
