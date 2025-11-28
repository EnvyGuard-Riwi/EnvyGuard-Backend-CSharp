using System.Text;
using System.Text.Json; // Necesario para entender JSON
using EnvyGuard.Agent.Models;
using EnvyGuard.Agent.Services;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace EnvyGuard.Agent.Messaging;

public class CommandConsumer
{
    private readonly RabbitMqConnection _connectionProvider;
    private readonly CommandExecutor _executor;
    private readonly IConfiguration _config;
    private readonly ILogger<CommandConsumer> _logger;

    public CommandConsumer(
        RabbitMqConnection connectionProvider, 
        CommandExecutor executor, 
        IConfiguration config,
        ILogger<CommandConsumer> logger)
    {
        _connectionProvider = connectionProvider;
        _executor = executor;
        _config = config;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken stoppingToken)
    {
        var queueName = _config["RabbitMQ:QueueName"] ?? "pc_commands";
        
        var connection = await _connectionProvider.GetConnectionAsync(stoppingToken);
        var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);

        await channel.QueueDeclareAsync(
            queue: queueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null,
            cancellationToken: stoppingToken);

        _logger.LogInformation("🎧 Agente escuchando JSON en cola: {Queue}", queueName);

        var consumer = new AsyncEventingBasicConsumer(channel);
        
        consumer.ReceivedAsync += async (model, ea) =>
        {
            var body = ea.Body.ToArray();
            var message = Encoding.UTF8.GetString(body);
            
            _logger.LogInformation("📩 Mensaje recibido: {Json}", message);

            try
            {
                // 1. Deserializar el JSON a nuestro objeto PcCommand
                // La opción PropertyNameCaseInsensitive permite que "action": "Reboot" funcione igual
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var command = JsonSerializer.Deserialize<PcCommand>(message, options);

                if (command != null)
                {
                    // 2. Ejecutar la lógica segura
                    await _executor.ExecuteAsync(command);
                    
                    // 3. Confirmar procesado (Ack)
                    await channel.BasicAckAsync(ea.DeliveryTag, multiple: false, cancellationToken: stoppingToken);
                }
                else
                {
                    _logger.LogWarning("El mensaje recibido no era un comando válido o estaba vacío.");
                    // Aceptamos el mensaje para quitarlo de la cola aunque esté mal, para no bloquear
                    await channel.BasicAckAsync(ea.DeliveryTag, multiple: false, cancellationToken: stoppingToken);
                }
            }
            catch (JsonException)
            {
                _logger.LogError("Error: El mensaje recibido no es un JSON válido.");
                // Rechazamos el mensaje (sin reencolar) porque está corrupto
                await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false, cancellationToken: stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error crítico procesando comando.");
                // Aquí podrías decidir si haces Nack con requeue=true para intentar de nuevo
            }
        };

        await channel.BasicConsumeAsync(queue: queueName, autoAck: false, consumer: consumer, cancellationToken: stoppingToken);
    }
}