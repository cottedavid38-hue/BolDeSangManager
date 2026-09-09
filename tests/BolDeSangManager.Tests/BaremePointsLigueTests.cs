using BolDeSangManager.Data;
using BolDeSangManager.Data.Enums;
using BolDeSangManager.Data.Models;
using BolDeSangManager.Services;
using BolDeSangManager.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace BolDeSangManager.Tests;

/// <summary>
/// Barème de points de classement paramétrable et recalcul rétroactif.
///
/// Les deux propriétés qui rendent l'édition en cours de saison sûre :
///  1. le barème par défaut (3/1/0, aucun bonus) reproduit EXACTEMENT le calcul
///     en dur d'avant — un recalcul sur une ligue existante ne change rien ;
///  2. la saisie au fil de l'eau et le recalcul complet convergent, parce qu'ils
///     appellent la même fonction pure.
/// </summary>
public class BaremePointsLigueTests : IDisposable
{
    private readonly TestDbFactory _factory = new();
    public void Dispose() => _factory.Dispose();

    private const string CommissaireId = "commissaire-test";

    private static LeagueService CreateLeagueService(ApplicationDbContext db) =>
        new(db, NullLogger<LeagueService>.Instance, new StubAuth(),
            new StaffService(db, NullLogger<StaffService>.Instance));

    private static MatchService CreateMatchService(ApplicationDbContext db)
    {
        var settings = new SettingsService(db);
        return new(db, NullLogger<MatchService>.Instance,
            new GmailEmailSender(settings, NullLogger<GmailEmailSender>.Instance), settings);
    }

    /// <summary>Ligue lancée, deux équipes, un match prêt à recevoir sa feuille.</summary>
    private async Task<(int ligueId, int matchId, int domId, int extId, int jDomId, int jExtId)>
        SetupAsync()
    {
        await using var db = _factory.CreateContext();

        var comm = DataSeeder.CreateUser("bpcomm");
        comm.Id = CommissaireId;
        var c1 = DataSeeder.CreateUser("bpc1");
        var c2 = DataSeeder.CreateUser("bpc2");
        db.Users.AddRange(comm, c1, c2);
        await db.SaveChangesAsync();

        var (game, rv) = await DataSeeder.SeedGameAsync(db);
        var (tt, pos) = await DataSeeder.SeedTeamTypeAsync(db, game.Id);
        var ligue = await DataSeeder.SeedLeagueAsync(db, game.Id, rv.Id, comm.Id, LeagueStatus.EnCours);

        var div = new Division { LeagueId = ligue.Id, Nom = "D1", Ordre = 1 };
        db.Divisions.Add(div);
        await db.SaveChangesAsync();

        var dom = await DataSeeder.SeedTeamAsync(db, ligue.Id, c1.Id, tt.Id, "Dom");
        var ext = await DataSeeder.SeedTeamAsync(db, ligue.Id, c2.Id, tt.Id, "Ext");
        dom.DivisionId = div.Id;
        ext.DivisionId = div.Id;
        await db.SaveChangesAsync();

        var jDom = await DataSeeder.SeedPlayerAsync(db, dom.Id, pos.Id, "JD", 1);
        var jExt = await DataSeeder.SeedPlayerAsync(db, ext.Id, pos.Id, "JE", 1);
        var match = await DataSeeder.SeedMatchAsync(db, dom.Id, ext.Id, div.Id);

        return (ligue.Id, match.Id, dom.Id, ext.Id, jDom.Id, jExt.Id);
    }

