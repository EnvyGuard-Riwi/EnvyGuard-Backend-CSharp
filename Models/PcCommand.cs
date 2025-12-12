namespace EnvyGuard.Agent.Models;

public class PcCommand
{
    public string Action { get; set; } = string.Empty;

    // Parámetros extra (ej: lista de webs a bloquear)
    public string? Parameters { get; set; }

    // La IP a la que nos vamos a conectar
    public string TargetIp { get; set; } = string.Empty; 
    
    // NUEVO: Puerto SSH (Por defecto 22, pero se puede cambiar a 4000 para túneles)
    public int Port { get; set; } = 22;

    // Necesario SOLO para encender (WOL)
    public string? MacAddress { get; set; } 
}