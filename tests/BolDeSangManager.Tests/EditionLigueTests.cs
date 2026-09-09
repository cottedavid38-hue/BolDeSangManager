using BolDeSangManager.Data;
using BolDeSangManager.Data.Enums;
using BolDeSangManager.Data.Models;
using BolDeSangManager.Helpers;
using BolDeSangManager.Services;
using BolDeSangManager.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace BolDeSangManager.Tests;

/// <summary>
/// Édition des paramètres d'une ligue tant que la saison n'est pas lancée.
/// Deux verrous distincts, volontairement séparés :
///  - le lancement de la saison ferme TOUT (le calendrier et les feuilles de
///    match dépendent du format et du barème d'XP) ;
///  - la première équipe inscrite ferme les paramètres STRUCTURANTS (version des
///    règles, jeu, budget, staff), dont dépendent des lignes déjà écrites.
/// </summary>
public class EditionLigueHelpersTests
{
    [Theory]
    [InlineData(LeagueStatus.Creation, true)]
    [InlineData(LeagueStatus.Inscription, true)]
    [InlineData(LeagueStatus.EnCours, false)]
    [InlineData(LeagueStatus.PhaseDeRepos, false)]
    [InlineData(LeagueStatus.PlayOffs, false)]
    [InlineData(LeagueStatus.Termine, false)]
    public void ParametresEditables_seulement_avant_lancement(LeagueStatus statut, bool attendu)
        => Assert.Equal(attendu, DisplayHelpers.ParametresLigueEditables(statut));

    [Theory]
    [InlineData(LeagueStatus.Creation, 0, true)]
    [InlineData(LeagueStatus.Creation, 1, false)]
    [InlineData(LeagueStatus.Inscription, 0, true)]
    [InlineData(LeagueStatus.Inscription, 3, false)]
    [InlineData(LeagueStatus.EnCours, 0, false)]
    public void ParametresStructurants_verrouilles_des_la_premiere_equipe(
        LeagueStatus statut, int nombreEquipes, bool attendu)
        => Assert.Equal(attendu, DisplayHelpers.ParametresStructurantsEditables(statut, nombreEquipes));
}

/// <summary>
/// LeagueService.ModifierLigueAsync : autorité serveur sur l'édition d'une ligue.
/// Le grisage de l'écran n'est PAS une sécurité — tout est revalidé ici.
/// </summary>
public class ModifierLigueAsyncTests : IDisposable
{
    private readonly TestDbFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static LeagueService CreateService(
        ApplicationDbContext db, string userId, int ligueId) =>
        new(db, NullLogger<LeagueService>.Instance,
            new StubAuth(userId, ligueId),
            new StaffService(db, NullLogger<StaffService>.Instance));

    /// <summary>Autorise uniquement le couple (userId, ligueId) fourni.</summary>
    private sealed class StubAuth(string userId, int ligueId) : IAuthorizationService
    {
        public Task<bool> EstAdminAsync(string u) => Task.FromResult(false);
        public Task<bool> EstGrandCommissaireAsync(string u) => Task.FromResult(false);
        public Task<bool> EstCommissaireDeLigueAsync(string u, int l) => Task.FromResult(false);
        public Task<bool> PeutGererLigueAsync(string u, int l) =>
            Task.FromResult(u == userId && l == ligueId);
        public Task<bool> PeutEditerDonneesAsync(string u) => Task.FromResult(false);
        public Task<bool> PeutGererSettingsAsync(string u) => Task.FromResult(false);
    }

    /// <summary>Ligue seedée + de quoi inscrire une équipe si le test le demande.</summary>
    private async Task<(ApplicationUser coach, League ligue, TeamType teamType)> SetupAsync(
        LeagueStatus statut = LeagueStatus.Inscription)
    {
        await using var db = _factory.CreateContext();
        var user = DataSeeder.CreateUser("commissaire");
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var (game, rv) = await DataSeeder.SeedGameAsync(db);
        var ligue = await DataSeeder.SeedLeagueAsync(db, game.Id, rv.Id, user.Id, statut);
        var (teamType, _) = await DataSeeder.SeedTeamTypeAsync(db, game.Id);
        return (user, ligue, teamType);
    }

