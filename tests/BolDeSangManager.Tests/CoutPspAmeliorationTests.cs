using BolDeSangManager.Data.Enums;
using BolDeSangManager.Data.Models;
using BolDeSangManager.Services;
using BolDeSangManager.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace BolDeSangManager.Tests;

/// <summary>
/// Coût en PSP d'une amélioration : le barème PROPOSE, le coach dispose.
///
/// Décision produit explicite : le coût n'est jamais imposé, exactement comme
/// l'XP de la feuille de match depuis R4. Le service ne refuse que ce qui
/// dépasse la cagnotte du joueur.
///
/// ⚠️ Ces tests verrouillent aussi les deux `Include` de
/// <c>MatchService.GetMatchAsync</c> dont dépend l'écran d'après-match : sans
/// <c>Improvements</c> le rang vaudrait toujours 1, sans <c>RulesVersion</c> le
/// barème de l'association serait ignoré au profit du LRB — deux pannes
/// SILENCIEUSES qu'aucune erreur ne signale.
/// </summary>
public class CoutPspAmeliorationTests : IDisposable
{
    private readonly TestDbFactory _factory = new();
    public void Dispose() => _factory.Dispose();

    /// <summary>
    /// Le service accepte une valeur DIFFÉRENTE du barème : c'est la définition
    /// même d'une proposition corrigeable.
    /// </summary>
    [Fact]
    public async Task AppliquerAmelioration_AccepteUneValeurCorrigeeALaMain()
    {
        await using var db = _factory.CreateContext();
        var (joueur, skill) = await SeedJoueurAvecPspAsync(db, psp: 30);

        var svc = new TeamService(db, NullLogger<TeamService>.Instance);

        // Le barème propose 6 PSP pour une principale choisie au rang 1 ; le
        // coach en dépense volontairement 9.
        await svc.AppliquerAmeliorationAsync(
            joueur.Id, ImprovementType.SelectionPrimaire, skillId: skill.Id, pspDepensee: 9);

        await using var relecture = _factory.CreateContext();
        var relu = await relecture.TeamPlayers
            .Include(p => p.Improvements)
            .FirstAsync(p => p.Id == joueur.Id);

        Assert.Equal(21, relu.PointsStarPlayer);                     // 30 − 9
        Assert.Equal(9, Assert.Single(relu.Improvements).PspDepensee); // la valeur SAISIE
    }

    /// <summary>
    /// Contre-épreuve : seule la cagnotte fait limite. Sans ce test, un service
    /// qui n'imposerait rien du tout passerait le test ci-dessus.
    /// </summary>
    [Fact]
    public async Task AppliquerAmelioration_RefuseAuDelaDeLaCagnotte()
    {
        await using var db = _factory.CreateContext();
        var (joueur, skill) = await SeedJoueurAvecPspAsync(db, psp: 5);

        var svc = new TeamService(db, NullLogger<TeamService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.AppliquerAmeliorationAsync(
                joueur.Id, ImprovementType.SelectionPrimaire, skillId: skill.Id, pspDepensee: 6));
    }

    /// <summary>
    /// Le rang de la prochaine amélioration = nombre d'améliorations déjà prises
    /// + 1. C'est lui qui commande le coût proposé, et il vient d'une collection
    /// qui doit être chargée par la requête.
    /// </summary>
    [Fact]
    public async Task RangProchaineAmelioration_SuitLeNombreDAmeliorations()
    {
        await using var db = _factory.CreateContext();
        var (joueur, skill) = await SeedJoueurAvecPspAsync(db, psp: 60);

        var svc = new TeamService(db, NullLogger<TeamService>.Instance);
        var bareme = BaremeAmelioration.ParDefaut();

        // Rang 1 : le LRB propose 6 PSP pour une principale choisie.
        Assert.Equal(6, bareme.CoutPsp(ImprovementType.SelectionPrimaire, 1));
        await svc.AppliquerAmeliorationAsync(
            joueur.Id, ImprovementType.SelectionPrimaire, skillId: skill.Id, pspDepensee: 6);

        await using var relecture = _factory.CreateContext();
        var relu = await relecture.TeamPlayers
            .Include(p => p.Improvements)
            .FirstAsync(p => p.Id == joueur.Id);

        var rangSuivant = relu.Improvements.Count + 1;
        Assert.Equal(2, rangSuivant);
        // Rang 2 : plus cher que le rang 1 — c'est toute la logique du tableau.
        Assert.Equal(8, bareme.CoutPsp(ImprovementType.SelectionPrimaire, rangSuivant));
    }