    /// <summary>Saisit une feuille : domicile gagne 3-1 en <paramref name="tours"/> tours.</summary>
    private async Task SaisirFeuilleAsync(int matchId, int jDomId, int jExtId, int? tours)
    {
        await using var db = _factory.CreateContext();
        var svc = CreateMatchService(db);

        var feuille = new MatchSheet
        {
            MatchId = matchId,
            SaisiParId = CommissaireId,
            TouchdownsDomicile = 3,
            TouchdownsExterieur = 1,
            EliminationsDomicile = 2,
            EliminationsExterieur = 1,
            NombreDeTours = tours
        };

        var records = new List<MatchPlayerRecord>
        {
            new()
            {
                TeamPlayerId = jDomId, EstCoteDomicile = true,
                Touchdowns = 3, Passes = 4, Interceptions = 1,
                EliminationsInfligees = 2, Deviations = 2, Agressions = 5
            },
            new()
            {
                TeamPlayerId = jExtId, EstCoteDomicile = false,
                Touchdowns = 1, Passes = 2, Interceptions = 0,
                EliminationsInfligees = 1, Deviations = 1, Agressions = 4
            }
        };

        await svc.SaisirFeuilleMatchAsync(matchId, feuille, records, CommissaireId);
    }

    /// <summary>
    /// Barème de l'association : la BASE est le cas normal (match décidé tôt),
    /// le palier décrit la dégradation à partir du 13e tour.
    /// </summary>
    private static BaremePoints BaremeReference() => new()
    {
        Victoire = 3000, Nul = 1500, Defaite = 0,
        ParTouchdown = 5, ParElimination = 2, ParInterception = 1,
        ParPasse = 1, ParDeviation = 1, ParAgression = 1
    };

    private static List<PalierPointsLigue> PalierReference() =>
    [
        new() { APartirDuTour = 13, PointsVictoire = 2000, PointsNul = 1500, PointsDefaite = 1000 }
    ];

    // ── 1. Non-régression : le défaut reproduit l'ancien calcul en dur ────────

    [Fact]
    public async Task BaremeParDefaut_SaisieDonneLesMemesPointsQuAvant_3_et_0()
    {
        var (_, matchId, domId, extId, jDom, jExt) = await SetupAsync();
        await SaisirFeuilleAsync(matchId, jDom, jExt, tours: null);

        await using var db = _factory.CreateContext();
        Assert.Equal(3, (await db.Teams.FindAsync(domId))!.PointsLigue);
        Assert.Equal(0, (await db.Teams.FindAsync(extId))!.PointsLigue);
    }

    [Fact]
    public async Task Recalcul_AvecBaremeParDefaut_NeChangeRien()
    {
        // La garantie demandée avant déploiement : passer le recalcul sur une
        // ligue existante ne doit toucher à aucun total.
        var (ligueId, matchId, domId, extId, jDom, jExt) = await SetupAsync();
        await SaisirFeuilleAsync(matchId, jDom, jExt, tours: null);

        int avantDom, avantExt;
        await using (var db = _factory.CreateContext())
        {
            avantDom = (await db.Teams.FindAsync(domId))!.PointsLigue;
            avantExt = (await db.Teams.FindAsync(extId))!.PointsLigue;
        }

        await using (var db = _factory.CreateContext())
        {
            var rejoues = await CreateLeagueService(db).RecalculerClassementAsync(ligueId);
            Assert.Equal(1, rejoues);
        }

        await using (var db = _factory.CreateContext())
        {
            Assert.Equal(avantDom, (await db.Teams.FindAsync(domId))!.PointsLigue);
            Assert.Equal(avantExt, (await db.Teams.FindAsync(extId))!.PointsLigue);
        }
    }

    // ── 2. Convergence saisie / recalcul ──────────────────────────────────────

    [Fact]
    public async Task SaisieAuFilDeLEau_EtRecalculComplet_DonnentLeMemeTotal()
    {
        // Sans cette propriété, éditer un barème en cours de saison produirait
        // des totaux faux : c'est LA justification de la fonction pure partagée.
        var (ligueId, matchId, domId, extId, jDom, jExt) = await SetupAsync();

        await using (var db = _factory.CreateContext())
        {
            await CreateLeagueService(db)
                .ModifierBaremePointsAsync(ligueId, BaremeReference(), PalierReference(), CommissaireId);
        }

        await SaisirFeuilleAsync(matchId, jDom, jExt, tours: 11);

        int filDom, filExt;
        await using (var db = _factory.CreateContext())
        {
            filDom = (await db.Teams.FindAsync(domId))!.PointsLigue;
            filExt = (await db.Teams.FindAsync(extId))!.PointsLigue;
        }

        // Victoire avant le 13e tour = 3000 (points de base), + 3 TD×5 + 2 élim×2
        // + 1 int + 4 passes + 2 déviations + 5 agressions = 3000 + 31 = 3031
        Assert.Equal(3031, filDom);
        // Défaite avant le 13e tour = 0, + 5 + 2 + 0 + 2 + 1 + 4 = 14
        Assert.Equal(14, filExt);

        await using (var db = _factory.CreateContext())
            await CreateLeagueService(db).RecalculerClassementAsync(ligueId);

        await using (var db = _factory.CreateContext())
        {
            Assert.Equal(filDom, (await db.Teams.FindAsync(domId))!.PointsLigue);
            Assert.Equal(filExt, (await db.Teams.FindAsync(extId))!.PointsLigue);
        }
    }

