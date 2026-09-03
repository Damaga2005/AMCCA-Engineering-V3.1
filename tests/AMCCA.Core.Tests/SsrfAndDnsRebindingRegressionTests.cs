using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AMCCA.Core.Contracts;
using AMCCA.Core.Security;
using FluentAssertions;
using Xunit;

namespace AMCCA.Core.Tests;

public class SsrfAndDnsRebindingRegressionTests
{
    [Theory]
    [InlineData("http://127.0.0.1/admin")]
    [InlineData("http://127.0.0.2:8080/")]
    [InlineData("http://localhost/api")]
    [InlineData("http://[::1]/")]
    public void DEF014_Loopback_IsRejected(string url)
    {
        var uri = new Uri(url);
        var act = () => SsrfValidator.ValidateDestinationUri(uri);

        act.Should().Throw<AmccaException>()
            .Where(e => e.ErrorCode == AmccaErrors.Sec003);
    }

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://metadata.google.internal/computeMetadata/v1/")]
    [InlineData("http://instance-data/latest/meta-data/")]
    public void DEF014_CloudMetadata_IsRejected(string url)
    {
        var uri = new Uri(url);
        var act = () => SsrfValidator.ValidateDestinationUri(uri);

        act.Should().Throw<AmccaException>()
            .Where(e => e.ErrorCode == AmccaErrors.Sec003);
    }

    [Theory]
    [InlineData("http://10.0.0.1/secret")]
    [InlineData("http://172.16.0.1/config")]
    [InlineData("http://172.31.255.255/")]
    [InlineData("http://192.168.1.1/router")]
    [InlineData("http://192.168.0.254/")]
    public void DEF014_Rfc1918PrivateRanges_AreRejected(string url)
    {
        var uri = new Uri(url);
        var act = () => SsrfValidator.ValidateDestinationUri(uri);

        act.Should().Throw<AmccaException>()
            .Where(e => e.ErrorCode == AmccaErrors.Sec003);
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("file://C:/Windows/win.ini")]
    [InlineData("gopher://evil.com/")]
    [InlineData("ftp://internal.repo/")]
    public void DEF014_NonHttpSchemes_AreRejected(string url)
    {
        var uri = new Uri(url);
        var act = () => SsrfValidator.ValidateDestinationUri(uri);

        act.Should().Throw<AmccaException>()
            .Where(e => e.ErrorCode == AmccaErrors.Sec003);
    }

    [Fact]
    public void DEF014_Ipv4MappedIpv6_PrivateAddress_IsRejected()
    {
        var mappedLoopback = IPAddress.Parse("::ffff:127.0.0.1");
        SsrfValidator.IsPrivateOrReservedIp(mappedLoopback).Should().BeTrue();

        var mappedRfc1918 = IPAddress.Parse("::ffff:192.168.1.50");
        SsrfValidator.IsPrivateOrReservedIp(mappedRfc1918).Should().BeTrue();

        var mappedMetadata = IPAddress.Parse("::ffff:169.254.169.254");
        SsrfValidator.IsPrivateOrReservedIp(mappedMetadata).Should().BeTrue();
    }

    [Fact]
    public void DEF014_LegitimatePublicIp_IsNotPrivate()
    {
        var publicDns = IPAddress.Parse("8.8.8.8");
        SsrfValidator.IsPrivateOrReservedIp(publicDns).Should().BeFalse();

        var cloudflareDns = IPAddress.Parse("1.1.1.1");
        SsrfValidator.IsPrivateOrReservedIp(cloudflareDns).Should().BeFalse();
    }

    [Fact]
    public async Task DEF014_SafeSocketsHttpHandler_RejectsConnectionToPrivateIpAtSocketLevel()
    {
        // Prove that ConnectCallback intercepts and rejects private IPs even if DNS rebinding occurred
        using var handler = SsrfValidator.CreateSafeSocketsHttpHandler();
        using var client = new HttpClient(handler);

        // Attempting to connect to 127.0.0.1 should fail with AmccaException Sec003
        var act = async () => await client.GetAsync("http://127.0.0.1:54321/probe");

        await act.Should().ThrowAsync<Exception>()
            .Where(e => e is AmccaException || e.InnerException is AmccaException);
    }
}
