using BolDeSangManager.Data;
using BolDeSangManager.Data.Models;
using BolDeSangManager.Helpers;
using BolDeSangManager.Services;
using BolDeSangManager.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace BolDeSangManager.Tests;

/// <summary>
/// Règle « Capitaine » : le coach désigne un joueur, qui gagne une compétence.
///
/// Écrite de façon GÉNÉRIQUE comme les deux autres comportements automatiques :
/// la compétence offerte est un paramètre de la liaison race↔règle, pas une
/// valeur en dur. Une future règle offrant « Blocage » au lieu de « Pro » se
/// règle en admin, sans développement.
/// </summary>
public class CapitaineTests
{
    private static TeamService Svc(BolDeSangManager.Data.ApplicationDbContext db) =>
        new(db, NullLogger<TeamService>.Instance);

    /// <summary>Race dotée de la règle, paramétrée sur la compétence voulue.</summary>
    private static TeamType RaceAvecCapitaine(string competenceOfferte)
    {
        var regle = new SpecialRule
        {
            Nom = "Capitaine",
            Code = SpecialRuleCodes.CompetenceAuCapitaine
        };

        return new TeamType
        {
            Nom = "Humains",
            ReglesSpecialesListe =
            [
                new TeamTypeSpecialRule { SpecialRule = regle, OptionsChoix = competenceOfferte }
            ]
        };
    }

    private static TeamPlayer Joueur(string nom, int numero = 1) =>
        new() { Nom = nom, Numero = numero, PlayerPosition = new PlayerPosition { Nom = "Blitzer" } };

    // ── Attribution de la compétence ─────────────────────────────────────────

    [Fact]
    public void LeCapitaineGagneLaCompetenceParametree()
    {
        var tt = RaceAvecCapitaine("Pro");
        var capitaine = Joueur("Marcus");
        capitaine.EstCapitaine = true;

        var offerte = CapitaineHelper.CompetenceOfferte(capitaine, tt);

        Assert.Equal("Pro", offerte);
    }

    [Fact]
    public void UnJoueurQuiNEstPasCapitaineNeGagneRien()
    {
        var tt = RaceAvecCapitaine("Pro");

        Assert.Null(CapitaineHelper.CompetenceOfferte(Joueur("Second"), tt));
    }

    /// <summary>
    /// Généricité : c'est le paramètre qui décide, pas une constante « Pro ».
    /// </summary>
    [Fact]
    public void LaCompetenceOfferteSuitLeParametreDeLaRace()
    {
        var tt = RaceAvecCapitaine("Blocage");
        var capitaine = Joueur("Brutus");
        capitaine.EstCapitaine = true;

        Assert.Equal("Blocage", CapitaineHelper.CompetenceOfferte(capitaine, tt));
    }

    [Fact]
    public void SansParametreAucuneCompetenceNEstOfferte()
    {
        var tt = RaceAvecCapitaine("");
        var capitaine = Joueur("Sans effet");
        capitaine.EstCapitaine = true;

        Assert.Null(CapitaineHelper.CompetenceOfferte(capitaine, tt));
    }

    [Fact]
    public void UneRaceSansLaRegleNOffreRien()
    {
        var tt = new TeamType { Nom = "Orques" };
        var capitaine = Joueur("Grishnak");
        capitaine.EstCapitaine = true;

        Assert.Null(CapitaineHelper.CompetenceOfferte(capitaine, tt));
    }

    // ── Désignation en base ──────────────────────────────────────────────────

    [Fact]
    public async Task DesignerUnCapitaineRetireLePrecedent()
    {
        using var factory = new TestDbFactory();
        var (equipeId, j1, j2) = await SeedEquipeAsync(factory);

        using (var db = factory.CreateContext())
            await Svc(db).DefinirCapitaineAsync(equipeId, j1);

        using (var db = factory.CreateContext())
            await Svc(db).DefinirCapitaineAsync(equipeId, j2);

        using (var db = factory.CreateContext())
        {
            var joueurs = await db.TeamPlayers.Where(p => p.TeamId == equipeId).ToListAsync();
            Assert.Single(joueurs.Where(p => p.EstCapitaine));
            Assert.True(joueurs.First(p => p.Id == j2).EstCapitaine);
        }
    }

