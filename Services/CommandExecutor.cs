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

                // COMANDO SED:
                // -i : Editar el archivo ahí mismo (in-place)
                // /patron/d : Borrar (delete) las líneas que coincidan con el patrón
                // Esto borrará tanto "facebook.com" como "www.facebook.com" porque ambos contienen la palabra.
                
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

            using var client = new UdpClient();
            client.EnableBroadcast = true;
            await client.SendAsync(packet, packet.Length, new IPEndPoint(IPAddress.Broadcast, 9));
            
            _logger.LogInformation("✨ Paquete Mágico (WOL) enviado a la MAC: {Mac}", macAddress);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falló el envío de WOL a {Mac}", macAddress);
        }
    }
}