namespace EnvyGuard.Agent.Models;

public class PcCommand
{
    public string Action { get; set; } = string.Empty;

    // Parámetros extra (ej: lista de webs a bloquear)
    public string? Parameters { get; set; }

    // IMPORTANTE: La IP a la que nos vamos a conectar por SSH
    // El backend Java debe llenar esto.
    public string TargetIp { get; set; } = string.Empty; 
    
    // NUEVO: Necesario SOLO para encender (WOL)
    public string? MacAddress { get; set; } 
}