using Aria.Web.Data;
using Aria.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Aria.Web.Components.Layout;

public partial class NavMenu
{
    // ── Contacts & Exchange ────────────────────────────────────────────────────
    internal List<ContactDto>      _contacts         = [];
    internal bool                  _contactsLoaded;
    internal List<ExchangeSession> _pendingInvites   = [];
    internal bool                  _showAddContact;
    internal string                _addContactName   = "";
    internal string                _addContactKey    = "";
    internal string?               _addContactError;
    internal string?               _inviteFormKey;
    internal string                _inviteTopic      = "";
    internal int                   _inviteRounds     = 5;
    internal string?               _inviteError;
    internal bool                  _inviteSending;

    // ── Devices (bridge nodes) panel ──
    internal List<NodeInfo>        _nodes   = [];
    internal List<PendingNodeInfo> _pending = [];
    internal readonly Dictionary<string, string> _pendingCodes = new();  // nodeId → typed join code
    internal bool           _nodeBusy;
    internal string?        _nodeError;

    // ── HIVE panel ────────────────────────────────────────────────────────────
    internal List<AgentCollective> _hiveCollectives = [];

    internal bool _keyCopied;

    // ── Souls panel toggle (loads contacts when opening) ──────────────────────

    internal async Task ToggleSoulsPanelAsync()
    {
        TogglePanel("souls");
        if (_activePanel == "souls" && !_contactsLoaded)
            await LoadContactsAsync();
    }

    // ── Contacts ──────────────────────────────────────────────────────────────

    internal async Task LoadContactsAsync()
    {
        var userId = SessionState.CurrentUser?.Id.ToString();
        if (userId == null || !BridgeClient.HasBridge(userId)) return;
        _contacts       = await BridgeClient.GetContactsAsync(userId);
        _contactsLoaded = true;
        StateHasChanged();
    }

    internal void ToggleInviteForm(string publicKey)
    {
        _inviteFormKey = _inviteFormKey == publicKey ? null : publicKey;
        _inviteTopic   = "";
        _inviteRounds  = 5;
        _inviteError   = null;
    }

    internal void CloseInviteForm()
    {
        _inviteFormKey = null;
        _inviteTopic   = "";
        _inviteError   = null;
    }

    internal void ToggleAddContact()
    {
        _showAddContact  = !_showAddContact;
        _addContactName  = "";
        _addContactKey   = "";
        _addContactError = null;
    }

    internal async Task AddContactAsync()
    {
        _addContactError = null;
        var userId = SessionState.CurrentUser?.Id.ToString();
        if (userId == null) return;

        if (string.IsNullOrWhiteSpace(_addContactName) || string.IsNullOrWhiteSpace(_addContactKey))
        {
            _addContactError = "Name and public key required.";
            return;
        }

        var ok = await BridgeClient.AddContactAsync(userId, _addContactName.Trim(), _addContactKey.Trim());
        if (ok)
        {
            _showAddContact = false;
            _addContactName = "";
            _addContactKey  = "";
            await LoadContactsAsync();
        }
        else
        {
            _addContactError = "Failed to add contact. Check that the key is valid and not already added.";
        }
    }

    internal async Task SendInviteAsync(ContactDto contact)
    {
        _inviteError   = null;
        _inviteSending = true;
        StateHasChanged();

        if (!ExchangeSessionService.IsTopicAllowed(_inviteTopic))
        {
            _inviteError   = "Topic contains prohibited content.";
            _inviteSending = false;
            return;
        }

        var user = SessionState.CurrentUser;
        if (user == null) { _inviteSending = false; return; }

        var recipient = await UserService.GetByPublicKeyAsync(contact.PublicKey);
        if (recipient == null)
        {
            _inviteError   = "Recipient not registered on this server.";
            _inviteSending = false;
            return;
        }
        if (recipient.Id == user.Id)
        {
            _inviteError   = "Cannot invite yourself.";
            _inviteSending = false;
            return;
        }

        var agentLabel = SessionState.ActiveSubAgent?.GeneratedName ?? "ARIA";
        var agentId    = SessionState.ActiveSubAgent?.Id;

        var session = ExchangeService.CreateInvite(
            user.Id, user.Name, agentLabel, agentId,
            recipient.Id, recipient.Name,
            _inviteTopic.Trim(), _inviteRounds);

        _inviteSending = false;
        CloseInviteForm();
        Nav.NavigateTo($"/exchange/{session.Id}");
    }

