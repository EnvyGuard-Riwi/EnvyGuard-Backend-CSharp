using System.Text;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using EnvyGuard.Agent.Services;

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
        
        // Obtener conexión y crear canal
        var connection = await _connectionProvider.GetConnectionAsync(stoppingToken);
        var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);

        // Declarar cola (asegura que existe)
        await channel.QueueDeclareAsync(
            queue: queueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null,
            cancellationToken: stoppingToken);

        _logger.LogInformation("Esperando comandos en cola: {Queue}", queueName);

        var consumer = new AsyncEventingBasicConsumer(channel);
        
        consumer.ReceivedAsync += async (model, ea) =>
        {
            var body = ea.Body.ToArray();
            var message = Encoding.UTF8.GetString(body);
            
            _logger.LogInformation("Comando recibido: {Command}", message);

            try
            {
                // Ejecutar el comando en el sistema Linux
                await _executor.ExecuteAsync(message);
                
                // Confirmar procesado (Ack)
                await channel.BasicAckAsync(ea.DeliveryTag, multiple: false, cancellationToken: stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error procesando comando.");
                // Opcional: BasicNackAsync si quieres reencolar
            }
        };

        await channel.BasicConsumeAsync(queue: queueName, autoAck: false, consumer: consumer, cancellationToken: stoppingToken);
    }
}