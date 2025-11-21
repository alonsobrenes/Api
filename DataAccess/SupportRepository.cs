using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using static EPApi.DataAccess.ISupportRepository;

namespace EPApi.DataAccess
{
    public class SupportRepository : ISupportRepository
    {
        private readonly string _cs;

        public SupportRepository(IConfiguration cfg)
        {
            _cs = cfg.GetConnectionString("Default") ?? throw new InvalidOperationException("Missing ConnectionStrings:Default");
        }

        public async Task<Guid> CreateTicketAsync(int userId, Guid? orgId, string subject, string description, string? category, string? priority, CancellationToken ct = default)
        {
            await using var conn = new SqlConnection(_cs);
            await conn.OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            try
            {
                Guid ticketId;
                await using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = (SqlTransaction)tx;
                    cmd.CommandText = @"
INSERT INTO dbo.support_tickets (user_id, org_id, subject, description, category, priority)
OUTPUT inserted.id
VALUES (@uid, @org, @subj, @desc, @cat, @prio);";
                    cmd.Parameters.Add(new SqlParameter("@uid", SqlDbType.Int) { Value = userId });
                    cmd.Parameters.Add(new SqlParameter("@org", SqlDbType.UniqueIdentifier) { Value = (object?)orgId ?? DBNull.Value });
                    cmd.Parameters.Add(new SqlParameter("@subj", SqlDbType.NVarChar, 200) { Value = subject });
                    cmd.Parameters.Add(new SqlParameter("@desc", SqlDbType.NVarChar, -1) { Value = description });
                    cmd.Parameters.Add(new SqlParameter("@cat", SqlDbType.NVarChar, 50) { Value = (object?)category ?? DBNull.Value });
                    cmd.Parameters.Add(new SqlParameter("@prio", SqlDbType.NVarChar, 20) { Value = (object?)priority ?? DBNull.Value });

                    var obj = await cmd.ExecuteScalarAsync(ct);
                    ticketId = (Guid)obj!;
                }

                // Primer mensaje (del usuario)
                await using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = (SqlTransaction)tx;
                    cmd.CommandText = @"
INSERT INTO dbo.support_messages (ticket_id, sender_user_id, body)
VALUES (@tid, @uid, @body);";
                    cmd.Parameters.Add(new SqlParameter("@tid", SqlDbType.UniqueIdentifier) { Value = ticketId });
                    cmd.Parameters.Add(new SqlParameter("@uid", SqlDbType.Int) { Value = userId });
                    cmd.Parameters.Add(new SqlParameter("@body", SqlDbType.NVarChar, -1) { Value = description });
                    await cmd.ExecuteNonQueryAsync(ct);
                }

                await tx.CommitAsync(ct);
                return ticketId;
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        }

