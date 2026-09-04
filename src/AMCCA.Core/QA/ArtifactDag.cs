using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace AMCCA.Core.QA;

public class ArtifactDagNode
{
    public string NodeId { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Version { get; set; } = "1.0";
    public string ContentHash { get; set; } = string.Empty;
    public string Status { get; set; } = "ACTIVE"; // ACTIVE, INVALIDATED, FAILED, SUPERSEDED
}

public class ArtifactDag
{
    private readonly Dictionary<string, ArtifactDagNode> _nodes = new();
    private readonly Dictionary<string, List<string>> _outgoingEdges = new();
    private readonly Dictionary<string, List<string>> _incomingEdges = new();

    public void AddNode(string nodeId, string kind, string version = "1.0", string hash = "")
    {
        _nodes[nodeId] = new ArtifactDagNode
        {
            NodeId = nodeId,
            Kind = kind,
            Version = version,
            ContentHash = hash,
            Status = "ACTIVE"
        };

        if (!_outgoingEdges.ContainsKey(nodeId))
        {
            _outgoingEdges[nodeId] = new List<string>();
        }
        if (!_incomingEdges.ContainsKey(nodeId))
        {
            _incomingEdges[nodeId] = new List<string>();
        }
    }

    public void AddEdge(string parentNodeId, string childNodeId)
    {
        if (parentNodeId == childNodeId)
        {
            throw new InvalidOperationException($"Cannot add self-edge ({parentNodeId} -> {childNodeId}): cycle detected.");
        }

        if (IsReachable(childNodeId, parentNodeId))
        {
            throw new InvalidOperationException($"Cannot add edge ({parentNodeId} -> {childNodeId}): cycle detected in artifact DAG.");
        }

        if (!_outgoingEdges.ContainsKey(parentNodeId))
        {
            _outgoingEdges[parentNodeId] = new List<string>();
        }
        if (!_incomingEdges.ContainsKey(childNodeId))
        {
            _incomingEdges[childNodeId] = new List<string>();
        }

        _outgoingEdges[parentNodeId].Add(childNodeId);
        _incomingEdges[childNodeId].Add(parentNodeId);
    }

    private bool IsReachable(string fromNodeId, string targetNodeId)
    {
        var visited = new HashSet<string>();
        var queue = new Queue<string>();
        queue.Enqueue(fromNodeId);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current == targetNodeId) return true;
            if (!visited.Add(current)) continue;

            if (_outgoingEdges.TryGetValue(current, out var children))
            {
                foreach (var child in children)
                {
                    queue.Enqueue(child);
                }
            }
        }

        return false;
    }

    public IReadOnlyList<string> InvalidateDescendants(string rootNodeId)
    {
        var invalidated = new List<string>();
        var visited = new HashSet<string>();
        var queue = new Queue<string>();

        if (_outgoingEdges.TryGetValue(rootNodeId, out var directChildren))
        {
            foreach (var child in directChildren)
            {
                queue.Enqueue(child);
            }
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (visited.Add(current))
            {
                if (_nodes.TryGetValue(current, out var node))
                {
                    node.Status = "INVALIDATED";
                    invalidated.Add(current);
                }

                if (_outgoingEdges.TryGetValue(current, out var nextChildren))
                {
                    foreach (var next in nextChildren)
                    {
                        queue.Enqueue(next);
                    }
                }
            }
        }

        return invalidated;
    }

    public void SetNodeStatus(string nodeId, string status)
    {
        if (_nodes.TryGetValue(nodeId, out var node))
        {
            node.Status = status;
        }
    }

    public string? GetNodeStatus(string nodeId)
    {
        return _nodes.TryGetValue(nodeId, out var node) ? node.Status : null;
    }

    public ArtifactDagNode? GetNode(string nodeId)
    {
        return _nodes.TryGetValue(nodeId, out var node) ? node : null;
    }

    public bool NodeExists(string nodeId) => _nodes.ContainsKey(nodeId);

    public IReadOnlyList<string> GetAllNodeIds() => _nodes.Keys.OrderBy(k => k).ToList();

    public IReadOnlyList<string> GetOutgoingChildren(string nodeId)
    {
        return _outgoingEdges.TryGetValue(nodeId, out var list) ? list : Array.Empty<string>();
    }
}

public class ReworkResolutionResult
{
    public bool CanRework { get; set; }
    public string? FailureReason { get; set; }
    public string FailedNodeId { get; set; } = string.Empty;
    public string FailureSignature { get; set; } = string.Empty;
    public IReadOnlyList<string> InvalidatedNodes { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> ValidNodes { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> ReconstructionOrder { get; set; } = Array.Empty<string>();
}

public class DagReworkResolver
{
    private readonly int _maxReworkAttempts;

    public DagReworkResolver(int maxReworkAttempts = 3)
    {
        _maxReworkAttempts = maxReworkAttempts;
    }

    public bool CanAttemptRework(int currentAttempts)
    {
        return currentAttempts < _maxReworkAttempts;
    }

    public static string ComputeFailureSignature(string checkId, string nodeKind, string expected, string actual)
    {
        var raw = $"{checkId}:{nodeKind}:{expected}:{actual}";
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    public ReworkResolutionResult ResolveRework(
        ArtifactDag dag,
        string failedNodeId,
        int currentAttempts,
        string checkId,
        string expected,
        string actual,
        string? lastFailureSignature = null)
    {
        var node = dag.GetNode(failedNodeId);
        var nodeKind = node?.Kind ?? "unknown";
        var signature = ComputeFailureSignature(checkId, nodeKind, expected, actual);

        var result = new ReworkResolutionResult
        {
            FailedNodeId = failedNodeId,
            FailureSignature = signature
        };

        // SPEC/37: "Two consecutive identical signatures mean regeneration is not converging; the loop stops and the production moves to FAILED"
        if (!string.IsNullOrEmpty(lastFailureSignature) && string.Equals(lastFailureSignature, signature, StringComparison.OrdinalIgnoreCase))
        {
            result.CanRework = false;
            result.FailureReason = "REPEATED_FAILURE_SIGNATURE";
            return result;
        }

        // SPEC/37: "Verify rework_attempts < policy.rework.max_attempts; otherwise transition T-2A1 to FAILED"
        if (currentAttempts >= _maxReworkAttempts)
        {
            result.CanRework = false;
            result.FailureReason = "MAX_ATTEMPTS_EXCEEDED";
            return result;
        }

        result.CanRework = true;

        // Mark failed node
        dag.SetNodeStatus(failedNodeId, "FAILED");

        // Propagate invalidation down the DAG (descendants only, never ancestors or sibling branches)
        var invalidated = dag.InvalidateDescendants(failedNodeId);
        result.InvalidatedNodes = invalidated;

        // Valid nodes are all nodes that remain ACTIVE
        var allNodes = dag.GetAllNodeIds();
        result.ValidNodes = allNodes.Where(n => dag.GetNodeStatus(n) == "ACTIVE").ToList();

        // Compute reconstruction order (failedNode first, then topological order of descendants)
        var reconstruction = new List<string> { failedNodeId };
        reconstruction.AddRange(invalidated);
        result.ReconstructionOrder = reconstruction;

        return result;
    }
}
