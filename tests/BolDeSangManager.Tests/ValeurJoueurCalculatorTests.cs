using BolDeSangManager.Data.Enums;
using BolDeSangManager.Data.Models;
using BolDeSangManager.Helpers;
using BolDeSangManager.Services;
using BolDeSangManager.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace BolDeSangManager.Tests;

/// <summary>
/// La valeur d'un joueur est CALCULÉE (coût du poste + hausses du barème), plus
/// lue dans une colonne figée.
///
/// C'est ce qui rend les corrections propagables : corriger le coût d'un poste
/// ou une ligne du barème doit se répercuter sur les joueurs déjà recrutés et
/// les améliorations déjà acquises — demande explicite de l'utilisateur.
/// </summary>
public class ValeurJoueurCalculatorTests
{
    private static readonly BaremeAmelioration Lrb = BaremeAmelioration.ParDefaut();

    private static TeamPlayer Joueur(int coutPoste,
        params (ImprovementType type, AffectedStat? stat, bool elite)[] ameliorations)
    {
        var joueur = new TeamPlayer
        {
            PlayerPosition = new PlayerPosition { Nom = "Poste", Cout = coutPoste }
        };

        foreach (var (a, i) in ameliorations.Select((a, i) => (a, i + 1)))
            joueur.Improvements.Add(new PlayerImprovement
            {
                Palier = i,
                Type = a.type,
                StatAmelioree = a.stat,
                Skill = a.elite ? new Skill { Nom = "Élite", EstElite = true } : null
            });

        return joueur;
    }

    [Fact]
    public void SansAmelioration_LaValeurEstLeCoutDuPoste()
    {
        Assert.Equal(50_000, ValeurJoueurCalculator.Calculer(Joueur(50_000), Lrb));
    }

    [Fact]
    public void AvecDeuxAmeliorations_LaValeurEstLaSomme()
    {
        // 50 000 + compétence principale (20 000) + 1 F (60 000) = 130 000
        var joueur = Joueur(50_000,
            (ImprovementType.SelectionPrimaire, null, false),
            (ImprovementType.AmeliorationCarac, AffectedStat.Force, false));

        Assert.Equal(130_000, ValeurJoueurCalculator.Calculer(joueur, Lrb));
    }

    /// <summary>
    /// LE test de la demande : corriger le coût d'un poste doit changer la
    /// valeur des joueurs qui l'occupent. Impossible avec une valeur stockée.
    /// </summary>
    [Fact]
    public void ChangerLeCoutDuPoste_ChangeLaValeurDuJoueur()
    {
        var joueur = Joueur(50_000, (ImprovementType.SelectionPrimaire, null, false));
        Assert.Equal(70_000, ValeurJoueurCalculator.Calculer(joueur, Lrb));

        joueur.PlayerPosition.Cout = 60_000;   // correction en admin
        Assert.Equal(80_000, ValeurJoueurCalculator.Calculer(joueur, Lrb));
    }

    /// <summary>Même démonstration côté barème : corriger une hausse se propage.</summary>
    [Fact]
    public void ChangerLeBareme_ChangeLaValeurDUneAmeliorationDejaAcquise()
    {
        var joueur = Joueur(50_000, (ImprovementType.SelectionPrimaire, null, false));
        var baremeCorrige = new BaremeAmelioration { HaussePrincipale = 30_000 };

        Assert.Equal(80_000, ValeurJoueurCalculator.Calculer(joueur, baremeCorrige));
    }

    [Fact]
    public void UneCompetenceElite_Ajoute10000()
    {
        var normale = Joueur(50_000, (ImprovementType.SelectionPrimaire, null, false));
        var elite   = Joueur(50_000, (ImprovementType.SelectionPrimaire, null, true));

        Assert.Equal(70_000, ValeurJoueurCalculator.Calculer(normale, Lrb));
        Assert.Equal(80_000, ValeurJoueurCalculator.Calculer(elite, Lrb));
    }

    /// <summary>
    /// La colonne legacy <c>ValeurHausse</c> ne doit PLUS être lue : si elle
    /// l'était, ce joueur vaudrait 50 000 + 999 000.
    /// </summary>
    [Fact]
    public void LaColonneLegacyValeurHausse_NEstPasLue()
    {
        var joueur = Joueur(50_000, (ImprovementType.SelectionPrimaire, null, false));
        joueur.Improvements.First().ValeurHausse = 999_000;

        Assert.Equal(70_000, ValeurJoueurCalculator.Calculer(joueur, Lrb));
    }

    /// <summary>
    /// Poste non chargé (Include manquant) ou supprimé du catalogue : la valeur
    /// vaut 0. Il n'existe plus aucune valeur de repli en base depuis que la
    /// colonne a disparu — et c'est VOULU : un zéro à l'écran signale le
    /// problème, là où une valeur figée l'aurait masqué.
    /// </summary>
    [Fact]
    public void SansPoste_VautZero()
    {
        var joueur = new TeamPlayer { PlayerPosition = null! };
        Assert.Equal(0, ValeurJoueurCalculator.Calculer(joueur, Lrb));
    }

    // ── Anti-régression sur les Include ──────────────────────────────────────

