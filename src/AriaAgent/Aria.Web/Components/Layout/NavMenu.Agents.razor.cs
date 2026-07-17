using System.Text.Json;
using Aria.Agent;
using Aria.Web.Data;
using Aria.Web.Helpers;
using Aria.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Aria.Web.Components.Layout;

public partial class NavMenu
{
    internal string RenderSkillsPreview(string md) => MarkdownHelper.ToHtml(md);

    internal List<SubAgent> _subAgents = [];
    internal SubAgent?      _agentPendingDelete;
    internal int            _agentPendingDeleteCogCount;

    // ── Skills panel state ────────────────────────────────────────────────
    internal List<Skill> _skills           = [];
    internal bool        _skillEditing     = false;
    internal bool        _skillEditorWired = false;
    internal int?        _skillEditId;
    internal string      _skillName        = "";
    internal string      _skillContent     = "";
    internal bool        _skillSaved       = false;
    internal string?     _skillError;

    internal void NewSkill()
    {
        _skillEditId      = null;
        _skillName        = "";
        _skillContent     = "";
        _skillSaved       = false;
        _skillError       = null;
        _skillEditorWired = false;
        _skillEditing     = true;
    }

    internal void EditSkill(Skill sk)
    {
        _skillEditId      = sk.Id;
        _skillName        = sk.Name;
        _skillContent     = sk.MarkdownContent;
        _skillSaved       = false;
        _skillError       = null;
        _skillEditorWired = false;
        _skillEditing     = true;
    }

    internal void CancelSkillEdit() { _skillEditing = false; _skillEditorWired = false; _skillError = null; }

    [JSInvokable]
    public Task SaveSkillFromJs() => SaveSkillAsync();

    internal async Task SaveSkillAsync()
    {
        if (SessionState.CurrentUser == null) return;
        if (string.IsNullOrWhiteSpace(_skillName)) { _skillError = "Name is required."; return; }
        _skillError = null;
        _skillSaved = false;
        try
        {
            if (_skillEditId == null)
            {
                var created  = await SkillService.CreateAsync(SessionState.CurrentUser.Id, _skillName, _skillContent);
                _skillEditId = created.Id;
            }
            else
                await SkillService.UpdateAsync(_skillEditId.Value, _skillName, _skillContent);
            _skills     = await SkillService.GetForUserAsync(SessionState.CurrentUser.Id);
            _skillSaved = true;
        }
        catch (Exception ex) { _skillError = ex.Message; }
    }

    internal async Task DeleteSkillAsync()
    {
        if (_skillEditId == null) return;
        try
        {
            await SkillService.DeleteAsync(_skillEditId.Value);
            if (SessionState.CurrentUser != null)
                _skills = await SkillService.GetForUserAsync(SessionState.CurrentUser.Id);
            _skillEditing     = false;
            _skillEditorWired = false;
        }
        catch (Exception ex) { _skillError = ex.Message; }
    }

    // ── Agent modal state ─────────────────────────────────────────────────
    internal bool                     _agentModal;
    internal int?                     _editingAgentId;
    internal string                   _editName            = "";
    internal string                   _editNickname        = "";
    internal string                   _editArchetypeName   = "";
    internal string                   _editPersonalityText = "";
    internal string                   _editDirectives      = "";
    internal HashSet<int>             _editAgentSkillIds   = [];
    internal string                   _editColor           = "#8B0000";
    internal string?                  _editSourceName;
    internal string?                  _editModelId;
    internal Dictionary<string, bool> _editToolStates      = new();
    internal HashSet<string>          _editMcpNames        = [];
    internal bool                     _agentModalSaved;
    internal string?                  _agentModalError;
    internal bool                     _isHiring;
    internal bool                     _hiringDone;
    internal string                   _hiringMessage       = "";
    internal string?                  _pendingSpriteKey;

    internal static readonly string[] HiringMessages =
    [
        "// Contacting the Fixer...",
        "// Verifying gene-seed records...",
        "// Cross-referencing Administratum archives...",
        "// Negotiating contract terms...",
        "// Confirming kill-count certifications...",
        "// Awaiting Inquisitorial clearance...",
        "// Inducting into service...",
    ];

