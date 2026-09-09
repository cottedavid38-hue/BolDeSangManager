using BolDeSangManager.Helpers;
using BolDeSangManager.Data.Models;
using BolDeSangManager.Services;
using BolDeSangManager.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace BolDeSangManager.Tests;

/// <summary>
/// « Maîtres de la Non-Vie » (LRB p.94) : à l'après-match, l'équipe peut
/// embaucher gratuitement un joueur du poste visé.
///
/// Option A retenue avec l'utilisateur : l'application PROPOSE les postes
/// éligibles (ceux portant le mot-clé de la fiche de race) et le commissaire
/// choisit. Comme pour « Vil Prix », le mot-clé vient d'OptionsChoix, pas du
/// code : une future édition visant un autre poste se règle en admin.
/// </summary>
public class RecrutementGratuitTests
{
    private record Contexte(int EquipeId, int PosteTroisQuartId, int PosteAutreId,
                            int MatchId, int DivisionId, int AdverseId);

    /// <summary>
    /// Équipe de Morts-Ambulants avec deux postes : un Trois-quart (éligible)
    /// et un Gros Bras (non éligible).
    /// </summary>
    private static async Task<Contexte> SeedAsync(TestDbFactory factory, string? motCleVise,
                                                  int limite = 1, int tresorerie = 0)
    {
        using var db = factory.CreateContext();

        var coach = DataSeeder.CreateUser("coach");
        db.Users.Add(coach);
        await db.SaveChangesAsync();

        var (game, version) = await DataSeeder.SeedGameAsync(db);
        var ligue = await DataSeeder.SeedLeagueAsync(db, game.Id, version.Id, coach.Id);

        var tt = new TeamType
        {
            Nom = "Morts-Ambulants", GameId = game.Id,
            RulesVersionId = version.Id, CoutRelance = 70_000
        };
        db.TeamTypes.Add(tt);
        await db.SaveChangesAsync();

        var troisQuart = new PlayerPosition
        {
            Nom = "Trois-quart Zombie", TeamTypeId = tt.Id,
            MotsCles = "Trois-quart,Zombie", Cout = 40_000, QuantiteMax = 16
        };
        var autre = new PlayerPosition
        {
            Nom = "Goule", TeamTypeId = tt.Id,
            MotsCles = "Goule", Cout = 75_000, QuantiteMax = 4
        };
        db.PlayerPositions.AddRange(troisQuart, autre);

        if (motCleVise is not null)
        {
            var regle = new SpecialRule
            {
                RulesVersionId = version.Id, Nom = "Maîtres de la Non-Vie",
                Code = SpecialRuleCodes.RecrutementGratuitParMotCle, Description = "…"
            };
            db.SpecialRules.Add(regle);
            await db.SaveChangesAsync();

            db.TeamTypeSpecialRules.Add(new TeamTypeSpecialRule
            {
                TeamTypeId = tt.Id, SpecialRuleId = regle.Id, OptionsChoix = motCleVise,
                LimiteParApresMatch = limite
            });
        }

        var equipe = new Team
        {
            Nom = "Les Marcheurs", CoachId = coach.Id, LeagueId = ligue.Id,
            TeamTypeId = tt.Id, Tresorerie = tresorerie   // vide par défaut : c'est tout l'enjeu
        };
        var adverse = new Team
        {
            Nom = "Adversaire", CoachId = coach.Id, LeagueId = ligue.Id, TeamTypeId = tt.Id
        };
        db.Teams.AddRange(equipe, adverse);
        await db.SaveChangesAsync();

        // Un match réel : le droit à la recrue offerte est rattaché AU MATCH,
        // il se renouvelle donc à chaque après-match.
        var division = new Division { Nom = "Division Unique", LeagueId = ligue.Id };
        db.Divisions.Add(division);
        await db.SaveChangesAsync();

        var match = new Match
        {
            DivisionId = division.Id, Ronde = 1,
            EquipeDomicileId = equipe.Id, EquipeExterieurId = adverse.Id
        };
        db.Matches.Add(match);
        await db.SaveChangesAsync();

        return new Contexte(equipe.Id, troisQuart.Id, autre.Id,
                            match.Id, division.Id, adverse.Id);
    }

    private static TeamService Svc(Data.ApplicationDbContext db) =>
        new(db, NullLogger<TeamService>.Instance);

    // ── Postes éligibles proposés ────────────────────────────────────────────

