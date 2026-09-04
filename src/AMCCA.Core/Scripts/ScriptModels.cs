using System.Collections.Generic;

namespace AMCCA.Core.Scripts;

public record ScriptLine(
    int LineNumber,
    string Text,
    string? ClaimId,
    bool IsMaterialFact,
    bool UncertaintyWordingPresent);

public record ScriptDocument(
    string ProductionId,
    IReadOnlyList<ScriptLine> Lines,
    int EstimatedSpokenDurationSec = 60);
