namespace EnvyGuard.Agent.Models;

public class PcCommand
{
    public string Action { get; set; } = string.Empty;

    // Parámetros opcionales (ej: mensaje para mostrar al usuario antes de apagar)
    public string? Parameters { get; set; }

    // ID del PC destino (para saber si el mensaje es para mí)
    // Por ahora lo recibiremos, más adelante validaremos si coincide con este PC
    public string TargetPcId { get; set; } = string.Empty;
}