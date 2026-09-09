using BolDeSangManager.Data;
using BolDeSangManager.Data.Enums;
using BolDeSangManager.Data.Models;
using BolDeSangManager.Services;
using BolDeSangManager.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace BolDeSangManager.Tests;

/// <summary>
/// Confirmation d'une feuille de match PAR UN COMMISSAIRE, à la place du coach
/// adverse qui ne confirme pas (« débloquer un match qui traîne »).
///
/// Le cas normal — l'adversaire confirme lui-même — est couvert par
/// MatchServiceTests.CycleComplet_SeCloutureSansAucuneInterventionCommissaire.
/// Ici on ne teste que ce que le drapeau `estCommissaire` change, et surtout ce
/// qu'il NE change PAS pour un simple coach.
/// </summary>
public class ConfirmationParCommissaireTests : IDisposable
{
    private readonly TestDbFactory _factory = new();
    public void Dispose() => _factory.Dispose();

    private static MatchService CreateService(ApplicationDbContext db)
    {
        var settings = new SettingsService(db);
        var emailSender = new GmailEmailSender(settings, NullLogger<GmailEmailSender>.Instance);
        return new(db, NullLogger<MatchService>.Instance, emailSender, settings);
    }

    /// <summary>
    /// Ligue + deux équipes + un match, et une feuille déjà saisie par le coach
    /// domicile. On rend `commissaire`, `coachDom` et `coachExt` distincts pour
    /// que « le commissaire n'est coach d'aucune des deux équipes » soit un fait
    /// du montage et pas une hypothèse.
    /// </summary>
    private async Task<(Match match, string commissaireId, string coachDom, string coachExt)>
        SetupFeuilleSaisieAsync()
    {
        await using var db = _factory.CreateContext();

        var commissaire = DataSeeder.CreateUser("ccomm");
        var coach1 = DataSeeder.CreateUser("ccoach1");
        var coach2 = DataSeeder.CreateUser("ccoach2");
        db.Users.AddRange(commissaire, coach1, coach2);
        await db.SaveChangesAsync();

        var (game, rv) = await DataSeeder.SeedGameAsync(db);
        var (teamType, _) = await DataSeeder.SeedTeamTypeAsync(db, game.Id);
        var ligue = await DataSeeder.SeedLeagueAsync(db, game.Id, rv.Id, commissaire.Id);

        var div = new Division { LeagueId = ligue.Id, Nom = "Division Unique", Ordre = 1 };
        db.Divisions.Add(div);
        await db.SaveChangesAsync();

        var dom = await DataSeeder.SeedTeamAsync(db, ligue.Id, coach1.Id, teamType.Id, "Dom");
        var ext = await DataSeeder.SeedTeamAsync(db, ligue.Id, coach2.Id, teamType.Id, "Ext");
        dom.DivisionId = div.Id;
        ext.DivisionId = div.Id;
        await db.SaveChangesAsync();

        var match = await DataSeeder.SeedMatchAsync(db, dom.Id, ext.Id, div.Id);

        await using var db2 = _factory.CreateContext();
        var svc = CreateService(db2);
        await svc.SaisirFeuilleMatchAsync(match.Id, new MatchSheet
        {
            TouchdownsDomicile = 2,
            TouchdownsExterieur = 1,
            GainsDomicile = 0,
            GainsExterieur = 0
        }, [], coach1.Id);

        return (match, commissaire.Id, coach1.Id, coach2.Id);
    }

    // ─── Ce que le commissaire peut faire ─────────────────────────────────────

    [Fact]
    public async Task Commissaire_NonCoach_PeutConfirmer()
    {
        var (match, commissaireId, _, _) = await SetupFeuilleSaisieAsync();
        await using var db = _factory.CreateContext();
        var svc = CreateService(db);

        await svc.ConfirmerFeuilleCoachAsync(match.Id, commissaireId, estCommissaire: true);

        await using var verif = _factory.CreateContext();
        var m = await verif.Matches.FindAsync(match.Id);
        Assert.Equal(MatchStatus.ValidationCompetences, m!.Statut);
    }

    [Fact]
    public async Task ConfirmationCommissaire_LaisseUneTraceNommeeEtDatee()
    {
        var (match, commissaireId, _, _) = await SetupFeuilleSaisieAsync();
        await using var db = _factory.CreateContext();
        var svc = CreateService(db);

        await svc.ConfirmerFeuilleCoachAsync(match.Id, commissaireId, estCommissaire: true);

        await using var verif = _factory.CreateContext();
        var feuille = verif.MatchSheets.Single(f => f.MatchId == match.Id);
        // Le pseudo vient de la BASE, pas de l'écran : User.Identity.Name vaut
        // l'e-mail, qu'on ne veut pas publier aux deux coaches.
        Assert.Equal("Coachccomm", feuille.ConfirmeeParCommissaire);
        Assert.NotNull(feuille.ConfirmeeParCommissaireLe);
    }

