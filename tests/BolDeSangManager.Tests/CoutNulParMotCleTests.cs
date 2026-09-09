using BolDeSangManager.Data.Enums;
using BolDeSangManager.Data.Models;
using BolDeSangManager.Helpers;
using BolDeSangManager.Services;
using BolDeSangManager.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace BolDeSangManager.Tests;

/// <summary>
/// « Trois-quarts à Vil Prix » (LRB p.93) : dans la VEA, le coût d'embauche des
/// joueurs visés compte pour 0 po.
///
/// Le mot-clé ciblé n'est PAS codé en dur : il vient de
/// <c>TeamTypeSpecialRule.OptionsChoix</c>, saisi en admin sur la fiche de race.
/// Une future édition qui viserait « Gros Bras » se règle donc sans dev.
/// </summary>
public class CoutNulParMotCleTests
{
    /// <summary>Race porteuse de la règle, ciblant <paramref name="motCle"/>.</summary>
    private static TeamType RaceAvecVilPrix(string motCle)
    {
        var tt = new TeamType { Nom = "Snotlings", CoutRelance = 60_000 };
        tt.ReglesSpecialesListe.Add(new TeamTypeSpecialRule
        {
            OptionsChoix = motCle,
            SpecialRule = new SpecialRule
            {
                Nom = "Trois-quarts à Vil Prix",
                Code = SpecialRuleCodes.CoutNulParMotCle,
                Description = "Les Coûts d'Embauche des Trois-quarts comptent pour 0 po."
            }
        });
        return tt;
    }

    /// <summary>
    /// Joueur d'un poste coûtant <paramref name="cout"/>, éventuellement porteur
    /// d'améliorations.
    ///
    /// ⚠️ La valeur d'un joueur est CALCULÉE (coût du poste + hausses du barème),
    /// plus lue dans une colonne : un joueur « amélioré » se fabrique donc en lui
    /// donnant de vraies <c>PlayerImprovement</c>, pas en forçant un montant.
    /// </summary>
    private static TeamPlayer Joueur(TeamType tt, string motsCles, int cout,
        params (ImprovementType type, AffectedStat? stat)[] ameliorations)
    {
        var poste = new PlayerPosition
        {
            Nom = "Poste", TeamType = tt, MotsCles = motsCles, Cout = cout
        };
        tt.Postes.Add(poste);

        var joueur = new TeamPlayer { PlayerPosition = poste };
        foreach (var (amelioration, i) in ameliorations.Select((a, i) => (a, i + 1)))
            joueur.Improvements.Add(new PlayerImprovement
            {
                Palier = i, Type = amelioration.type, StatAmelioree = amelioration.stat
            });
        return joueur;
    }

    // ── Sans la règle : rien ne change ───────────────────────────────────────

    [Fact]
    public void SansLaRegle_LaVeaCompteLeCoutComplet()
    {
        var tt = new TeamType { Nom = "Humains", CoutRelance = 50_000 };
        var equipe = new Team { TeamType = tt };
        equipe.Joueurs.Add(Joueur(tt, "Trois-quart,Humain", 50_000));

        Assert.Equal(50_000, VeaCalculator.Calculer(equipe));
    }

    // ── Avec la règle ────────────────────────────────────────────────────────

    [Fact]
    public void AvecLaRegle_LeCoutDEmbaucheEstDeduit()
    {
        var tt = RaceAvecVilPrix("Trois-quart");
        var equipe = new Team { TeamType = tt };
        equipe.Joueurs.Add(Joueur(tt, "Trois-quart,Snotling", 15_000));

        Assert.Equal(0, VeaCalculator.Calculer(equipe));
    }

    /// <summary>
    /// Le LRB précise : « Toute augmentation de valeur de ces joueurs est
    /// incluse normalement. » On déduit donc le COÛT D'EMBAUCHE, on ne met pas
    /// la valeur du joueur à zéro — sinon les améliorations gagnées en ligue
    /// disparaîtraient de la VEA.
    /// </summary>
    [Fact]
    public void AvecLaRegle_LesAmeliorationsRestentComptees()
    {
        var tt = RaceAvecVilPrix("Trois-quart");
        var equipe = new Team { TeamType = tt };
        // Embauché 15 000, vaut 35 000 après une compétence principale (+20 000).
        equipe.Joueurs.Add(Joueur(tt, "Trois-quart,Snotling", 15_000,
            (ImprovementType.SelectionPrimaire, null)));

        Assert.Equal(20_000, VeaCalculator.Calculer(equipe));
    }