    /// <summary>
    /// Test d'INTÉGRATION : la valeur n'est juste que si la requête de service
    /// charge <c>PlayerPosition</c>, <c>Improvements</c> (avec leur
    /// <c>Skill</c> pour le drapeau Élite) et <c>League.RulesVersion</c> pour le
    /// barème. Retirer l'un de ces Include de <c>GetEquipeAsync</c> doit faire
    /// tomber ce test — c'est tout son intérêt, la panne étant sinon totalement
    /// silencieuse.
    /// </summary>
    [Fact]
    public async Task GetEquipeAsync_ChargeToutCeQuExigeLeCalcul()
    {
        using var factory = new TestDbFactory();
        int equipeId;

        using (var db = factory.CreateContext())
        {
            var coach = DataSeeder.CreateUser("val");
            db.Users.Add(coach);
            await db.SaveChangesAsync();

            var (game, version) = await DataSeeder.SeedGameAsync(db);

            // Barème VOLONTAIREMENT différent du LRB par défaut : sans ça, un
            // Include manquant sur League.RulesVersion retomberait sur les mêmes
            // chiffres et le test passerait pour de mauvaises raisons.
            version.HausseSecondaire = 45_000;
            version.SurcoutElite = 15_000;
            await db.SaveChangesAsync();

            var ligue = await DataSeeder.SeedLeagueAsync(db, game.Id, version.Id, coach.Id);
            var (teamType, position) = await DataSeeder.SeedTeamTypeAsync(db, game.Id);
            var (_, elite) = await DataSeeder.SeedSkillsAsync(db);

            var equipe = new Team
            {
                Nom = "Les Calculés", CoachId = coach.Id,
                LeagueId = ligue.Id, TeamTypeId = teamType.Id
            };
            db.Teams.Add(equipe);
            await db.SaveChangesAsync();

            var joueur = new TeamPlayer
            {
                TeamId = equipe.Id, PlayerPositionId = position.Id,
                Nom = "Grok", Numero = 1
            };
            db.TeamPlayers.Add(joueur);
            await db.SaveChangesAsync();

            // Compétence secondaire ÉLITE : 40 000 + 10 000.
            db.PlayerImprovements.Add(new PlayerImprovement
            {
                TeamPlayerId = joueur.Id, Palier = 1,
                Type = ImprovementType.SelectionSecondaire, SkillId = elite.Id
            });
            await db.SaveChangesAsync();
            equipeId = equipe.Id;
        }

        using (var db = factory.CreateContext())
        {
            var svc = new TeamService(db, NullLogger<TeamService>.Instance);
            var equipe = await svc.GetEquipeAsync(equipeId);
            Assert.NotNull(equipe);

            var joueur = equipe!.Joueurs.Single();
            var bareme = ValeurJoueurCalculator.BaremeDe(equipe);

            // 50 000 (poste) + 45 000 (secondaire, barème de la version)
            // + 15 000 (surcoût Élite de la version)
            Assert.Equal(110_000, ValeurJoueurCalculator.Calculer(joueur, bareme));
        }
    }

    /// <summary>
    /// Contre-épreuve du test précédent : sans les Include, la valeur est FAUSSE
    /// et pourtant aucune exception n'est levée. C'est la preuve que le test
    /// ci-dessus discrimine bien et ne passerait pas « par hasard ».
    /// </summary>
    [Fact]
    public async Task SansInclude_LaValeurEstFausseSansErreur()
    {
        using var factory = new TestDbFactory();
        int equipeId;

        using (var db = factory.CreateContext())
        {
            var coach = DataSeeder.CreateUser("val2");
            db.Users.Add(coach);
            await db.SaveChangesAsync();

            var (game, version) = await DataSeeder.SeedGameAsync(db);
            var ligue = await DataSeeder.SeedLeagueAsync(db, game.Id, version.Id, coach.Id);
            var (teamType, position) = await DataSeeder.SeedTeamTypeAsync(db, game.Id);

            var equipe = new Team
            {
                Nom = "Les Oubliés", CoachId = coach.Id,
                LeagueId = ligue.Id, TeamTypeId = teamType.Id
            };
            db.Teams.Add(equipe);
            await db.SaveChangesAsync();

            var joueur = new TeamPlayer
            {
                TeamId = equipe.Id, PlayerPositionId = position.Id,
                Nom = "Grok", Numero = 1
            };
            db.TeamPlayers.Add(joueur);
            await db.SaveChangesAsync();

            db.PlayerImprovements.Add(new PlayerImprovement
            {
                TeamPlayerId = joueur.Id, Palier = 1,
                Type = ImprovementType.SelectionPrimaire
            });
            await db.SaveChangesAsync();
            equipeId = equipe.Id;
        }

        using (var db = factory.CreateContext())
        {
            // Requête volontairement NUE : le poste est chargé, pas les améliorations.
            var equipe = await db.Teams
                .Include(t => t.Joueurs).ThenInclude(j => j.PlayerPosition)
                .FirstAsync(t => t.Id == equipeId);

            var joueur = equipe.Joueurs.Single();

            // 50 000 au lieu de 70 000 : silencieusement faux.
            Assert.Equal(50_000, ValeurJoueurCalculator.Calculer(joueur, Lrb));
        }
    }
}