    /// <summary>Copie de la ligue existante, à ajuster champ par champ dans le test.</summary>
    private static League Modifiee(League source) => new()
    {
        Nom = source.Nom,
        Description = source.Description,
        GameId = source.GameId,
        RulesVersionId = source.RulesVersionId,
        Format = source.Format,
        BudgetDepart = source.BudgetDepart,
        NombreEquipesPlayoff = source.NombreEquipesPlayoff,
        PspParTouchdown = source.PspParTouchdown,
        PspParPasse = source.PspParPasse,
        PspParInterception = source.PspParInterception,
        PspParElimination = source.PspParElimination,
        PspBonusMvp = source.PspBonusMvp,
    };

    [Fact]
    public async Task Modifie_nom_format_et_bareme_avant_lancement()
    {
        var (user, ligue, _) = await SetupAsync();
        await using var db = _factory.CreateContext();
        var svc = CreateService(db, user.Id, ligue.Id);

        var m = Modifiee(ligue);
        m.Nom = "Ligue renommée";
        m.Description = "Nouvelle description";
        m.Format = LeagueFormat.Libre;
        m.NombreEquipesPlayoff = 8;
        m.PspParTouchdown = 5;
        m.PspBonusMvp = 7;

        await svc.ModifierLigueAsync(ligue.Id, m, user.Id);

        await using var db2 = _factory.CreateContext();
        var relue = await db2.Leagues.FindAsync(ligue.Id);
        Assert.NotNull(relue);
        Assert.Equal("Ligue renommée", relue.Nom);
        Assert.Equal("Nouvelle description", relue.Description);
        Assert.Equal(LeagueFormat.Libre, relue.Format);
        Assert.Equal(8, relue.NombreEquipesPlayoff);
        Assert.Equal(5, relue.PspParTouchdown);
        Assert.Equal(7, relue.PspBonusMvp);
    }

    [Fact]
    public async Task Refuse_toute_modification_apres_lancement()
    {
        var (user, ligue, _) = await SetupAsync(LeagueStatus.EnCours);
        await using var db = _factory.CreateContext();
        var svc = CreateService(db, user.Id, ligue.Id);

        var m = Modifiee(ligue);
        m.Nom = "Trop tard";

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ModifierLigueAsync(ligue.Id, m, user.Id));

