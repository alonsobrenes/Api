namespace EPApi.Models
{
    public sealed class LogAutoAttemptDto
    {
        public Guid TestId { get; set; }
        public Guid? PatientId { get; set; }   // puede ser null (por si algún flujo no lo pasa)
        public DateTime? StartedAtUtc { get; set; } // opcional; si llega lo usamos como started_at
    }
}