        public async Task<IReadOnlyList<MyTicketRow>> GetMyTicketsAsync(int userId, int top = 50, string? status = null, string? q = null, CancellationToken ct = default)
        {
            var list = new List<MyTicketRow>(Math.Clamp(top, 1, 500));

            await using var conn = new SqlConnection(_cs);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();

            // Buscamos últimas actividades con LEFT JOIN a messages y MAX(created_at_utc)
            cmd.CommandText = $@"
SELECT TOP (@top)
  t.id,
  t.subject,
  t.status,
  t.priority,
  t.category,
  t.created_at_utc,
  t.updated_at_utc,
  MAX(m.created_at_utc) AS last_message_at_utc
FROM dbo.support_tickets AS t
LEFT JOIN dbo.support_messages AS m ON m.ticket_id = t.id
WHERE t.user_id = @uid
  {(string.IsNullOrWhiteSpace(status) ? "" : "AND t.status = @st")}
  {(string.IsNullOrWhiteSpace(q) ? "" : "AND (t.subject LIKE @q OR t.description LIKE @q)")}
GROUP BY t.id, t.subject, t.status, t.priority, t.category, t.created_at_utc, t.updated_at_utc
ORDER BY COALESCE(MAX(m.created_at_utc), t.updated_at_utc, t.created_at_utc) DESC;
";
            cmd.Parameters.Add(new SqlParameter("@top", SqlDbType.Int) { Value = Math.Clamp(top, 1, 500) });
            cmd.Parameters.Add(new SqlParameter("@uid", SqlDbType.Int) { Value = userId });
            if (!string.IsNullOrWhiteSpace(status))
                cmd.Parameters.Add(new SqlParameter("@st", SqlDbType.NVarChar, 20) { Value = status });
            if (!string.IsNullOrWhiteSpace(q))
                cmd.Parameters.Add(new SqlParameter("@q", SqlDbType.NVarChar, 210) { Value = $"%{q}%" });

            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                list.Add(new MyTicketRow
                {
                    Id = rd.GetGuid(0),
                    Subject = rd.GetString(1),
                    Status = rd.GetString(2),
                    Priority = rd.IsDBNull(3) ? null : rd.GetString(3),
                    Category = rd.IsDBNull(4) ? null : rd.GetString(4),
                    CreatedAtUtc = rd.GetDateTime(5),
                    UpdatedAtUtc = rd.IsDBNull(6) ? null : rd.GetDateTime(6),
                    LastMessageAtUtc = rd.IsDBNull(7) ? null : rd.GetDateTime(7),
                });
            }
            return list;
        }

        public async Task<TicketWithMessages?> GetTicketWithMessagesAsync(Guid id, int userId, CancellationToken ct = default)
        {
            await using var conn = new SqlConnection(_cs);
            await conn.OpenAsync(ct);

            // Primero, confirmamos que el ticket pertenece al usuario
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, subject, status, priority, category, created_at_utc, updated_at_utc FROM dbo.support_tickets WHERE id=@id AND user_id=@uid;";
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id });
            cmd.Parameters.Add(new SqlParameter("@uid", SqlDbType.Int) { Value = userId });

            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return null;

            var ticket = new TicketWithMessages
            {
                Id = rd.GetGuid(0),
                Subject = rd.GetString(1),
                Status = rd.GetString(2),
                Priority = rd.IsDBNull(3) ? null : rd.GetString(3),
                Category = rd.IsDBNull(4) ? null : rd.GetString(4),
                CreatedAtUtc = rd.GetDateTime(5),
                UpdatedAtUtc = rd.IsDBNull(6) ? null : rd.GetDateTime(6),
                Messages = new List<TicketMessage>()
            };
            await rd.CloseAsync();

            // Cargar mensajes
            await using var cmd2 = conn.CreateCommand();
            cmd2.CommandText = @"
