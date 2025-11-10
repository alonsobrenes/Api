using EPApi.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace EPApi.DataAccess
{
    public interface IPaymentsRepository
    {
        Task<IReadOnlyList<PaymentListItem>> ListByOrgAsync(Guid orgId, int limit, CancellationToken ct);

        // Inserta un pago nuevo (devuelve payment_id)
        Task<Guid> InsertAsync(Guid orgId,
                               string provider,
                               string? providerPaymentId,
                               string? orderNumber,
                               int amountCents,
                               string currencyIso,
                               string status,
                               string? errorCode,
                               string? idempotencyKey,
                               CancellationToken ct);

        // Upsert desde un proveedor: si ya existe por providerPaymentId u orderNumber, actualiza; si no, inserta
        Task<Guid> UpsertFromProviderAsync(Guid orgId,
                                           string provider,
                                           string? providerPaymentId,
                                           string? orderNumber,
                                           int amountCents,
                                           string currencyIso,
                                           string status,
                                           string? errorCode,
                                           string? idempotencyKey,
                                           CancellationToken ct);

        // Agrega un evento "crudo" asociado al payment_id
        Task AppendEventRawAsync(Guid? paymentId,
                                 Guid? orgId,
                                 string eventType,
                                 string rawPayloadJson,
                                 DateTime happenedAtUtc,
                                 CancellationToken ct);
    }
}