    // ── 3. Édition rétroactive du barème ──────────────────────────────────────

    [Fact]
    public async Task ModifierBareme_RecalculeLesMatchsDejaJoues()
    {
        var (ligueId, matchId, domId, extId, jDom, jExt) = await SetupAsync();
        await SaisirFeuilleAsync(matchId, jDom, jExt, tours: 11);

        await using (var db = _factory.CreateContext())
        {
            Assert.Equal(3, (await db.Teams.FindAsync(domId))!.PointsLigue);
        }

        int rejoues;
        await using (var db = _factory.CreateContext())
        {
            rejoues = await CreateLeagueService(db)
                .ModifierBaremePointsAsync(ligueId, BaremeReference(), PalierReference(), CommissaireId);
        }

        Assert.Equal(1, rejoues);

        await using (var db = _factory.CreateContext())
        {
            Assert.Equal(3031, (await db.Teams.FindAsync(domId))!.PointsLigue);
            Assert.Equal(14, (await db.Teams.FindAsync(extId))!.PointsLigue);
        }
    }

    [Fact]
    public async Task MatchSansNombreDeTours_UtiliseLesPointsDeBase()
    {
        // Le cas du déploiement : les matchs déjà joués n'ont pas de nombre de
        // tours. Ils tombent sur la ligne de base, pas sur le palier.
        var (ligueId, matchId, domId, extId, jDom, jExt) = await SetupAsync();
        await SaisirFeuilleAsync(matchId, jDom, jExt, tours: null);

        await using (var db = _factory.CreateContext())
        {
            await CreateLeagueService(db)
                .ModifierBaremePointsAsync(ligueId, BaremeReference(), PalierReference(), CommissaireId);
        }

        await using (var db = _factory.CreateContext())
        {
            // Points de BASE (3000) + 31 de bonus : sans nombre de tours, le
            // palier « à partir du 13e » ne peut pas s'appliquer.
            Assert.Equal(3031, (await db.Teams.FindAsync(domId))!.PointsLigue);
            // Défaite de base = 0, + 14 de bonus
            Assert.Equal(14, (await db.Teams.FindAsync(extId))!.PointsLigue);
        }
    }

    [Fact]
    public async Task MatchsSansNombreDeTours_SontComptesPourAlerterLeCommissaire()
    {
        var (ligueId, matchId, _, _, jDom, jExt) = await SetupAsync();
        await SaisirFeuilleAsync(matchId, jDom, jExt, tours: null);

        // Sans palier, l'information n'a aucun sens : on ne dérange personne.
        await using (var db = _factory.CreateContext())
            Assert.Equal(0, await CreateLeagueService(db).CompterMatchsSansNombreDeToursAsync(ligueId));

        await using (var db = _factory.CreateContext())
            await CreateLeagueService(db)
                .ModifierBaremePointsAsync(ligueId, BaremeReference(), PalierReference(), CommissaireId);

        await using (var db = _factory.CreateContext())
            Assert.Equal(1, await CreateLeagueService(db).CompterMatchsSansNombreDeToursAsync(ligueId));
    }

