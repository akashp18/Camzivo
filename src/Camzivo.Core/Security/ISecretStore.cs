namespace Camzivo.Core.Security;

/// <summary>
/// Stores device passwords outside the database. The device row only keeps a reference (<c>secret_ref</c>), so a
/// database copy — a backup, a support bundle, a stolen profile — never contains a usable credential.
///
/// Implementations must be per-user and must not throw for "not found": a missing secret degrades the device to
/// "needs credentials" rather than failing startup.
/// </summary>
public interface ISecretStore
{
    /// <summary>Saves (or replaces) a secret and returns the reference to persist on the device.</summary>
    string Save(Guid deviceId, string secret);

    /// <summary>Returns the secret, or null when it was never stored or has been removed.</summary>
    string? Load(string secretReference);

    /// <summary>Removes the secret; safe to call when it does not exist.</summary>
    bool Delete(string secretReference);

    static string ReferenceFor(Guid deviceId) => $"device-{deviceId:N}";
}
