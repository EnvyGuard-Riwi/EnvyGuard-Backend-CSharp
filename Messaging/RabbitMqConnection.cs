using RabbitMQ.Client;

namespace EnvyGuard.Agent.Messaging;

public class RabbitMqConnection : IDisposable
{
    private readonly ConnectionFactory _factory;
    private IConnection? _connection;
    private readonly IConfiguration _config;

    public RabbitMqConnection(IConfiguration config)
    {
        _config = config;
        _factory = new ConnectionFactory
        {
            HostName = config["RabbitMQ:HostName"] ?? "localhost",
            UserName = config["RabbitMQ:UserName"] ?? "guest",
            Password = config["RabbitMQ:Password"] ?? "guest",
            VirtualHost = config["RabbitMQ:VirtualHost"] ?? "/"
        };
    }

    public async Task<IConnection> GetConnectionAsync(CancellationToken token = default)
    {
        if (_connection == null || !_connection.IsOpen)
        {
            _connection = await _factory.CreateConnectionAsync(token);
        }
        return _connection;
    }

    public void Dispose()
    {
        _connection?.Dispose();
    }
}