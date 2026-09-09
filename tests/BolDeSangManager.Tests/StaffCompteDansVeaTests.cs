using BolDeSangManager.Data.Models;
using BolDeSangManager.Helpers;
using BolDeSangManager.Services;
using BolDeSangManager.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace BolDeSangManager.Tests;

/// <summary>
/// Tout le staff n'entre pas dans la VEA. Les <b>Fans dévoués</b> représentent
/// le public de l'équipe, pas sa puissance sur le terrain : ils ne doivent pas
/// gonfler la valeur d'équipe (et donc pas fausser les coups de pouce accordés
/// à l'adversaire le plus faible).
///
/// La règle n'est PAS écrite en dur sur le nom « Fans dévoués » : chaque staff
/// porte un drapeau <c>CompteDansVea</c> réglable dans l'Admin, pour qu'une
/// édition future — ou un staff inventé par l'association — se règle sans dev.
/// </summary>
public class StaffCompteDansVeaTests : IDisposable
{
    private readonly TestDbFactory _factory = new();
    public void Dispose() => _factory.Dispose();

    private static Team EquipeAvec(params TeamStaff[] staff)
    {
        var equipe = new Team
        {
            Nom = "Les Testeurs",
            TeamType = new TeamType { Nom = "Nains", CoutRelance = 70_000 }
        };
        equipe.Joueurs.Add(new TeamPlayer
        {
            Numero = 1, Nom = "Grim", Blessures = [],
            // La valeur d'un joueur vient de son poste depuis que la colonne
            // ValeurActuelle a disparu : 80k de joueur pour ces scénarios.
            PlayerPosition = new PlayerPosition { Nom = "Blitzer", Cout = 80_000 }
        });
        foreach (var s in staff) equipe.Staff.Add(s);
        return equipe;
    }

    // ── Le calcul ─────────────────────────────────────────────────────────────

    [Fact]
    public void Vea_ExclutLeStaffNonComptabilise()
    {
        var equipe = EquipeAvec(new TeamStaff
        {
            Quantite = 3,
            LeagueStaffType = new LeagueStaffType
            {
                Nom = "Fans dévoués", Cout = 10_000, CompteDansVea = false
            }
        });

        // 80k du joueur seul : les 30k de fans ne comptent pas.
        Assert.Equal(80_000, VeaCalculator.Calculer(equipe));
    }

    /// <summary>
    /// Test discriminant : le même staff, drapeau coché, DOIT compter. Sans ce
    /// test, une VEA qui ignorerait tout le staff passerait le test ci-dessus.
    /// </summary>
    [Fact]
    public void Vea_CompteLeStaffComptabilise()
    {
        var equipe = EquipeAvec(new TeamStaff
        {
            Quantite = 3,
            LeagueStaffType = new LeagueStaffType
            {
                Nom = "Fans dévoués", Cout = 10_000, CompteDansVea = true
            }
        });

        Assert.Equal(110_000, VeaCalculator.Calculer(equipe));
    }

    [Fact]
    public void Vea_MelangeDeStaffComptabiliseEtNon()
    {
        var equipe = EquipeAvec(
            new TeamStaff
            {
                Quantite = 3,
                LeagueStaffType = new LeagueStaffType
                { Nom = "Fans dévoués", Cout = 10_000, CompteDansVea = false }
            },
            new TeamStaff
            {
                Quantite = 2,
                LeagueStaffType = new LeagueStaffType
                { Nom = "Relances", CoutDepuisTypeEquipe = true, CompteDansVea = true }
            },
            new TeamStaff
            {
                Quantite = 1,
                LeagueStaffType = new LeagueStaffType
                { Nom = "Apothicaire", Cout = 50_000, CompteDansVea = true }
            });

        // 80k joueur + 140k relances + 50k apothicaire, sans les fans.
        Assert.Equal(270_000, VeaCalculator.Calculer(equipe));
    }

    /// <summary>
    /// Un staff exclu de la VEA reste PAYANT : le drapeau ne touche que la
    /// valeur d'équipe, pas la facturation au budget de départ.
    /// </summary>
    [Fact]
    public void StaffExcluDeLaVea_ResteFactureAuBudget()
    {
        var type = new LeagueStaffType
        {
            Nom = "Fans dévoués", Cout = 10_000, MinCreation = 1, CompteDansVea = false
        };

        Assert.Equal(20_000, StaffService.CoutFactureCreation(type, teamType: null, quantite: 3));
    }

    // ── Le réglage par défaut, de bout en bout ───────────────────────────────

    /// <summary>
    /// Une base neuve doit livrer les Fans dévoués DÉJÀ décochés : c'est la
    /// règle attendue, l'association n'a rien à régler pour l'obtenir.
    /// </summary>
    [Fact]
    public async Task StaffStandard_LivreLesFansHorsVea_EtLeResteDedans()
    {
        await using var db = _factory.CreateContext();
        var version = await SeedVersionAvecStaffStandardAsync(db);

        var staff = await db.StaffTypes
            .Where(s => s.RulesVersionId == version.Id)
            .ToDictionaryAsync(s => s.Nom, s => s.CompteDansVea);

        Assert.False(staff[StaffService.NomFans]);
        Assert.True(staff[StaffService.NomRelances]);
        Assert.True(staff[StaffService.NomApothicaire]);
    }

