using System.Collections.Generic;

namespace AMCCA.Core.QA;

public class ArtifactDag
{
    private readonly Dictionary<string, (string Kind, string Status)> _nodes = new();
    private readonly Dictionary<string, List<string>> _outgoingEdges = new();

    public void AddNode(string nodeId, string kind)
    {
        _nodes[nodeId] = (kind, "ACTIVE");
        if (!_outgoingEdges.ContainsKey(nodeId))
        {
            _outgoingEdges[nodeId] = new List<string>();
        }
    }

    public void AddEdge(string parentNodeId, string childNodeId)
    {
        if (!_outgoingEdges.ContainsKey(parentNodeId))
        {
            _outgoingEdges[parentNodeId] = new List<string>();
        }
        _outgoingEdges[parentNodeId].Add(childNodeId);
    }

    public IReadOnlyList<string> InvalidateDescendants(string rootNodeId)
    {
        var invalidated = new HashSet<string>();
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
            if (invalidated.Add(current))
            {
                if (_nodes.TryGetValue(current, out var nodeData))
                {
                    // Mark invalidated, never delete (I-08, SPEC/37)
                    _nodes[current] = (nodeData.Kind, "INVALIDATED");
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

        return new List<string>(invalidated);
    }

    public string? GetNodeStatus(string nodeId)
    {
        return _nodes.TryGetValue(nodeId, out var data) ? data.Status : null;
    }

    public bool NodeExists(string nodeId) => _nodes.ContainsKey(nodeId);
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
        // SPEC/37: "Verify rework_attempts < policy.rework.max_attempts; otherwise transition to FAILED"
        return currentAttempts < _maxReworkAttempts;
    }
}
