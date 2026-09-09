using BolDeSangManager.Data;
using BolDeSangManager.Data.Models;
using BolDeSangManager.Services;
using BolDeSangManager.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace BolDeSangManager.Tests;

/// <summary>
/// Staff configurable : définitions portées par les règles, copiées dans la
/// ligue, quantités détenues par les équipes.
/// </summary>
public class StaffServiceTests : IDisposable
{
    private readonly TestDbFactory _factory = new();
    public void Dispose() => _factory.Dispose();

    private static StaffService CreateService(ApplicationDbContext db) =>
        new(db, NullLogger<StaffService>.Instance);

    private static StaffDefinition Def(int versionId, string nom, int cout = 10_000,
        int min = 0, int max = 6, int? maxLigue = null, bool coutRace = false) =>
        new()
        {
            RulesVersionId = versionId, Nom = nom, Cout = cout,
            MinCreation = min, MaxCreation = max, MaxLigue = maxLigue,
            CoutDepuisTypeEquipe = coutRace
        };

    // ─── Définitions côté règles ──────────────────────────────────────────────

    [Fact]
    public async Task AjouterStaffType_EnregistreLaDefinition()
    {
        await using var db = _factory.CreateContext();
        var (_, rv) = await DataSeeder.SeedGameAsync(db);
        var svc = CreateService(db);

        await svc.AjouterStaffTypeAsync(Def(rv.Id, "Sorcier de touche", 30_000, max: 2));

        var liste = await svc.GetStaffTypesAsync(rv.Id);
        Assert.Contains(liste, s => s.Nom == "Sorcier de touche" && s.Cout == 30_000);
    }

