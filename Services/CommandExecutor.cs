using EnvyGuard.Agent.Models;
using Renci.SshNet; 
using System.Net.Sockets;
using System.Net;
using System.Globalization;

namespace EnvyGuard.Agent.Services;

public class CommandExecutor
{
    private readonly ILogger<CommandExecutor> _logger;
    private readonly IConfiguration _config; 
    private readonly string _sshUser;
    private readonly string _sshKeyPath;

    public CommandExecutor(ILogger<CommandExecutor> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;
        _sshUser = config["SshConfig:User"] ?? "root"; 
        _sshKeyPath = config["SshConfig:KeyPath"] ?? "keys/id_rsa";
    }

    public async Task ExecuteAsync(PcCommand command)
    {
        _logger.LogInformation("Procesando: {Action}", command.Action);

        // --- CASO 1: ENCENDER (Wake-on-LAN) ---
        if (command.Action.ToLower() == "wakeup" || command.Action.ToLower() == "wol")
        {
            if (string.IsNullOrEmpty(command.MacAddress))
            {
                _logger.LogError("❌ Para encender necesito la MAC Address.");
                return;
            }
            await SendWakeOnLan(command.MacAddress);
            return; 
        }
        
        // --- CASO 2: COMANDOS SSH (Apagar, Reiniciar, etc) ---
        
        if (string.IsNullOrEmpty(command.TargetIp))
        {
            _logger.LogError("❌ Error: La IP destino está vacía.");
            return;
        }

        // Determinar puerto (si viene 0, forzamos 22)
        int sshPort = command.Port > 0 ? command.Port : 22;

        _logger.LogInformation("🚀 Conectando a {Ip}:{Port} usuario: {User} acción: {Action}", command.TargetIp, sshPort, _sshUser, command.Action);

        try
        {
            SshClient client;
            
            // LÓGICA HÍBRIDA: ¿Tenemos contraseña en la configuración?
            string? sshPassword = _config["SshConfig:Password"];

            if (!string.IsNullOrEmpty(sshPassword))
            {
                // MODO CONTRASEÑA 
                _logger.LogWarning("🔑 Usando autenticación por CONTRASEÑA.");
                client = new SshClient(command.TargetIp, sshPort, _sshUser, sshPassword);
            }
            else
            {
                // MODO LLAVE 
                _logger.LogInformation("Gd Usando autenticación por LLAVE (Key File).");
                
                if (!File.Exists(_sshKeyPath))
                {
                    _logger.LogError("❌ No encuentro el archivo de llave en: {Path} y no hay contraseña configurada.", _sshKeyPath);
                    return;
                }

                var keyFile = new PrivateKeyFile(_sshKeyPath);
                client = new SshClient(command.TargetIp, sshPort, _sshUser, keyFile);
            }

            // Usamos el cliente creado
            using (client)
            {
                client.Connect();
                _logger.LogInformation("✅ Conexión SSH establecida.");

                // Pasamos la contraseña al constructor del comando (si existe)
                string linuxCommand = BuildLinuxCommand(command, sshPassword);
                
                var sshCommand = client.RunCommand(linuxCommand);
                
                if (sshCommand.ExitStatus == 0)
                {
                    _logger.LogInformation("🎉 Éxito: {Output}", sshCommand.Result);
                }
                else
                {
                    _logger.LogError("⚠️ Fallo en remoto: {Error}", sshCommand.Error);
                }

                client.Disconnect();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("🔥 Error conectando por SSH a {Ip}: {Message}", command.TargetIp, ex.Message);
        }
    }

    private string BuildLinuxCommand(PcCommand command, string? sshPassword)
    {
        // 1. Definir el prefijo: sudo normal O echo pass | sudo -S
        string sudoPrefix = string.IsNullOrEmpty(sshPassword) 
            ? "sudo" // Si no hay pass, intenta sudo normal (confiando en NOPASSWD)
            : $"echo '{sshPassword}' | sudo -S"; 

        switch (command.Action.ToLower())
        {
            case "shutdown":
                return $"{sudoPrefix} shutdown -h now";
            
            case "lock_session":
                // Esto fuerza el bloqueo de todas las sesiones gráficas activas (GNOME, KDE, etc.)
                // Funciona en Ubuntu 18.04+, Debian 10+, Fedora (sistemas con systemd)
                return $"{sudoPrefix} loginctl lock-sessions";
            
            case "reboot":
                return $"{sudoPrefix} reboot";
            
            case "block_sites":
                if (string.IsNullOrEmpty(command.Parameters)) return "echo 'Nada que bloquear'";

                string domain = command.Parameters.Trim();
                
                // ESTRATEGIA BLINDADA:
                // 1. Bloqueamos IPv4 (127.0.0.1)
                // 2. Bloqueamos IPv6 (::1)
                // 3. Bloqueamos con y sin www
                string content = $"\n127.0.0.1 {domain}\n127.0.0.1 www.{domain}\n::1 {domain}\n::1 www.{domain}";

                // Usamos bash -c con comillas escapadas para que no choque con sudo
                return $"{sudoPrefix} bash -c \"echo '{content}' >> /etc/hosts\"";

			case "unblock_sites":
                if (string.IsNullOrEmpty(command.Parameters)) return "echo 'Nada que desbloquear'";
                
                string domainToUnblock = command.Parameters.Trim();
                return $"{sudoPrefix} sed -i '/{domainToUnblock}/d' /etc/hosts && echo 'Sitio liberado: {domainToUnblock}'";
            
            case "format":
                string safeUsers = "cohorte4|cohorte6|rwadmin|coders|mari|envyguard_admin";
                // Aquí usamos sudoPrefix también para el bash script
                return $@"
                    {sudoPrefix} bash -c '
                    cd /home
                    for D in *; do
                        if [[ ! ""$D"" =~ ^({safeUsers})$ ]]; then
                            echo ""Detectado intruso: $D - ELIMINANDO...""
                            pkill -u ""$D"" || true
                            userdel -r ""$D"" || echo ""Error borrando $D""
                            if [ -d ""$D"" ]; then rm -rf ""$D""; fi
                        else
                            echo ""Limpiando usuario seguro: $D""
                            rm -rf ""/home/$D/Downloads/""* 2>/dev/null
                            rm -rf ""/home/$D/Descargas/""* 2>/dev/null
                            rm -rf ""/home/$D/Documents/""* 2>/dev/null
                            rm -rf ""/home/$D/Documentos/""* 2>/dev/null
                            rm -rf ""/home/$D/Desktop/""* 2>/dev/null
                            rm -rf ""/home/$D/Escritorio/""* 2>/dev/null
                            rm -rf ""/home/$D/Pictures/""* 2>/dev/null
                            rm -rf ""/home/$D/Imágenes/""* 2>/dev/null
                            rm -rf ""/home/$D/Music/""* 2>/dev/null
                            rm -rf ""/home/$D/Música/""* 2>/dev/null
                            rm -rf ""/home/$D/.cache/""* 2>/dev/null
                            rm -rf ""/home/$D/.local/share/Trash/""* 2>/dev/null
                        fi
                    done
                    echo ""Limpieza finalizada.""
                    '
                ";

            case "test":
                return "echo 'Hola! La conexión SSH funciona correctamente.'";
            
            case "install_app":
                if (string.IsNullOrEmpty(command.Parameters)) 
                    return "echo '❌ Error: Debes enviar el nombre de la aplicación en Parameters.'";

                string appName = command.Parameters.Trim();

                // 🛡️ SEGURIDAD: Validar que el nombre solo tenga letras, números, guiones o puntos.
                // Esto evita que alguien mande: "git; rm -rf /"
                if (appName.Any(c => !char.IsLetterOrDigit(c) && c != '-' && c != '.' && c != '_' && c != ' '))
                {
                    return $"echo '❌ Error: El nombre de la aplicación \"{appName}\" contiene caracteres sospechosos.'";
                }

                // Lógica del comando:
                // 1. sudo apt-get update
                // 2. export DEBIAN_FRONTEND=noninteractive (Para que no salgan pantallas azules de configuración)
                // 3. sudo apt-get install -y (Para responder "Sí" a todo automáticamente)
                return $"{sudoPrefix} apt-get update -y && export DEBIAN_FRONTEND=noninteractive && {sudoPrefix} apt-get install -y {appName}";
            
            case "install_snap":
                if (string.IsNullOrEmpty(command.Parameters)) return "echo 'Falta el nombre del snap'";
                string snapName = command.Parameters.Trim();
                // Snap requiere --classic para IDEs como Rider
                return $"{sudoPrefix} snap install {snapName} --classic";
            

            case "create_sudo_user":
                if (string.IsNullOrEmpty(command.Parameters)) 
                    return "echo '❌ Error: Parametros vacios. Formato: usuario contrasena'";

                var parts = command.Parameters.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) 
                    return "echo '❌ Error: Se requiere usuario y contrasena'";

                string newUser = parts[0];
                string newPass = parts[1];

                if (newUser.Any(c => !char.IsLetterOrDigit(c) && c != '_' && c != '-'))
                    return $"echo '❌ Error: El usuario \"{newUser}\" tiene caracteres invalidos'";

                // 1. Crear usuario (-m home, -s shell bash, -G sudo)
                // 2. Asignar contraseña
                return $"{sudoPrefix} useradd -m -s /bin/bash -G sudo {newUser} && echo '{newUser}:{newPass}' | {sudoPrefix} chpasswd && echo '✅ Usuario {newUser} creado con permisos sudo'";

            case "delete_user":
                if (string.IsNullOrEmpty(command.Parameters)) return "echo '❌ Error: Falta el nombre del usuario a eliminar'";
                
                string userToDelete = command.Parameters.Trim();
                
                if (userToDelete.Any(c => !char.IsLetterOrDigit(c) && c != '_' && c != '-'))
                    return $"echo '❌ Error: El usuario \"{userToDelete}\" tiene caracteres invalidos'";

                return $"{sudoPrefix} userdel -r {userToDelete} && echo '🗑️ Usuario {userToDelete} eliminado correctamente'";

            default:
                return $"echo 'Acción {command.Action} no reconocida'";
        }
    }
    
