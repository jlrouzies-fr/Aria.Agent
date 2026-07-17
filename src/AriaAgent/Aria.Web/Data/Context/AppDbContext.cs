using Microsoft.EntityFrameworkCore;

namespace Aria.Web.Data.Context;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User>                 Users                 => Set<User>();
    public DbSet<UserToolConfig>       UserToolConfigs       => Set<UserToolConfig>();
    public DbSet<UserMcpServer>        UserMcpServers        => Set<UserMcpServer>();
    public DbSet<Cogitation>           Cogitations           => Set<Cogitation>();
    public DbSet<CogitationFolder>     CogitationFolders     => Set<CogitationFolder>();
    public DbSet<CogitationMessage>    CogitationMessages    => Set<CogitationMessage>();
    public DbSet<UserSourcePreference> UserSourcePreferences => Set<UserSourcePreference>();

    public DbSet<ModelFormatCache> ModelFormatCaches => Set<ModelFormatCache>();
    public DbSet<UserVoxSettings>  UserVoxSettings    => Set<UserVoxSettings>();
    public DbSet<SubAgent>         SubAgents          => Set<SubAgent>();
    public DbSet<SubAgentToolState> SubAgentToolStates => Set<SubAgentToolState>();
    public DbSet<Skill>            Skills             => Set<Skill>();
    public DbSet<SubAgentSkill>    SubAgentSkills     => Set<SubAgentSkill>();

    public DbSet<AgentCronJob>     AgentCronJobs     => Set<AgentCronJob>();
    // Channels are node-authoritative (stored on the bridge, not the server) — no server-side table.
    public DbSet<SoulNodeKey>      SoulNodeKeys      => Set<SoulNodeKey>();
    public DbSet<SyncRecord>       SyncRecords       => Set<SyncRecord>();
    public DbSet<UiAccessKnock>    UiAccessKnocks    => Set<UiAccessKnock>();
    public DbSet<TrustedDevice>    TrustedDevices    => Set<TrustedDevice>();

    public DbSet<WargameMap>      WargameMaps      => Set<WargameMap>();
    public DbSet<WargameFaction>  WargameFactions  => Set<WargameFaction>();
    public DbSet<WargameTile>     WargameTiles     => Set<WargameTile>();
    public DbSet<WargameUnit>     WargameUnits     => Set<WargameUnit>();
    public DbSet<WargameTurnLog>  WargameTurnLogs  => Set<WargameTurnLog>();
    public DbSet<WargameBuilding> WargameBuildings => Set<WargameBuilding>();

    // Hive / Collective
    public DbSet<AgentCollective>  AgentCollectives  => Set<AgentCollective>();
    public DbSet<CollectiveMember> CollectiveMembers => Set<CollectiveMember>();
    public DbSet<MemberEdgeNode>   MemberEdgeNodes   => Set<MemberEdgeNode>();
    public DbSet<CollectiveTask>   CollectiveTasks   => Set<CollectiveTask>();
    public DbSet<CollectiveEvent>  CollectiveEvents  => Set<CollectiveEvent>();

    protected override void OnModelCreating(ModelBuilder m)
    {
        m.Entity<User>()
         .HasIndex(u => u.Name)
         .IsUnique();

        m.Entity<UserToolConfig>()
         .HasIndex(c => new { c.UserId, c.ToolId })
         .IsUnique();

        m.Entity<UserSourcePreference>()
         .HasIndex(p => new { p.UserId, p.SourceName })
         .IsUnique();

        m.Entity<ModelFormatCache>()
         .HasIndex(c => new { c.EndpointUrl, c.ModelId })
         .IsUnique();

        m.Entity<UserVoxSettings>()
         .HasIndex(v => v.UserId)
         .IsUnique();

        m.Entity<SubAgent>()
         .HasOne(a => a.User).WithMany()
         .HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Cascade);

        m.Entity<SubAgentToolState>()
         .HasOne(s => s.SubAgent).WithMany(a => a.ToolStates)
         .HasForeignKey(s => s.SubAgentId).OnDelete(DeleteBehavior.Cascade);

        m.Entity<SubAgentToolState>()
         .HasIndex(s => new { s.SubAgentId, s.ToolId })
         .IsUnique();

        m.Entity<Skill>()
         .HasOne(s => s.User).WithMany()
         .HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);

        m.Entity<SubAgentSkill>()
         .HasOne(x => x.SubAgent).WithMany(a => a.SubAgentSkills)
         .HasForeignKey(x => x.SubAgentId).OnDelete(DeleteBehavior.Cascade);

        m.Entity<SubAgentSkill>()
         .HasOne(x => x.Skill).WithMany(s => s.SubAgentSkills)
         .HasForeignKey(x => x.SkillId).OnDelete(DeleteBehavior.Cascade);

        m.Entity<SubAgentSkill>()
         .HasIndex(x => new { x.SubAgentId, x.SkillId })
         .IsUnique();

        m.Entity<Cogitation>()
         .HasOne(c => c.SubAgent).WithMany()
         .HasForeignKey(c => c.SubAgentId).OnDelete(DeleteBehavior.SetNull);

        m.Entity<Cogitation>()
         .HasOne(c => c.Collective).WithMany()
         .HasForeignKey(c => c.CollectiveId).OnDelete(DeleteBehavior.SetNull);

        m.Entity<Cogitation>()
         .HasOne(c => c.Folder).WithMany(f => f.Cogitations)
         .HasForeignKey(c => c.FolderId).OnDelete(DeleteBehavior.SetNull);

        m.Entity<CogitationFolder>()
         .HasOne(f => f.User).WithMany()
         .HasForeignKey(f => f.UserId).OnDelete(DeleteBehavior.Cascade);

        m.Entity<AgentCronJob>()
         .HasOne(j => j.User).WithMany()
         .HasForeignKey(j => j.UserId).OnDelete(DeleteBehavior.Cascade);

        m.Entity<AgentCronJob>()
         .HasOne(j => j.SubAgent).WithMany()
         .HasForeignKey(j => j.SubAgentId).OnDelete(DeleteBehavior.SetNull);

        m.Entity<SyncRecord>()
         .HasIndex(r => new { r.UserId, r.EntityType, r.EntityId })
         .IsUnique();
        m.Entity<SyncRecord>()
         .HasIndex(r => new { r.UserId, r.UpdatedAt });

        m.Entity<UiAccessKnock>()
         .HasIndex(k => k.UserId);
        m.Entity<UiAccessKnock>()
         .HasIndex(k => k.ExpiresAt);

        m.Entity<UiAccessKnock>()
         .HasIndex(k => k.UserId);

        m.Entity<WargameTile>()
         .HasOne(t => t.Map).WithMany(mp => mp.Tiles)
         .HasForeignKey(t => t.MapId).OnDelete(DeleteBehavior.Cascade);

        m.Entity<WargameTile>()
         .HasOne(t => t.Owner).WithMany()
         .HasForeignKey(t => t.OwnerFactionId).OnDelete(DeleteBehavior.SetNull);

        m.Entity<WargameUnit>()
         .HasOne(u => u.Faction).WithMany(f => f.Units)
         .HasForeignKey(u => u.FactionId).OnDelete(DeleteBehavior.Cascade);

        m.Entity<WargameTurnLog>()
         .HasOne(l => l.Faction).WithMany(f => f.TurnLogs)
         .HasForeignKey(l => l.FactionId).OnDelete(DeleteBehavior.Cascade);

        m.Entity<WargameBuilding>()
         .HasOne(b => b.Faction).WithMany(f => f.Buildings)
         .HasForeignKey(b => b.FactionId).OnDelete(DeleteBehavior.Cascade);

        // ── Hive / Collective ─────────────────────────────────────────────
        m.Entity<AgentCollective>()
         .HasOne(c => c.User).WithMany()
         .HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade);

        m.Entity<AgentCollective>()
         .HasOne(c => c.OvermindSubAgent).WithMany()
         .HasForeignKey(c => c.OvermindSubAgentId).OnDelete(DeleteBehavior.SetNull);

        m.Entity<CollectiveMember>()
         .HasOne(mbr => mbr.Collective).WithMany(c => c.Members)
         .HasForeignKey(mbr => mbr.CollectiveId).OnDelete(DeleteBehavior.Cascade);

        m.Entity<CollectiveMember>()
         .HasOne(mbr => mbr.SubAgent).WithMany()
         .HasForeignKey(mbr => mbr.SubAgentId).OnDelete(DeleteBehavior.Cascade);

        m.Entity<MemberEdgeNode>()
         .HasOne(n => n.Member).WithMany(mbr => mbr.EdgeNodes)
         .HasForeignKey(n => n.MemberId).OnDelete(DeleteBehavior.Cascade);

        m.Entity<CollectiveTask>()
         .HasOne(t => t.Collective).WithMany(c => c.Tasks)
         .HasForeignKey(t => t.CollectiveId).OnDelete(DeleteBehavior.Cascade);

        m.Entity<CollectiveTask>()
         .HasOne(t => t.AssignedMember).WithMany()
         .HasForeignKey(t => t.AssignedMemberId).OnDelete(DeleteBehavior.SetNull);

        m.Entity<CollectiveEvent>()
         .HasOne(ev => ev.Collective).WithMany(c => c.Events)
         .HasForeignKey(ev => ev.CollectiveId).OnDelete(DeleteBehavior.Cascade);

        m.Entity<CollectiveEvent>()
         .HasOne(ev => ev.ActorMember).WithMany()
         .HasForeignKey(ev => ev.ActorMemberId).OnDelete(DeleteBehavior.SetNull);
    }
}