SELECT m.id, m.sender_user_id, m.body, m.created_at_utc, m.is_internal
FROM dbo.support_messages m
WHERE m.ticket_id = @tid
ORDER BY m.created_at_utc ASC;";
            cmd2.Parameters.Add(new SqlParameter("@tid", SqlDbType.UniqueIdentifier) { Value = id });

            await using var rd2 = await cmd2.ExecuteReaderAsync(ct);
            while (await rd2.ReadAsync(ct))
            {
                ticket.Messages.Add(new TicketMessage
                {
                    Id = rd2.GetGuid(0),
                    SenderUserId = rd2.GetInt32(1),
                    Body = rd2.GetString(2),
                    CreatedAtUtc = rd2.GetDateTime(3),
                    IsInternal = rd2.GetBoolean(4)
                });
            }
            return ticket;
        }

        public async Task AddMessageAsync(Guid ticketId, int senderUserId, string body, CancellationToken ct = default)
        {
            await using var conn = new SqlConnection(_cs);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO dbo.support_messages (ticket_id, sender_user_id, body)
VALUES (@tid, @uid, @body);";
            cmd.Parameters.Add(new SqlParameter("@tid", SqlDbType.UniqueIdentifier) { Value = ticketId });
            cmd.Parameters.Add(new SqlParameter("@uid", SqlDbType.Int) { Value = senderUserId });
            cmd.Parameters.Add(new SqlParameter("@body", SqlDbType.NVarChar, -1) { Value = body });
            await cmd.ExecuteNonQueryAsync(ct);

            // Actualizar updated_at del ticket
            await using var cmd2 = conn.CreateCommand();
            cmd2.CommandText = "UPDATE dbo.support_tickets SET updated_at_utc = SYSUTCDATETIME() WHERE id=@tid;";
            cmd2.Parameters.Add(new SqlParameter("@tid", SqlDbType.UniqueIdentifier) { Value = ticketId });
            await cmd2.ExecuteNonQueryAsync(ct);
        }

        public async Task<IReadOnlyList<AdminTicketRow>> AdminListTicketsAsync(
    int top = 100,
    string? status = null,
    int? assignedToUserId = null,
    int? userId = null,
    string? category = null,
    string? priority = null,
    DateTime? createdFromUtc = null,
    DateTime? createdToUtc = null,
    string? q = null,
    CancellationToken ct = default)
        {
            var list = new List<AdminTicketRow>(Math.Clamp(top, 1, 500));
            await using var conn = new SqlConnection(_cs);
            await conn.OpenAsync(ct);

            var where = new List<string>();
            if (!string.IsNullOrWhiteSpace(status)) where.Add("t.status = @st");
            if (assignedToUserId.HasValue) where.Add("t.assigned_to_user_id = @ass");
            if (userId.HasValue) where.Add("t.user_id = @uid");
            if (!string.IsNullOrWhiteSpace(category)) where.Add("t.category = @cat");
            if (!string.IsNullOrWhiteSpace(priority)) where.Add("t.priority = @prio");
            if (createdFromUtc.HasValue) where.Add("t.created_at_utc >= @from");
            if (createdToUtc.HasValue) where.Add("t.created_at_utc < @to");
            if (!string.IsNullOrWhiteSpace(q)) where.Add("(t.subject LIKE @q OR t.description LIKE @q)");

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
SELECT TOP (@top)
  t.id, t.user_id, t.org_id, t.subject, t.status, t.priority, t.category, t.assigned_to_user_id,
  t.created_at_utc, t.updated_at_utc,
  MAX(m.created_at_utc) AS last_message_at_utc,  
    u.email         AS opened_by_email,
    b.trade_name    AS opened_by_org_trade_name,
    b.legal_name    AS opened_by_org_legal_name
FROM dbo.support_tickets AS t
INNER JOIN dbo.users u
    ON u.id = t.user_id
INNER JOIN dbo.org_members om
	ON u.id = om.user_id
INNER JOIN org_billing_profiles b
	ON om.org_id = b.org_id
LEFT JOIN dbo.support_messages AS m ON m.ticket_id = t.id
{(where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "")}
GROUP BY t.id, t.user_id, t.org_id, t.subject, t.status, t.priority, t.category, t.assigned_to_user_id, t.created_at_utc, t.updated_at_utc, u.email, b.trade_name, b.legal_name
ORDER BY COALESCE(MAX(m.created_at_utc), t.updated_at_utc, t.created_at_utc) DESC;";
            cmd.Parameters.Add(new SqlParameter("@top", SqlDbType.Int) { Value = Math.Clamp(top, 1, 500) });
            if (!string.IsNullOrWhiteSpace(status)) cmd.Parameters.Add(new SqlParameter("@st", SqlDbType.NVarChar, 20) { Value = status });
            if (assignedToUserId.HasValue) cmd.Parameters.Add(new SqlParameter("@ass", SqlDbType.Int) { Value = assignedToUserId.Value });
            if (userId.HasValue) cmd.Parameters.Add(new SqlParameter("@uid", SqlDbType.Int) { Value = userId.Value });
            if (!string.IsNullOrWhiteSpace(category)) cmd.Parameters.Add(new SqlParameter("@cat", SqlDbType.NVarChar, 50) { Value = category });
            if (!string.IsNullOrWhiteSpace(priority)) cmd.Parameters.Add(new SqlParameter("@prio", SqlDbType.NVarChar, 20) { Value = priority });
            if (createdFromUtc.HasValue) cmd.Parameters.Add(new SqlParameter("@from", SqlDbType.DateTime2) { Value = createdFromUtc.Value });
            if (createdToUtc.HasValue) cmd.Parameters.Add(new SqlParameter("@to", SqlDbType.DateTime2) { Value = createdToUtc.Value });
            if (!string.IsNullOrWhiteSpace(q)) cmd.Parameters.Add(new SqlParameter("@q", SqlDbType.NVarChar, 210) { Value = $"%{q}%" });
            
            await using var rd = await cmd.ExecuteReaderAsync(ct);

            while (await rd.ReadAsync(ct))
            {
                list.Add(new AdminTicketRow
                {
                    Id = rd.GetGuid(0),
                    UserId = rd.GetInt32(1),
                    OrgId = rd.IsDBNull(2) ? null : rd.GetGuid(2),
                    Subject = rd.GetString(3),
                    Status = rd.GetString(4),
                    Priority = rd.IsDBNull(5) ? null : rd.GetString(5),
                    Category = rd.IsDBNull(6) ? null : rd.GetString(6),
                    AssignedToUserId = rd.IsDBNull(7) ? null : rd.GetInt32(7),
                    CreatedAtUtc = rd.GetDateTime(8),
                    UpdatedAtUtc = rd.IsDBNull(9) ? null : rd.GetDateTime(9),
                    LastMessageAtUtc = rd.IsDBNull(10) ? null : rd.GetDateTime(10),
                    OpenedBy = new TicketOpenedBy
                    {
                        Email = rd.GetString("opened_by_email"),
                        OrgTradeName = rd.GetString("opened_by_org_trade_name"),
                        OrgLegalName = rd.GetString("opened_by_org_legal_name")
                    }

                });
            }
            
            return list;
        }

        public async Task<TicketWithMessages?> AdminGetTicketWithMessagesAsync(Guid id, CancellationToken ct = default)
        {
            await using var conn = new SqlConnection(_cs);
            await conn.OpenAsync(ct);

            // Datos del ticket (sin restricción por user_id)
            TicketWithMessages? ticket = null;
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $@"
SELECT 
    t.id,
    t.subject,
    t.status,
    t.priority,
    t.category,
    t.created_at_utc,
    t.updated_at_utc,
    u.id            AS opened_by_user_id,
    u.email         AS opened_by_email,
    b.trade_name    AS opened_by_org_trade_name,
    b.legal_name    AS opened_by_org_legal_name
FROM support_tickets t
INNER JOIN dbo.users u
    ON u.id = t.user_id
INNER JOIN dbo.org_members m
	ON u.id = m.user_id
INNER JOIN org_billing_profiles b
	ON m.org_id = b.org_id
WHERE t.id = @id;";

                //cmd.CommandText = "SELECT id, subject, status, priority, category, created_at_utc, updated_at_utc FROM dbo.support_tickets WHERE id=@id;";
                cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id });
                await using var rd = await cmd.ExecuteReaderAsync(ct);
                if (await rd.ReadAsync(ct))
                {
                    ticket = new TicketWithMessages
                    {
                        Id = rd.GetGuid(0),
                        Subject = rd.GetString(1),
                        Status = rd.GetString(2),
                        Priority = rd.IsDBNull(3) ? null : rd.GetString(3),
                        Category = rd.IsDBNull(4) ? null : rd.GetString(4),
                        CreatedAtUtc = rd.GetDateTime(5),
                        UpdatedAtUtc = rd.IsDBNull(6) ? null : rd.GetDateTime(6),
                        Messages = new List<TicketMessage>(),
                        OpenedBy = new TicketOpenedBy
                        {
                            Email = rd.GetString("opened_by_email"),
                            OrgTradeName = rd.GetString("opened_by_org_trade_name"),
                            OrgLegalName = rd.GetString("opened_by_org_legal_name")
                        }
                    };
                    

                }
            }
            if (ticket is null) return null;

            // Mensajes (se incluyen internos; el filtro de visibilidad es del lado controller)
            await using (var cmd2 = conn.CreateCommand())
            {
                cmd2.CommandText = @"
SELECT id, sender_user_id, body, created_at_utc, is_internal
FROM dbo.support_messages
WHERE ticket_id = @tid
ORDER BY created_at_utc ASC;";
                cmd2.Parameters.Add(new SqlParameter("@tid", SqlDbType.UniqueIdentifier) { Value = id });
                await using var rd2 = await cmd2.ExecuteReaderAsync(ct);
                while (await rd2.ReadAsync(ct))
                {
                    ticket.Messages.Add(new TicketMessage
                    {
                        Id = rd2.GetGuid(0),
                        SenderUserId = rd2.GetInt32(1),
                        Body = rd2.GetString(2),
                        CreatedAtUtc = rd2.GetDateTime(3),
                        IsInternal = rd2.GetBoolean(4)
                    });
                }
            }
            return ticket;
        }

        public async Task AddAdminMessageAsync(Guid ticketId, int adminUserId, string body, bool isInternal, CancellationToken ct = default)
        {
            await using var conn = new SqlConnection(_cs);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO dbo.support_messages (ticket_id, sender_user_id, body, is_internal)
VALUES (@tid, @uid, @body, @int);";
            cmd.Parameters.Add(new SqlParameter("@tid", SqlDbType.UniqueIdentifier) { Value = ticketId });
            cmd.Parameters.Add(new SqlParameter("@uid", SqlDbType.Int) { Value = adminUserId });
            cmd.Parameters.Add(new SqlParameter("@body", SqlDbType.NVarChar, -1) { Value = body });
            cmd.Parameters.Add(new SqlParameter("@int", SqlDbType.Bit) { Value = isInternal });
            await cmd.ExecuteNonQueryAsync(ct);

            await using var cmd2 = conn.CreateCommand();
            cmd2.CommandText = "UPDATE dbo.support_tickets SET updated_at_utc = SYSUTCDATETIME() WHERE id=@tid;";
            cmd2.Parameters.Add(new SqlParameter("@tid", SqlDbType.UniqueIdentifier) { Value = ticketId });
            await cmd2.ExecuteNonQueryAsync(ct);
        }

        public async Task UpdateTicketAsync(Guid id, string? status, int? assignedToUserId, CancellationToken ct = default)
        {
            await using var conn = new SqlConnection(_cs);
            await conn.OpenAsync(ct);

            var sets = new List<string> { "updated_at_utc = SYSUTCDATETIME()" };
            await using var cmd = conn.CreateCommand();

            if (!string.IsNullOrWhiteSpace(status))
            {
                // Normalizamos el status para compararlo
                var normalizedStatus = status.Trim().ToLowerInvariant();

                // Actualizar status
                sets.Add("status = @st");
                cmd.Parameters.Add(new SqlParameter("@st", SqlDbType.NVarChar, 20) { Value = status });

                // Manejo de closed_at_utc según el nuevo estado
                if (normalizedStatus == "closed")
                {
                    // Se está cerrando el ticket (o manteniendo cerrado)
                    sets.Add("closed_at_utc = SYSUTCDATETIME()");
                }
                else
                {
                    // Se está reabriendo o cambiando a otro estado
                    sets.Add("closed_at_utc = NULL");
                }
            }

            // Permitir asignar o desasignar (NULL)
            if (assignedToUserId.HasValue)
                sets.Add("assigned_to_user_id = @ass");
            else if (assignedToUserId == null)
                sets.Add("assigned_to_user_id = NULL");

            if (assignedToUserId.HasValue)
                cmd.Parameters.Add(new SqlParameter("@ass", SqlDbType.Int) { Value = assignedToUserId.Value });

            cmd.CommandText = $"UPDATE dbo.support_tickets SET {string.Join(", ", sets)} WHERE id = @id;";
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id });

            await cmd.ExecuteNonQueryAsync(ct);
        }

        public async Task<int?> GetTicketOwnerUserIdAsync(Guid ticketId, CancellationToken ct = default)
        {
            await using var conn = new SqlConnection(_cs);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT user_id FROM dbo.support_tickets WHERE id=@id;";
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = ticketId });
            var obj = await cmd.ExecuteScalarAsync(ct);
            if (obj == null || obj == DBNull.Value) return null;
            return (int)obj;
        }

        public async Task<bool> UpdateTicketStatusByOwnerAsync(Guid ticketId, int ownerUserId, string status, CancellationToken ct = default)
        {
            await using var conn = new SqlConnection(_cs);
            await conn.OpenAsync(ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
UPDATE dbo.support_tickets
SET status = @status,
    updated_at_utc = SYSUTCDATETIME()
WHERE id = @id
  AND user_id = @uid
  AND status <> @status;";

            cmd.Parameters.Add(new SqlParameter("@status", SqlDbType.NVarChar, 32) { Value = status });
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = ticketId });
            cmd.Parameters.Add(new SqlParameter("@uid", SqlDbType.Int) { Value = ownerUserId });

            var rows = await cmd.ExecuteNonQueryAsync(ct);
            return rows > 0;
        }

        public async Task<int> CountTicketsCreatedSinceAsync(int userId, DateTime sinceUtc, CancellationToken ct = default)
        {
            await using var conn = new SqlConnection(_cs);
            await conn.OpenAsync(ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT COUNT(*)
FROM dbo.support_tickets
WHERE user_id = @uid
  AND created_at_utc >= @since;";
            cmd.Parameters.Add(new SqlParameter("@uid", SqlDbType.Int) { Value = userId });
            cmd.Parameters.Add(new SqlParameter("@since", SqlDbType.DateTime2) { Value = sinceUtc });

            var obj = await cmd.ExecuteScalarAsync(ct);
            return (obj is int i) ? i : Convert.ToInt32(obj);
        }

        public async Task<IReadOnlyList<OrgTicketRow>> GetOrgTicketsForOrgAsync(Guid orgId, int top = 100, CancellationToken ct = default)
        {
            var list = new List<OrgTicketRow>(Math.Clamp(top, 1, 500));

            await using var conn = new SqlConnection(_cs);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();

            cmd.CommandText = @"
SELECT TOP (@top)
    t.id,
    t.user_id,                       -- dueño del ticket
    t.subject,
    t.status,
    t.priority,
    t.category,
    t.created_at_utc,
    t.updated_at_utc,
    MAX(m.created_at_utc) AS last_message_at_utc,
    b.legal_name,
    b.trade_name,
    u.email
FROM dbo.support_tickets AS t
INNER JOIN dbo.users AS u
    ON u.id = t.user_id
INNER JOIN dbo.org_members om
    ON u.id = om.user_id
INNER JOIN org_billing_profiles b
    ON om.org_id = b.org_id
LEFT JOIN dbo.support_messages AS m
    ON m.ticket_id = t.id
WHERE t.org_id = @org
GROUP BY
    t.id,
    t.user_id,
    t.subject,
    t.status,
    t.priority,
    t.category,
    t.created_at_utc,
    t.updated_at_utc,
    b.legal_name,
    b.trade_name,
    u.email
ORDER BY COALESCE(MAX(m.created_at_utc), t.updated_at_utc, t.created_at_utc) DESC;";

            cmd.Parameters.Add(new SqlParameter("@top", SqlDbType.Int) { Value = Math.Clamp(top, 1, 500) });
            cmd.Parameters.Add(new SqlParameter("@org", SqlDbType.UniqueIdentifier) { Value = orgId });

            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                var id = rd.GetGuid(0);
                var userId = rd.GetInt32(1);
                var subject = rd.IsDBNull(2) ? "" : rd.GetString(2);
                var status = rd.IsDBNull(3) ? "" : rd.GetString(3);
                var priority = rd.IsDBNull(4) ? null : rd.GetString(4);
                var category = rd.IsDBNull(5) ? null : rd.GetString(5);
                var createdAtUtc = rd.GetDateTime(6);
                var updatedAtUtc = rd.IsDBNull(7) ? (DateTime?)null : rd.GetDateTime(7);
                var lastMsgAtUtc = rd.IsDBNull(8) ? (DateTime?)null : rd.GetDateTime(8);

                var legalName = rd.IsDBNull(9) ? "" : rd.GetString(9);
                var tradeName = rd.IsDBNull(10) ? "" : rd.GetString(10);
                var email = rd.IsDBNull(11) ? "" : rd.GetString(11);
                
                list.Add(new OrgTicketRow
                {
                    Id = id,
                    Subject = subject,
                    Status = status,
                    Priority = priority,
                    Category = category,
                    CreatedAtUtc = createdAtUtc,
                    UpdatedAtUtc = updatedAtUtc,
                    LastMessageAtUtc = lastMsgAtUtc,
                    CreatedByUserId = userId,
                    CreatedByName = legalName,
                    CreatedByEmail = email
                });
            }

            return list;
        }

        public async Task<TicketWithMessages?> GetTicketWithMessagesForOrgAsync(Guid id, Guid orgId, CancellationToken ct = default)
        {
            await using var conn = new SqlConnection(_cs);
            await conn.OpenAsync(ct);

            // Confirmamos que el ticket pertenece a la organización
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT id, subject, status, priority, category, created_at_utc, updated_at_utc
FROM dbo.support_tickets
WHERE id = @id AND org_id = @org;";
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id });
            cmd.Parameters.Add(new SqlParameter("@org", SqlDbType.UniqueIdentifier) { Value = orgId });

            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return null;

            var ticket = new TicketWithMessages
            {
                Id = rd.GetGuid(0),
                Subject = rd.GetString(1),
                Status = rd.GetString(2),
                Priority = rd.IsDBNull(3) ? null : rd.GetString(3),
                Category = rd.IsDBNull(4) ? null : rd.GetString(4),
                CreatedAtUtc = rd.GetDateTime(5),
                UpdatedAtUtc = rd.IsDBNull(6) ? (DateTime?)null : rd.GetDateTime(6),
                Messages = new List<TicketMessage>()
            };
            await rd.CloseAsync();

            // Cargar mensajes
            await using var cmd2 = conn.CreateCommand();
            cmd2.CommandText = @"