    /// <summary>Un poste sans le mot-clé visé n'est pas concerné.</summary>
    [Fact]
    public void AvecLaRegle_LesAutresPostesSontIntacts()
    {
        var tt = RaceAvecVilPrix("Trois-quart");
        var equipe = new Team { TeamType = tt };
        equipe.Joueurs.Add(Joueur(tt, "Trois-quart,Snotling", 15_000));
        equipe.Joueurs.Add(Joueur(tt, "Gros Bras,Troll", 115_000));

        Assert.Equal(115_000, VeaCalculator.Calculer(equipe));
    }

    /// <summary>
    /// Le mot-clé vient de la fiche de race : viser « Gros Bras » doit exonérer
    /// les Gros Bras et PAS les Trois-quarts. C'est ce test qui prouve que la
    /// règle est générique et pas un cas particulier déguisé.
    /// </summary>
    [Fact]
    public void LeMotCleVientDeLaFicheDeRace()
    {
        var tt = RaceAvecVilPrix("Gros Bras");
        var equipe = new Team { TeamType = tt };
        equipe.Joueurs.Add(Joueur(tt, "Trois-quart,Snotling", 15_000));
        equipe.Joueurs.Add(Joueur(tt, "Gros Bras,Troll", 115_000));

        Assert.Equal(15_000, VeaCalculator.Calculer(equipe));
    }

    /// <summary>Plusieurs mots-clés visés : chacun exonère ses postes.</summary>
    [Fact]
    public void PlusieursMotsClesSontAcceptes()
    {
        var tt = RaceAvecVilPrix("Trois-quart, Gros Bras");
        var equipe = new Team { TeamType = tt };
        equipe.Joueurs.Add(Joueur(tt, "Trois-quart,Snotling", 15_000));
        // Troll embauché 115 000, +1 AR (+10 000) → 125 000, moins le coût = 10 000.
        equipe.Joueurs.Add(Joueur(tt, "Gros Bras,Troll", 115_000,
            (ImprovementType.AmeliorationForceArmure, AffectedStat.Armure)));

        Assert.Equal(10_000, VeaCalculator.Calculer(equipe));
    }

    /// <summary>
    /// Garde-fou : un mot-clé non renseigné ne doit exonérer PERSONNE. Sans
    /// cette borne, une chaîne vide correspondrait à tout le monde et mettrait
    /// la VEA à zéro.
    /// </summary>
    [Fact]
    public void MotCleVide_NExonerePersonne()
    {
        var tt = RaceAvecVilPrix("");
        var equipe = new Team { TeamType = tt };
        equipe.Joueurs.Add(Joueur(tt, "Trois-quart,Snotling", 15_000));

        Assert.Equal(15_000, VeaCalculator.Calculer(equipe));
    }

    /// <summary>
    /// « Trois-quart » ne doit pas correspondre à « Trois-quarts Vétéran » :
    /// les mots-clés sont comparés entiers, pas en sous-chaîne.
    /// </summary>
    [Fact]
    public void LaComparaisonPorteSurLeMotCleEntier()
    {
        var tt = RaceAvecVilPrix("Trois-quart");
        var equipe = new Team { TeamType = tt };
        equipe.Joueurs.Add(Joueur(tt, "Trois-quartier,Snotling", 15_000));

        Assert.Equal(15_000, VeaCalculator.Calculer(equipe));
    }

    /// <summary>La VEA ne descend jamais sous zéro.</summary>
    [Fact]
    public void UnJoueurNeContribueJamaisNegativement()
    {
        var tt = RaceAvecVilPrix("Trois-quart");
        var equipe = new Team { TeamType = tt };
        // Cas limite : joueur sans aucune amélioration, donc valeur = coût
        // d'embauche exactement. Le Math.Max reste le garde-fou si un barème
        // futur permettait une hausse négative.
        equipe.Joueurs.Add(Joueur(tt, "Trois-quart,Snotling", 15_000));

        Assert.Equal(0, VeaCalculator.Calculer(equipe));
    }

