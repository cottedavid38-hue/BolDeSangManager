using BolDeSangManager.Data;
using BolDeSangManager.Data.Enums;
using BolDeSangManager.Data.Models;
using BolDeSangManager.Services;
using BolDeSangManager.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BolDeSangManager.Tests;

/// <summary>
/// R4 — l'XP devient une cagnotte dépensable (abandon des paliers LRB) :
/// le coach saisit l'XP gagnée sur la feuille, puis l'XP consommée à l'après-match,
/// et le commissaire peut corriger a posteriori.
/// </summary>
public class XpLibreTests : IDisposable
{
    private readonly TestDbFactory _factory = new();
    public void Dispose() => _factory.Dispose();

    // ── Barème ────────────────────────────────────────────────────────────────

    [Fact]
    public void Bareme_BloodBowl_AppliqueLesValeursLRB()
    {
        var b = BaremePsp.ParDefaut(GameType.BloodBowl);
        // 2 TD (×3) + 1 passe + 1 interception (×2) + 2 élims (×2) + MVP (+4)
        Assert.Equal(6 + 1 + 2 + 4 + 4, b.Calculer(
            touchdowns: 2, passes: 1, interceptions: 1, eliminations: 2, estMvp: true));
    }

    [Fact]
    public void Bareme_DungeonBowl_CompteLeTouchdown5()
    {
        var b = BaremePsp.ParDefaut(GameType.DungeonBowl);
        Assert.Equal(10, b.Calculer(touchdowns: 2, passes: 0, interceptions: 0, eliminations: 0, estMvp: false));
    }

    [Fact]
    public void Bareme_EstPersonnalisable_PourLaCarteBaremeParLigue()
    {
        // point d'extension R6 : une ligue pourra fournir son propre barème
        var b = new BaremePsp { ParTouchdown = 10, BonusMvp = 0 };
        Assert.Equal(20, b.Calculer(touchdowns: 2, passes: 0, interceptions: 0, eliminations: 0, estMvp: true));
    }

    [Fact]
    public void Bareme_DeviationEtAgression_NeRapportentRienParDefaut()
    {
        // DEV et AGRO sont saisies pour le CLASSEMENT, pas pour la progression :
        // ajouter ces deux actions ne doit rien changer à l'XP des ligues
        // existantes. C'est la garantie de non-régression du lot « barème de points ».
        var b = BaremePsp.ParDefaut(GameType.BloodBowl);
        var sansActions = b.Calculer(touchdowns: 1, passes: 0, interceptions: 0,
                                     eliminations: 0, estMvp: false);
        var avecActions = b.Calculer(touchdowns: 1, passes: 0, interceptions: 0,
                                     eliminations: 0, estMvp: false,
                                     deviations: 3, agressions: 7);
        Assert.Equal(sansActions, avecActions);

        var record = new MatchPlayerRecord { Touchdowns = 1, Deviations = 3, Agressions = 7 };
        Assert.Equal(3, b.Calculer(record));
    }

    [Fact]
    public void Bareme_DeviationEtAgression_ComptentSiLaLigueLesValorise()
    {
        var b = new BaremePsp { ParTouchdown = 0, BonusMvp = 0, ParDeviation = 1, ParAgression = 2 };
        Assert.Equal(3 * 1 + 2 * 2, b.Calculer(
            touchdowns: 0, passes: 0, interceptions: 0, eliminations: 0, estMvp: false,
            deviations: 3, agressions: 2));
    }

    // ── Barème porté par la version de règles, puis par la ligue (R6) ─────────

    [Fact]
    public void BaremeDeVersion_ReprendLesValeursDesRegles()
    {
        var version = new RulesVersion
        {
            PspParTouchdown = 6, PspParPasse = 2,
            PspParInterception = 5, PspParElimination = 1, PspBonusMvp = 10
        };

        var b = BaremePsp.DeVersion(version, GameType.BloodBowl);

        // 1 TD (6) + 2 passes (4) + 1 interception (5) + 3 élims (3) + MVP (10)
        Assert.Equal(6 + 4 + 5 + 3 + 10, b.Calculer(
            touchdowns: 1, passes: 2, interceptions: 1, eliminations: 3, estMvp: true));
    }

    [Fact]
    public void BaremeDeVersion_SansVersion_RetombeSurLeDefautDuJeu()
    {
        Assert.Equal(3, BaremePsp.DeVersion(null, GameType.BloodBowl).ParTouchdown);
        Assert.Equal(5, BaremePsp.DeVersion(null, GameType.DungeonBowl).ParTouchdown);
    }

    [Fact]
    public void AppliquerA_EcritLeBaremeSurLaVersionDeRegles()
    {
        var version = new RulesVersion();
        new BaremePsp { ParTouchdown = 7, ParPasse = 3, ParInterception = 0, ParElimination = 4, BonusMvp = 1 }
            .AppliquerA(version);

        Assert.Equal(7, version.PspParTouchdown);
        Assert.Equal(3, version.PspParPasse);
        Assert.Equal(0, version.PspParInterception);
        Assert.Equal(4, version.PspParElimination);
        Assert.Equal(1, version.PspBonusMvp);
    }

