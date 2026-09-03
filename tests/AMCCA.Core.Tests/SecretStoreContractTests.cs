using System;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;
using AMCCA.Core.Security;
using FluentAssertions;
using Xunit;

namespace AMCCA.Core.Tests;

public class SecretStoreContractTests
{
    [Fact]
    public void ValidSecretReference_ParsesSuccessfully()
    {
        var validUri = "secret://amcca/gateway_api_key";
        var parsed = SecretReference.Parse(validUri);

        parsed.Vault.Should().Be("amcca");
        parsed.Name.Should().Be("gateway_api_key");
        parsed.Uri.Should().Be(validUri);
    }

    [Theory]
    [InlineData("http://vault/key")]
    [InlineData("secret://")]
    [InlineData("secret:///key")]
    [InlineData("secret://vault/")]
    [InlineData("literal_plain_password_123")]
    public void InvalidSecretReference_ThrowsSec002(string invalidUri)
    {
        var act = () => SecretReference.Parse(invalidUri);

        act.Should().Throw<AmccaException>()
            .Where(e => e.ErrorCode == AmccaErrors.Sec002);
    }

    [Fact]
    public void SecretReference_ToString_NeverRevealsSecretContent()
    {
        var validUri = "secret://amcca/gateway_api_key";
        var parsed = SecretReference.Parse(validUri);

        parsed.ToString().Should().Be("secret://amcca/gateway_api_key");
    }

    [Fact]
    public async Task InMemorySecretStore_StoresAndRetrievesSecretsCorrectly()
    {
        var store = new InMemorySecretStore();
        var secretRef = SecretReference.Parse("secret://amcca/test_token");

        await store.SetSecretAsync(secretRef, "super_secret_value_xyz");
        var retrieved = await store.GetSecretAsync(secretRef);

        retrieved.Should().Be("super_secret_value_xyz");
    }

    [Fact]
    public async Task InMemorySecretStore_IsReachableReturnsTrue()
    {
        var store = new InMemorySecretStore();
        var isReachable = await store.IsReachableAsync();

        isReachable.Should().BeTrue();
    }
}
