using BolDeSangManager.Data;
using BolDeSangManager.Data.Enums;
using BolDeSangManager.Data.Models;
using BolDeSangManager.Services;
using BolDeSangManager.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace BolDeSangManager.Tests;

/// <summary>
/// Renommer un joueur d'une équipe déjà engagée dans une ligue (#16).
///
/// Le nom n'entre dans AUCUN calcul (ni VEA, ni budget, ni classement) : rien
/// ne justifie de le verrouiller au lancement de la saison. Le piège est
/// ailleurs — le seul chemin existant qui écrivait un nom de joueur,
/// <c>ModifierEquipeAsync</c>, supprime et recrée tout le roster. Élargir SON
/// verrou aurait détruit XP, compétences et blessures ; d'où une commande
/// dédiée, et le test de préservation ci-dessous qui en est la preuve.
/// </summary>
public class RenommerJoueurTests : IDisposable
{
    private readonly TestDbFactory _factory = new();
    public void Dispose() => _factory.Dispose();

    private static TeamService Svc(ApplicationDbContext db) =>
        new(db, NullLogger<TeamService>.Instance);

    [Fact]
    public async Task RenommerJoueur_SaisonLancee_EnregistreLeNouveauNom()
    {
        await using var db = _factory.CreateContext();
        var (equipe, joueur, coachId) = await SeedAsync(db, LeagueStatus.EnCours);

        await Svc(db).RenommerJoueurAsync(joueur.Id, coachId, "Grimli Barbe-de-Fer");

        await using var relecture = _factory.CreateContext();
        var relu = await relecture.TeamPlayers.FirstAsync(p => p.Id == joueur.Id);
        Assert.Equal("Grimli Barbe-de-Fer", relu.Nom);
        Assert.Equal(equipe.Id, relu.TeamId);
    }

    /// <summary>
    /// LE test du ticket : renommer ne doit rien détruire. Si quelqu'un
    /// réimplémente un jour ce renommage en passant par
    /// <c>ModifierEquipeAsync</c> (qui recrée le roster), ce test tombe —
    /// c'est exactement son rôle.
    /// </summary>
    [Fact]
    public async Task RenommerJoueur_PreserveXpCompetencesEtBlessures()
    {
        await using var db = _factory.CreateContext();
        var (_, joueur, coachId) = await SeedAsync(db, LeagueStatus.EnCours);

        // On charge le joueur de tout ce qu'une saison lui a apporté.
        var (skill, _) = await DataSeeder.SeedSkillsAsync(db);
        joueur.PointsStarPlayer = 12;
        joueur.ValeurActuelle = 90_000;
        joueur.ModForce = 1;
        joueur.Competences.Add(new TeamPlayerSkill { SkillId = skill.Id, EstCompetenceDepart = false });
        joueur.Blessures.Add(new PlayerInjury { Type = InjuryType.BlessurePersistante, Description = "Vieille douleur" });
        await db.SaveChangesAsync();

        await Svc(db).RenommerJoueurAsync(joueur.Id, coachId, "Le Balafré");

        await using var relecture = _factory.CreateContext();
        var relu = await relecture.TeamPlayers
            .Include(p => p.Competences)
            .Include(p => p.Blessures)
            .FirstAsync(p => p.Id == joueur.Id);

        Assert.Equal("Le Balafré", relu.Nom);
        Assert.Equal(12, relu.PointsStarPlayer);        // XP conservée
        Assert.Equal(90_000, relu.ValeurActuelle);
        Assert.Equal(1, relu.ModForce);                 // amélioration conservée
        Assert.Single(relu.Competences);                // compétence acquise conservée
        Assert.Single(relu.Blessures);                  // blessure conservée
    }

