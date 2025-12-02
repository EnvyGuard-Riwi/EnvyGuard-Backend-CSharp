using EnvyGuard.Agent.Models;
using Renci.SshNet; // Aquí usamos la librería que acabas de instalar
using System.Net.Sockets;
using System.Net;
using System.Globalization;

namespace EnvyGuard.Agent.Services;

public class CommandExecutor
{
    private readonly ILogger<CommandExecutor> _logger;
    private readonly string _sshUser;
    private readonly string _sshKeyPath;

    // Leemos la configuración del appsettings.json
    public CommandExecutor(ILogger<CommandExecutor> logger, IConfiguration config)
    {
        _logger = logger;
        _sshUser = config["SshConfig:User"] ?? "root"; 
        _sshKeyPath = config["SshConfig:KeyPath"] ?? "keys/id_rsa";
    }

    public async Task ExecuteAsync(PcCommand command)
    {
        _logger.LogInformation("Procesando: {Action}", command.Action);

        // CASO ESPECIAL: ENCENDER (No usa SSH)
        if (command.Action.ToLower() == "wakeup" || command.Action.ToLower() == "wol")
        {
            if (string.IsNullOrEmpty(command.MacAddress))
            {
                _logger.LogError("❌ Para encender necesito la MAC Address.");
                return;
            }
            await SendWakeOnLan(command.MacAddress);
            return; // Terminamos aquí, no seguimos al SSH
        }
        
        _logger.LogInformation("🚀 Iniciando conexión SSH a {Ip} para acción: {Action}", command.TargetIp, command.Action);

        if (string.IsNullOrEmpty(command.TargetIp))
        {
            _logger.LogError("❌ Error: La IP destino está vacía en el mensaje recibido.");
            return;
        }

        try
        {
            // 1. Verificamos que exista la llave SSH
            if (!File.Exists(_sshKeyPath))
            {
                _logger.LogError("❌ No encuentro el archivo de llave privada en: {Path}. Asegúrate de generarla.", _sshKeyPath);
                return;
            }

            // 2. Preparamos la conexión usando la llave (NO contraseña)
            var keyFile = new PrivateKeyFile(_sshKeyPath);
            using var client = new SshClient(command.TargetIp, _sshUser, keyFile);
            
            // 3. Conectamos
            client.Connect();
            _logger.LogInformation("✅ Conexión SSH establecida con {Ip}", command.TargetIp);

            // 4. Construimos el comando Linux
            string linuxCommand = BuildLinuxCommand(command);

            // 5. Ejecutamos el comando remotamente
            var sshCommand = client.RunCommand(linuxCommand);
            
            // 6. Revisamos si funcionó
            if (sshCommand.ExitStatus == 0)
            {
                _logger.LogInformation("🎉 Comando ejecutado con éxito en {Ip}. Salida: {Output}", command.TargetIp, sshCommand.Result);
            }
            else
            {
                _logger.LogError("⚠️ El comando falló en {Ip}. Error: {Error}", command.TargetIp, sshCommand.Error);
            }

            client.Disconnect();
        }
        catch (Exception ex)
        {
            // Capturamos errores de red (ej: PC apagado) para que el agente no se cierre
            _logger.LogError("🔥 Error conectando por SSH a {Ip}: {Message}", command.TargetIp, ex.Message);
        }
    }

