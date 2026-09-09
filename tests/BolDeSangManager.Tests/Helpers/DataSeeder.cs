using BolDeSangManager.Data;
using BolDeSangManager.Data.Enums;
using BolDeSangManager.Data.Models;
using BolDeSangManager.Services;
using Microsoft.EntityFrameworkCore;

namespace BolDeSangManager.Tests.Helpers;

/// <summary>
/// Insère les données minimales nécessaires aux tests dans le DbContext fourni.
/// </summary>
public static class DataSeeder
{
    public static ApplicationUser CreateUser(string suffix = "")
    {
        var id = Guid.NewGuid().ToString();
        var email = $"user{suffix}@test.fr";
        return new ApplicationUser
        {
            Id = id,
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid().ToString(),
            ConcurrencyStamp = Guid.NewGuid().ToString(),
            PseudoCoach = $"Coach{suffix}"
        };
    }

    public static async Task<(Game game, RulesVersion version)> SeedGameAsync(ApplicationDbContext db)
    {
        var game = new Game { Nom = "Blood Bowl", Type = GameType.BloodBowl };
        db.Games.Add(game);
        await db.SaveChangesAsync();

        var version = new RulesVersion { GameId = game.Id, Nom = "Saison 3", EstActive = true, Ordre = 1 };
        db.RulesVersions.Add(version);
        await db.SaveChangesAsync();

        return (game, version);
    }

    /// <summary>
    /// Renvoie l'identifiant de la catégorie <paramref name="nom"/> pour cette version,
    /// en la créant au besoin. Les compétences exigent une catégorie depuis R2.
    /// </summary>
    public static async Task<int> GetOrCreateCategorieAsync(
        ApplicationDbContext db, int versionId, string nom = "Générale", string code = "G")
    {
        var existante = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .FirstOrDefaultAsync(db.SkillCategories.Where(c => c.RulesVersionId == versionId && c.Nom == nom));
        if (existante is not null) return existante.Id;

        var cat = new SkillCategoryDef { RulesVersionId = versionId, Nom = nom, Code = code };
        db.SkillCategories.Add(cat);
        await db.SaveChangesAsync();
        return cat.Id;
    }

    public static async Task<(TeamType teamType, PlayerPosition position)> SeedTeamTypeAsync(
        ApplicationDbContext db, int gameId)
    {
        var versionId = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .FirstAsync(db.RulesVersions.Where(v => v.GameId == gameId).Select(v => v.Id));

        var teamType = new TeamType
        {
            GameId = gameId,
            RulesVersionId = versionId,
            Nom = "Humains",
            CoutRelance = 50_000,
            Categorie = 2
        };
        db.TeamTypes.Add(teamType);
        await db.SaveChangesAsync();

        var position = new PlayerPosition
        {
            TeamTypeId = teamType.Id,
            Nom = "Lineman",
            QuantiteMax = 16,
            Cout = 50_000,
            Mouvement = 6,
            Force = 3,
            Agilite = "3+",
            CapacitePasse = "4+",
            Armure = "9+",
            CompetencesPrincipales = "G",
            CompetencesSecondaires = "AF"
        };
        db.PlayerPositions.Add(position);
        await db.SaveChangesAsync();

        return (teamType, position);
    }

    public static async Task<(Skill normal, Skill elite)> SeedSkillsAsync(ApplicationDbContext db)
    {
        var versionId = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .FirstAsync(db.RulesVersions.Select(v => v.Id));

        var catGenerale = await GetOrCreateCategorieAsync(db, versionId, "Générale", "G");
        var catScelerate = await GetOrCreateCategorieAsync(db, versionId, "Scélérate", "S");

        var normal = new Skill
        {
            RulesVersionId = versionId,
            Nom = "Blocage",
            Categorie = SkillCategory.Generale,
            SkillCategoryDefId = catGenerale,
            Description = "Compétence de blocage.",
            EstElite = false,
            EstTrait = false
        };
        var elite = new Skill
        {
            RulesVersionId = versionId,
            Nom = "Meurtre Prémédité",
            Categorie = SkillCategory.Scelerate,
            SkillCategoryDefId = catScelerate,
            Description = "Compétence élite.",
            EstElite = true,
            EstTrait = false
        };
        db.Skills.AddRange(normal, elite);
        await db.SaveChangesAsync();
        return (normal, elite);
    }