    [Fact]
    public async Task SansLaRegle_AucunPosteEligible()
    {
        using var factory = new TestDbFactory();
        var ctx = await SeedAsync(factory, motCleVise: null);

        using var db = factory.CreateContext();
        Assert.Empty(await Svc(db).GetPostesRecrutementGratuitAsync(ctx.EquipeId));
    }

    /// <summary>Seuls les postes portant le mot-clé visé sont proposés.</summary>
    [Fact]
    public async Task AvecLaRegle_SeulsLesPostesDuMotCleSontProposes()
    {
        using var factory = new TestDbFactory();
        var ctx = await SeedAsync(factory, motCleVise: "Trois-quart");

        using var db = factory.CreateContext();
        var postes = await Svc(db).GetPostesRecrutementGratuitAsync(ctx.EquipeId);

        Assert.Single(postes);
        Assert.Equal("Trois-quart Zombie", postes[0].Nom);
    }

    /// <summary>
    /// Le mot-clé vient de la fiche de race : viser « Goule » doit proposer la
    /// Goule et pas le Trois-quart. C'est la preuve que la règle est générique.
    /// </summary>
    [Fact]
    public async Task LeMotCleVientDeLaFicheDeRace()
    {
        using var factory = new TestDbFactory();
        var ctx = await SeedAsync(factory, motCleVise: "Goule");

        using var db = factory.CreateContext();
        var postes = await Svc(db).GetPostesRecrutementGratuitAsync(ctx.EquipeId);

        Assert.Single(postes);
        Assert.Equal("Goule", postes[0].Nom);
    }

    /// <summary>Un mot-clé vide ne doit proposer personne, pas tout le monde.</summary>
    [Fact]
    public async Task MotCleVide_NeProposeAucunPoste()
    {
        using var factory = new TestDbFactory();
        var ctx = await SeedAsync(factory, motCleVise: "");

        using var db = factory.CreateContext();
        Assert.Empty(await Svc(db).GetPostesRecrutementGratuitAsync(ctx.EquipeId));
    }

    // ── Recrutement gratuit ──────────────────────────────────────────────────

    /// <summary>
    /// Le cœur de la règle : l'embauche est gratuite, donc possible avec une
    /// trésorerie à zéro, et ne débite rien.
    /// </summary>
    [Fact]
    public async Task RecruterGratuitement_NeDebitePasLaTresorerie()
    {
        using var factory = new TestDbFactory();
        var ctx = await SeedAsync(factory, motCleVise: "Trois-quart");

        using (var db = factory.CreateContext())
            await Svc(db).RecruterJoueurGratuitAsync(ctx.EquipeId, ctx.PosteTroisQuartId, "Zomb", 7);

        using (var db = factory.CreateContext())
        {
            var equipe = await db.Teams.Include(t => t.Joueurs).FirstAsync(t => t.Id == ctx.EquipeId);
            Assert.Equal(0, equipe.Tresorerie);
            Assert.Single(equipe.Joueurs);
            Assert.Equal("Zomb", equipe.Joueurs.First().Nom);
        }
    }

    /// <summary>
    /// Le LRB est explicite : « il ajoute quand même sa valeur à la Valeur
    /// d'Équipe ». Gratuit à l'achat ne veut pas dire sans valeur.
    /// </summary>
    [Fact]
    public async Task RecruterGratuitement_LeJoueurGardeSaValeur()
    {
        using var factory = new TestDbFactory();
        var ctx = await SeedAsync(factory, motCleVise: "Trois-quart");

        using (var db = factory.CreateContext())
            await Svc(db).RecruterJoueurGratuitAsync(ctx.EquipeId, ctx.PosteTroisQuartId, "Zomb", 7);

        using (var db = factory.CreateContext())
        {
            var joueur = await db.TeamPlayers
                .Include(j => j.PlayerPosition)
                .Include(j => j.Improvements)
                .FirstAsync(j => j.TeamId == ctx.EquipeId);
            // Une recrue GRATUITE garde la valeur de son poste : elle ne coûte
            // rien au budget, mais elle vaut son prix dans la VEA.
            Assert.Equal(40_000,
                ValeurJoueurCalculator.Calculer(joueur, BaremeAmelioration.ParDefaut()));
        }
    }

