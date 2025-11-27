using System.Diagnostics;

namespace EnvyGuard.Agent.Services;

public class CommandExecutor
{
    private readonly ILogger<CommandExecutor> _logger;

    public CommandExecutor(ILogger<CommandExecutor> logger)
    {
        _logger = logger;
    }

    public async Task ExecuteAsync(string command)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = $"-c \"{command.Replace("\"", "\\\"")}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };

        process.Start();
        await process.WaitForExitAsync();
        
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();

        if (process.ExitCode != 0)
        {
            _logger.LogError("Error ejecutando comando: {Error}", error);
            throw new Exception(error);
        }

        if (!string.IsNullOrWhiteSpace(output))
            _logger.LogInformation("Salida: {Output}", output);
    }
}