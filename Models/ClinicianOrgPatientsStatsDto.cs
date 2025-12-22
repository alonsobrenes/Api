namespace EPApi.Models
{
    public sealed class ClinicianOrgPatientsStatsDto
    {
        public int ClinicianUserId { get; set; }

        public string FirstName { get; set; } = "";
        public string LastName1 { get; set; } = "";
        public string? LastName2 { get; set; }
        public string Email { get; set; } = "";

        /// <summary>Pacientes únicos atendidos por este profesional en el período.</summary>
        public int UniquePatients { get; set; }

        /// <summary>Total de contactos clínicos (tests + sesiones + entrevistas).</summary>
        public int ContactsCount { get; set; }

        public int TestsCount { get; set; }
        public int SessionsCount { get; set; }
        public int InterviewsCount { get; set; }
    }

    public sealed class ClinicianOrgPatientContactStatsDto
    {
        public int ClinicianUserId { get; set; }
        public string ClinicianEmail { get; set; } = "";
        public string ClinicianFullName { get; set; } = "";

        public Guid PatientId { get; set; }
        public string PatientFirstName { get; set; } = "";
        public string PatientLastName1 { get; set; } = "";
        public string? PatientLastName2 { get; set; }
        public string? PatientEmail { get; set; }
        public string? Date { get; set; }
        public string? IdentificationNumber { get; set; }

        public int ContactsCount { get; set; }
        public int TestsCount { get; set; }
        public int SessionsCount { get; set; }
        public int InterviewsCount { get; set; }
    }

    public sealed class ClinicianOrgPatientsByProfessionalSeriesPointDto
    {
        public DateTime Date { get; set; }

        public int ClinicianUserId { get; set; }
        public string ClinicianEmail { get; set; } = "";
        public string ClinicianFullName { get; set; } = "";
        public int PatientsCount { get; set; }     // únicos por día y profesional
        public int ContactsCount { get; set; }     // total eventos por día y profesional

        public int TestsCount { get; set; }
        public int SessionsCount { get; set; }
        public int InterviewsCount { get; set; }
    }

    public sealed class OrgPatientsByProfessionalStatsResponseDto
    {
        public List<ClinicianOrgPatientContactStatsDto> Details { get; set; } = new();
        public List<ClinicianOrgPatientsByProfessionalSeriesPointDto> Series { get; set; } = new();

        public int TotalContacts { get; set; }
        public int TotalUniquePatients { get; set; }
    }

}