    /// <summary>
    /// Garde-fou serveur : un poste hors du mot-clé ne peut pas être recruté
    /// gratuitement, même si l'écran le proposait.
    /// </summary>
    [Fact]
    public async Task RecruterGratuitement_PosteNonEligible_EstRefuse()
    {
        using var factory = new TestDbFactory();
        var ctx = await SeedAsync(factory, motCleVise: "Trois-quart");

        using var db = factory.CreateContext();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Svc(db).RecruterJoueurGratuitAsync(ctx.EquipeId, ctx.PosteAutreId, "Goule", 8));

        Assert.Contains("gratuit", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Une équipe sans la règle ne peut rien recruter gratuitement.</summary>
    [Fact]
    public async Task RecruterGratuitement_SansLaRegle_EstRefuse()
    {
        using var factory = new TestDbFactory();
        var ctx = await SeedAsync(factory, motCleVise: null);

        using var db = factory.CreateContext();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Svc(db).RecruterJoueurGratuitAsync(ctx.EquipeId, ctx.PosteTroisQuartId, "Zomb", 7));
    }

    /// <summary>
    /// Les limites de roster restent opposables : la gratuité ne dispense pas
    /// du plafond de 16 joueurs ni du maximum par poste.
    /// </summary>
    [Fact]
    public async Task RecruterGratuitement_RespecteLeMaximumDuPoste()
    {
        using var factory = new TestDbFactory();
        var ctx = await SeedAsync(factory, motCleVise: "Goule");

        // La Goule est plafonnée à 4 : on en place 4, la 5e doit être refusée.
        using (var db = factory.CreateContext())
        {
            for (var i = 1; i <= 4; i++)
                db.TeamPlayers.Add(new TeamPlayer
                {
                    TeamId = ctx.EquipeId, PlayerPositionId = ctx.PosteAutreId,
                    Nom = $"G{i}", Numero = i
                });
            await db.SaveChangesAsync();
        }

        using (var db = factory.CreateContext())
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => Svc(db).RecruterJoueurGratuitAsync(ctx.EquipeId, ctx.PosteAutreId, "G5", 5));
            Assert.Contains("Limite", ex.Message);
        }
    }

    // ── Plafond par phase d'après-match ──────────────────────────────────────

    /// <summary>
    /// Le LRB accorde UNE recrue offerte par phase d'après-match. Sans plafond,
    /// un coach remplissait tout son effectif gratuitement d'un coup — constaté
    /// avant correction : 3 recrutements d'affilée passaient avec 0 po en caisse.
    /// </summary>
    [Fact]
    public async Task LaSecondeRecrueDuMemeMatchEstRefusee()
    {
        using var factory = new TestDbFactory();
        var ctx = await SeedAsync(factory, "Trois-quart", limite: 1);

        using (var db = factory.CreateContext())
            await Svc(db).RecruterJoueurGratuitAsync(ctx.EquipeId, ctx.PosteTroisQuartId, "Z1", 1, ctx.MatchId);

        using (var db = factory.CreateContext())
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => Svc(db).RecruterJoueurGratuitAsync(ctx.EquipeId, ctx.PosteTroisQuartId, "Z2", 2, ctx.MatchId));
            Assert.Contains("déjà", ex.Message);
        }

        using (var db = factory.CreateContext())
            Assert.Equal(1, await db.TeamPlayers.CountAsync(p => p.TeamId == ctx.EquipeId));
    }

    /// <summary>Le droit se renouvelle : il est lié au match, pas à l'équipe.</summary>
    [Fact]
    public async Task LeDroitSeRenouvelleAuMatchSuivant()
    {
        using var factory = new TestDbFactory();
        var ctx = await SeedAsync(factory, "Trois-quart", limite: 1);

        int match2;
        using (var db = factory.CreateContext())
        {
            var m = new Match
            {
                DivisionId = ctx.DivisionId, Ronde = 2,
                EquipeDomicileId = ctx.EquipeId, EquipeExterieurId = ctx.AdverseId
            };
            db.Matches.Add(m);
            await db.SaveChangesAsync();
            match2 = m.Id;
        }

        using (var db = factory.CreateContext())
            await Svc(db).RecruterJoueurGratuitAsync(ctx.EquipeId, ctx.PosteTroisQuartId, "Z1", 1, ctx.MatchId);
        using (var db = factory.CreateContext())
            await Svc(db).RecruterJoueurGratuitAsync(ctx.EquipeId, ctx.PosteTroisQuartId, "Z2", 2, match2);

        using (var db = factory.CreateContext())
            Assert.Equal(2, await db.TeamPlayers.CountAsync(p => p.TeamId == ctx.EquipeId));
    }

    /// <summary>Le plafond est un paramètre : une race peut en accorder deux.</summary>
    [Fact]
    public async Task LaLimiteEstConfigurableParRace()
    {
        using var factory = new TestDbFactory();
        var ctx = await SeedAsync(factory, "Trois-quart", limite: 2);

        using (var db = factory.CreateContext())
            await Svc(db).RecruterJoueurGratuitAsync(ctx.EquipeId, ctx.PosteTroisQuartId, "Z1", 1, ctx.MatchId);
        using (var db = factory.CreateContext())
            await Svc(db).RecruterJoueurGratuitAsync(ctx.EquipeId, ctx.PosteTroisQuartId, "Z2", 2, ctx.MatchId);

        using (var db = factory.CreateContext())
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => Svc(db).RecruterJoueurGratuitAsync(ctx.EquipeId, ctx.PosteTroisQuartId, "Z3", 3, ctx.MatchId));

        using (var db = factory.CreateContext())
            Assert.Equal(2, await db.TeamPlayers.CountAsync(p => p.TeamId == ctx.EquipeId));
    }

    /// <summary>
    /// Supprimer une recrue offerte rend le droit : c'est l'intérêt de marquer
    /// le JOUEUR plutôt que d'incrémenter un compteur qu'il faudrait corriger.
    /// </summary>
    [Fact]
    public async Task SupprimerLaRecrueLibereLeDroit()
    {
        using var factory = new TestDbFactory();
        var ctx = await SeedAsync(factory, "Trois-quart", limite: 1);

        using (var db = factory.CreateContext())
            await Svc(db).RecruterJoueurGratuitAsync(ctx.EquipeId, ctx.PosteTroisQuartId, "Z1", 1, ctx.MatchId);

        using (var db = factory.CreateContext())
        {
            var j = await db.TeamPlayers.FirstAsync(p => p.TeamId == ctx.EquipeId);
            db.TeamPlayers.Remove(j);
            await db.SaveChangesAsync();
        }

        using (var db = factory.CreateContext())
            await Svc(db).RecruterJoueurGratuitAsync(ctx.EquipeId, ctx.PosteTroisQuartId, "Z2", 2, ctx.MatchId);

        using (var db = factory.CreateContext())
            Assert.Equal(1, await db.TeamPlayers.CountAsync(p => p.TeamId == ctx.EquipeId));
    }

    /// <summary>
    /// Le recrutement PAYANT reste libre : la règle ne s'applique pas toujours,
    /// et le coach doit pouvoir compléter son effectif normalement.
    /// </summary>
    [Fact]
    public async Task LeRecrutementPayantResteIllimite()
    {
        using var factory = new TestDbFactory();
        var ctx = await SeedAsync(factory, "Trois-quart", limite: 1, tresorerie: 500_000);

        using (var db = factory.CreateContext())
            await Svc(db).RecruterJoueurGratuitAsync(ctx.EquipeId, ctx.PosteTroisQuartId, "Offert", 1, ctx.MatchId);

        for (var i = 2; i <= 4; i++)
            using (var db = factory.CreateContext())
                await Svc(db).RecruterJoueurAsync(ctx.EquipeId, ctx.PosteTroisQuartId, $"Paye{i}", i);

        using (var db = factory.CreateContext())
        {
            Assert.Equal(4, await db.TeamPlayers.CountAsync(p => p.TeamId == ctx.EquipeId));
            var equipe = await db.Teams.FindAsync(ctx.EquipeId);
            // 3 achats à 40k ; la recrue offerte n'a rien coûté.
            Assert.Equal(500_000 - 3 * 40_000, equipe!.Tresorerie);
        }
    }

    /// <summary>Une limite à 0 signifie « pas de plafond ».</summary>
    [Fact]
    public async Task UneLimiteAZeroNePlafonnePas()
    {
        using var factory = new TestDbFactory();
        var ctx = await SeedAsync(factory, "Trois-quart", limite: 0);

        for (var i = 1; i <= 3; i++)
            using (var db = factory.CreateContext())
                await Svc(db).RecruterJoueurGratuitAsync(ctx.EquipeId, ctx.PosteTroisQuartId, $"Z{i}", i, ctx.MatchId);

        using (var db = factory.CreateContext())
            Assert.Equal(3, await db.TeamPlayers.CountAsync(p => p.TeamId == ctx.EquipeId));
    }
}