SELECT m.id, m.sender_user_id, m.body, m.created_at_utc, m.is_internal
FROM dbo.support_messages m
WHERE m.ticket_id = @tid
ORDER BY m.created_at_utc ASC;";
            cmd2.Parameters.Add(new SqlParameter("@tid", SqlDbType.UniqueIdentifier) { Value = id });

            await using var rd2 = await cmd2.ExecuteReaderAsync(ct);
            while (await rd2.ReadAsync(ct))
            {
                ticket.Messages.Add(new TicketMessage
                {
                    Id = rd2.GetGuid(0),
                    SenderUserId = rd2.GetInt32(1),
                    Body = rd2.GetString(2),
                    CreatedAtUtc = rd2.GetDateTime(3),
                    IsInternal = rd2.GetBoolean(4)
                });
            }

            return ticket;
        }

        public Task<TicketWithMessages?> GetTicketWithMessagesAnyUserAsync(Guid id, int userId,CancellationToken ct = default)
        {
            return GetTicketWithMessagesAsync(id, userId, ct);
        }


        public sealed class TicketWithMessages
        {
            public Guid Id { get; init; }
            public string Subject { get; init; } = "";
            public string Status { get; init; } = "open";
            public string? Priority { get; init; }
            public string? Category { get; init; }
            public DateTime CreatedAtUtc { get; init; }
            public DateTime? UpdatedAtUtc { get; init; }
            public List<TicketMessage> Messages { get; init; } = new();
            public TicketOpenedBy? OpenedBy { get; init; }
        }

        public sealed class TicketOpenedBy
        {
            public string Email { get; set; } = "";
            public string OrgTradeName { get; set; } = "";
            public string OrgLegalName { get; set; } = "";
        }


        public sealed class TicketMessage
        {
            public Guid Id { get; init; }
            public int SenderUserId { get; init; }
            public string Body { get; init; } = "";
            public DateTime CreatedAtUtc { get; init; }
            public bool IsInternal { get; init; }
        }

    }
}