    public static async Task<League> SeedLeagueAsync(
        ApplicationDbContext db, int gameId, int rulesVersionId, string commissaireId,
        LeagueStatus statut = LeagueStatus.Inscription,
        LeagueFormat format = LeagueFormat.RoundRobinAvecPlayoffs)
    {
        var ligue = new League
        {
            Nom = "Ligue de Test",
            Description = "Tests automatisés",
            CommissaireId = commissaireId,
            GameId = gameId,
            RulesVersionId = rulesVersionId,
            Format = format,
            BudgetDepart = 1_000_000,
            NombreEquipesPlayoff = 4,
            Statut = statut,
            CreeLe = DateTime.UtcNow
        };
        db.Leagues.Add(ligue);
        await db.SaveChangesAsync();

        // Staff standard de la ligue. En production c'est CreerLigueAsync qui le
        // copie depuis les règles ; ici les ligues sont insérées directement, il
        // faut donc le matérialiser sinon aucune équipe ne peut acheter de staff.
        if (!await db.LeagueStaffTypes.AnyAsync(l => l.LeagueId == ligue.Id))
        {
            db.LeagueStaffTypes.AddRange(
                new LeagueStaffType
                {
                    LeagueId = ligue.Id, Nom = StaffService.NomFans, Ordre = 1,
                    Cout = 10_000, MinCreation = 0, MaxCreation = 9
                },
                new LeagueStaffType
                {
                    LeagueId = ligue.Id, Nom = StaffService.NomRelances, Ordre = 2,
                    Cout = 0, CoutDepuisTypeEquipe = true, MinCreation = 0, MaxCreation = 8, MaxLigue = 8
                },
                new LeagueStaffType
                {
                    LeagueId = ligue.Id, Nom = StaffService.NomCoachs, Ordre = 3,
                    Cout = 10_000, MinCreation = 0, MaxCreation = 6
                },
                new LeagueStaffType
                {
                    LeagueId = ligue.Id, Nom = StaffService.NomCheerleaders, Ordre = 4,
                    Cout = 10_000, MinCreation = 0, MaxCreation = 6
                },
                new LeagueStaffType
                {
                    LeagueId = ligue.Id, Nom = StaffService.NomApothicaire, Ordre = 5,
                    Cout = 50_000, MinCreation = 0, MaxCreation = 1, MaxLigue = 1
                });
            await db.SaveChangesAsync();
        }

        return ligue;
    }

    public static async Task<Team> SeedTeamAsync(
        ApplicationDbContext db, int ligueId, string coachId, int teamTypeId, string nom = "Équipe A")
    {
        var team = new Team
        {
            Nom = nom,
            CoachId = coachId,
            LeagueId = ligueId,
            TeamTypeId = teamTypeId,
            Tresorerie = 500_000,
            FansDevoues = 1,
            NombreRelances = 2,
            CreeLe = DateTime.UtcNow
        };
        db.Teams.Add(team);
        await db.SaveChangesAsync();
        return team;
    }

    public static async Task<TeamPlayer> SeedPlayerAsync(
        ApplicationDbContext db, int teamId, int positionId, string nom = "Joueur 1", int numero = 1)
    {
        var player = new TeamPlayer
        {
            TeamId = teamId,
            PlayerPositionId = positionId,
            Nom = nom,
            Numero = numero,
            RecruteLe = DateTime.UtcNow
        };
        db.TeamPlayers.Add(player);
        await db.SaveChangesAsync();
        return player;
    }

    /// <summary>
    /// Crée un match minimal (sans division) reliant deux équipes.
    /// </summary>
    public static async Task<Match> SeedMatchAsync(
        ApplicationDbContext db, int domicileId, int exterieurId, int? divisionId = null)
    {
        var match = new Match
        {
            DivisionId = divisionId,
            Ronde = 1,
            EquipeDomicileId = domicileId,
            EquipeExterieurId = exterieurId,
            Statut = MatchStatus.AJouer,
            EstPlayoff = false
        };
        db.Matches.Add(match);
        await db.SaveChangesAsync();
        return match;
    }
}
