using Aria.Agent;
using Aria.Web.Data;
using Aria.Web.Services;
using Microsoft.AspNetCore.Components;

namespace Aria.Web.Components.Pages;

public partial class Hive
{
    // Option lists for the themed dropdowns (first entry is the "none/default" choice with value "").
    public List<(string Value, string Label)> AgentOptions()
    {
        var list = new List<(string, string)> { ("", "— Base Aria —") };
        list.AddRange(_allAgents.Select(a => (a.Id.ToString(), a.DisplayName)));
        return list;
    }

    // No leading "— default —" entry: the Hive requires an explicit channel + model for the
    // Overmind (and, through it, the drones) rather than silently inheriting the chat's default,
    // so a collective always runs on a known model. The sidebar seeds the current default as a
    // concrete pick, and Start refuses to launch until both are set.
    public List<(string Value, string Label)> SourceOptions()
    {
        var list = new List<(string, string)>();

        var nodes = SessionState.CurrentUser != null
            ? BridgeRegistry.GetNodes(SessionState.CurrentUser.Id).ToList()
            : [];

        foreach (var s in _availableSources)
        {
            var label = s.Name;
            if (s.IsBridged && nodes.Count > 0)
            {
                var node = !string.IsNullOrEmpty(s.BridgeNodeId)
                    ? nodes.FirstOrDefault(n => n.NodeId == s.BridgeNodeId)
                    : nodes.FirstOrDefault();
                if (node != null)
                    label += $" ({node.Label})";
            }
            list.Add((s.Name, label));
        }

        return list;
    }

    public List<(string Value, string Label)> ModelOptions(ModelSource src)
    {
        var list = new List<(string, string)>();
        list.AddRange(src.Models.Select(m => (m, m)));
        return list;
    }

    public void OnOvermindAgentChanged(string? val)
    {
        _editOvermindSubAgentId = string.IsNullOrEmpty(val) ? null : int.Parse(val);
        _ = SaveConfigAsync();
    }

    public void OnOvermindSourceChanged(string? val)
    {
        _editOvermindSource = string.IsNullOrEmpty(val) ? null : val;
        _editOvermindModel  = null;
        _ = SaveConfigAsync();
    }

    public void OnOvermindModelChanged(string? val)
    {
        _editOvermindModel = string.IsNullOrEmpty(val) ? null : val;
        _ = SaveConfigAsync();
    }

    public static List<(string Value, string Label)> BehaviorOptions() => new()
    {
        ("0", "Hive Mind"),
        ("1", "Cohort Specialists")
    };

    public const string BehaviorTip =
        "Hive Mind: every drone answers the same objective, then the Overmind picks or merges the best result. " +
        "Cohort Specialists: the objective is split into sub-tasks, each handled by the best-fit specialist drone.";

    public void OnBehaviorChanged(string? v)
    {
        _editBehavior = v == "1" ? CollectiveBehavior.CohortSpecialists : CollectiveBehavior.HiveMind;
        _ = SaveConfigAsync();
    }
}
