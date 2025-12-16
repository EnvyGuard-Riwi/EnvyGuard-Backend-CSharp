using EnvyGuard.Agent.Messaging;

namespace EnvyGuard.Agent;

public class Worker : BackgroundService
{
    private readonly CommandConsumer _consumer;
    private readonly ILogger<Worker> _logger;

    public Worker(CommandConsumer consumer, ILogger<Worker> logger)
    {
        _consumer = consumer;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Agente Iniciado.");
        try 
        {
            await _consumer.StartAsync(stoppingToken);
            
            // Mantener vivo el servicio mientras no se cancele
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (TaskCanceledException)
        {
            // Ignorar al cerrar
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Fallo crítico en el Worker.");
        }
    }
}