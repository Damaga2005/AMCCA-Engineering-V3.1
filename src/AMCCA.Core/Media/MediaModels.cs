using System.Collections.Generic;
using System.Text.Json;

namespace AMCCA.Core.Media;

public record MediaProfile(
    string ProfileId,
    string Version,
    string Container,
    string VideoCodec,
    string AudioCodec,
    int Width,
    int Height,
    int Fps,
    int BitrateKbps,
    double LoudnessTargetLufs,
    string SourceRef,
    string RetrievedAt);

public record TimelineItem(
    string AssetId,
    long StartMs,
    long DurationMs,
    string ContentHash);

public record TimelineTrack(
    string TrackId,
    string Kind,
    IReadOnlyList<TimelineItem> Items);

public record TimelineDefinition(
    string ProductionId,
    long DurationMs,
    IReadOnlyList<TimelineTrack> Tracks);

public record SyntheticDisclosure(
    bool HasSyntheticVisuals,
    bool HasSyntheticAudio,
    string GeneratorModelId,
    string DisclosureText)
{
    public string GenerateC2paManifest(string productionId)
    {
        var manifestObj = new
        {
            schema_version = "3.1.0",
            claim_generator = "AMCCA-V3.1-SyntheticEngine",
            title = $"C2PA Manifest for Production {productionId}",
            assertions = new object[]
            {
                new
                {
                    label = "c2pa.actions",
                    data = new
                    {
                        actions = new[]
                        {
                            new
                            {
                                action = "c2pa.created",
                                softwareAgent = GeneratorModelId,
                                parameters = new
                                {
                                    has_synthetic_visuals = HasSyntheticVisuals,
                                    has_synthetic_audio = HasSyntheticAudio,
                                    disclosure = DisclosureText
                                }
                            }
                        }
                    }
                }
            }
        };

        return JsonSerializer.Serialize(manifestObj, new JsonSerializerOptions { WriteIndented = true });
    }
}
