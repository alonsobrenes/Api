using System;

namespace EPApi.Services.Storage
{
    /// <summary>
    /// Helper centralizado para construir rutas relativas (storage_key)
    /// dentro del contenedor de archivos (Blob / Azurite / FileSystem).
    /// 
    /// Todas las rutas devueltas usan '/' como separador.
    /// </summary>
    public static class StoragePathHelper
    {
        /// <summary>
        /// Adjuntos de paciente que cuentan contra la cuota de almacenamiento.
        ///   org/{orgIdN}/patient/{patientIdN}/{fileIdN}
        /// </summary>
        public static string GetPatientAttachmentPath(Guid orgId, Guid patientId, Guid fileId)
        {
            var orgIdN = orgId.ToString("N");
            var patientIdN = patientId.ToString("N");
            var fileIdN = fileId.ToString("N");
            return $"org/{orgIdN}/patient/{patientIdN}/{fileIdN}";
        }

        /// <summary>
        /// Audio de entrevista (también cuenta contra la cuota).
        ///   org/{orgIdN}/interviews/{interviewIdN}/audio.{ext}
        /// </summary>
        public static string GetInterviewAudioPath(Guid orgId, Guid interviewId, string extensionWithoutDot)
        {
            var orgIdN = orgId.ToString("N");
            var interviewIdN = interviewId.ToString("N");
            var ext = string.IsNullOrWhiteSpace(extensionWithoutDot)
                ? "dat"
                : extensionWithoutDot.Trim().TrimStart('.').ToLowerInvariant();

            return $"org/{orgIdN}/interviews/{interviewIdN}/audio.{ext}";
        }

        /// <summary>
        /// Firma manuscrita de consentimiento (core, sin cuota).
        ///   core/consents/org/{orgIdN}/patient/{patientIdN}/{consentIdN}/signature.png
        /// </summary>
        public static string GetConsentSignaturePath(Guid orgId, Guid patientId, Guid consentId)
        {
            var orgIdN = orgId.ToString("N");
            var patientIdN = patientId.ToString("N");
            var consentIdN = consentId.ToString("N");
            return $"core/consents/org/{orgIdN}/patient/{patientIdN}/{consentIdN}/signature.png";
        }

        /// <summary>
        /// PDF del consentimiento (core, sin cuota).
        ///   core/consents/org/{orgIdN}/patient/{patientIdN}/{consentIdN}/consent.pdf
        /// </summary>
        public static string GetConsentPdfPath(Guid orgId, Guid patientId, Guid consentId)
        {
            var orgIdN = orgId.ToString("N");
            var patientIdN = patientId.ToString("N");
            var consentIdN = consentId.ToString("N");
            return $"core/consents/org/{orgIdN}/patient/{patientIdN}/{consentIdN}/consent.pdf";
        }

        /// <summary>
        /// Avatar de usuario (core, sin cuota).
        ///   core/avatars/user/{userId}.png
        /// Ajustamos a int porque userId en tu sistema suele ser int.
        /// </summary>
        public static string GetUserAvatarPath(int userId, string? extensionWithoutDot)
        {
            var ext = string.IsNullOrWhiteSpace(extensionWithoutDot)
                ? "png"
                : extensionWithoutDot.Trim().TrimStart('.').ToLowerInvariant();

            return $"core/avatars/user/{userId}.{ext}";
        }

        /// <summary>
        /// Adjuntos de tickets de soporte (core, sin cuota).
        ///   support/tickets/{ticketIdN}/{fileIdN}
        /// </summary>
        public static string GetSupportTicketAttachmentPath(Guid ticketId, Guid fileId)
        {
            var ticketIdN = ticketId.ToString("N");
            var fileIdN = fileId.ToString("N");
            return $"support/tickets/{ticketIdN}/{fileIdN}";
        }
    }
}