    [Fact]
    public async Task OnPeutRetirerLeCapitaineSansEnDesignerUnAutre()
    {
        using var factory = new TestDbFactory();
        var (equipeId, j1, _) = await SeedEquipeAsync(factory);

        using (var db = factory.CreateContext())
            await Svc(db).DefinirCapitaineAsync(equipeId, j1);

        using (var db = factory.CreateContext())
            await Svc(db).DefinirCapitaineAsync(equipeId, null);

        using (var db = factory.CreateContext())
            Assert.Empty(await db.TeamPlayers.Where(p => p.TeamId == equipeId && p.EstCapitaine).ToListAsync());
    }

    /// <summary>
    /// Un joueur d'une AUTRE équipe ne peut pas être désigné : l'écran propose,
    /// le service fait autorité.
    /// </summary>
    [Fact]
    public async Task UnJoueurEtrangerALEquipeEstRefuse()
    {
        using var factory = new TestDbFactory();
        var (equipeId, _, _) = await SeedEquipeAsync(factory);
        var (_, autreJoueur, _) = await SeedEquipeAsync(factory, "Les Autres");

        using var db = factory.CreateContext();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Svc(db).DefinirCapitaineAsync(equipeId, autreJoueur));
    }

    /// <summary>
    /// Un capitaine mort perd son titre : le coach en redésigne un librement,
    /// et surtout la compétence ne doit plus s'afficher sur un joueur absent.
    /// </summary>
    [Fact]
    public async Task UnCapitaineMortPerdSonTitre()
    {
        using var factory = new TestDbFactory();
        var (equipeId, j1, _) = await SeedEquipeAsync(factory);

        using (var db = factory.CreateContext())
            await Svc(db).DefinirCapitaineAsync(equipeId, j1);

        using (var db = factory.CreateContext())
        {
            var j = await db.TeamPlayers.FindAsync(j1);
            j!.EstMort = true;
            await db.SaveChangesAsync();
            await Svc(db).NettoyerCapitaineAsync(equipeId);
        }

        using (var db = factory.CreateContext())
            Assert.Empty(await db.TeamPlayers.Where(p => p.TeamId == equipeId && p.EstCapitaine).ToListAsync());
    }

    // ── Valeur d'équipe ──────────────────────────────────────────────────────

    /// <summary>
    /// Choix produit : le capitanat est un TITRE, pas une progression gagnée en
    /// jouant. Il ne doit donc pas gonfler la VEA — sinon désigner un capitaine
    /// pénaliserait l'équipe au Jeu Égal.
    /// </summary>
    [Fact]
    public void LeCapitanatNeChangePasLaVea()
    {
        var tt = RaceAvecCapitaine("Pro");
        var equipe = new Team { TeamType = tt };
        var j = Joueur("Marcus");
        // La valeur vient désormais du poste : on la pose là plutôt que sur le
        // joueur, la colonne ValeurActuelle n'existe plus.
        j.PlayerPosition.Cout = 90_000;
        equipe.Joueurs.Add(j);

        var avant = VeaCalculator.Calculer(equipe);
        j.EstCapitaine = true;
        var apres = VeaCalculator.Calculer(equipe);

        Assert.Equal(avant, apres);
    }

    // ── Feuille imprimée ─────────────────────────────────────────────────────

    /// <summary>
    /// La compétence du capitaine est CALCULÉE, pas stockée : elle ne remonte
    /// donc pas toute seule dans le PDF, qui lit les compétences en base. Ce
    /// test verrouille sa présence sur la feuille imprimée — c'est exactement
    /// l'oubli signalé par l'utilisateur.
    /// </summary>
    [Fact]
    public void LaCompetenceDuCapitaineFigureSurLaFeuilleImprimee()
    {
        var tt = RaceAvecCapitaine("Pro");
        tt.Nom = "Humains";

        var capitaine = Joueur("Marcus", 1);
        capitaine.EstCapitaine = true;
        var second = Joueur("Brutus", 2);

        var equipe = new Team { Nom = "Les Bretteurs", TeamType = tt };
        equipe.Joueurs.Add(capitaine);
        equipe.Joueurs.Add(second);

        var texte = LireTextePdf(new PdfService().GenererFeuilleEquipe(equipe, false));

        Assert.Contains("Pro (capitaine)", texte);
    }

    /// <summary>Un joueur sans le titre ne doit pas hériter de la compétence.</summary>
    [Fact]
    public void LaCompetenceNApparaitPasQuandAucunCapitaineNEstDesigne()
    {
        var tt = RaceAvecCapitaine("Pro");
        tt.Nom = "Humains";

        var equipe = new Team { Nom = "Les Bretteurs", TeamType = tt };
        equipe.Joueurs.Add(Joueur("Marcus", 1));

        var texte = LireTextePdf(new PdfService().GenererFeuilleEquipe(equipe, false));

        Assert.DoesNotContain("(capitaine)", texte);
    }

    private static string LireTextePdf(byte[] pdf)
    {
        using var doc = UglyToad.PdfPig.PdfDocument.Open(pdf);
        return string.Join("\n", doc.GetPages().Select(p => p.Text));
    }

    // ── Fixture ──────────────────────────────────────────────────────────────

    private static async Task<(int equipeId, int j1, int j2)> SeedEquipeAsync(
        TestDbFactory factory, string nom = "Les Testeurs")
    {
        using var db = factory.CreateContext();

        var jeu = new Game { Nom = "Blood Bowl " + Guid.NewGuid() };
        db.Games.Add(jeu);
        await db.SaveChangesAsync();

        // Team.CoachId et League sont des FK obligatoires : sans un utilisateur
        // et une ligue réels, le SaveChanges échoue sur « FOREIGN KEY
        // constraint failed », ce qui ressemble à un bug du code testé.
        var coach = new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(),
            UserName = $"coach{Guid.NewGuid():N}@test.fr",
            Email = $"coach{Guid.NewGuid():N}@test.fr",
            PseudoCoach = "Coach test"
        };
        db.Users.Add(coach);
        await db.SaveChangesAsync();

        var version = new RulesVersion { Nom = "V", GameId = jeu.Id };
        db.RulesVersions.Add(version);
        await db.SaveChangesAsync();

        var ligue = new League
        {
            Nom = "Ligue " + Guid.NewGuid(),
            GameId = jeu.Id,
            RulesVersionId = version.Id,
            CommissaireId = coach.Id
        };
        db.Leagues.Add(ligue);
        await db.SaveChangesAsync();

        var tt = new TeamType { Nom = "Humains", RulesVersionId = version.Id, GameId = jeu.Id };
        db.TeamTypes.Add(tt);
        await db.SaveChangesAsync();

        var poste = new PlayerPosition { Nom = "Blitzer", TeamTypeId = tt.Id, Cout = 90_000 };
        db.PlayerPositions.Add(poste);
        await db.SaveChangesAsync();

        var equipe = new Team
        {
            Nom = nom, TeamTypeId = tt.Id, CoachId = coach.Id, LeagueId = ligue.Id
        };
        db.Teams.Add(equipe);
        await db.SaveChangesAsync();

        var j1 = new TeamPlayer { TeamId = equipe.Id, Nom = "A", Numero = 1, PlayerPositionId = poste.Id };
        var j2 = new TeamPlayer { TeamId = equipe.Id, Nom = "B", Numero = 2, PlayerPositionId = poste.Id };
        db.TeamPlayers.AddRange(j1, j2);
        await db.SaveChangesAsync();

        return (equipe.Id, j1.Id, j2.Id);
    }
}