    internal void AcceptInvite(ExchangeSession inv)
    {
        var label = SessionState.ActiveSubAgent?.GeneratedName ?? "ARIA";
        var agId  = SessionState.ActiveSubAgent?.Id;
        ExchangeService.AcceptInvite(inv.Id, label, agId);
        _pendingInvites = ExchangeService.GetPendingForUser(SessionState.CurrentUser!.Id).ToList();
        Nav.NavigateTo($"/exchange/{inv.Id}");
    }

    internal void DeclineInvite(ExchangeSession inv)
    {
        ExchangeService.DeclineInvite(inv.Id);
        _pendingInvites = ExchangeService.GetPendingForUser(SessionState.CurrentUser!.Id).ToList();
    }

    internal void OnExchangeInviteReceived(string recipientUserId, ExchangeSession _)
    {
        if (recipientUserId != SessionState.CurrentUser?.Id) return;
        _pendingInvites = ExchangeService.GetPendingForUser(recipientUserId).ToList();
        InvokeAsync(StateHasChanged);
    }

    internal void OnExchangeStatusChanged(string _)
    {
        var userId = SessionState.CurrentUser?.Id;
        if (userId is { Length: > 0 })
            _pendingInvites = ExchangeService.GetPendingForUser(userId).ToList();
        InvokeAsync(StateHasChanged);
    }

    internal async Task CopyPublicKey(string key)
    {
        await JS.InvokeVoidAsync("navigator.clipboard.writeText", key);
        _keyCopied = true;
        StateHasChanged();
        await Task.Delay(2000);
        _keyCopied = false;
    }

    internal async Task CopyText(string text) =>
        await JS.InvokeVoidAsync("navigator.clipboard.writeText", text);

    internal bool _guidCopied;
    internal async Task CopyGuid(string guid)
    {
        await JS.InvokeVoidAsync("navigator.clipboard.writeText", guid);
        _guidCopied = true;
        StateHasChanged();
        await Task.Delay(2000);
        _guidCopied = false;
    }

    // ── Devices panel actions ──────────────────────────────────────────────────

    // Just-approved device (nodeId + label): drives the "detection can take a few minutes" notice
    // so the wait after APPROVE doesn't read as a failure. Cleared once the node connects.
    internal string? _justApprovedNodeId;
    internal string? _justApprovedLabel;

