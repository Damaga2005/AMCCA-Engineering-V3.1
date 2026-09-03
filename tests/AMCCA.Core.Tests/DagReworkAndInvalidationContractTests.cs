using System;
using System.Collections.Generic;
using AMCCA.Core.QA;
using FluentAssertions;
using Xunit;

namespace AMCCA.Core.Tests;

public class DagReworkAndInvalidationContractTests
{
    [Fact]
    public void NormativeTreeExample_WhenBFails_InvalidatesOnlyBDescendants_PreservingA_C_E()
    {
        // Tree:
        // A
        // ├── B
        // │   └── D
        // └── C
        //     └── E
        var dag = new ArtifactDag();
        dag.AddNode("A", "script", version: "1.0", hash: "hash-a");
        dag.AddNode("B", "storyboard", version: "1.0", hash: "hash-b");
        dag.AddNode("C", "voiceover", version: "1.0", hash: "hash-c");
        dag.AddNode("D", "render_b", version: "1.0", hash: "hash-d");
        dag.AddNode("E", "audio_mix", version: "1.0", hash: "hash-e");

        dag.AddEdge("A", "B");
        dag.AddEdge("B", "D");
        dag.AddEdge("A", "C");
        dag.AddEdge("C", "E");

        var resolver = new DagReworkResolver(maxReworkAttempts: 3);
        var result = resolver.ResolveRework(
            dag,
            failedNodeId: "B",
            currentAttempts: 0,
            checkId: "CHECK-STORYBOARD",
            expected: "valid_prompts",
            actual: "missing_prompts");

        result.CanRework.Should().BeTrue();
        result.InvalidatedNodes.Should().Contain("D");
        result.InvalidatedNodes.Should().NotContain("A");
        result.InvalidatedNodes.Should().NotContain("C");
        result.InvalidatedNodes.Should().NotContain("E");

        // Status verification
        dag.GetNodeStatus("B").Should().Be("FAILED");
        dag.GetNodeStatus("D").Should().Be("INVALIDATED");
        dag.GetNodeStatus("A").Should().Be("ACTIVE");
        dag.GetNodeStatus("C").Should().Be("ACTIVE");
        dag.GetNodeStatus("E").Should().Be("ACTIVE");

        result.ValidNodes.Should().Contain(new[] { "A", "C", "E" });
        result.ReconstructionOrder.Should().ContainInOrder("B", "D");
    }

    [Fact]
    public void MultipleDescendants_DeepDag_InvalidatesAllDownstreamTopologically()
    {
        // A -> B -> C -> D -> E
        var dag = new ArtifactDag();
        dag.AddNode("A", "script");
        dag.AddNode("B", "storyboard");
        dag.AddNode("C", "video");
        dag.AddNode("D", "render");
        dag.AddNode("E", "manifest");

        dag.AddEdge("A", "B");
        dag.AddEdge("B", "C");
        dag.AddEdge("C", "D");
        dag.AddEdge("D", "E");

        var resolver = new DagReworkResolver(maxReworkAttempts: 3);
        var result = resolver.ResolveRework(dag, "B", currentAttempts: 1, "CHK-1", "exp", "act");

        result.CanRework.Should().BeTrue();
        result.InvalidatedNodes.Should().Equal("C", "D", "E");
        dag.GetNodeStatus("A").Should().Be("ACTIVE");
        dag.GetNodeStatus("B").Should().Be("FAILED");
        dag.GetNodeStatus("C").Should().Be("INVALIDATED");
        dag.GetNodeStatus("D").Should().Be("INVALIDATED");
        dag.GetNodeStatus("E").Should().Be("INVALIDATED");
    }

    [Fact]
    public void IndependentBranches_AreNotAffectedBySiblingFailure()
    {
        // Root -> Branch1(N1 -> N2)
        //      -> Branch2(N3 -> N4)
        var dag = new ArtifactDag();
        dag.AddNode("Root", "intent");
        dag.AddNode("N1", "script");
        dag.AddNode("N2", "storyboard");
        dag.AddNode("N3", "metadata");
        dag.AddNode("N4", "tags");

        dag.AddEdge("Root", "N1");
        dag.AddEdge("N1", "N2");
        dag.AddEdge("Root", "N3");
        dag.AddEdge("N3", "N4");

        var resolver = new DagReworkResolver(maxReworkAttempts: 3);
        var result = resolver.ResolveRework(dag, "N1", currentAttempts: 1, "CHK-2", "e", "a");

        result.InvalidatedNodes.Should().Contain("N2");
        result.InvalidatedNodes.Should().NotContain("N3");
        result.InvalidatedNodes.Should().NotContain("N4");
        result.ValidNodes.Should().Contain(new[] { "Root", "N3", "N4" });
    }

    [Fact]
    public void CyclesInDag_AreStrictlyRejected()
    {
        var dag = new ArtifactDag();
        dag.AddNode("A", "script");
        dag.AddNode("B", "storyboard");
        dag.AddEdge("A", "B");

        var act = () => dag.AddEdge("B", "A"); // Cycle
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*cycle*");
    }

    [Fact]
    public void ReworkAttemptsExhausted_TransitionsToFailed()
    {
        var dag = new ArtifactDag();
        dag.AddNode("A", "script");
        dag.AddNode("B", "storyboard");
        dag.AddEdge("A", "B");

        var resolver = new DagReworkResolver(maxReworkAttempts: 3);
        var result = resolver.ResolveRework(dag, "B", currentAttempts: 3, "CHK-1", "e", "a");

        result.CanRework.Should().BeFalse();
        result.FailureReason.Should().Be("MAX_ATTEMPTS_EXCEEDED");
    }

    [Fact]
    public void RepeatedIdenticalFailureSignature_StopsReworkToPreventInfiniteLoop()
    {
        var dag = new ArtifactDag();
        dag.AddNode("A", "script");
        dag.AddNode("B", "storyboard");
        dag.AddEdge("A", "B");

        var resolver = new DagReworkResolver(maxReworkAttempts: 3);

        // Run 1 produces signature
        var res1 = resolver.ResolveRework(dag, "B", currentAttempts: 1, "CHK-1", "valid", "bad_syntax");
        res1.CanRework.Should().BeTrue();
        var sig1 = res1.FailureSignature;

        // Run 2 with identical signature stops rather than looping (SPEC/37: "Two consecutive identical signatures mean regeneration is not converging; the loop stops")
        var res2 = resolver.ResolveRework(dag, "B", currentAttempts: 2, "CHK-1", "valid", "bad_syntax", lastFailureSignature: sig1);
        res2.CanRework.Should().BeFalse();
        res2.FailureReason.Should().Be("REPEATED_FAILURE_SIGNATURE");
    }

    [Fact]
    public void IdempotentRepeatedResolution_ProducesIdenticalOutcome()
    {
        var dag = new ArtifactDag();
        dag.AddNode("A", "script");
        dag.AddNode("B", "storyboard");
        dag.AddNode("C", "render");
        dag.AddEdge("A", "B");
        dag.AddEdge("B", "C");

        var resolver = new DagReworkResolver(maxReworkAttempts: 3);
        var res1 = resolver.ResolveRework(dag, "B", 0, "CHK", "exp", "act");
        var res2 = resolver.ResolveRework(dag, "B", 0, "CHK", "exp", "act");

        res1.FailureSignature.Should().Be(res2.FailureSignature);
        res1.InvalidatedNodes.Should().Equal(res2.InvalidatedNodes);
        res1.ReconstructionOrder.Should().Equal(res2.ReconstructionOrder);
    }
}
