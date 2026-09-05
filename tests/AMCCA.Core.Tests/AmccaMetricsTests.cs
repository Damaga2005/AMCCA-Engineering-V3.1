using System.Collections.Generic;
using System.Diagnostics.Metrics;
using AMCCA.Core.Diagnostics;
using FluentAssertions;
using Xunit;

namespace AMCCA.Core.Tests;

public class AmccaMetricsTests
{
    [Fact]
    public void CountTransition_EmitsOnTheOrchestratorMeter_TaggedByToState()
    {
        long measured = 0;
        string? tagValue = null;

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == AmccaMetrics.MeterName && instrument.Name == "amcca.production.transitions")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, state) =>
        {
            measured += value;
            foreach (var t in tags)
            {
                if (t.Key == "to_state") tagValue = t.Value as string;
            }
        });
        listener.Start();

        AmccaMetrics.CountTransition("RESEARCH_VERIFIED");

        measured.Should().Be(1);
        tagValue.Should().Be("RESEARCH_VERIFIED");
    }

    [Fact]
    public void CountJob_EmitsTaggedByOutcome()
    {
        var seen = new List<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == AmccaMetrics.MeterName && instrument.Name == "amcca.jobs.processed")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, state) =>
        {
            foreach (var t in tags)
            {
                if (t.Key == "outcome" && t.Value is string s) seen.Add(s);
            }
        });
        listener.Start();

        AmccaMetrics.CountJob("Completed");
        AmccaMetrics.CountJob("HandlerThrew");

        seen.Should().BeEquivalentTo(new[] { "Completed", "HandlerThrew" });
    }
}