    [Fact]
    public void BaremeDeLigue_ReprendLesValeursConfigurees()
    {
        var ligue = new League
        {
            PspParTouchdown = 6, PspParPasse = 2,
            PspParInterception = 5, PspParElimination = 1, PspBonusMvp = 10
        };

        var b = BaremePsp.DeLigue(ligue, GameType.BloodBowl);

        Assert.Equal(6 + 4 + 5 + 3 + 10, b.Calculer(
            touchdowns: 1, passes: 2, interceptions: 1, eliminations: 3, estMvp: true));
    }

    [Fact]
    public void BaremeDeLigue_SansLigue_RetombeSurLeDefautDuJeu()
    {
        Assert.Equal(3, BaremePsp.DeLigue(null, GameType.BloodBowl).ParTouchdown);
        Assert.Equal(5, BaremePsp.DeLigue(null, GameType.DungeonBowl).ParTouchdown);
    }

    [Fact]
    public void BaremeDeLigue_UneLigueNeuveEstConformeAuLRB()
    {
        // les valeurs par défaut du modèle doivent rester la règle officielle
        var b = BaremePsp.DeLigue(new League(), GameType.BloodBowl);
        Assert.Equal(3, b.ParTouchdown);
        Assert.Equal(1, b.ParPasse);
        Assert.Equal(2, b.ParInterception);
        Assert.Equal(2, b.ParElimination);
        Assert.Equal(4, b.BonusMvp);
    }

    [Fact]
    public void AppliquerA_EcritLeBaremeSurLaLigue()
    {
        var ligue = new League();
        new BaremePsp { ParTouchdown = 7, ParPasse = 3, ParInterception = 0, ParElimination = 4, BonusMvp = 1 }
            .AppliquerA(ligue);

        Assert.Equal(7, ligue.PspParTouchdown);
        Assert.Equal(3, ligue.PspParPasse);
        Assert.Equal(0, ligue.PspParInterception);
        Assert.Equal(4, ligue.PspParElimination);
        Assert.Equal(1, ligue.PspBonusMvp);
    }

    [Fact]
    public void BaremeDeLigue_ValeursAZero_NeuralisentUneAction()
    {
        // une ligue peut décider que les éliminations ne rapportent rien
        var ligue = new League { PspParElimination = 0 };
        var b = BaremePsp.DeLigue(ligue, GameType.BloodBowl);

        Assert.Equal(0, b.Calculer(touchdowns: 0, passes: 0, interceptions: 0, eliminations: 5, estMvp: false));
    }

    [Fact]
    public async Task ModifierBaremePsp_EnregistreSurLaVersionDeRegles()
    {
        using var db = _factory.CreateContext();
        var (_, version) = await DataSeeder.SeedGameAsync(db);

        var svc = new DataEditService(db, NullLogger<DataEditService>.Instance);
        await svc.ModifierBaremePspAsync(version.Id, new BaremePsp
        {
            ParTouchdown = 8, ParPasse = 0, ParInterception = 3, ParElimination = 1, BonusMvp = 6
        });

        var relu = await db.RulesVersions.FindAsync(version.Id);
        Assert.Equal(8, relu!.PspParTouchdown);
        Assert.Equal(0, relu.PspParPasse);
        Assert.Equal(3, relu.PspParInterception);
        Assert.Equal(1, relu.PspParElimination);
        Assert.Equal(6, relu.PspBonusMvp);
    }

    // ── Cagnotte : l'amélioration débite l'XP saisie ──────────────────────────

    private async Task<(TeamService svc, int joueurId, int skillId)> PreparerJoueurAsync(int xpDepart)
    {
        var db = _factory.CreateContext();
        var coach = DataSeeder.CreateUser("xp");
        db.Users.Add(coach);
        await db.SaveChangesAsync();

        var (game, version) = await DataSeeder.SeedGameAsync(db);
        var (teamType, position) = await DataSeeder.SeedTeamTypeAsync(db, game.Id);
        var ligue = await DataSeeder.SeedLeagueAsync(db, game.Id, version.Id, coach.Id);
        var catId = await DataSeeder.GetOrCreateCategorieAsync(db, version.Id);

        var skill = new Skill
        {
            Nom = "Blocage", Categorie = SkillCategory.Generale,
            SkillCategoryDefId = catId, RulesVersionId = version.Id
        };
        db.Skills.Add(skill);

        var equipe = new Team
        {
            Nom = "Les Bourrins", CoachId = coach.Id,
            LeagueId = ligue.Id, TeamTypeId = teamType.Id
        };
        db.Teams.Add(equipe);
        await db.SaveChangesAsync();

        var joueur = new TeamPlayer
        {
            TeamId = equipe.Id, PlayerPositionId = position.Id,
            Nom = "Grok", Numero = 1, PointsStarPlayer = xpDepart
        };
        db.TeamPlayers.Add(joueur);
        await db.SaveChangesAsync();

        return (new TeamService(db, NullLogger<TeamService>.Instance), joueur.Id, skill.Id);
    }