    // ── 4. Garde-fous ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ModifierBareme_RefuseSiOnNeGerePasLaLigue()
    {
        var (ligueId, _, _, _, _, _) = await SetupAsync();

        await using var db = _factory.CreateContext();
        var svc = new LeagueService(db, NullLogger<LeagueService>.Instance, new StubAuth(autorise: false),
            new StaffService(db, NullLogger<StaffService>.Instance));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.ModifierBaremePointsAsync(ligueId, BaremeReference(), PalierReference(), "intrus"));
    }

    [Fact]
    public async Task ModifierBareme_RefuseDeuxPaliersSurLeMemeTour()
    {
        var (ligueId, _, _, _, _, _) = await SetupAsync();

        await using var db = _factory.CreateContext();
        List<PalierPointsLigue> doublon =
        [
            new() { APartirDuTour = 12, PointsVictoire = 3000, PointsNul = 1500, PointsDefaite = 0 },
            new() { APartirDuTour = 12, PointsVictoire = 100, PointsNul = 50, PointsDefaite = 0 }
        ];

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateLeagueService(db).ModifierBaremePointsAsync(
                ligueId, BaremeReference(), doublon, CommissaireId));
    }

    [Fact]
    public async Task ModifierBareme_NeTouchePasAuxAutresParametresDeLaLigue()
    {
        // Même verrouillage que pour Reglement / ModeBrouillard : la commande
        // dédiée ne doit modifier QUE le barème.
        var (ligueId, _, _, _, _, _) = await SetupAsync();

        string nomAvant;
        int budgetAvant, xpTdAvant;
        LeagueStatus statutAvant;
        await using (var db = _factory.CreateContext())
        {
            var l = await db.Leagues.FindAsync(ligueId);
            (nomAvant, budgetAvant, xpTdAvant, statutAvant) =
                (l!.Nom, l.BudgetDepart, l.PspParTouchdown, l.Statut);
        }

        await using (var db = _factory.CreateContext())
            await CreateLeagueService(db)
                .ModifierBaremePointsAsync(ligueId, BaremeReference(), PalierReference(), CommissaireId);

        await using (var db = _factory.CreateContext())
        {
            var l = await db.Leagues.FindAsync(ligueId);
            Assert.Equal(nomAvant, l!.Nom);
            Assert.Equal(budgetAvant, l.BudgetDepart);
            Assert.Equal(xpTdAvant, l.PspParTouchdown);
            Assert.Equal(statutAvant, l.Statut);
            Assert.Equal(3000, l.PointsVictoire);
        }
    }

    [Fact]
    public async Task ModifierBareme_RemplaceLesPaliersSansLesEmpiler()
    {
        var (ligueId, _, _, _, _, _) = await SetupAsync();

        await using (var db = _factory.CreateContext())
            await CreateLeagueService(db)
                .ModifierBaremePointsAsync(ligueId, BaremeReference(), PalierReference(), CommissaireId);

        await using (var db = _factory.CreateContext())
            await CreateLeagueService(db).ModifierBaremePointsAsync(
                ligueId, BaremeReference(),
                [new PalierPointsLigue { APartirDuTour = 8, PointsVictoire = 500, PointsNul = 200, PointsDefaite = 0 }],
                CommissaireId);

        await using (var db = _factory.CreateContext())
        {
            var paliers = await db.PaliersPointsLigue.Where(p => p.LeagueId == ligueId).ToListAsync();
            Assert.Single(paliers);
            Assert.Equal(8, paliers[0].APartirDuTour);
        }
    }

    private class StubAuth(bool autorise = true) : IAuthorizationService
    {
        public Task<bool> EstAdminAsync(string userId) => Task.FromResult(autorise);
        public Task<bool> EstGrandCommissaireAsync(string userId) => Task.FromResult(autorise);
        public Task<bool> EstCommissaireDeLigueAsync(string userId, int ligueId) => Task.FromResult(autorise);
        public Task<bool> PeutGererLigueAsync(string userId, int ligueId) => Task.FromResult(autorise);
        public Task<bool> PeutEditerDonneesAsync(string userId) => Task.FromResult(autorise);
        public Task<bool> PeutGererSettingsAsync(string userId) => Task.FromResult(autorise);
    }
}
