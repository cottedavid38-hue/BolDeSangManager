using BolDeSangManager.Data.Models;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace BolDeSangManager.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
    : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<Game> Games => Set<Game>();
    public DbSet<RulesVersion> RulesVersions => Set<RulesVersion>();
    public DbSet<TeamType> TeamTypes => Set<TeamType>();
    public DbSet<PlayerPosition> PlayerPositions => Set<PlayerPosition>();
    public DbSet<PlayerPositionSkill> PlayerPositionSkills => Set<PlayerPositionSkill>();
    public DbSet<PlayerPositionCategoryAccess> PlayerPositionCategoryAccesses => Set<PlayerPositionCategoryAccess>();
    public DbSet<PoolPosition> PoolPositions => Set<PoolPosition>();
    public DbSet<PoolPositionSkill> PoolPositionSkills => Set<PoolPositionSkill>();
    public DbSet<PoolPositionCategoryAccess> PoolPositionCategoryAccesses => Set<PoolPositionCategoryAccess>();
    public DbSet<Skill> Skills => Set<Skill>();
    public DbSet<SkillCategoryDef> SkillCategories => Set<SkillCategoryDef>();
    public DbSet<League> Leagues => Set<League>();
    public DbSet<Division> Divisions => Set<Division>();
    public DbSet<EcheanceRonde> EcheancesRondes => Set<EcheanceRonde>();
    public DbSet<PalierPointsLigue> PaliersPointsLigue => Set<PalierPointsLigue>();
    public DbSet<PalierAmeliorationPsp> PaliersAmelioration => Set<PalierAmeliorationPsp>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<TeamPlayer> TeamPlayers => Set<TeamPlayer>();
    public DbSet<TeamPlayerSkill> TeamPlayerSkills => Set<TeamPlayerSkill>();
    public DbSet<PlayerInjury> PlayerInjuries => Set<PlayerInjury>();
    public DbSet<Match> Matches => Set<Match>();
    public DbSet<MatchSheet> MatchSheets => Set<MatchSheet>();
    public DbSet<MatchPlayerRecord> MatchPlayerRecords => Set<MatchPlayerRecord>();
    public DbSet<AppConfig> AppConfigs => Set<AppConfig>();
    public DbSet<PlayerImprovement> PlayerImprovements => Set<PlayerImprovement>();
    public DbSet<XpCorrection> XpCorrections => Set<XpCorrection>();
    public DbSet<PhaseDeReposValidation> PhaseDeReposValidations => Set<PhaseDeReposValidation>();
    public DbSet<LeagueAward> LeagueAwards => Set<LeagueAward>();
    public DbSet<TeamTypeKeywordLimit> TeamTypeKeywordLimits => Set<TeamTypeKeywordLimit>();
    public DbSet<LeagueCommissioner> LeagueCommissioners => Set<LeagueCommissioner>();
    public DbSet<StaffDefinition> StaffTypes => Set<StaffDefinition>();
    public DbSet<LeagueStaffType> LeagueStaffTypes => Set<LeagueStaffType>();
    public DbSet<TeamStaff> TeamStaffs => Set<TeamStaff>();
    public DbSet<SpecialRule> SpecialRules => Set<SpecialRule>();
    public DbSet<Inducement> Inducements => Set<Inducement>();
    public DbSet<StarPlayer> StarPlayers => Set<StarPlayer>();
    public DbSet<ThemedLeague> ThemedLeagues => Set<ThemedLeague>();
    public DbSet<TeamTypeSpecialRule> TeamTypeSpecialRules => Set<TeamTypeSpecialRule>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // PlayerPositionSkill — composite key
        builder.Entity<PlayerPositionSkill>()
            .HasKey(pps => new { pps.PlayerPositionId, pps.SkillId });

        // PoolPositionSkill — clé composite (miroir de PlayerPositionSkill)
        builder.Entity<PoolPositionSkill>()
            .HasKey(pps => new { pps.PoolPositionId, pps.SkillId });

        // ── Staff configurable ────────────────────────────────────────────────
        // StaffType → RulesVersion : cascade, comme la Réserve. Supprimer une
        // version emporte ses définitions de staff.
        builder.Entity<StaffDefinition>()
            .HasOne(s => s.RulesVersion)
            .WithMany(v => v.StaffTypes)
            .HasForeignKey(s => s.RulesVersionId)
            .OnDelete(DeleteBehavior.Cascade);

        // LeagueStaffType → League : cascade (supprimer la ligue emporte ses copies).
        builder.Entity<LeagueStaffType>()
            .HasOne(l => l.League)
            .WithMany(g => g.StaffTypes)
            .HasForeignKey(l => l.LeagueId)
            .OnDelete(DeleteBehavior.Cascade);

        // LeagueStaffType → StaffType : SetNull. La copie doit survivre à la
        // suppression de sa définition d'origine, sinon supprimer une version de
        // règles effacerait le staff de ligues déjà lancées.
        builder.Entity<LeagueStaffType>()
            .HasOne(l => l.StaffDefinition)
            .WithMany()
            .HasForeignKey(l => l.StaffTypeId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.Entity<TeamStaff>()
            .HasOne(t => t.Team)
            .WithMany(e => e.Staff)
            .HasForeignKey(t => t.TeamId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<TeamStaff>()
            .HasOne(t => t.LeagueStaffType)
            .WithMany(l => l.Achats)
            .HasForeignKey(t => t.LeagueStaffTypeId)
            .OnDelete(DeleteBehavior.Cascade);

        // Une équipe ne détient qu'une ligne par type de staff.
        builder.Entity<TeamStaff>()
            .HasIndex(t => new { t.TeamId, t.LeagueStaffTypeId })
            .IsUnique();

        // PoolPosition → RulesVersion (cascade : suppression version => suppression pool)
        builder.Entity<PoolPosition>()
            .HasOne(p => p.RulesVersion)
            .WithMany(v => v.PoolPositions)
            .HasForeignKey(p => p.RulesVersionId)
            .OnDelete(DeleteBehavior.Cascade);

        // PoolPositionSkill → PoolPosition (cascade) et → Skill (restrict)
        builder.Entity<PoolPositionSkill>()
            .HasOne(pps => pps.PoolPosition)
            .WithMany(p => p.CompetencesDepart)
            .HasForeignKey(pps => pps.PoolPositionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<PoolPositionSkill>()
            .HasOne(pps => pps.Skill)
            .WithMany()
            .HasForeignKey(pps => pps.SkillId)
            .OnDelete(DeleteBehavior.Restrict);

        // Match — deux FK vers Team, désactiver la suppression en cascade
        builder.Entity<Match>()
            .HasOne(m => m.EquipeDomicile)
            .WithMany()
            .HasForeignKey(m => m.EquipeDomicileId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Entity<Match>()
            .HasOne(m => m.EquipeExterieur)
            .WithMany()
            .HasForeignKey(m => m.EquipeExterieurId)
            .OnDelete(DeleteBehavior.Restrict);

        // Match — une seule MatchSheet par match
        builder.Entity<Match>()
            .HasOne(m => m.Feuille)
            .WithOne(f => f.Match)
            .HasForeignKey<MatchSheet>(f => f.MatchId);

        // League — FK vers ApplicationUser (Commissaire)
        builder.Entity<League>()
            .HasOne(l => l.Commissaire)
            .WithMany(u => u.LiguesCommissaireees)
            .HasForeignKey(l => l.CommissaireId)
            .OnDelete(DeleteBehavior.Restrict);

        // EcheanceRonde — une seule date par (ligue, ronde). Cascade : les
        // échéances n'ont aucun sens sans leur ligue.
        builder.Entity<EcheanceRonde>()
            .HasIndex(e => new { e.LeagueId, e.Ronde })
            .IsUnique();

        builder.Entity<EcheanceRonde>()
            .HasOne(e => e.League)
            .WithMany()
            .HasForeignKey(e => e.LeagueId)
            .OnDelete(DeleteBehavior.Cascade);

        // PalierPointsLigue — un seul palier par (ligue, seuil de tours).
        // Cascade : un palier n'a aucun sens hors de sa ligue.
        builder.Entity<PalierPointsLigue>()
            .HasIndex(p => new { p.LeagueId, p.APartirDuTour })
            .IsUnique();

        builder.Entity<PalierPointsLigue>()
            .HasOne(p => p.League)
            .WithMany(l => l.PaliersPoints)
            .HasForeignKey(p => p.LeagueId)
            .OnDelete(DeleteBehavior.Cascade);

        // PalierAmeliorationPsp — un seul palier par (version, rang).
        // Cascade : un palier n'a aucun sens hors de sa version de règles.
        builder.Entity<PalierAmeliorationPsp>()
            .HasIndex(p => new { p.RulesVersionId, p.Rang })
            .IsUnique();

        builder.Entity<PalierAmeliorationPsp>()
            .HasOne(p => p.RulesVersion)
            .WithMany(v => v.PaliersAmelioration)
            .HasForeignKey(p => p.RulesVersionId)
            .OnDelete(DeleteBehavior.Cascade);

        // Team — FK vers ApplicationUser (Coach)
        builder.Entity<Team>()
            .HasOne(t => t.Coach)
            .WithMany(u => u.Equipes)
            .HasForeignKey(t => t.CoachId)
            .OnDelete(DeleteBehavior.Restrict);

        // PlayerInjury — FK nullable vers Match
        builder.Entity<PlayerInjury>()
            .HasOne(pi => pi.Match)
            .WithMany(m => m.Blessures)
            .HasForeignKey(pi => pi.MatchId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.SetNull);

        // MatchSheet — FK vers ApplicationUser (SaisiPar)
        builder.Entity<MatchSheet>()
            .HasOne(ms => ms.SaisiPar)
            .WithMany()
            .HasForeignKey(ms => ms.SaisiParId)
            .OnDelete(DeleteBehavior.Restrict);

        // Team - Division (nullable)
        builder.Entity<Team>()
            .HasOne(t => t.Division)
            .WithMany(d => d.Equipes)
            .HasForeignKey(t => t.DivisionId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.SetNull);

        // Match - Division (nullable)
        builder.Entity<Match>()
            .HasOne(m => m.Division)
            .WithMany(d => d.Matchs)
            .HasForeignKey(m => m.DivisionId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.SetNull);

        // PlayerImprovement → TeamPlayer (cascade)
        builder.Entity<PlayerImprovement>()
            .HasOne(pi => pi.TeamPlayer)
            .WithMany(tp => tp.Improvements)
            .HasForeignKey(pi => pi.TeamPlayerId)
            .OnDelete(DeleteBehavior.Cascade);

        // PlayerImprovement → Skill (set null si skill supprimé)
        builder.Entity<PlayerImprovement>()
            .HasOne(pi => pi.Skill)
            .WithMany()
            .HasForeignKey(pi => pi.SkillId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.SetNull);

        // XpCorrection → TeamPlayer (cascade) : la trace suit le joueur
        builder.Entity<XpCorrection>()
            .HasOne(c => c.TeamPlayer)
            .WithMany()
            .HasForeignKey(c => c.TeamPlayerId)
            .OnDelete(DeleteBehavior.Cascade);

        // XpCorrection → auteur : on garde la trace même si le compte est supprimé
        builder.Entity<XpCorrection>()
            .HasOne(c => c.CorrigePar)
            .WithMany()
            .HasForeignKey(c => c.CorrigeParId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.Entity<XpCorrection>().Ignore(c => c.Ecart);

        // PhaseDeReposValidation → League (cascade)
        builder.Entity<PhaseDeReposValidation>()
            .HasOne(prv => prv.League)
            .WithMany(l => l.ValidationsRepos)
            .HasForeignKey(prv => prv.LeagueId)
            .OnDelete(DeleteBehavior.Cascade);

        // PhaseDeReposValidation → Team (restrict pour éviter cascade circulaire)
        builder.Entity<PhaseDeReposValidation>()
            .HasOne(prv => prv.Team)
            .WithMany()
            .HasForeignKey(prv => prv.TeamId)
            .OnDelete(DeleteBehavior.Restrict);

        // PhaseDeReposValidation — une seule validation par équipe par ligue
        builder.Entity<PhaseDeReposValidation>()
            .HasIndex(prv => new { prv.LeagueId, prv.TeamId })
            .IsUnique();

        // LeagueAward → League (cascade)
        builder.Entity<LeagueAward>()
            .HasOne(a => a.League)
            .WithMany(l => l.Awards)
            .HasForeignKey(a => a.LeagueId)
            .OnDelete(DeleteBehavior.Cascade);

        // TeamTypeKeywordLimit → TeamType (cascade)
        builder.Entity<TeamTypeKeywordLimit>()
            .HasOne(l => l.TeamType)
            .WithMany(t => t.LimitesMotsCles)
            .HasForeignKey(l => l.TeamTypeId)
            .OnDelete(DeleteBehavior.Cascade);

        // LeagueAward → TeamPlayer / Team / Coach (tous optionnels, set null)
        builder.Entity<LeagueAward>()
            .HasOne(a => a.TeamPlayer)
            .WithMany()
            .HasForeignKey(a => a.TeamPlayerId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.SetNull);

        builder.Entity<LeagueAward>()
            .HasOne(a => a.Team)
            .WithMany()
            .HasForeignKey(a => a.TeamId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.SetNull);

        builder.Entity<LeagueAward>()
            .HasOne(a => a.Coach)
            .WithMany()
            .HasForeignKey(a => a.CoachId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.SetNull);

        // LeagueCommissioner — many-to-many League↔User
        builder.Entity<LeagueCommissioner>()
            .HasOne(lc => lc.League)
            .WithMany(l => l.CommissairesDeLigue)
            .HasForeignKey(lc => lc.LeagueId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<LeagueCommissioner>()
            .HasOne(lc => lc.User)
            .WithMany()
            .HasForeignKey(lc => lc.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Entity<LeagueCommissioner>()
            .HasIndex(lc => new { lc.LeagueId, lc.UserId })
            .IsUnique();

        // TeamType → RulesVersion (Restrict pour éviter cascade circulaire avec Game→RulesVersion→TeamType)
        builder.Entity<TeamType>()
            .HasOne(t => t.RulesVersion)
            .WithMany()
            .HasForeignKey(t => t.RulesVersionId)
            .OnDelete(DeleteBehavior.Restrict);

        // Skill → RulesVersion
        builder.Entity<Skill>()
            .HasOne(s => s.RulesVersion)
            .WithMany()
            .HasForeignKey(s => s.RulesVersionId)
            .OnDelete(DeleteBehavior.Restrict);

        // SpecialRule → RulesVersion (Restrict, comme TeamType et Skill :
        // supprimer une version doit passer par SupprimerVersionAsync, qui
        // retire les enfants dans le bon ordre, pas par une cascade implicite).
        builder.Entity<SpecialRule>()
            .HasOne(r => r.RulesVersion)
            .WithMany()
            .HasForeignKey(r => r.RulesVersionId)
            .OnDelete(DeleteBehavior.Restrict);

        // Nom unique par version : deux « Capitaine » dans la même édition
        // seraient indiscernables sur une feuille d'équipe.
        builder.Entity<SpecialRule>()
            .HasIndex(r => new { r.RulesVersionId, r.Nom })
            .IsUnique();

        // Coups de pouce et star players : catalogues informatifs rattachés à
        // une version. Suppression en cascade avec la version, comme le reste
        // des données de jeu.
        builder.Entity<Inducement>()
            .HasOne(i => i.RulesVersion).WithMany()
            .HasForeignKey(i => i.RulesVersionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<Inducement>()
            .HasIndex(i => new { i.RulesVersionId, i.Nom })
            .IsUnique();

        builder.Entity<StarPlayer>()
            .HasOne(s => s.RulesVersion).WithMany()
            .HasForeignKey(s => s.RulesVersionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<StarPlayer>()
            .HasIndex(s => new { s.RulesVersionId, s.Nom })
            .IsUnique();

        // Catalogue de ligues thématiques + ses deux tables de liaison.
        builder.Entity<ThemedLeague>()
            .HasOne(l => l.RulesVersion).WithMany()
            .HasForeignKey(l => l.RulesVersionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<ThemedLeague>()
            .HasIndex(l => new { l.RulesVersionId, l.Nom })
            .IsUnique();

        builder.Entity<TeamTypeThemedLeague>()
            .HasKey(x => new { x.TeamTypeId, x.ThemedLeagueId });

        builder.Entity<TeamTypeThemedLeague>()
            .HasOne(x => x.TeamType).WithMany(t => t.LiguesListe)
            .HasForeignKey(x => x.TeamTypeId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<TeamTypeThemedLeague>()
            .HasOne(x => x.ThemedLeague).WithMany(l => l.Equipes)
            .HasForeignKey(x => x.ThemedLeagueId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<StarPlayerThemedLeague>()
            .HasKey(x => new { x.StarPlayerId, x.ThemedLeagueId });

        builder.Entity<StarPlayerThemedLeague>()
            .HasOne(x => x.StarPlayer).WithMany(s => s.Ligues)
            .HasForeignKey(x => x.StarPlayerId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<StarPlayerThemedLeague>()
            .HasOne(x => x.ThemedLeague).WithMany(l => l.StarPlayers)
            .HasForeignKey(x => x.ThemedLeagueId)
            .OnDelete(DeleteBehavior.Cascade);

        // TeamTypeSpecialRule : table de liaison, clé composite.
        builder.Entity<TeamTypeSpecialRule>()
            .HasKey(l => new { l.TeamTypeId, l.SpecialRuleId });

        // Cascade depuis la fiche d'équipe : supprimer une race retire ses
        // rattachements, jamais les règles elles-mêmes.
        builder.Entity<TeamTypeSpecialRule>()
            .HasOne(l => l.TeamType)
            .WithMany(t => t.ReglesSpecialesListe)
            .HasForeignKey(l => l.TeamTypeId)
            .OnDelete(DeleteBehavior.Cascade);

        // Restrict depuis la règle : supprimer une règle encore rattachée doit
        // échouer avec un message clair plutôt que vider silencieusement les
        // fiches d'équipe qui s'en servent.
        builder.Entity<TeamTypeSpecialRule>()
            .HasOne(l => l.SpecialRule)
            .WithMany(r => r.TeamTypes)
            .HasForeignKey(l => l.SpecialRuleId)
            .OnDelete(DeleteBehavior.Restrict);

        // SkillCategoryDef → RulesVersion (cascade : les catégories suivent leur version)
        builder.Entity<SkillCategoryDef>()
            .HasOne(c => c.RulesVersion)
            .WithMany()
            .HasForeignKey(c => c.RulesVersionId)
            .OnDelete(DeleteBehavior.Cascade);

        // Unicité du nom et du code au sein d'une même version
        builder.Entity<SkillCategoryDef>()
            .HasIndex(c => new { c.RulesVersionId, c.Nom })
            .IsUnique();
        builder.Entity<SkillCategoryDef>()
            .HasIndex(c => new { c.RulesVersionId, c.Code })
            .IsUnique();

        // Skill → SkillCategoryDef : Restrict, une catégorie utilisée ne peut pas être supprimée
        builder.Entity<Skill>()
            .HasOne(s => s.SkillCategoryDef)
            .WithMany(c => c.Competences)
            .HasForeignKey(s => s.SkillCategoryDefId)
            .OnDelete(DeleteBehavior.Restrict);

        // Accès d'un poste aux catégories — clé composite (poste, catégorie)
        builder.Entity<PlayerPositionCategoryAccess>()
            .HasKey(a => new { a.PlayerPositionId, a.SkillCategoryDefId });

        builder.Entity<PlayerPositionCategoryAccess>()
            .HasOne(a => a.PlayerPosition)
            .WithMany(p => p.AccesCategories)
            .HasForeignKey(a => a.PlayerPositionId)
            .OnDelete(DeleteBehavior.Cascade);

        // Restrict : une catégorie référencée par un accès de poste ne peut pas être supprimée
        builder.Entity<PlayerPositionCategoryAccess>()
            .HasOne(a => a.SkillCategoryDef)
            .WithMany()
            .HasForeignKey(a => a.SkillCategoryDefId)
            .OnDelete(DeleteBehavior.Restrict);

        // Idem pour la Réserve
        builder.Entity<PoolPositionCategoryAccess>()
            .HasKey(a => new { a.PoolPositionId, a.SkillCategoryDefId });

        builder.Entity<PoolPositionCategoryAccess>()
            .HasOne(a => a.PoolPosition)
            .WithMany(p => p.AccesCategories)
            .HasForeignKey(a => a.PoolPositionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<PoolPositionCategoryAccess>()
            .HasOne(a => a.SkillCategoryDef)
            .WithMany()
            .HasForeignKey(a => a.SkillCategoryDefId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
