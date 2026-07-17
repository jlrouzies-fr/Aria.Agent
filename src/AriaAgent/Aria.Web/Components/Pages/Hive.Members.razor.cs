using Aria.Web.Data;
using Aria.Web.Services;

namespace Aria.Web.Components.Pages;

public partial class Hive
{
    public List<SubAgent> _recruitableAgents =>
        _allAgents.Where(a => _members.All(m => m.SubAgentId != a.Id)).ToList();

    public async Task RecruitDroneAsync(int subAgentId)
    {
        if (_collective == null) return;
        await CollectiveService.AddMemberAsync(_collective.Id, subAgentId, null);
        await RefreshAsync();
    }

    public async Task RemoveMemberAsync(int memberId)
    {
        await CollectiveService.RemoveMemberAsync(memberId);
        await RefreshAsync();
    }

    public void SelectDrone(CollectiveMember m) => _selectedDrone = m;
    public void CloseDroneDrawer() => _selectedDrone = null;
}
