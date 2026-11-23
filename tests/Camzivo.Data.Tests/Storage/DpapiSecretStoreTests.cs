using System.Text;
using Camzivo.Data.Storage;
using Xunit;

namespace Camzivo.Data.Tests.Storage;

/// <summary>
/// Real DPAPI round trips. These run only on Windows (the test project is net8.0 but the product is Windows-only);
/// they prove the P/Invoke marshalling, not any device behaviour.
/// </summary>
public sealed class DpapiSecretStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "camzivo-secret-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Save_ThenLoad_RoundTripsTheSecret()
    {
        var store = new DpapiSecretStore(_root);
        var id = Guid.NewGuid();

        var reference = store.Save(id, "Sup3rSecret!✓");

        Assert.Equal("device-" + id.ToString("N"), reference);
        Assert.Equal("Sup3rSecret!✓", store.Load(reference));
    }

    [Fact]
    public void Save_WritesCiphertextOnly()
    {
        var store = new DpapiSecretStore(_root);
        var reference = store.Save(Guid.NewGuid(), "Sup3rSecret!");
        var bytes = File.ReadAllBytes(Path.Combine(_root, reference + ".bin"));

        Assert.DoesNotContain("Sup3rSecret!", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
    }

    [Fact]
    public void Load_ReturnsNullForUnknownOrMissingSecret()
    {
        var store = new DpapiSecretStore(_root);

        Assert.Null(store.Load("device-" + Guid.NewGuid().ToString("N")));
        Assert.Null(store.Load(string.Empty));
    }

    [Fact]
    public void Save_ReplacesAnExistingSecretInPlace()
    {
        var store = new DpapiSecretStore(_root);
        var id = Guid.NewGuid();

        store.Save(id, "old-password");
        var reference = store.Save(id, "new-password");

        Assert.Equal("new-password", store.Load(reference));
        Assert.False(File.Exists(Path.Combine(_root, reference + ".bin.tmp")));
    }

    [Fact]
    public void Delete_RemovesTheCiphertext()
    {
        var store = new DpapiSecretStore(_root);
        var reference = store.Save(Guid.NewGuid(), "Sup3rSecret!");

        Assert.True(store.Delete(reference));
        Assert.Null(store.Load(reference));
        Assert.False(store.Delete(reference));
    }

    [Fact]
    public void Load_ReturnsNullWhenTheCiphertextIsCorrupt()
    {
        var store = new DpapiSecretStore(_root);
        var reference = store.Save(Guid.NewGuid(), "Sup3rSecret!");
        var path = Path.Combine(_root, reference + ".bin");

        File.WriteAllBytes(path, [1, 2, 3, 4, 5, 6, 7, 8]);

        Assert.Null(store.Load(reference));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory in %TEMP% is not worth failing a test over.
        }
    }
}