    internal static readonly (string Hex, string Label)[] AgentColorPresets =
    [
        ("#8B0000", "Crimson"),
        ("#B8860B", "Mechanicus Gold"),
        ("#C4621D", "Burnt Orange"),
        ("#1C3A6A", "Void Blue"),
        ("#1A6A5A", "Teal"),
        ("#4A0D6A", "Chaos Purple"),
        ("#7A1A4A", "Dark Rose"),
        ("#2D5A1B", "Plague Green"),
        ("#4A3728", "Bone"),
        ("#1A1A3A", "Midnight"),
    ];

    internal void InquireNewAgent()
    {
        var spriteKey = AgentSprites.PickSpriteKey("");
        var (name, personality, archetypeName) = AgentPersona.Generate(spriteKey);
        _editingAgentId      = null;
        _editName            = name;
        _editNickname        = "";
        _editArchetypeName   = archetypeName;
        _editPersonalityText = personality;
        _editDirectives      = "";
        _editAgentSkillIds   = [];
        _editColor           = "#8B0000";
        _editSourceName      = null;
        _editModelId         = null;
        _pendingSpriteKey    = spriteKey;
        _hiringDone          = false;
        _editToolStates      = ToolRegistry.All
            .ToDictionary(d => d.Id, d => SessionState.IsToolEnabled(d.Id));
        _editMcpNames        = new HashSet<string>(SessionState.McpServers.Select(s => s.Name));
        _agentModalSaved     = false;
        _agentModalError     = null;
        _agentModal          = true;
    }

    internal async Task OpenAgentModal(SubAgent agent)
    {
        _editingAgentId      = agent.Id;
        _editName            = agent.GeneratedName;
        _editNickname        = agent.Nickname ?? "";
        _editArchetypeName   = agent.ArchetypeName;
        _editPersonalityText = agent.GeneratedPersonality;
        _editDirectives      = agent.UserDirectives ?? "";
        _editAgentSkillIds   = await SkillService.GetAssignedIdsAsync(agent.Id);
        _editColor           = agent.AccentColor;
        _editSourceName      = agent.ModelSourceName;
        _editModelId         = agent.ModelId;
        _editToolStates      = agent.ToolStates.ToDictionary(ts => ts.ToolId, ts => ts.Enabled);
        try
        {
            _editMcpNames = agent.EnabledMcpNamesJson is not null
                ? new HashSet<string>(JsonSerializer.Deserialize<List<string>>(agent.EnabledMcpNamesJson) ?? [])
                : [];
        }
        catch { _editMcpNames = []; }
        _agentModalSaved = false;
        _agentModalError = null;
        _agentModal      = true;
    }

    internal void CloseAgentModal() { _agentModal = false; _agentModalError = null; }

    internal void ToggleAgentSkill(int skillId)
    {
        if (!_editAgentSkillIds.Remove(skillId)) _editAgentSkillIds.Add(skillId);
    }

    internal void ToggleEditTool(string toolId) =>
        _editToolStates[toolId] = !_editToolStates.GetValueOrDefault(toolId);

    internal void ToggleEditMcp(string name)
    {
        if (!_editMcpNames.Remove(name)) _editMcpNames.Add(name);
    }

    internal void OnEditSourceChanged(string? val)
    {
        _editSourceName = string.IsNullOrEmpty(val) ? null : val;
        _editModelId    = null;
    }

    internal List<(string Value, string Label)> AgentSourceOptions()
    {
        var list = new List<(string, string)> { ("", "— same as soul —") };
        list.AddRange(_userSources.Select(s => (s.Name, s.Name)));
        return list;
    }

    internal List<(string Value, string Label)> SourceModelOptions(ModelSource src)
    {
        var list = new List<(string, string)> { ("", "— default —") };
        list.AddRange(src.Models.Select(m => (m, m)));
        return list;
    }

    internal void OnColorPickerInput(ChangeEventArgs e) =>
        _editColor = e.Value?.ToString() ?? _editColor;

