using System.Globalization;
using Camzivo.Core.Security;
using Camzivo.Data.Sqlite;

namespace Camzivo.Data.Repositories;

public sealed record AuditRecord(long Id, DateTimeOffset AtUtc, string Actor, string Action, string Subject, string Detail);

/// <summary>
/// Append-only audit trail for sensitive actions. Detail is redacted on write so a credential can never be
/// captured here even if a caller passes a URL by mistake.
/// </summary>
public sealed class AuditRepository(SqliteConnectionFactory factory)
{
    public async Task<long> AddAsync(
        string action,
        string subject,
        string detail,
        string actor,
        DateTimeOffset atUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO audit_entry (at_utc, actor, action, subject, detail)
            VALUES ($at, $actor, $action, $subject, $detail);
            SELECT last_insert_rowid();
            """;

        command.Parameters.AddWithValue("$at", atUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$actor", actor);
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$subject", CredentialRedactor.Sanitize(subject));
        command.Parameters.AddWithValue("$detail", CredentialRedactor.Sanitize(detail));

        var id = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(id, CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<AuditRecord>> RecentAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 1000);

        await using var connection = factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, at_utc, actor, action, subject, detail
            FROM audit_entry ORDER BY id DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        var results = new List<AuditRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new AuditRecord(
                Id: reader.GetInt64(0),
                AtUtc: DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                Actor: reader.GetString(2),
                Action: reader.GetString(3),
                Subject: reader.GetString(4),
                Detail: reader.GetString(5)));
        }

        return results;
    }
}