    private string BuildLinuxCommand(PcCommand command)
    {
        // Nota: Estos comandos requieren que el usuario tenga permisos sudo NOPASSWD
        switch (command.Action.ToLower())
        {
            case "shutdown":
                return "sudo shutdown -h now";
            
            case "reboot":
                return "sudo reboot";
            
            case "block_sites":
                // Ejemplo simple: agregar facebook al hosts
                if(string.IsNullOrEmpty(command.Parameters)) return "echo 'Nada que bloquear'";
                return $"echo '127.0.0.1 {command.Parameters}' | sudo tee -a /etc/hosts";
            
            case "format":
                // LISTA BLANCA (Asegúrate que coincida con tu ls -l)
                string safeUsers = "cohorte4|cohorte6|rwadmin|coders|mari|envyguard_admin";

                return $@"
                    sudo bash -c '
                    cd /home
                    for D in *; do
                        if [[ ! ""$D"" =~ ^({safeUsers})$ ]]; then
                            echo ""Detectado intruso o cuenta antigua: $D - ELIMINANDO...""
                            pkill -u ""$D"" || true
                            userdel -r ""$D"" || echo ""No se pudo borrar $D""
                            if [ -d ""$D"" ]; then rm -rf ""$D""; fi
                        else
                            echo ""Mantenimiento a usuario seguro: $D""
                            
                            # --- LIMPIEZA BILINGÜE (INGLÉS / ESPAÑOL) ---
                            # Usamos 2>/dev/null para silenciar errores si la carpeta no existe
                            
                            # 1. Descargas / Downloads
                            rm -rf ""/home/$D/Downloads/""* 2>/dev/null
                            rm -rf ""/home/$D/Descargas/""* 2>/dev/null

                            # 2. Documentos / Documents
                            rm -rf ""/home/$D/Documents/""* 2>/dev/null
                            rm -rf ""/home/$D/Documentos/""* 2>/dev/null

                            # 3. Escritorio / Desktop
                            rm -rf ""/home/$D/Desktop/""* 2>/dev/null
                            rm -rf ""/home/$D/Escritorio/""* 2>/dev/null

                            # 4. Imágenes / Pictures (Ojo con la tilde)
                            rm -rf ""/home/$D/Pictures/""* 2>/dev/null
                            rm -rf ""/home/$D/Imágenes/""* 2>/dev/null

                            # 5. Música / Music (Ojo con la tilde)
                            rm -rf ""/home/$D/Music/""* 2>/dev/null
                            rm -rf ""/home/$D/Música/""* 2>/dev/null

                            # 6. Caché (Igual para todos)
                            rm -rf ""/home/$D/.cache/""* 2>/dev/null
                            
                            # 7. Papelera de reciclaje (Trash)
                            rm -rf ""/home/$D/.local/share/Trash/""* 2>/dev/null
                        fi
                    done
                    echo ""Limpieza profunda finalizada (Inglés/Español).""
                    '
                ";

            case "test":
                return "echo 'Hola! La conexión SSH funciona correctamente.'";

            default:
                return $"echo 'Acción {command.Action} no reconocida'";
        }
    }
    
    private async Task SendWakeOnLan(string macAddress)
    {
        try
        {
            // 1. Limpiar la MAC (quitar : o -)
            var macClean = macAddress.Replace(":", "").Replace("-", "");
            
            // 2. Convertir string a bytes
            // El formato MAC son 6 bytes (ej: AA BB CC DD EE FF)
            if (macClean.Length != 12) throw new ArgumentException("MAC Address inválida");

            byte[] macBytes = new byte[6];
            for (int i = 0; i < 6; i++)
            {
                string byteValue = macClean.Substring(i * 2, 2);
                macBytes[i] = byte.Parse(byteValue, NumberStyles.HexNumber);
            }

            // 3. Construir el "Paquete Mágico"
            // Estructura: 6 bytes de 0xFF + 16 veces la MAC Address
            byte[] packet = new byte[6 + 16 * 6];
            
            // Poner los 6 primeros bytes en FF
            for (int i = 0; i < 6; i++) packet[i] = 0xFF;
            
            // Repetir la MAC 16 veces
            for (int i = 0; i < 16; i++)
            {
                Array.Copy(macBytes, 0, packet, 6 + i * 6, 6);
            }

            // 4. Enviar el grito a toda la red (Broadcast) por el puerto 9
            using var client = new UdpClient();
            client.EnableBroadcast = true;
            
            // Enviamos a la IP de Broadcast (255.255.255.255)
            await client.SendAsync(packet, packet.Length, new IPEndPoint(IPAddress.Broadcast, 9));
            
            _logger.LogInformation("✨ Paquete Mágico (WOL) enviado a la MAC: {Mac}", macAddress);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falló el envío de WOL a {Mac}", macAddress);
        }
    }
}