    internal async Task CommitAgentAsync()
    {
        if (SessionState.CurrentUser == null) return;
        _agentModalError = null;

        var mcpJson        = _editMcpNames.Count > 0
            ? JsonSerializer.Serialize(_editMcpNames.ToList())
            : null;
        var enabledToolIds = _editToolStates.Where(kv => kv.Value).Select(kv => kv.Key).ToList();

        if (_editingAgentId == null)
        {
            _isHiring   = true;
            _hiringDone = false;
            foreach (var line in HiringMessages)
            {
                _hiringMessage = line;
                await InvokeAsync(StateHasChanged);
                await Task.Delay(700);
            }
            _isHiring   = false;
            _hiringDone = true;
            await InvokeAsync(StateHasChanged);
            return;
        }

        try
        {
            var saved = await SubAgentService.UpdateAsync(
                _editingAgentId.Value,
                _editNickname, _editDirectives, _editColor,
                _editSourceName, _editModelId,
                mcpJson, enabledToolIds);

            await SkillService.SetAgentSkillsAsync(_editingAgentId.Value, _editAgentSkillIds);

            _subAgents = await SubAgentService.GetForUserAsync(SessionState.CurrentUser.Id);
            if (SessionState.ActiveSubAgent?.Id == saved.Id)
                SessionState.RefreshActiveSubAgent(saved);

            _agentModalSaved = true;
            CloseAgentModal();
        }
        catch (Exception ex)
        {
            _agentModalError = $"Error: {ex.Message}";
        }
    }

    internal async Task AcceptContractAsync()
    {
        if (SessionState.CurrentUser == null) return;
        _hiringDone      = false;
        _agentModalError = null;

        var mcpJson        = _editMcpNames.Count > 0
            ? JsonSerializer.Serialize(_editMcpNames.ToList())
            : null;
        var enabledToolIds = _editToolStates.Where(kv => kv.Value).Select(kv => kv.Key).ToList();

        try
        {
            var saved = await SubAgentService.CreateAsync(
                SessionState.CurrentUser.Id,
                _editName, _editPersonalityText, _editArchetypeName,
                string.IsNullOrWhiteSpace(_editNickname) ? null : _editNickname.Trim(),
                _editDirectives, _editColor,
                _editSourceName, _editModelId,
                mcpJson, enabledToolIds, _pendingSpriteKey);

            if (_editAgentSkillIds.Count > 0)
                await SkillService.SetAgentSkillsAsync(saved.Id, _editAgentSkillIds);

            _subAgents       = await SubAgentService.GetForUserAsync(SessionState.CurrentUser.Id);
            _agentModalSaved = true;
            CloseAgentModal();
        }
        catch (Exception ex)
        {
            _agentModalError = $"Error: {ex.Message}";
        }
    }

    internal void DeclineContract()
    {
        _hiringDone       = false;
        _pendingSpriteKey = null;
        CloseAgentModal();
    }

    internal void ActivateAgent(SubAgent agent)
    {
        if (SessionState.ActiveSubAgent?.Id == agent.Id)
            SessionState.ActiveSubAgent = null;
        else
            SessionState.ActiveSubAgent = agent;
        ClosePanel();
    }

    internal void StandDownAgent() => SessionState.ActiveSubAgent = null;

    internal async Task RequestDeleteAgentAsync(SubAgent agent)
    {
        if (SessionState.CurrentUser == null) return;
        _agentPendingDeleteCogCount = await CogitationService.CountByAgentAsync(SessionState.CurrentUser.Id, agent.Id);
        _agentPendingDelete = agent;
    }

    internal async Task ConfirmDeleteAgentAsync()
    {
        if (_agentPendingDelete == null || SessionState.CurrentUser == null) return;
        if (SessionState.ActiveSubAgent?.Id == _agentPendingDelete.Id)
            SessionState.ActiveSubAgent = null;
        await SubAgentService.DeleteAsync(_agentPendingDelete.Id);
        _subAgents          = await SubAgentService.GetForUserAsync(SessionState.CurrentUser.Id);
        _agentPendingDelete = null;
    }

    internal void CancelDeleteAgent() => _agentPendingDelete = null;
}
