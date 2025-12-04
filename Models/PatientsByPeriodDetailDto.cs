namespace EPApi.Models
{
    public sealed class PatientsByPeriodDetailDto
    {
        public DateTime Date { get; set; }
        public Guid PatientId { get; set; }
        public string FirstName { get; set; } = "";
        public string LastName1 { get; set; } = "";
        public string? LastName2 { get; set; }
        public string? IdentificationNumber { get; set; }
        public int ContactsCount { get; set; }
    }

    public sealed class PatientsByPeriodBucketDto
    {
        public DateTime Date { get; set; }      // mismo bucket que arriba
        public int PatientsCount { get; set; }  // pacientes únicos ese día
    }

    public sealed class PatientsByPeriodStatsDto
    {
        public int TotalUniquePatients { get; set; }
        public int TotalContacts { get; set; }   // suma de ContactsCount

        public List<PatientsByPeriodBucketDto> Series { get; set; } = new();
        public List<PatientsByPeriodDetailDto> Details { get; set; } = new();
    }
}