    [Fact]
    public async Task Amelioration_DebiteLaCagnotteDuMontantSaisi()
    {
        var (svc, joueurId, skillId) = await PreparerJoueurAsync(xpDepart: 20);

        await svc.AppliquerAmeliorationAsync(joueurId, ImprovementType.SelectionPrimaire,
            skillId: skillId, pspDepensee: 8);

        await using var db = _factory.CreateContext();
        var joueur = await db.TeamPlayers.Include(j => j.Improvements).FirstAsync(j => j.Id == joueurId);
        Assert.Equal(12, joueur.PointsStarPlayer);            // 20 - 8
        Assert.Equal(8, joueur.Improvements.Single().PspDepensee);
    }

    [Fact]
    public async Task Amelioration_RefuseeSiXpInsuffisante()
    {
        var (svc, joueurId, skillId) = await PreparerJoueurAsync(xpDepart: 5);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.AppliquerAmeliorationAsync(joueurId, ImprovementType.SelectionPrimaire,
                skillId: skillId, pspDepensee: 8));

        Assert.Contains("XP", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Amelioration_RefuseUnMontantNegatifOuNul()
    {
        var (svc, joueurId, skillId) = await PreparerJoueurAsync(xpDepart: 20);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.AppliquerAmeliorationAsync(joueurId, ImprovementType.SelectionPrimaire,
                skillId: skillId, pspDepensee: 0));
    }

    [Fact]
    public async Task PlusieursAmeliorations_TantQueLaCagnotteLePermet()
    {
        // l'ancien système bloquait à 6 améliorations (6 paliers) ; ce n'est plus le cas
        var (svc, joueurId, skillId) = await PreparerJoueurAsync(xpDepart: 100);

        for (int i = 0; i < 8; i++)
            await svc.AppliquerAmeliorationAsync(joueurId, ImprovementType.SelectionPrimaire,
                skillId: skillId, pspDepensee: 10);

        await using var db = _factory.CreateContext();
        var joueur = await db.TeamPlayers.Include(j => j.Improvements).FirstAsync(j => j.Id == joueurId);
        Assert.Equal(8, joueur.Improvements.Count);
        Assert.Equal(20, joueur.PointsStarPlayer);   // 100 - 80
    }

    [Fact]
    public async Task Amelioration_NumeroteLesPaliersDansLOrdre()
    {
        var (svc, joueurId, skillId) = await PreparerJoueurAsync(xpDepart: 50);

        await svc.AppliquerAmeliorationAsync(joueurId, ImprovementType.SelectionPrimaire, skillId: skillId, pspDepensee: 6);
        await svc.AppliquerAmeliorationAsync(joueurId, ImprovementType.SelectionPrimaire, skillId: skillId, pspDepensee: 6);

        await using var db = _factory.CreateContext();
        var paliers = await db.PlayerImprovements
            .Where(i => i.TeamPlayerId == joueurId).OrderBy(i => i.Palier).Select(i => i.Palier).ToListAsync();
        Assert.Equal([1, 2], paliers);
    }

    // ── Correction commissaire ────────────────────────────────────────────────

    [Fact]
    public async Task CorrigerXp_MetAJourLeJoueurEtJournaliseLaCorrection()
    {
        var (svc, joueurId, _) = await PreparerJoueurAsync(xpDepart: 10);

        // le commissaire est le coach créé par PreparerJoueurAsync (compte existant)
        await using var dbSetup = _factory.CreateContext();
        var commissaireId = (await dbSetup.Users.FirstAsync()).Id;

        await svc.CorrigerXpAsync(joueurId, nouvelleValeur: 25,
            motif: "Erreur de saisie sur la feuille du match 3", commissaireId: commissaireId);

        await using var db = _factory.CreateContext();
        var joueur = await db.TeamPlayers.FirstAsync(j => j.Id == joueurId);
        Assert.Equal(25, joueur.PointsStarPlayer);

        var trace = await db.PspCorrections.SingleAsync(c => c.TeamPlayerId == joueurId);
        Assert.Equal(10, trace.AncienneValeur);
        Assert.Equal(25, trace.NouvelleValeur);
        Assert.Equal(15, trace.Ecart);
        Assert.Equal(commissaireId, trace.CorrigeParId);
        Assert.Contains("match 3", trace.Motif);
    }

    [Fact]
    public async Task CorrigerXp_RefuseUneValeurNegative()
    {
        var (svc, joueurId, _) = await PreparerJoueurAsync(xpDepart: 10);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.CorrigerXpAsync(joueurId, nouvelleValeur: -5, motif: "test", commissaireId: "c1"));
    }

    [Fact]
    public async Task CorrigerXp_ExigeUnMotif()
    {
        var (svc, joueurId, _) = await PreparerJoueurAsync(xpDepart: 10);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.CorrigerXpAsync(joueurId, nouvelleValeur: 20, motif: "  ", commissaireId: "c1"));
    }
}
