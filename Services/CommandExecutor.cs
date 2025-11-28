using System.Diagnostics;
using EnvyGuard.Agent.Models; // Importante para usar PcCommand

namespace EnvyGuard.Agent.Services;

public class CommandExecutor
{
    private readonly ILogger<CommandExecutor> _logger;

    public CommandExecutor(ILogger<CommandExecutor> logger)
    {
        _logger = logger;
    }

    public async Task ExecuteAsync(PcCommand command)
    {
        _logger.LogInformation("Procesando acción: {Action} para PC: {Target}", command.Action, command.TargetPcId);

        try
        {
            switch (command.Action.ToLower())
            {
                case "shutdown":
                    _logger.LogWarning("⚠️ Recibido comando de APAGADO.");
                    // En producción (PC Real): await RunProcess("shutdown", "-h now");
                    // En pruebas (Docker): Solo logueamos para no apagar tu contenedor o servidor VPS
                    _logger.LogInformation("[SIMULACIÓN] El sistema se apagaría ahora.");
                    break;

                case "reboot":
                    _logger.LogWarning("⚠️ Recibido comando de REINICIO.");
                    // En producción (PC Real): await RunProcess("reboot", "");
                    _logger.LogInformation("[SIMULACIÓN] El sistema se reiniciaría ahora.");
                    break;

                case "lock":
                    _logger.LogInformation("🔒 Bloqueando sesión de usuario...");
                    // Este comando depende del entorno de escritorio (Gnome, KDE). 
                    // Ejemplo común para Gnome:
                    // await RunProcess("loginctl", "lock-session");
                    _logger.LogInformation("[SIMULACIÓN] Sesión bloqueada.");
                    break;
                
                case "test":
                    // Un comando simple para probar conectividad
                    await RunProcess("echo", $"Hola desde el agente! Param: {command.Parameters}");
                    break;

                default:
                    _logger.LogError("❌ Acción no reconocida: {Action}", command.Action);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al ejecutar el comando {Action}", command.Action);
            throw; // Re-lanzar para que RabbitMQ sepa que falló
        }
    }

    // Método genérico para ejecutar procesos en Linux
    private async Task RunProcess(string fileName, string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        
        process.Start();
        await process.WaitForExitAsync();

        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();

        if (!string.IsNullOrWhiteSpace(output))
            _logger.LogInformation("Salida del proceso: {Output}", output);

        if (!string.IsNullOrWhiteSpace(error))
            _logger.LogError("Error del proceso: {Error}", error);
    }
}