    /// <summary>
    /// ⚠️ Test anti-régression des `Include` : il part d'une VRAIE requête de
    /// service (<c>GetMatchAsync</c>), pas d'un objet monté à la main. Retirer
    /// l'`Include` de `Improvements` ou de `RulesVersion` le fait tomber.
    /// </summary>
    [Fact]
    public async Task GetMatchAsync_ChargeLesAmeliorationsEtLeBaremeDeVersion()
    {
        await using var db = _factory.CreateContext();
        var contexte = await SeedMatchCompletAsync(db);

        await using var lecture = _factory.CreateContext();
        var settings = new SettingsService(lecture);
        var svc = new MatchService(lecture, NullLogger<MatchService>.Instance,
            new GmailEmailSender(settings, NullLogger<GmailEmailSender>.Instance), settings);
        var match = await svc.GetMatchAsync(contexte.MatchId);

        Assert.NotNull(match);

        // Le barème de la version doit être atteignable depuis le match.
        var version = match!.Division?.League?.RulesVersion;
        Assert.NotNull(version);
        var bareme = BaremeAmelioration.DeVersion(version);
        Assert.Equal(25_000, bareme.Hausse(ImprovementType.SelectionPrimaire));  // valeur maison
        Assert.Equal(2, bareme.CoutPsp(ImprovementType.SelectionPrimaire, 1));   // palier maison

        // Les améliorations du joueur doivent être chargées : c'est ce qui donne
        // le rang, donc le coût proposé.
        var record = match.Feuille!.RecordsJoueurs.First(r => r.TeamPlayerId == contexte.JoueurId);
        Assert.Single(record.TeamPlayer!.Improvements);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static async Task<(TeamPlayer joueur, Skill skill)> SeedJoueurAvecPspAsync(
        Data.ApplicationDbContext db, int psp)
    {
        var (game, version) = await DataSeeder.SeedGameAsync(db);
        var (teamType, position) = await DataSeeder.SeedTeamTypeAsync(db, game.Id);
        var (skill, _) = await DataSeeder.SeedSkillsAsync(db);

        var coach = DataSeeder.CreateUser("coach_psp");
        db.Users.Add(coach);
        await db.SaveChangesAsync();

        var ligue  = await DataSeeder.SeedLeagueAsync(db, game.Id, version.Id, coach.Id);
        var equipe = await DataSeeder.SeedTeamAsync(db, ligue.Id, coach.Id, teamType.Id);
        var joueur = await DataSeeder.SeedPlayerAsync(db, equipe.Id, position.Id);

        joueur.PointsStarPlayer = psp;
        await db.SaveChangesAsync();

        return (joueur, skill);
    }

    private sealed record ContexteMatch(int MatchId, int JoueurId);

    /// <summary>
    /// Un match complet, avec une version au barème NON standard : si la requête
    /// oublie de charger la version, le test lira les valeurs LRB et échouera.
    /// </summary>
    private static async Task<ContexteMatch> SeedMatchCompletAsync(Data.ApplicationDbContext db)
    {
        var (game, version) = await DataSeeder.SeedGameAsync(db);

        // Barème volontairement différent du LRB : c'est le marqueur du test.
        version.HaussePrincipale = 25_000;
        version.PaliersAmelioration.Add(new PalierAmeliorationPsp
        {
            Rang = 1, CoutAleaPrincipale = 1, CoutChoixPrincipale = 2,
            CoutSecondaire = 3, CoutCaracteristique = 4
        });
        await db.SaveChangesAsync();

        var (teamType, position) = await DataSeeder.SeedTeamTypeAsync(db, game.Id);
        var coach = DataSeeder.CreateUser("coach_match");
        db.Users.Add(coach);
        await db.SaveChangesAsync();

        var ligue = await DataSeeder.SeedLeagueAsync(db, game.Id, version.Id, coach.Id);
        var dom   = await DataSeeder.SeedTeamAsync(db, ligue.Id, coach.Id, teamType.Id, "Domicile");
        var ext   = await DataSeeder.SeedTeamAsync(db, ligue.Id, coach.Id, teamType.Id, "Exterieur");

        var division = new Division { LeagueId = ligue.Id, Nom = "Division 1" };
        db.Divisions.Add(division);
        await db.SaveChangesAsync();

        var joueur = await DataSeeder.SeedPlayerAsync(db, dom.Id, position.Id);
        db.PlayerImprovements.Add(new PlayerImprovement
        {
            TeamPlayerId = joueur.Id, Palier = 1,
            Type = ImprovementType.SelectionPrimaire, ValeurHausse = 20_000, PspDepensee = 6
        });

        var match = new Match
        {
            DivisionId = division.Id, Ronde = 1,
            EquipeDomicileId = dom.Id, EquipeExterieurId = ext.Id,
            Statut = MatchStatus.ValidationCompetences
        };
        db.Matches.Add(match);
        await db.SaveChangesAsync();

        var feuille = new MatchSheet { MatchId = match.Id, SaisiParId = coach.Id, SaisiLe = DateTime.UtcNow };
        db.MatchSheets.Add(feuille);
        await db.SaveChangesAsync();

        db.MatchPlayerRecords.Add(new MatchPlayerRecord
        {
            MatchSheetId = feuille.Id, TeamPlayerId = joueur.Id, EstCoteDomicile = true
        });
        await db.SaveChangesAsync();

        return new ContexteMatch(match.Id, joueur.Id);
    }
}