    /// <summary>
    /// Le drapeau doit suivre la copie règles → ligue, sinon le réglage admin
    /// serait perdu à la création de chaque ligue.
    /// </summary>
    [Fact]
    public async Task CopieVersLigue_ConserveLeDrapeau()
    {
        await using var db = _factory.CreateContext();
        var version = await SeedVersionAvecStaffStandardAsync(db);

        var ligue = await SeedLigueAsync(db, version);

        var svc = new StaffService(db, NullLogger<StaffService>.Instance);
        await svc.CopierVersLigueAsync(ligue.Id, version.Id);

        var copies = await db.LeagueStaffTypes
            .Where(l => l.LeagueId == ligue.Id)
            .ToDictionaryAsync(l => l.Nom, l => l.CompteDansVea);

        Assert.False(copies[StaffService.NomFans]);
        Assert.True(copies[StaffService.NomRelances]);
    }

    /// <summary>
    /// Le commissaire peut décider l'inverse pour SA ligue : le réglage est
    /// modifiable et persisté.
    /// </summary>
    [Fact]
    public async Task ModifierStaffLigue_EnregistreLeDrapeau()
    {
        await using var db = _factory.CreateContext();
        var version = await SeedVersionAvecStaffStandardAsync(db);
        var ligue = await SeedLigueAsync(db, version);

        var svc = new StaffService(db, NullLogger<StaffService>.Instance);
        await svc.CopierVersLigueAsync(ligue.Id, version.Id);

        var fans = await db.LeagueStaffTypes
            .FirstAsync(l => l.LeagueId == ligue.Id && l.Nom == StaffService.NomFans);
        fans.CompteDansVea = true;
        await svc.ModifierStaffLigueAsync(fans);

        await using var relecture = _factory.CreateContext();
        var relu = await relecture.LeagueStaffTypes.FirstAsync(l => l.Id == fans.Id);
        Assert.True(relu.CompteDansVea);
    }

    /// <summary>
    /// Le drapeau doit survivre à une modification côté RÈGLES. Piège observé :
    /// l'écran d'admin construit une copie de travail champ par champ ; un champ
    /// oublié laisse le défaut C# (<c>true</c>) écraser la valeur réelle, et la
    /// simple ouverture de la modale remet les fans dans la VEA.
    /// </summary>
    [Fact]
    public async Task ModifierStaffType_ConserveLeDrapeau()
    {
        await using var db = _factory.CreateContext();
        var version = await SeedVersionAvecStaffStandardAsync(db);

        var svc = new StaffService(db, NullLogger<StaffService>.Instance);
        var fans = await db.StaffTypes
            .FirstAsync(s => s.RulesVersionId == version.Id && s.Nom == StaffService.NomFans);

        // On modifie un autre champ : le drapeau ne doit pas bouger.
        fans.Cout = 12_000;
        await svc.ModifierStaffTypeAsync(fans);

        await using var relecture = _factory.CreateContext();
        var relu = await relecture.StaffTypes.FirstAsync(s => s.Id == fans.Id);
        Assert.Equal(12_000, relu.Cout);
        Assert.False(relu.CompteDansVea);
    }

    // ── Helper ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Crée une ligue rattachée à la version, avec le commissaire que la FK
    /// (Restrict, non nullable) exige.
    /// </summary>
    private static async Task<League> SeedLigueAsync(
        Data.ApplicationDbContext db, RulesVersion version)
    {
        var commissaire = new Data.ApplicationUser
        {
            Id = Guid.NewGuid().ToString(),
            UserName = "commissaire@test.fr",
            Email = "commissaire@test.fr",
            PseudoCoach = "Commissaire"
        };
        db.Users.Add(commissaire);
        await db.SaveChangesAsync();

        var ligue = new League
        {
            Nom = "Ligue test",
            GameId = version.GameId,
            RulesVersionId = version.Id,
            CommissaireId = commissaire.Id
        };
        db.Leagues.Add(ligue);
        await db.SaveChangesAsync();
        return ligue;
    }

    private static async Task<RulesVersion> SeedVersionAvecStaffStandardAsync(
        Data.ApplicationDbContext db)
    {
        var jeu = new Game { Nom = "Blood Bowl" };
        db.Games.Add(jeu);
        await db.SaveChangesAsync();

        var version = new RulesVersion { Nom = "Test", GameId = jeu.Id, EstActive = true };
        db.RulesVersions.Add(version);
        await db.SaveChangesAsync();

        await Data.DbSeeder.SeedStaffStandardPourTestsAsync(db, version.Id);
        return version;
    }
}