        await using var db2 = _factory.CreateContext();
        Assert.Equal("Ligue de Test", (await db2.Leagues.FindAsync(ligue.Id))!.Nom);
    }

    [Fact]
    public async Task Refuse_changement_de_budget_si_une_equipe_existe()
    {
        var (user, ligue, teamType) = await SetupAsync();
        await using (var seed = _factory.CreateContext())
            await DataSeeder.SeedTeamAsync(seed, ligue.Id, user.Id, teamType.Id);

        await using var db = _factory.CreateContext();
        var svc = CreateService(db, user.Id, ligue.Id);

        var m = Modifiee(ligue);
        m.BudgetDepart = 1_500_000;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ModifierLigueAsync(ligue.Id, m, user.Id));

        await using var db2 = _factory.CreateContext();
        Assert.Equal(1_000_000, (await db2.Leagues.FindAsync(ligue.Id))!.BudgetDepart);
    }

    [Fact]
    public async Task Refuse_changement_de_version_de_regles_si_une_equipe_existe()
    {
        var (user, ligue, teamType) = await SetupAsync();
        int autreVersionId;
        await using (var seed = _factory.CreateContext())
        {
            await DataSeeder.SeedTeamAsync(seed, ligue.Id, user.Id, teamType.Id);
            var autre = new RulesVersion { GameId = ligue.GameId, Nom = "Version 2", Ordre = 2 };
            seed.RulesVersions.Add(autre);
            await seed.SaveChangesAsync();
            autreVersionId = autre.Id;
        }

        await using var db = _factory.CreateContext();
        var svc = CreateService(db, user.Id, ligue.Id);

        var m = Modifiee(ligue);
        m.RulesVersionId = autreVersionId;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ModifierLigueAsync(ligue.Id, m, user.Id));

        await using var db2 = _factory.CreateContext();
        Assert.Equal(ligue.RulesVersionId, (await db2.Leagues.FindAsync(ligue.Id))!.RulesVersionId);
    }

    [Fact]
    public async Task Autorise_changement_de_budget_si_aucune_equipe()
    {
        var (user, ligue, _) = await SetupAsync();
        await using var db = _factory.CreateContext();
        var svc = CreateService(db, user.Id, ligue.Id);

        var m = Modifiee(ligue);
        m.BudgetDepart = 1_200_000;

        await svc.ModifierLigueAsync(ligue.Id, m, user.Id);

        await using var db2 = _factory.CreateContext();
        Assert.Equal(1_200_000, (await db2.Leagues.FindAsync(ligue.Id))!.BudgetDepart);
    }

    /// <summary>
    /// Test DISCRIMINANT : le règlement et le mode brouillard ont leur propre
    /// écran et restent modifiables en cours de saison. Une copie de travail
    /// construite champ par champ les remettrait à leur défaut C# — c'est
    /// exactement le piège déjà rencontré sur les modales d'admin.
    /// </summary>
    [Fact]
    public async Task Ne_touche_pas_au_reglement_ni_au_mode_brouillard()
    {
        var (user, ligue, _) = await SetupAsync();
        await using (var seed = _factory.CreateContext())
        {
            var l = await seed.Leagues.FindAsync(ligue.Id);
            l!.Reglement = "# Mon règlement";
            l.ModeBrouillard = true;
            await seed.SaveChangesAsync();
        }

        await using var db = _factory.CreateContext();
        var svc = CreateService(db, user.Id, ligue.Id);

        var m = Modifiee(ligue);
        m.Nom = "Autre nom";

        await svc.ModifierLigueAsync(ligue.Id, m, user.Id);

        await using var db2 = _factory.CreateContext();
        var relue = await db2.Leagues.FindAsync(ligue.Id);
        Assert.Equal("# Mon règlement", relue!.Reglement);
        Assert.True(relue.ModeBrouillard);
    }

    [Fact]
    public async Task Refuse_si_utilisateur_non_commissaire()
    {
        var (user, ligue, _) = await SetupAsync();
        await using var db = _factory.CreateContext();
        var svc = CreateService(db, user.Id, ligue.Id);

        var m = Modifiee(ligue);
        m.Nom = "Piratage";

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ModifierLigueAsync(ligue.Id, m, "un-autre-coach"));

        await using var db2 = _factory.CreateContext();
        Assert.Equal("Ligue de Test", (await db2.Leagues.FindAsync(ligue.Id))!.Nom);
    }

    [Fact]
    public async Task Refuse_le_staff_si_une_equipe_existe()
    {
        var (user, ligue, teamType) = await SetupAsync();
        List<LeagueStaffType> staff;
        await using (var seed = _factory.CreateContext())
        {
            await DataSeeder.SeedTeamAsync(seed, ligue.Id, user.Id, teamType.Id);
            staff = await seed.LeagueStaffTypes
                .Where(s => s.LeagueId == ligue.Id).AsNoTracking().ToListAsync();
        }
        staff[0].Cout = 99_000;

        await using var db = _factory.CreateContext();
        var svc = CreateService(db, user.Id, ligue.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ModifierLigueAsync(ligue.Id, Modifiee(ligue), user.Id, staff));

        await using var db2 = _factory.CreateContext();
        var relu = await db2.LeagueStaffTypes.FirstAsync(s => s.LeagueId == ligue.Id && s.Ordre == 1);
        Assert.Equal(10_000, relu.Cout);
    }

    [Fact]
    public async Task Remplace_le_staff_si_aucune_equipe()
    {
        var (user, ligue, _) = await SetupAsync();
        List<LeagueStaffType> staff;
        await using (var seed = _factory.CreateContext())
            staff = await seed.LeagueStaffTypes
                .Where(s => s.LeagueId == ligue.Id).AsNoTracking().ToListAsync();
        staff[0].Cout = 20_000;
        staff[0].MaxCreation = 5;

        await using var db = _factory.CreateContext();
        var svc = CreateService(db, user.Id, ligue.Id);

        await svc.ModifierLigueAsync(ligue.Id, Modifiee(ligue), user.Id, staff);

        await using var db2 = _factory.CreateContext();
        var relu = await db2.LeagueStaffTypes.FirstAsync(s => s.LeagueId == ligue.Id && s.Ordre == 1);
        Assert.Equal(20_000, relu.Cout);
        Assert.Equal(5, relu.MaxCreation);
        Assert.Equal(5, await db2.LeagueStaffTypes.CountAsync(s => s.LeagueId == ligue.Id));
    }
}

