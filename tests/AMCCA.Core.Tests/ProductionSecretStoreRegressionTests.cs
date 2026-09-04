using System.Reflection;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;
using AMCCA.Core.Security;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AMCCA.Core.Tests;

/// <summary>
/// SEC-05 — the in-memory secret store is test/development only. The production composition root
/// must resolve an OS-backed store, and a guard fails startup closed if it does not.
/// </summary>
public class ProductionSecretStoreRegressionTests
{
    [Fact]
    public async Task InMemorySecretStore_IsMarkedEphemeral_ButStillFunctionsForTests()
    {
        var store = new InMemorySecretStore();

        store.Should().BeAssignableTo<IEphemeralSecretStore>();

        var reference = SecretReference.Parse("secret://vault/name");
        await store.SetSecretAsync(reference, "v");
        (await store.GetSecretAsync(reference)).Should().Be("v");
    }

    [Fact]
    public void Guard_RejectsEphemeralStore_WithSec002()
    {
        var act = () => SecretStoreGuard.EnsureProductionGrade(new InMemorySecretStore());

        act.Should().Throw<AmccaException>().Which.ErrorCode.Should().Be(AmccaErrors.Sec002);
    }

    [Fact]
    public void Guard_RejectsMissingStore_WithSec002()
    {
        var act = () => SecretStoreGuard.EnsureProductionGrade(null);

        act.Should().Throw<AmccaException>().Which.ErrorCode.Should().Be(AmccaErrors.Sec002);
    }

    [Fact]
    public void Guard_AcceptsDpapiStore()
    {
        var act = () => SecretStoreGuard.EnsureProductionGrade(new WindowsDpapiSecretStore(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "amcca_sec05_" + System.Guid.NewGuid().ToString("N"))));

        act.Should().NotThrow();
    }

    [Fact]
    public void ProductionComposition_ResolvesNonEphemeralSecretStore()
    {
        // Invoke App.ConfigureServices(IServiceCollection) — the real composition root — without
        // constructing the WPF Application.
        var method = typeof(AMCCA.App.App).GetMethod(
            "ConfigureServices", BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull("the production composition root must exist");

        var services = new ServiceCollection();
        method!.Invoke(null, new object[] { services });

        using var provider = services.BuildServiceProvider();
        var store = provider.GetService<ISecretStore>();

        store.Should().NotBeNull();
        store.Should().NotBeAssignableTo<IEphemeralSecretStore>();
        SecretStoreGuard.EnsureProductionGrade(store); // must not throw
    }
}
