using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Camzivo.Core.Security;

namespace Camzivo.Data.Storage;

/// <summary>
/// Per-user DPAPI credential storage (CryptProtectData). The database only keeps the reference returned by
/// <see cref="Save"/>; the ciphertext lives in its own file under the profile's <c>secrets</c> folder.
///
/// DPAPI with <c>CRYPTPROTECT_UI_FORBIDDEN</c> runs in the caller's user context: another Windows account on the same
/// machine cannot decrypt these files, and Camzivo stores no key of its own. Implemented by P/Invoke rather than the
/// <c>System.Security.Cryptography.ProtectedData</c> package so the data layer keeps its single existing dependency.
/// </summary>
public sealed class DpapiSecretStore : ISecretStore
{
    private const int UiForbidden = 0x1;

    private readonly string _root;

    public DpapiSecretStore(string secretsRoot)
    {
        if (string.IsNullOrWhiteSpace(secretsRoot))
        {
            throw new ArgumentException("Secrets root is required.", nameof(secretsRoot));
        }

        _root = secretsRoot;
    }

    public string Save(Guid deviceId, string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);

        var reference = ISecretStore.ReferenceFor(deviceId);
        Directory.CreateDirectory(_root);

        var cipher = Protect(Encoding.UTF8.GetBytes(secret), Entropy(deviceId));
        var path = PathFor(reference);
        var temporary = path + ".tmp";

                    // Write-then-replace so an interrupted save cannot leave a half-written secret behind.
            File.WriteAllBytes(temporary, cipher);
            ReplaceWithRetry(temporary, path);

        return reference;
    }

    public string? Load(string secretReference)
    {
        if (string.IsNullOrWhiteSpace(secretReference))
        {
            return null;
        }

        var path = PathFor(secretReference);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var cipher = File.ReadAllBytes(path);
            var plain = Unprotect(cipher, Entropy(ReferenceId(secretReference)));
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception exception) when (exception is CryptographicException
                                              or IOException
                                              or UnauthorizedAccessException
                                              or FormatException)
        {
            // A secret written by another user or a corrupted file is "missing", not a crash.
            return null;
        }
    }

    public bool Delete(string secretReference)
    {
        if (string.IsNullOrWhiteSpace(secretReference))
        {
            return false;
        }

        try
        {
            var path = PathFor(secretReference);
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private string PathFor(string reference) => Path.Combine(_root, reference + ".bin");

    /// <summary>
    /// Replace the ciphertext with a short retry. Security software can briefly hold a newly written file open; a
    /// failed credential rotation must not turn into a lost password, and retrying is safe because the temporary file
    /// is complete before the move starts.
    /// </summary>
    private static void ReplaceWithRetry(string temporary, string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(temporary, path, overwrite: true);
                return;
            }
            catch (Exception exception) when (attempt < 4 && exception is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(25);
            }
        }
    }

    private static byte[] Entropy(Guid deviceId) => Encoding.UTF8.GetBytes(deviceId.ToString("N"));

    /// <summary>Recovers the device id from a stored reference so the same entropy is used for unprotect.</summary>
    private static Guid ReferenceId(string reference)
    {
        const string prefix = "device-";
        var value = reference.StartsWith(prefix, StringComparison.Ordinal) ? reference[prefix.Length..] : reference;
        return Guid.TryParseExact(value, "N", out var id) ? id : Guid.Empty;
    }

    private static byte[] Protect(byte[] plain, byte[] entropy)
    {
        using var dataIn = new PinnedBuffer(plain);
        using var entropyIn = new PinnedBuffer(entropy);

        if (!CryptProtectData(ref dataIn.Blob, null, ref entropyIn.Blob, IntPtr.Zero, IntPtr.Zero, UiForbidden, out var result))
        {
            throw new CryptographicException(new Win32Exception(Marshal.GetLastWin32Error()).Message);
        }

        try
        {
            return CopyOut(result);
        }
        finally
        {
            if (result.pbData != IntPtr.Zero)
            {
                LocalFree(result.pbData);
            }
        }
    }

    private static byte[] Unprotect(byte[] cipher, byte[] entropy)
    {
        using var dataIn = new PinnedBuffer(cipher);
        using var entropyIn = new PinnedBuffer(entropy);

        if (!CryptUnprotectData(ref dataIn.Blob, IntPtr.Zero, ref entropyIn.Blob, IntPtr.Zero, IntPtr.Zero, UiForbidden, out var result))
        {
            throw new CryptographicException(new Win32Exception(Marshal.GetLastWin32Error()).Message);
        }

        try
        {
            return CopyOut(result);
        }
        finally
        {
            if (result.pbData != IntPtr.Zero)
            {
                LocalFree(result.pbData);
            }
        }
    }

    /// <summary>Pins a managed array for the duration of one DPAPI call.</summary>
    private sealed class PinnedBuffer : IDisposable
    {
        private readonly GCHandle _handle;

        public PinnedBuffer(byte[] bytes)
        {
            _handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            Blob = new DataBlob
            {
                cbData = bytes.Length,
                pbData = bytes.Length == 0 ? IntPtr.Zero : _handle.AddrOfPinnedObject(),
            };
        }

        // A field, not a property: the P/Invoke signatures need it by ref.
        public DataBlob Blob;

        public void Dispose()
        {
            if (_handle.IsAllocated)
            {
                _handle.Free();
            }
        }
    }

    private static byte[] CopyOut(DataBlob blob)
    {
        if (blob.cbData <= 0 || blob.pbData == IntPtr.Zero)
        {
            return [];
        }

        var copy = new byte[blob.cbData];
        Marshal.Copy(blob.pbData, copy, 0, blob.cbData);
        return copy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? description,
        ref DataBlob entropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        ref DataBlob entropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);
}