/// <summary>
/// Supprimer une ligue ne doit laisser AUCUN enfant orphelin en base.
/// Découvert sur la base réelle : 12 LeagueStaffTypes rattachés à des ligues
/// disparues (pragma foreign_key_check). Ce test couvre chaque table portant
/// un LeagueId, pas seulement celle qui a été prise en défaut.
/// </summary>
public class SuppressionLigueSansOrphelinTests : IDisposable
{
    private readonly TestDbFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static LeagueService CreateService(ApplicationDbContext db) =>
        new(db, NullLogger<LeagueService>.Instance, new StubAuthAlwaysTrue(),
            new StaffService(db, NullLogger<StaffService>.Instance));

    private sealed class StubAuthAlwaysTrue : IAuthorizationService
    {
        public Task<bool> EstAdminAsync(string u) => Task.FromResult(true);
        public Task<bool> EstGrandCommissaireAsync(string u) => Task.FromResult(true);
        public Task<bool> EstCommissaireDeLigueAsync(string u, int l) => Task.FromResult(true);
        public Task<bool> PeutGererLigueAsync(string u, int l) => Task.FromResult(true);
        public Task<bool> PeutEditerDonneesAsync(string u) => Task.FromResult(true);
        public Task<bool> PeutGererSettingsAsync(string u) => Task.FromResult(true);
    }

    [Fact]
    public async Task SupprimerLigue_ne_laisse_aucun_enfant_orphelin()
    {
        int ligueId;
        await using (var db = _factory.CreateContext())
        {
            var commissaire = DataSeeder.CreateUser("orph-comm");
            var coach = DataSeeder.CreateUser("orph-coach");
            db.Users.AddRange(commissaire, coach);
            await db.SaveChangesAsync();

            var (game, rv) = await DataSeeder.SeedGameAsync(db);
            var (teamType, position) = await DataSeeder.SeedTeamTypeAsync(db, game.Id);
            var ligue = await DataSeeder.SeedLeagueAsync(db, game.Id, rv.Id, commissaire.Id);
            ligueId = ligue.Id;

            var equipe = await DataSeeder.SeedTeamAsync(db, ligueId, coach.Id, teamType.Id);
            await DataSeeder.SeedPlayerAsync(db, equipe.Id, position.Id);

            // Un enfant de chaque table portant un LeagueId, plus un achat de
            // staff (TeamStaff → LeagueStaffType) qui doit partir avec.
            var staff = await db.LeagueStaffTypes.FirstAsync(s => s.LeagueId == ligueId);
            db.TeamStaffs.Add(new TeamStaff
            {
                TeamId = equipe.Id, LeagueStaffTypeId = staff.Id, Quantite = 1
            });
            db.EcheancesRondes.Add(new EcheanceRonde
            {
                LeagueId = ligueId, Ronde = 1, DateLimite = DateTime.UtcNow
            });
            db.LeagueCommissioners.Add(new LeagueCommissioner
            {
                LeagueId = ligueId, UserId = coach.Id, AssigneLe = DateTime.UtcNow
            });
            db.LeagueAwards.Add(new LeagueAward
            {
                LeagueId = ligueId, TeamId = equipe.Id, Type = AwardType.Champion
            });
            db.PhaseDeReposValidations.Add(new PhaseDeReposValidation
            {
                LeagueId = ligueId, TeamId = equipe.Id, ValideLe = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        await using (var db = _factory.CreateContext())
            await CreateService(db).SupprimerLigueAsync(ligueId);

        await using var verif = _factory.CreateContext();
        Assert.False(await verif.Leagues.AnyAsync(l => l.Id == ligueId));

        Assert.Empty(await verif.LeagueStaffTypes.Where(s => s.LeagueId == ligueId).ToListAsync());
        Assert.Empty(await verif.EcheancesRondes.Where(e => e.LeagueId == ligueId).ToListAsync());
        Assert.Empty(await verif.LeagueCommissioners.Where(c => c.LeagueId == ligueId).ToListAsync());
        Assert.Empty(await verif.LeagueAwards.Where(a => a.LeagueId == ligueId).ToListAsync());
        Assert.Empty(await verif.PhaseDeReposValidations.Where(p => p.LeagueId == ligueId).ToListAsync());
        Assert.Empty(await verif.Teams.Where(t => t.LeagueId == ligueId).ToListAsync());

        // Contrôle transverse : la vraie question posée par foreign_key_check.
        var orphelinsStaff = await verif.TeamStaffs
            .Where(ts => !verif.LeagueStaffTypes.Any(l => l.Id == ts.LeagueStaffTypeId))
            .CountAsync();
        Assert.Equal(0, orphelinsStaff);
    }
}
