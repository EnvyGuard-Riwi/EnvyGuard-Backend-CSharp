using System.Text;
using System.Text.Json;
using EnvyGuard.Agent.Models;
using EnvyGuard.Agent.Services;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace EnvyGuard.Agent.Messaging;

public class CommandConsumer
{
    private readonly CommandExecutor _executor;
    private readonly IConfiguration _config;
    private readonly ILogger<CommandConsumer> _logger;

    public CommandConsumer(
        CommandExecutor executor, 
        IConfiguration config,
        ILogger<CommandConsumer> logger)
    {
        _executor = executor;
        _config = config;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken stoppingToken)
    {
        // Leer configuración con valores por defecto seguros
        var queueName = _config["RabbitMQ:QueueName"] ?? "pc_commands";
        var hostName = _config["RabbitMQ:HostName"] ?? "localhost";
        var userName = _config["RabbitMQ:UserName"] ?? "guest";
        var password = _config["RabbitMQ:Password"] ?? "guest";

        _logger.LogInformation("🔌 Iniciando conexión BLINDADA a {Host}...", hostName);

        var factory = new ConnectionFactory
        {
            HostName = hostName,
            UserName = userName,
            Password = password,
            VirtualHost = "/",
            AutomaticRecoveryEnabled = true,
            NetworkRecoveryInterval = TimeSpan.FromSeconds(5),
            RequestedHeartbeat = TimeSpan.FromSeconds(5) // Heartbeat agresivo para evitar corte por firewall
        };

        // ⚠️ CORRECCIÓN AQUÍ: Quitamos el 'using' y arreglamos los parámetros
        var connection = await factory.CreateConnectionAsync(stoppingToken);
        
        // Aquí estaba el error CS1503: Hay que especificar el nombre del parámetro
        var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);

        await channel.QueueDeclareAsync(
            queue: queueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null,
            cancellationToken: stoppingToken);

        // QoS: Procesar 1 mensaje a la vez
        await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false, cancellationToken: stoppingToken);

        _logger.LogInformation("🎧 Agente ESCUCHANDO (Versión Inmortal) en cola: {Queue}", queueName);

        var consumer = new AsyncEventingBasicConsumer(channel);
        
        consumer.ReceivedAsync += async (model, ea) =>
        {
            var body = ea.Body.ToArray();
            var message = Encoding.UTF8.GetString(body);
            
            _logger.LogInformation("📩 Mensaje recibido: {Json}", message);

            try
            {
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var command = JsonSerializer.Deserialize<PcCommand>(message, options);

                if (command != null)
                {
                    await _executor.ExecuteAsync(command);
                    
                    if (channel.IsOpen)
                    {
                        await channel.BasicAckAsync(ea.DeliveryTag, false, stoppingToken);
                        _logger.LogInformation("✅ Mensaje confirmado (Ack).");
                    }
                }
                else
                {
                    _logger.LogWarning("Mensaje vacío.");
                    if (channel.IsOpen) await channel.BasicAckAsync(ea.DeliveryTag, false, stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "⚠️ Error procesando. Enviando Nack.");
                if (channel.IsOpen)
                {
                    // Requeue=false para borrar el mensaje malo
                    await channel.BasicNackAsync(ea.DeliveryTag, false, false, stoppingToken);
                }
            }
        };

        await channel.BasicConsumeAsync(queue: queueName, autoAck: false, consumer: consumer, cancellationToken: stoppingToken);
        
        // Mantener vivo por siempre
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }
}