    [Fact]
    public async Task AjouterStaffType_RefuseUnNomDejaPrisDansLaVersion()
    {
        await using var db = _factory.CreateContext();
        var (_, rv) = await DataSeeder.SeedGameAsync(db);
        var svc = CreateService(db);

        await svc.AjouterStaffTypeAsync(Def(rv.Id, "Apothicaire"));

        // insensible à la casse, comme partout ailleurs dans le projet
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.AjouterStaffTypeAsync(Def(rv.Id, "APOTHICAIRE")));
    }

    [Fact]
    public async Task AjouterStaffType_RefuseDesBornesIncoherentes()
    {
        await using var db = _factory.CreateContext();
        var (_, rv) = await DataSeeder.SeedGameAsync(db);
        var svc = CreateService(db);

        // min > max
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.AjouterStaffTypeAsync(Def(rv.Id, "Incoherent", min: 5, max: 2)));

        // plafond de ligue sous le max de création : l'équipe naîtrait hors-la-loi
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.AjouterStaffTypeAsync(Def(rv.Id, "Plafond bas", min: 0, max: 6, maxLigue: 3)));
    }

    // ─── Copie vers la ligue ──────────────────────────────────────────────────

    [Fact]
    public async Task CopierVersLigue_ReprendLesValeursDesRegles()
    {
        await using var db = _factory.CreateContext();
        var (game, rv) = await DataSeeder.SeedGameAsync(db);
        var coach = DataSeeder.CreateUser("com_copie");
        db.Users.Add(coach);
        await db.SaveChangesAsync();
        var ligue = await DataSeeder.SeedLeagueAsync(db, game.Id, rv.Id, coach.Id);

        var svc = CreateService(db);
        await svc.AjouterStaffTypeAsync(Def(rv.Id, "Cheerleaders", 10_000, max: 6));
        await svc.CopierVersLigueAsync(ligue.Id, rv.Id);

        var staffLigue = await svc.GetStaffLigueAsync(ligue.Id);
        var chee = Assert.Single(staffLigue.Where(s => s.Nom == "Cheerleaders"));
        Assert.Equal(10_000, chee.Cout);
        Assert.Equal(6, chee.MaxCreation);
    }

    [Fact]
    public async Task CopierVersLigue_NeFigePasLePrixDunStaffTarifeParRace()
    {
        // Les relances coûtent plus cher aux Nains qu'aux Elfes : figer un prix
        // unique dans la ligue casserait la règle.
        await using var db = _factory.CreateContext();
        var (game, rv) = await DataSeeder.SeedGameAsync(db);
        var coach = DataSeeder.CreateUser("com_race");
        db.Users.Add(coach);
        await db.SaveChangesAsync();
        var ligue = await DataSeeder.SeedLeagueAsync(db, game.Id, rv.Id, coach.Id);

        var svc = CreateService(db);
        await svc.AjouterStaffTypeAsync(Def(rv.Id, "Relances", cout: 0, max: 8, maxLigue: 8, coutRace: true));
        await svc.CopierVersLigueAsync(ligue.Id, rv.Id);

        var relances = Assert.Single((await svc.GetStaffLigueAsync(ligue.Id)).Where(s => s.Nom == "Relances"));
        Assert.True(relances.CoutDepuisTypeEquipe);
        Assert.Equal(0, relances.Cout);
    }

    [Fact]
    public async Task CoutUnitaire_PrendLePrixDeLaRacePourUnStaffTarifeParRace()
    {
        var teamType = new TeamType { Nom = "Nains", CoutRelance = 70_000 };

        var relances = new LeagueStaffType { Nom = "Relances", CoutDepuisTypeEquipe = true, Cout = 0 };
        var fans     = new LeagueStaffType { Nom = "Fans dévoués", Cout = 10_000 };

        Assert.Equal(70_000, StaffService.CoutUnitaire(relances, teamType));
        Assert.Equal(10_000, StaffService.CoutUnitaire(fans, teamType));
    }

    [Fact]
    public async Task CopierVersLigue_EstIdempotent()
    {
        await using var db = _factory.CreateContext();
        var (game, rv) = await DataSeeder.SeedGameAsync(db);
        var coach = DataSeeder.CreateUser("com_idem");
        db.Users.Add(coach);
        await db.SaveChangesAsync();
        var ligue = await DataSeeder.SeedLeagueAsync(db, game.Id, rv.Id, coach.Id);

        var svc = CreateService(db);
        await svc.AjouterStaffTypeAsync(Def(rv.Id, "Apothicaire", 50_000, max: 1, maxLigue: 1));

        var avant = (await svc.GetStaffLigueAsync(ligue.Id)).Count;
        await svc.CopierVersLigueAsync(ligue.Id, rv.Id);
        await svc.CopierVersLigueAsync(ligue.Id, rv.Id);   // second appel

        // Le seed de test fournit déjà « Apothicaire » : la copie ne doit pas le
        // dupliquer, et le second appel ne doit rien ajouter non plus.
        var apres = await svc.GetStaffLigueAsync(ligue.Id);
        Assert.Single(apres.Where(s => s.Nom == "Apothicaire"));
        Assert.Equal(avant, apres.Count);
    }

    // ─── Bornes appliquées aux équipes ────────────────────────────────────────

    private async Task<(int ligueId, int staffId, int teamId)> SetupEquipeAsync(
        ApplicationDbContext db, int min = 0, int max = 6, int? maxLigue = null)
    {
        var (game, rv) = await DataSeeder.SeedGameAsync(db);
        var coach = DataSeeder.CreateUser($"c_{Guid.NewGuid():N}");
        db.Users.Add(coach);
        await db.SaveChangesAsync();
        var (teamType, _) = await DataSeeder.SeedTeamTypeAsync(db, game.Id);
        var ligue = await DataSeeder.SeedLeagueAsync(db, game.Id, rv.Id, coach.Id);
        var equipe = await DataSeeder.SeedTeamAsync(db, ligue.Id, coach.Id, teamType.Id);

        var svc = CreateService(db);

        // Le seed fournit déjà « Fans dévoués » à la ligue : on ajuste CETTE copie
        // aux bornes voulues plutôt que d'en créer une seconde.
        var staff = Assert.Single(
            (await svc.GetStaffLigueAsync(ligue.Id)).Where(s => s.Nom == StaffService.NomFans));
        staff.MinCreation = min;
        staff.MaxCreation = max;
        staff.MaxLigue = maxLigue;
        await svc.ModifierStaffLigueAsync(staff);

        return (ligue.Id, staff.Id, equipe.Id);
    }

    [Fact]
    public async Task DefinirQuantite_RespecteLesBornesDeCreation()
    {
        await using var db = _factory.CreateContext();
        var (_, staffId, teamId) = await SetupEquipeAsync(db, min: 1, max: 3);
        var svc = CreateService(db);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.DefinirQuantiteAsync(teamId, staffId, 0, aLaCreation: true));   // sous le min
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.DefinirQuantiteAsync(teamId, staffId, 4, aLaCreation: true));   // au-dessus du max

        await svc.DefinirQuantiteAsync(teamId, staffId, 2, aLaCreation: true);
        Assert.Equal(2, await svc.GetQuantiteAsync(teamId, staffId));
    }

    [Fact]
    public async Task DefinirQuantite_EnCoursDeLigue_IgnoreLeMaxDeCreationMaisPasLePlafond()
    {
        await using var db = _factory.CreateContext();
        var (_, staffId, teamId) = await SetupEquipeAsync(db, min: 1, max: 3, maxLigue: 8);
        var svc = CreateService(db);

        // 5 dépasse le max de création (3) mais reste sous le plafond de ligue (8)
        await svc.DefinirQuantiteAsync(teamId, staffId, 5, aLaCreation: false);
        Assert.Equal(5, await svc.GetQuantiteAsync(teamId, staffId));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.DefinirQuantiteAsync(teamId, staffId, 9, aLaCreation: false));
    }

    [Fact]
    public async Task DefinirQuantite_SansPlafond_NestPasLimitee()
    {
        await using var db = _factory.CreateContext();
        var (_, staffId, teamId) = await SetupEquipeAsync(db, maxLigue: null);
        var svc = CreateService(db);

        await svc.DefinirQuantiteAsync(teamId, staffId, 250, aLaCreation: false);
        Assert.Equal(250, await svc.GetQuantiteAsync(teamId, staffId));
    }

    [Fact]
    public async Task PlafondAbaisse_BloqueLesAchatsMaisConserveLexistant()
    {
        // Décision produit : pas de vente forcée.
        await using var db = _factory.CreateContext();
        var (ligueId, staffId, teamId) = await SetupEquipeAsync(db, max: 6, maxLigue: 10);
        var svc = CreateService(db);

        await svc.DefinirQuantiteAsync(teamId, staffId, 9, aLaCreation: false);

        var staff = Assert.Single(
            (await svc.GetStaffLigueAsync(ligueId)).Where(s => s.Nom == "Fans dévoués"));
        staff.MaxLigue = 5;
        staff.MaxCreation = 5;
        await svc.ModifierStaffLigueAsync(staff);

        // l'équipe garde ses 9…
        Assert.Equal(9, await svc.GetQuantiteAsync(teamId, staffId));
        // …mais ne peut plus en acheter
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.DefinirQuantiteAsync(teamId, staffId, 10, aLaCreation: false));
    }

    [Fact]
    public async Task DefinirQuantite_RefuseUnStaffDesactive()
    {
        await using var db = _factory.CreateContext();
        var (ligueId, staffId, teamId) = await SetupEquipeAsync(db);
        var svc = CreateService(db);

        var staff = Assert.Single(
            (await svc.GetStaffLigueAsync(ligueId)).Where(s => s.Nom == "Fans dévoués"));
        staff.EstActif = false;
        await svc.ModifierStaffLigueAsync(staff);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.DefinirQuantiteAsync(teamId, staffId, 1, aLaCreation: false));
    }

    // ─── Écrêtage ─────────────────────────────────────────────────────────────

    [Fact]
    public void Ecreter_AppliquePlancherEtPlafond()
    {
        // Le plafond est DUR : il vaut pour les gains de match comme pour les achats.
        Assert.Equal(12, StaffService.Ecreter(14, minimum: 1, maxLigue: 12));
        Assert.Equal(1,  StaffService.Ecreter(-3, minimum: 1, maxLigue: 12));
        Assert.Equal(7,  StaffService.Ecreter(7,  minimum: 1, maxLigue: 12));
        Assert.Equal(99, StaffService.Ecreter(99, minimum: 1, maxLigue: null));
    }

    // ─── Clone / export / suppression de version ──────────────────────────────

    [Fact]
    public async Task ClonerVersion_EmporteLeStaff()
    {
        // Oubli classique du projet : sans ça, une nouvelle édition de règles
        // naîtrait sans aucun staff.
        await using var db = _factory.CreateContext();
        var (game, rv) = await DataSeeder.SeedGameAsync(db);
        var svc = CreateService(db);
        await svc.AjouterStaffTypeAsync(Def(rv.Id, "Apothicaire", 50_000, max: 1, maxLigue: 1));
        await svc.AjouterStaffTypeAsync(Def(rv.Id, "Relances", 0, max: 8, maxLigue: 8, coutRace: true));

        var edit = new DataEditService(db, NullLogger<DataEditService>.Instance);
        var nouvelle = await edit.CreerVersionAsync(game.Id, "Saison 4", cloneFromVersionId: rv.Id);

        var clone = await svc.GetStaffTypesAsync(nouvelle.Id);
        Assert.Equal(2, clone.Count);
        var relances = Assert.Single(clone, s => s.Nom == "Relances");
        Assert.True(relances.CoutDepuisTypeEquipe);
        Assert.Equal(8, relances.MaxLigue);
    }

    [Fact]
    public async Task SupprimerDefinition_NeVidePasLeStaffDesLiguesDejaCreees()
    {
        // La copie de ligue doit survivre à la suppression de sa définition
        // d'origine (FK SetNull), sinon une ligue en cours perdrait son staff.
        await using var db = _factory.CreateContext();
        var (game, rv) = await DataSeeder.SeedGameAsync(db);
        var coach = DataSeeder.CreateUser("com_del");
        db.Users.Add(coach);
        await db.SaveChangesAsync();

        var svc = CreateService(db);
        await svc.AjouterStaffTypeAsync(Def(rv.Id, "Cheerleaders", 10_000, max: 6));

        var ligue = await DataSeeder.SeedLeagueAsync(db, game.Id, rv.Id, coach.Id);
        await svc.CopierVersLigueAsync(ligue.Id, rv.Id);

        foreach (var def in await svc.GetStaffTypesAsync(rv.Id))
            await svc.SupprimerStaffTypeAsync(def.Id);

        var chee = Assert.Single(
            (await svc.GetStaffLigueAsync(ligue.Id)).Where(s => s.Nom == "Cheerleaders"));
        Assert.Null(chee.StaffTypeId);          // le lien est coupé, la copie reste
    }

    [Fact]
    public async Task CreerLigue_AvecStaffPersonnalise_UtiliseLesValeursDuCommissaire()
    {
        // Le commissaire règle le staff À LA CRÉATION de la ligue : ses valeurs
        // doivent primer sur celles des règles, sans modifier les règles.
        await using var db = _factory.CreateContext();
        var (game, rv) = await DataSeeder.SeedGameAsync(db);
        var commissaire = DataSeeder.CreateUser("com_perso");
        db.Users.Add(commissaire);
        await db.SaveChangesAsync();

        var svcStaff = CreateService(db);
        await svcStaff.AjouterStaffTypeAsync(Def(rv.Id, "Fans dévoués", 10_000, min: 1, max: 9));
        await svcStaff.AjouterStaffTypeAsync(Def(rv.Id, "Cheerleaders", 10_000, min: 0, max: 6));

        var perso = (await svcStaff.GetStaffTypesAsync(rv.Id))
            .Select(s => new LeagueStaffType
            {
                StaffTypeId = s.Id, Nom = s.Nom, Description = s.Description,
                Ordre = s.Ordre, EstActif = s.Nom != "Cheerleaders",   // désactivé pour cette ligue
                Cout = s.Nom == "Fans dévoués" ? 15_000 : s.Cout,       // prix relevé
                CoutDepuisTypeEquipe = s.CoutDepuisTypeEquipe,
                MinCreation = s.Nom == "Fans dévoués" ? 2 : s.MinCreation,
                MaxCreation = s.Nom == "Fans dévoués" ? 5 : s.MaxCreation,
                MaxLigue = s.Nom == "Fans dévoués" ? 12 : s.MaxLigue
            })
            .ToList();

        var ligueSvc = new LeagueService(
            db, NullLogger<LeagueService>.Instance,
            new StubAuthorizationService(), svcStaff);

        var ligue = await ligueSvc.CreerLigueAsync(new League
        {
            Nom = "Ligue perso", GameId = game.Id, RulesVersionId = rv.Id,
            BudgetDepart = 1_000_000
        }, commissaire.Id, perso);

        var staffLigue = await svcStaff.GetStaffLigueAsync(ligue.Id);

        var fans = Assert.Single(staffLigue.Where(s => s.Nom == "Fans dévoués"));
        Assert.Equal(15_000, fans.Cout);
        Assert.Equal(2, fans.MinCreation);
        Assert.Equal(5, fans.MaxCreation);
        Assert.Equal(12, fans.MaxLigue);

        var chee = Assert.Single(staffLigue.Where(s => s.Nom == "Cheerleaders"));
        Assert.False(chee.EstActif);

        // Les RÈGLES n'ont pas bougé : c'est bien une copie.
        var regles = await svcStaff.GetStaffTypesAsync(rv.Id);
        Assert.Equal(10_000, Assert.Single(regles.Where(s => s.Nom == "Fans dévoués")).Cout);
        Assert.True(Assert.Single(regles.Where(s => s.Nom == "Cheerleaders")).EstActif);
    }

    /// <summary>
    /// Le drapeau « compte dans la VEA » doit survivre au chemin de création
    /// AVEC staff personnalisé, celui qu'emprunte l'écran de création de ligue.
    ///
    /// Bug rencontré : la copie champ par champ omettait <c>CompteDansVea</c>,
    /// qui repartait donc au défaut C# (<c>true</c>). Les Fans dévoués, exclus
    /// de la VEA dans les règles, RÉINTÉGRAIENT la VEA de toute ligue créée
    /// depuis l'écran — deux ligues, deux calculs, sans rien à l'écran pour
    /// l'expliquer. La contre-épreuve (« Cheerleaders » resté à true) prouve
    /// qu'on ne se contente pas de tout forcer à false.
    /// </summary>
    [Fact]
    public async Task CreerLigue_AvecStaffPersonnalise_ConserveLeDrapeauVea()
    {
        await using var db = _factory.CreateContext();
        var (game, rv) = await DataSeeder.SeedGameAsync(db);
        var commissaire = DataSeeder.CreateUser("com_vea");
        db.Users.Add(commissaire);
        await db.SaveChangesAsync();

        var svcStaff = CreateService(db);
        var fansDef = Def(rv.Id, "Fans dévoués", 5_000, min: 1, max: 3);
        fansDef.CompteDansVea = false;                       // règle LRB : hors VEA
        await svcStaff.AjouterStaffTypeAsync(fansDef);
        await svcStaff.AjouterStaffTypeAsync(Def(rv.Id, "Cheerleaders", 10_000, min: 0, max: 6));

        // Copie de travail identique à celle de Ligues/Creer.razor.
        var perso = (await svcStaff.GetStaffTypesAsync(rv.Id))
            .Select(s => new LeagueStaffType
            {
                StaffTypeId = s.Id, Nom = s.Nom, Description = s.Description,
                Ordre = s.Ordre, EstActif = s.EstActif, Cout = s.Cout,
                CoutDepuisTypeEquipe = s.CoutDepuisTypeEquipe,
                MinCreation = s.MinCreation, MaxCreation = s.MaxCreation,
                MaxLigue = s.MaxLigue, CompteDansVea = s.CompteDansVea
            })
            .ToList();

        var ligueSvc = new LeagueService(
            db, NullLogger<LeagueService>.Instance,
            new StubAuthorizationService(), svcStaff);

        var ligue = await ligueSvc.CreerLigueAsync(new League
        {
            Nom = "Ligue VEA", GameId = game.Id, RulesVersionId = rv.Id,
            BudgetDepart = 1_000_000
        }, commissaire.Id, perso);

        var staffLigue = await svcStaff.GetStaffLigueAsync(ligue.Id);
        Assert.False(Assert.Single(staffLigue.Where(s => s.Nom == "Fans dévoués")).CompteDansVea);
        Assert.True(Assert.Single(staffLigue.Where(s => s.Nom == "Cheerleaders")).CompteDansVea);
    }

    private class StubAuthorizationService : BolDeSangManager.Services.IAuthorizationService
    {
        public Task<bool> PeutGererLigueAsync(string userId, int ligueId) => Task.FromResult(true);
        public Task<bool> EstCommissaireDeLigueAsync(string userId, int ligueId) => Task.FromResult(true);
        public Task<bool> EstGrandCommissaireAsync(string userId) => Task.FromResult(true);
        public Task<bool> EstAdminAsync(string userId) => Task.FromResult(true);
        public Task<bool> PeutEditerDonneesAsync(string userId) => Task.FromResult(true);
        public Task<bool> PeutGererSettingsAsync(string userId) => Task.FromResult(true);
    }
}
