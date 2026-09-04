using System;
using System.Net.Http;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Polly;
using Polly.Timeout;
using Serilog;
using Xunit;

namespace AMCCA.Core.Tests;

public class ArchitecturalDependenciesRegressionTests
{
    [Fact]
    public void DEF022_MandatoryArchitecturalDependencies_HttpClientFactory_Polly_Serilog_AreFunctional()
    {
        // 1. Dependency Injection setup with HttpClientFactory
        var services = new ServiceCollection();
        services.AddHttpClient("TestClient", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
        });

        var provider = services.BuildServiceProvider();
        var clientFactory = provider.GetRequiredService<IHttpClientFactory>();
        var httpClient = clientFactory.CreateClient("TestClient");
        httpClient.Should().NotBeNull();
        httpClient.Timeout.Should().Be(TimeSpan.FromSeconds(10));

        // 2. Polly Resilience Pipeline
        var pipeline = new ResiliencePipelineBuilder()
            .AddTimeout(TimeSpan.FromSeconds(5))
            .Build();

        pipeline.Should().NotBeNull();
        var executed = false;
        pipeline.Execute(() =>
        {
            executed = true;
        });
        executed.Should().BeTrue("Polly pipeline must execute successfully (D-006, DEF-022)");

        // 3. Serilog Structured Logging
        var testSink = new TestSink();
        var logger = new LoggerConfiguration()
            .WriteTo.Sink(testSink)
            .CreateLogger();

        logger.Information("AMCCA architecture test for {Component}", "DEF-022");
        testSink.Events.Should().HaveCount(1);
        testSink.Events[0].MessageTemplate.Text.Should().Contain("{Component}");
        testSink.Events[0].Properties.Should().ContainKey("Component");
    }

    private class TestSink : Serilog.Core.ILogEventSink
    {
        public System.Collections.Generic.List<Serilog.Events.LogEvent> Events { get; } = new();
        public void Emit(Serilog.Events.LogEvent logEvent) => Events.Add(logEvent);
    }
}