    /// <summary>Les morts et retraités restent hors VEA, règle ou pas.</summary>
    [Fact]
    public void LesJoueursInactifsRestentExclus()
    {
        var tt = RaceAvecVilPrix("Trois-quart");
        var equipe = new Team { TeamType = tt };
        var mort = Joueur(tt, "Trois-quart,Snotling", 15_000);
        mort.EstMort = true;
        equipe.Joueurs.Add(mort);

        Assert.Equal(0, VeaCalculator.Calculer(equipe));
    }

    /// <summary>
    /// Test d'INTÉGRATION : la règle ne s'applique que si la requête charge
    /// <c>TeamType.ReglesSpecialesListe</c>. Un Include oublié donnerait une VEA
    /// silencieusement fausse — le calcul serait juste, la donnée absente.
    /// Ce test lit l'équipe par <c>GetEquipeAsync</c>, le chemin réel des écrans.
    /// </summary>
    [Fact]
    public async Task GetEquipeAsync_ChargeLesReglesNecessairesAuCalcul()
    {
        using var factory = new TestDbFactory();
        int equipeId;

        using (var db = factory.CreateContext())
        {
            var coach = DataSeeder.CreateUser("coach");
            db.Users.Add(coach);
            await db.SaveChangesAsync();

            var (game, version) = await DataSeeder.SeedGameAsync(db);
            var ligue = await DataSeeder.SeedLeagueAsync(db, game.Id, version.Id, coach.Id);

            // GameId est obligatoire : sans lui, le SaveChanges échoue sur un
            // « FOREIGN KEY constraint failed » qui ressemble à un bug du code
            // testé alors que c'est la fixture.
            var tt = new TeamType
            {
                Nom = "Snotlings", GameId = game.Id,
                RulesVersionId = version.Id, CoutRelance = 60_000
            };
            db.TeamTypes.Add(tt);
            await db.SaveChangesAsync();

            var poste = new PlayerPosition
            {
                Nom = "Trois-quart Snotling", TeamTypeId = tt.Id,
                MotsCles = "Trois-quart,Snotling", Cout = 15_000, QuantiteMax = 16
            };
            db.PlayerPositions.Add(poste);

            var regle = new SpecialRule
            {
                RulesVersionId = version.Id, Nom = "Trois-quarts à Vil Prix",
                Code = SpecialRuleCodes.CoutNulParMotCle, Description = "…"
            };
            db.SpecialRules.Add(regle);
            await db.SaveChangesAsync();

            db.TeamTypeSpecialRules.Add(new TeamTypeSpecialRule
            {
                TeamTypeId = tt.Id, SpecialRuleId = regle.Id, OptionsChoix = "Trois-quart"
            });

            var equipe = new Team
            {
                Nom = "Les Petits", CoachId = coach.Id, LeagueId = ligue.Id, TeamTypeId = tt.Id
            };
            db.Teams.Add(equipe);
            await db.SaveChangesAsync();

            // Embauché 15 000, une compétence principale (+20 000) → 35 000,
            // moins le coût d'embauche exonéré : doit compter 20 000.
            var piti = new TeamPlayer
            {
                TeamId = equipe.Id, PlayerPositionId = poste.Id,
                Nom = "Piti", Numero = 1
            };
            db.TeamPlayers.Add(piti);
            await db.SaveChangesAsync();

            db.PlayerImprovements.Add(new PlayerImprovement
            {
                TeamPlayerId = piti.Id, Palier = 1,
                Type = ImprovementType.SelectionPrimaire
            });
            await db.SaveChangesAsync();
            equipeId = equipe.Id;
        }

        using (var db = factory.CreateContext())
        {
            var svc = new TeamService(db, NullLogger<TeamService>.Instance);
            var equipe = await svc.GetEquipeAsync(equipeId);

            Assert.NotNull(equipe);
            Assert.Equal(20_000, svc.CalculerVEA(equipe!));
        }
    }
}
