using EPApi.Models;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace EPApi.DataAccess
{
    public interface IPatientConsentsRepository
    {
        /// <summary>
        /// Obtiene el último consentimiento firmado para un paciente y un tipo de consentimiento (o null si no hay).
        /// </summary>
        Task<PatientConsentDto?> GetLatestAsync(
            Guid patientId,
            string consentType,
            CancellationToken ct = default);

        /// <summary>
        /// Historial de consentimientos para ese paciente y tipo, ordenados descendentemente por fecha de firma.
        /// </summary>
        Task<IReadOnlyList<PatientConsentDto>> GetHistoryAsync(
            Guid patientId,
            string consentType,
            CancellationToken ct = default);

        /// <summary>
        /// Crea un nuevo registro de consentimiento firmado.
        /// Devuelve el id (GUID) creado.
        /// </summary>
        Task<Guid> CreateAsync(
            Guid patientId,
            int createdByUserId,
            string consentType,
            string consentVersion,
            string? localAddendumCountry,
            string? localAddendumVersion,
            string? countryCode,
            string? language,
            string signedName,
            string? signedIdNumber,
            string signedByRelationship,
            string? signatureUri,
            string? ipAddress,
            string? userAgent,
            string? rawConsentText,
            CancellationToken ct = default);
    }
}