    /// <summary>
    /// Décision produit assumée : un commissaire qui est aussi coach peut
    /// confirmer SA PROPRE saisie. C'est exactement ce que le garde-fou
    /// « pas d'auto-validation » interdit à un coach ordinaire.
    /// </summary>
    [Fact]
    public async Task Commissaire_PeutConfirmerSaPropreSaisie()
    {
        var (match, _, coachDom, _) = await SetupFeuilleSaisieAsync();
        await using var db = _factory.CreateContext();
        var svc = CreateService(db);

        await svc.ConfirmerFeuilleCoachAsync(match.Id, coachDom, estCommissaire: true);

        await using var verif = _factory.CreateContext();
        Assert.Equal(MatchStatus.ValidationCompetences,
            (await verif.Matches.FindAsync(match.Id))!.Statut);
        Assert.Equal("Coachccoach1",
            verif.MatchSheets.Single(f => f.MatchId == match.Id).ConfirmeeParCommissaire);
    }

    // ─── Ce que le drapeau ne doit RIEN changer pour un coach ─────────────────

    [Fact]
    public async Task Coach_NeConfirmePasSaPropreSaisie()
    {
        var (match, _, coachDom, _) = await SetupFeuilleSaisieAsync();
        await using var db = _factory.CreateContext();
        var svc = CreateService(db);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.ConfirmerFeuilleCoachAsync(match.Id, coachDom));

        await using var verif = _factory.CreateContext();
        Assert.Equal(MatchStatus.FeuilleEnSaisie,
            (await verif.Matches.FindAsync(match.Id))!.Statut);
    }

    /// <summary>
    /// Le test qui DISCRIMINE : sans le `estCommissaire`, un utilisateur qui
    /// n'est coach d'aucune des deux équipes est refusé. Si ce test passait au
    /// vert après avoir retiré la garde du service, la garde ne servirait à rien.
    /// </summary>
    [Fact]
    public async Task Tiers_SansDroitCommissaire_EstRefuse()
    {
        var (match, commissaireId, _, _) = await SetupFeuilleSaisieAsync();
        await using var db = _factory.CreateContext();
        var svc = CreateService(db);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.ConfirmerFeuilleCoachAsync(match.Id, commissaireId, estCommissaire: false));

        await using var verif = _factory.CreateContext();
        Assert.Equal(MatchStatus.FeuilleEnSaisie,
            (await verif.Matches.FindAsync(match.Id))!.Statut);
    }

    /// <summary>
    /// Confirmation NORMALE par l'adversaire : aucune trace commissaire ne doit
    /// apparaître, sinon le bandeau « confirmé par le commissaire » s'afficherait
    /// sur tous les matchs confirmés régulièrement.
    /// </summary>
    [Fact]
    public async Task ConfirmationNormale_NeLaissePasDeTraceCommissaire()
    {
        var (match, _, _, coachExt) = await SetupFeuilleSaisieAsync();
        await using var db = _factory.CreateContext();
        var svc = CreateService(db);

        await svc.ConfirmerFeuilleCoachAsync(match.Id, coachExt);

        await using var verif = _factory.CreateContext();
        var feuille = verif.MatchSheets.Single(f => f.MatchId == match.Id);
        Assert.Null(feuille.ConfirmeeParCommissaire);
        Assert.Null(feuille.ConfirmeeParCommissaireLe);
    }

    /// <summary>
    /// Un commissaire qui est l'ADVERSAIRE et confirme normalement ne fait rien
    /// d'exceptionnel : pas de trace non plus. C'est ce qui distingue « il porte
    /// le rôle » de « il a usé du rôle ».
    /// </summary>
    [Fact]
    public async Task Commissaire_QuiConfirmeCommeAdversaire_NeLaissePasDeTrace()
    {
        var (match, _, _, coachExt) = await SetupFeuilleSaisieAsync();
        await using var db = _factory.CreateContext();
        var svc = CreateService(db);

        await svc.ConfirmerFeuilleCoachAsync(match.Id, coachExt, estCommissaire: true);

        await using var verif = _factory.CreateContext();
        var feuille = verif.MatchSheets.Single(f => f.MatchId == match.Id);
        Assert.Null(feuille.ConfirmeeParCommissaire);
        Assert.Null(feuille.ConfirmeeParCommissaireLe);
    }

    [Fact]
    public async Task Commissaire_NePeutPasConfirmerUnMatchPasEnSaisie()
    {
        var (match, commissaireId, _, _) = await SetupFeuilleSaisieAsync();
        await using var db = _factory.CreateContext();
        var svc = CreateService(db);

        await svc.ConfirmerFeuilleCoachAsync(match.Id, commissaireId, estCommissaire: true);

        await using var db2 = _factory.CreateContext();
        var svc2 = CreateService(db2);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc2.ConfirmerFeuilleCoachAsync(match.Id, commissaireId, estCommissaire: true));
    }
}