    internal async Task LoadNodesAsync()
    {
        if (SessionState.CurrentUser is not { } u) return;
        _nodes   = await NodeService.GetNodesAsync(u.Id);
        _pending = NodeService.GetPending(u.Id).ToList();
        if (_justApprovedNodeId != null && _nodes.Any(n => n.NodeId == _justApprovedNodeId && n.Online))
            _justApprovedNodeId = _justApprovedLabel = null;   // it's online — notice served its purpose
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>Drives the amber pulsing dot on the "// DEVICES" nav item — true while a device is
    /// awaiting pairing approval. Read live from the enrollment service so it works even when the
    /// devices panel has never been opened (the whole point: the user must NOTICE the request).</summary>
    internal bool HasPendingDevices =>
        SessionState.CurrentUser is { } u && PendingEnrollments.List(u.Id).Count > 0;

    // Live refresh when a device registers itself for pairing (or one is approved/expires).
    internal void OnPendingEnrollmentsChanged(string userId)
    {
        if (SessionState.CurrentUser?.Id != userId) return;
        if (_activePanel == "devices") { _ = LoadNodesAsync(); return; }
        _ = InvokeAsync(StateHasChanged);   // panel closed — still update the nav pending dot
    }

    internal async Task ApproveDeviceAsync(string nodeId)
    {
        if (_nodeBusy || SessionState.CurrentUser is not { } u) return;
        _nodeError = null;
        var code = (_pendingCodes.GetValueOrDefault(nodeId) ?? "");
        var digits = new string(code.Where(char.IsDigit).ToArray());
        if (digits.Length == 0) { _nodeError = "Enter the join code shown on the device"; return; }
        _nodeBusy = true;
        StateHasChanged();
        var (ok, error, _) = await NodeService.ApprovePendingAsync(u.Id, nodeId, digits);
        _nodeBusy = false;
        if (ok)
        {
            _justApprovedNodeId = nodeId;
            _justApprovedLabel  = _pending.FirstOrDefault(p => p.NodeId == nodeId)?.Label;
            _pendingCodes.Remove(nodeId);
            await LoadNodesAsync();
        }
        else    { _nodeError = error ?? "Approval failed"; StateHasChanged(); }
    }

    // Full delete (not a revoke tombstone): the device must re-pair to return.
    internal async Task RevokeNodeAsync(string nodeId)
    {
        if (SessionState.CurrentUser is not { } u) return;
        _nodeError = null;
        var (ok, error) = await NodeService.DeleteNodeAsync(u.Id, nodeId);
        if (ok) await LoadNodesAsync();
        else { _nodeError = error ?? "Deletion failed"; StateHasChanged(); }
    }

    // ── Layer A: trust THIS browser (defense-in-depth plan §3) ──────────────────
    // The fetch must originate in the browser so the HttpOnly aria-device cookie is sent, and it drives
    // the node Seal (the node opens a local approval page). Kept off the circuit's own HTTP call for
    // that reason. Blocks until the human approves at the node (or the 3-min ceremony times out).
    internal bool    _trustDeviceBusy;
    internal string? _trustDeviceMsg;

    internal async Task TrustThisBrowserAsync()
    {
        if (_trustDeviceBusy || SessionState.CurrentUser is not { } u || !SoulVerified) return;
        _trustDeviceMsg = "Approve on your node…";
        _trustDeviceBusy = true;
        StateHasChanged();

        TrustDeviceResult? result = null;
        try { result = await JS.InvokeAsync<TrustDeviceResult>("ariaInterop.trustThisBrowser", u.Id, "This browser"); }
        catch (Exception ex) { result = new TrustDeviceResult { Ok = false, Error = ex.Message }; }

        _trustDeviceBusy = false;
        _trustDeviceMsg = result is { Ok: true }
            ? "✓ This browser is now trusted — it can reach the terminal from any network."
            : $"✕ {result?.Error ?? "Not approved"}";
        StateHasChanged();
    }

    internal sealed class TrustDeviceResult
    {
        public bool    Ok    { get; set; }
        public string? Error { get; set; }
    }

    // ── HIVE panel ────────────────────────────────────────────────────────────

    internal async Task NewHiveCollectiveAsync()
    {
        if (SessionState.CurrentUser == null) return;
        var c = await CollectiveService.CreateAsync(SessionState.CurrentUser.Id, "New Collective");
        _hiveCollectives = await CollectiveService.GetListAsync(SessionState.CurrentUser.Id);
        OpenHiveCollective(c.Id);
    }

    internal void OpenHiveCollective(int collectiveId)
    {
        ClosePanel();
        Nav.NavigateTo($"/hive/{collectiveId}");
    }

    internal void OpenWarPlanner()
    {
        ClosePanel();
        if (!Nav.Uri.Contains("/wargame"))
            Nav.NavigateTo("/wargame");
    }

    internal async Task DeleteHiveCollectiveAsync(int id)
    {
        await CollectiveService.DeleteAsync(id);
        if (SessionState.CurrentUser != null)
            _hiveCollectives = await CollectiveService.GetListAsync(SessionState.CurrentUser.Id);
    }

    // Live rename from the Hive page (debounced there) — reflects immediately in this nav list
    // instead of waiting for the next full reload.
    private void OnCollectiveRenamed(int collectiveId, string name)
    {
        var row = _hiveCollectives.FirstOrDefault(c => c.Id == collectiveId);
        if (row == null) return;
        row.Name = name;
        _ = InvokeAsync(StateHasChanged);
    }
}