    private async Task SendWakeOnLan(string macAddress)
    {
        try
        {
            var macClean = macAddress.Replace(":", "").Replace("-", "");
            if (macClean.Length != 12) throw new ArgumentException("MAC Address inválida");

            byte[] macBytes = new byte[6];
            for (int i = 0; i < 6; i++)
            {
                macBytes[i] = byte.Parse(macClean.Substring(i * 2, 2), NumberStyles.HexNumber);
            }

            byte[] packet = new byte[6 + 16 * 6];
            for (int i = 0; i < 6; i++) packet[i] = 0xFF;
            for (int i = 0; i < 16; i++)
                Array.Copy(macBytes, 0, packet, 6 + i * 6, 6);

            // Obtener todas las interfaces de red activas
            var interfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up &&
                            n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                .ToList();

            if (!interfaces.Any())
            {
                _logger.LogWarning("⚠️ No se detectaron interfaces de red activas. Usando Broadcast global.");
                using var client = new UdpClient();
                client.EnableBroadcast = true;
                await client.SendAsync(packet, packet.Length, new IPEndPoint(IPAddress.Broadcast, 9));
                return;
            }

            foreach (var netInterface in interfaces)
            {
                var ipProps = netInterface.GetIPProperties();
                foreach (var unicast in ipProps.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        var broadcastAddress = GetBroadcastAddress(unicast.Address, unicast.IPv4Mask);
                        if (broadcastAddress != null)
                        {
                            try 
                            {
                                using var client = new UdpClient();
                                client.EnableBroadcast = true;
                                // Bind to the specific interface IP to force sending from correct interface
                                client.Client.Bind(new IPEndPoint(unicast.Address, 0)); 
                                await client.SendAsync(packet, packet.Length, new IPEndPoint(broadcastAddress, 9));
                                _logger.LogInformation("📡 WOL enviado desde {Ip} a broadcast {Broadcast} (Interfaz: {Name})", 
                                    unicast.Address, broadcastAddress, netInterface.Name);
                            }
                            catch (Exception sendEx)
                            {
                                _logger.LogWarning("⚠️ Falló envío por interfaz {Name}: {Error}", netInterface.Name, sendEx.Message);
                            }
                        }
                    }
                }
            }
            
            _logger.LogInformation("✨ Proceso de Wake-on-LAN completado para la MAC: {Mac}", macAddress);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falló el envío de WOL a {Mac}", macAddress);
        }
    }

    private IPAddress GetBroadcastAddress(IPAddress address, IPAddress mask)
    {
        if (mask == null) return IPAddress.Broadcast; // Fallback

        uint ipAddress = BitConverter.ToUInt32(address.GetAddressBytes(), 0);
        uint ipMaskV4 = BitConverter.ToUInt32(mask.GetAddressBytes(), 0);
        uint broadCastIpAddress = ipAddress | ~ipMaskV4;

        return new IPAddress(BitConverter.GetBytes(broadCastIpAddress));
    }
}