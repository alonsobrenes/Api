# Billing · Entitlements y Planes

Este documento resume los **entitlements** activos del producto y cómo se aplican por **plan** (solo / clínica / pro). Sirve como referencia funcional y técnica para backend y frontend.

---

## Entitlements activos

- `ai.opinion.monthly` — Límite mensual de opiniones generadas por IA.
- `tests.auto.monthly` — Límite mensual de tests automáticos.
- `sacks.monthly` — Límite mensual de SACKs.
- `seats` — Cantidad de usuarios/miembros.
- `storage.gb` — Almacenamiento (GB) para **adjuntos** de pacientes (no mensual).

> **Nota:** Los entitlements se guardan por organización en la tabla `entitlements`. El servicio los consulta vía `GetEntitlementLimitAsync(orgId, code)`.

---

## Valores por plan (simulados actuales)

| Plan    | ai.opinion.monthly | tests.auto.monthly | sacks.monthly | seats | storage.gb |
|---------|---------------------|--------------------|---------------|-------|------------|
| solo    | 50                  | 20                 | 5             | 1     | 10         |
| clinic  | 200                 | 100                | 20            | 5     | 50         |
| pro     | 1000                | 500                | 100           | 20    | 200        |

> Estos valores viven en el **diccionario** que arma el `BillingController` (checkout simulado / webhook) y se aplican a través del `BillingOrchestrator` a la tabla `entitlements`.

---

## Pipeline de aplicación

1. **Checkout / Webhook** determinan `plan_code` (solo/clinic/pro).
2. Se arma el mapa `{ entitlement_code -> valor }` según el plan.
3. `BillingOrchestrator.ApplySubscriptionAndEntitlementsAsync(...)`:
   - Actualiza `subscriptions` (plan/status/ventana).
   - **Upsert** en `entitlements` con los límites vigentes.

> La idempotencia del webhook se garantiza en `webhook_idempotency`. Firma HMAC verificada en `BillingController`.

---

## Consultas rápidas (SQL)

### Ver entitlements vigentes de una organización
```sql
DECLARE @OrgId UNIQUEIDENTIFIER = '00000000-0000-0000-0000-000000000000';

SELECT *
FROM dbo.entitlements
WHERE org_id = @OrgId
ORDER BY /* usa la columna correcta */ code /* ó feature_code */;
