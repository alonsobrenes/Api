using System;

namespace EPApi.Models
{
    public sealed class PatientConsentDto
    {
        public Guid Id { get; set; }
        public Guid PatientId { get; set; }

        public string ConsentType { get; set; } = "";          // ej. "psychotherapy_general"
        public string ConsentVersion { get; set; } = "";       // ej. "universal_v1_2024-11"

        public string? LocalAddendumCountry { get; set; }      // ISO-2, ej. "CR", "MX"
        public string? LocalAddendumVersion { get; set; }      // ej. "cr_v1_2025-01-10"

        public string? CountryCode { get; set; }               // país del profesional al firmar (ISO-2)
        public string? Language { get; set; }                  // ej. "es", "es-CR"

        public string SignedName { get; set; } = "";
        public string? SignedIdNumber { get; set; }
        public string SignedByRelationship { get; set; } = "paciente";

        public DateTime SignedAtUtc { get; set; }
        public DateTime? RevokedAtUtc { get; set; }

        public string? SignatureUri { get; set; }

        public int CreatedByUserId { get; set; }
        public DateTime CreatedAtUtc { get; set; }

        public string? IpAddress { get; set; }
        public string? UserAgent { get; set; }

        // Snapshot del texto mostrado (universal + addendum local) al momento de la firma
        public string? RawConsentText { get; set; }
    }
}