    /// <summary>
    /// Un joueur mort ou retraité reste renommable : c'est de l'historique
    /// (une faute de frappe se corrige après coup), pas une action de jeu.
    /// </summary>
    [Fact]
    public async Task RenommerJoueur_MortOuRetraite_EstAutorise()
    {
        await using var db = _factory.CreateContext();
        var (_, joueur, coachId) = await SeedAsync(db, LeagueStatus.EnCours);
        joueur.EstMort = true;
        await db.SaveChangesAsync();

        await Svc(db).RenommerJoueurAsync(joueur.Id, coachId, "Feu Bruno");

        await using var relecture = _factory.CreateContext();
        Assert.Equal("Feu Bruno", (await relecture.TeamPlayers.FirstAsync(p => p.Id == joueur.Id)).Nom);
    }

    /// <summary>
    /// Contre-épreuve d'autorisation : un autre coach ne renomme pas les joueurs
    /// d'autrui. Sans ce test, une commande qui n'aurait AUCUNE garde passerait
    /// tous les autres.
    /// </summary>
    [Fact]
    public async Task RenommerJoueur_ParUnAutreCoach_EstRefuse()
    {
        await using var db = _factory.CreateContext();
        var (_, joueur, _) = await SeedAsync(db, LeagueStatus.EnCours);

        var intrus = DataSeeder.CreateUser("intrus");
        db.Users.Add(intrus);
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Svc(db).RenommerJoueurAsync(joueur.Id, intrus.Id, "Pirate"));

        await using var relecture = _factory.CreateContext();
        Assert.NotEqual("Pirate", (await relecture.TeamPlayers.FirstAsync(p => p.Id == joueur.Id)).Nom);
    }

    /// <summary>
    /// Nom vidé = repli sur le numéro de maillot, comme à la création : une
    /// ligne d'effectif sans libellé serait illisible sur la feuille et le PDF.
    /// </summary>
    [Fact]
    public async Task RenommerJoueur_AvecNomVide_RetombeSurLeNumero()
    {
        await using var db = _factory.CreateContext();
        var (_, joueur, coachId) = await SeedAsync(db, LeagueStatus.EnCours);

        await Svc(db).RenommerJoueurAsync(joueur.Id, coachId, "   ");

        await using var relecture = _factory.CreateContext();
        var relu = await relecture.TeamPlayers.FirstAsync(p => p.Id == joueur.Id);
        Assert.Equal($"#{relu.Numero}", relu.Nom);
    }

    [Fact]
    public async Task RenommerJoueur_NomTropLong_EstRefuse()
    {
        await using var db = _factory.CreateContext();
        var (_, joueur, coachId) = await SeedAsync(db, LeagueStatus.EnCours);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Svc(db).RenommerJoueurAsync(joueur.Id, coachId, new string('x', 101)));
    }

    /// <summary>
    /// Le nom est nettoyé de ses espaces superflus, comme partout ailleurs.
    /// </summary>
    [Fact]
    public async Task RenommerJoueur_RogneLesEspaces()
    {
        await using var db = _factory.CreateContext();
        var (_, joueur, coachId) = await SeedAsync(db, LeagueStatus.EnCours);

        await Svc(db).RenommerJoueurAsync(joueur.Id, coachId, "  Thorin  ");

        await using var relecture = _factory.CreateContext();
        Assert.Equal("Thorin", (await relecture.TeamPlayers.FirstAsync(p => p.Id == joueur.Id)).Nom);
    }

    private static async Task<(Team equipe, TeamPlayer joueur, string coachId)> SeedAsync(
        ApplicationDbContext db, LeagueStatus statut)
    {
        var (game, version) = await DataSeeder.SeedGameAsync(db);
        var (teamType, position) = await DataSeeder.SeedTeamTypeAsync(db, game.Id);

        var coach = DataSeeder.CreateUser("coach_renom");
        db.Users.Add(coach);
        await db.SaveChangesAsync();

        var ligue = await DataSeeder.SeedLeagueAsync(db, game.Id, version.Id, coach.Id, statut);
        var equipe = await DataSeeder.SeedTeamAsync(db, ligue.Id, coach.Id, teamType.Id);
        var joueur = await DataSeeder.SeedPlayerAsync(db, equipe.Id, position.Id, "Bruno", numero: 7);

        return (equipe, joueur, coach.Id);
    }
}
