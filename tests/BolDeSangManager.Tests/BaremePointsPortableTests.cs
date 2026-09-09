using System.Text;
using System.Text.Json;
using BolDeSangManager.Data;
using BolDeSangManager.Data.Enums;
using BolDeSangManager.Data.Models;
using BolDeSangManager.Services;
using BolDeSangManager.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace BolDeSangManager.Tests;

/// <summary>
/// Propagation du barème de points dans les chaînes de COPIE : clonage de
/// version, export/import de version, export/import de ligue.
///
/// Ces trois-là sont les oublis classiques d'une nouvelle entité — les manquer
/// casse en silence la maintenabilité des éditions (l'association doit pouvoir
/// faire évoluer ses règles sans développeur).
/// </summary>
public class BaremePointsPortableTests : IDisposable
{
    private readonly TestDbFactory _factory = new();
    public void Dispose() => _factory.Dispose();

    private static BaremePoints BaremeReference() => new()
    {
        Victoire = 2000, Nul = 1500, Defaite = 1000,
        ParTouchdown = 5, ParElimination = 2, ParInterception = 1,
        ParPasse = 1, ParDeviation = 1, ParAgression = 1
    };

    // ── Clonage de version ────────────────────────────────────────────────────

    [Fact]
    public async Task ClonerVersion_ReprendLeBaremeDePoints()
    {
        int sourceId, gameId;
        await using (var db = _factory.CreateContext())
        {
            var (game, version) = await DataSeeder.SeedGameAsync(db);
            BaremeReference().AppliquerA(version);
            version.PspParDeviation = 7;
            version.PspParAgression = 9;
            await db.SaveChangesAsync();
            (sourceId, gameId) = (version.Id, game.Id);
        }

        int cloneId;
        await using (var db = _factory.CreateContext())
        {
            var svc = new DataEditService(db, NullLogger<DataEditService>.Instance);
            var clone = await svc.CreerVersionAsync(gameId, "Clone", sourceId);
            cloneId = clone.Id;
        }

        await using (var db = _factory.CreateContext())
        {
            var v = await db.RulesVersions.FindAsync(cloneId);
            Assert.Equal(2000, v!.PointsVictoire);
            Assert.Equal(1500, v.PointsNul);
            Assert.Equal(1000, v.PointsDefaite);
            Assert.Equal(5, v.PointsParTouchdown);
            Assert.Equal(1, v.PointsParAgression);
            Assert.Equal(7, v.PspParDeviation);
            Assert.Equal(9, v.PspParAgression);
        }
    }

    // ── Export / import de version de règles ──────────────────────────────────

    [Fact]
    public async Task ExportImportVersion_RestitueLeBareme()
    {
        int versionId;
        await using (var db = _factory.CreateContext())
        {
            var (_, version) = await DataSeeder.SeedGameAsync(db);
            BaremeReference().AppliquerA(version);
            await db.SaveChangesAsync();
            versionId = version.Id;
        }

        byte[] json;
        int gameId;
        await using (var db = _factory.CreateContext())
        {
            json = await new GameDataExportService(db, NullLogger<GameDataExportService>.Instance)
                .ExportAsync(versionId);
            gameId = (await db.RulesVersions.FindAsync(versionId))!.GameId;
        }

        await using (var db = _factory.CreateContext())
        {
            var (ok, erreurs) = await new GameDataExportService(db, NullLogger<GameDataExportService>.Instance)
                .ImportAsync(new MemoryStream(json), gameId, "Réimportée");
            Assert.True(ok, string.Join(" / ", erreurs));
        }

        await using (var db = _factory.CreateContext())
        {
            var v = await db.RulesVersions.FirstAsync(x => x.Nom == "Réimportée");
            Assert.Equal(2000, v!.PointsVictoire);
            Assert.Equal(1500, v.PointsNul);
            Assert.Equal(1000, v.PointsDefaite);
            Assert.Equal(5, v.PointsParTouchdown);
            Assert.Equal(2, v.PointsParElimination);
            Assert.Equal(1, v.PointsParPasse);
        }
    }

    [Fact]
    public async Task ImportVersion_FichierAnterieur_RepliSurLeBaremeParDefautEtNonSurDesZeros()
    {
        // Le piège le plus grave de tout le lot : un `?? 0` naïf donnerait
        // « aucun point par victoire » à toute version importée d'un ancien JSON.
        int versionId;
        await using (var db = _factory.CreateContext())
        {
            var (_, version) = await DataSeeder.SeedGameAsync(db);
            versionId = version.Id;
        }

        byte[] json;
        int gameId;
        await using (var db = _factory.CreateContext())
        {
            json = await new GameDataExportService(db, NullLogger<GameDataExportService>.Instance)
                .ExportAsync(versionId);
            gameId = (await db.RulesVersions.FindAsync(versionId))!.GameId;
        }

        // On retire les champs de barème du JSON : c'est exactement la forme d'un
        // export antérieur à cette fonctionnalité.
        var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
        foreach (var cle in doc.Keys.Where(k => k.StartsWith("points", StringComparison.OrdinalIgnoreCase)).ToList())
            doc.Remove(cle);
        var jsonAncien = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(doc));

        await using (var db = _factory.CreateContext())
        {
            var (ok, erreurs) = await new GameDataExportService(db, NullLogger<GameDataExportService>.Instance)
                .ImportAsync(new MemoryStream(jsonAncien), gameId, "Ancienne");
            Assert.True(ok, string.Join(" / ", erreurs));
        }

        await using (var db = _factory.CreateContext())
        {
            var v = await db.RulesVersions.FirstAsync(x => x.Nom == "Ancienne");
            Assert.Equal(3, v!.PointsVictoire);
            Assert.Equal(1, v.PointsNul);
            Assert.Equal(0, v.PointsDefaite);
        }
    }

    // ── Export / import de ligue ──────────────────────────────────────────────

    [Fact]
    public async Task ExportImportLigue_RestitueLeBaremeEtSesPaliers()
    {
        int ligueId;
        string commissaireId;

        await using (var db = _factory.CreateContext())
        {
            var comm = DataSeeder.CreateUser("bpexp");
            db.Users.Add(comm);
            await db.SaveChangesAsync();

            var (game, rv) = await DataSeeder.SeedGameAsync(db);
            var (tt, pos) = await DataSeeder.SeedTeamTypeAsync(db, game.Id);
            var ligue = await DataSeeder.SeedLeagueAsync(db, game.Id, rv.Id, comm.Id);

            BaremeReference().AppliquerA(ligue);
            db.PaliersPointsLigue.Add(new PalierPointsLigue
            {
                LeagueId = ligue.Id, APartirDuTour = 13,
                PointsVictoire = 2000, PointsNul = 1500, PointsDefaite = 1000
            });
            await db.SaveChangesAsync();

            var t1 = await DataSeeder.SeedTeamAsync(db, ligue.Id, comm.Id, tt.Id, "Alpha");
            await DataSeeder.SeedPlayerAsync(db, t1.Id, pos.Id, "Gromag", 1);

            (ligueId, commissaireId) = (ligue.Id, comm.Id);
        }

        byte[] json;
        await using (var db = _factory.CreateContext())
            json = await new LeagueExportService(db, NullLogger<LeagueExportService>.Instance,
                    new StaffService(db, NullLogger<StaffService>.Instance))
                .ExportAsync(ligueId);

        int importeeId;
        await using (var db = _factory.CreateContext())
        {
            var l = await new LeagueExportService(db, NullLogger<LeagueExportService>.Instance,
                    new StaffService(db, NullLogger<StaffService>.Instance))
                .ImportAsync(new MemoryStream(json), commissaireId);
            importeeId = l.Id;
        }

        await using (var db = _factory.CreateContext())
        {
            var l = await db.Leagues.Include(x => x.PaliersPoints)
                .FirstAsync(x => x.Id == importeeId);

            Assert.Equal(2000, l.PointsVictoire);
            Assert.Equal(1000, l.PointsDefaite);
            Assert.Equal(5, l.PointsParTouchdown);
            Assert.Equal(1, l.PointsParDeviation);

            var palier = Assert.Single(l.PaliersPoints);
            Assert.Equal(13, palier.APartirDuTour);
            Assert.Equal(2000, palier.PointsVictoire);
            Assert.Equal(1000, palier.PointsDefaite);
        }
    }

    // ── Création de ligue ─────────────────────────────────────────────────────

    [Fact]
    public async Task CreerLigue_HeriteDuBaremeDeLaVersionDeRegles()
    {
        // Le barème n'est PAS posté par le navigateur : le serveur le lit sur la
        // version de règles choisie. Deux niveaux, comme le barème d'XP.
        int gameId, versionId;
        string commId;

        await using (var db = _factory.CreateContext())
        {
            var comm = DataSeeder.CreateUser("bpcreer");
            db.Users.Add(comm);
            await db.SaveChangesAsync();

            var (game, version) = await DataSeeder.SeedGameAsync(db);
            BaremeReference().AppliquerA(version);
            await db.SaveChangesAsync();

            (gameId, versionId, commId) = (game.Id, version.Id, comm.Id);
        }

        int ligueId;
        await using (var db = _factory.CreateContext())
        {
            var svc = new LeagueService(db, NullLogger<LeagueService>.Instance, new StubAuth(),
                new StaffService(db, NullLogger<StaffService>.Instance));
            var ligue = await svc.CreerLigueAsync(new League
            {
                Nom = "Nouvelle", GameId = gameId, RulesVersionId = versionId,
                BudgetDepart = 1_000_000
            }, commId);
            ligueId = ligue.Id;
        }

        await using (var db = _factory.CreateContext())
        {
            var l = await db.Leagues.FindAsync(ligueId);
            Assert.Equal(2000, l!.PointsVictoire);
            Assert.Equal(5, l.PointsParTouchdown);
            Assert.Equal(1, l.PointsParAgression);
        }
    }

    [Fact]
    public async Task ModifierLaVersion_NeChangePasLesLiguesDejaCreees()
    {
        // Le gel est la fonctionnalité : une ligue en cours ne doit pas voir son
        // barème bouger parce que l'association a corrigé ses règles.
        int gameId, versionId;
        string commId;

        await using (var db = _factory.CreateContext())
        {
            var comm = DataSeeder.CreateUser("bpgel");
            db.Users.Add(comm);
            await db.SaveChangesAsync();
            var (game, version) = await DataSeeder.SeedGameAsync(db);
            (gameId, versionId, commId) = (game.Id, version.Id, comm.Id);
        }

        int ligueId;
        await using (var db = _factory.CreateContext())
        {
            var svc = new LeagueService(db, NullLogger<LeagueService>.Instance, new StubAuth(),
                new StaffService(db, NullLogger<StaffService>.Instance));
            var l = await svc.CreerLigueAsync(new League
            {
                Nom = "Ancienne", GameId = gameId, RulesVersionId = versionId, BudgetDepart = 1_000_000
            }, commId);
            ligueId = l.Id;
        }

        await using (var db = _factory.CreateContext())
        {
            var v = await db.RulesVersions.FindAsync(versionId);
            BaremeReference().AppliquerA(v!);
            await db.SaveChangesAsync();
        }

        int nouvelleId;
        await using (var db = _factory.CreateContext())
        {
            var svc = new LeagueService(db, NullLogger<LeagueService>.Instance, new StubAuth(),
                new StaffService(db, NullLogger<StaffService>.Instance));
            var l = await svc.CreerLigueAsync(new League
            {
                Nom = "Nouvelle", GameId = gameId, RulesVersionId = versionId, BudgetDepart = 1_000_000
            }, commId);
            nouvelleId = l.Id;
        }

        await using (var db = _factory.CreateContext())
        {
            // L'ancienne garde le barème d'origine…
            Assert.Equal(3, (await db.Leagues.FindAsync(ligueId))!.PointsVictoire);
            // …la nouvelle hérite du barème corrigé. Vérifier une seule des deux
            // ne prouverait rien.
            Assert.Equal(2000, (await db.Leagues.FindAsync(nouvelleId))!.PointsVictoire);
        }
    }

    // ── Suppression de ligue ──────────────────────────────────────────────────

    [Fact]
    public async Task SupprimerLigue_EmporteSesPaliers()
    {
        // ExecuteDeleteAsync n'exécute AUCUNE cascade EF : le graphe est à notre
        // charge, et un enfant oublié laisse des orphelins.
        int ligueId;
        string commId;

        await using (var db = _factory.CreateContext())
        {
            var comm = DataSeeder.CreateUser("bpsupp");
            db.Users.Add(comm);
            await db.SaveChangesAsync();

            var (game, rv) = await DataSeeder.SeedGameAsync(db);
            var ligue = await DataSeeder.SeedLeagueAsync(db, game.Id, rv.Id, comm.Id);
            db.PaliersPointsLigue.Add(new PalierPointsLigue
            {
                LeagueId = ligue.Id, APartirDuTour = 13,
                PointsVictoire = 2000, PointsNul = 1500, PointsDefaite = 1000
            });
            await db.SaveChangesAsync();
            (ligueId, commId) = (ligue.Id, comm.Id);
        }

        await using (var db = _factory.CreateContext())
            await new LeagueService(db, NullLogger<LeagueService>.Instance, new StubAuth(),
                new StaffService(db, NullLogger<StaffService>.Instance))
                .SupprimerLigueAsync(ligueId);

        await using (var db = _factory.CreateContext())
            Assert.Empty(await db.PaliersPointsLigue.Where(p => p.LeagueId == ligueId).ToListAsync());
    }

    // ── Édition admin de la version ───────────────────────────────────────────

    [Fact]
    public async Task ModifierBaremePointsDeVersion_NeTouchePasAuBaremeXp()
    {
        // Les deux barèmes vivent sur la même entité et sont édités par la même
        // modale : un champ oublié dans la copie de travail repartirait à son
        // défaut C# et écraserait la valeur en base.
        int versionId;
        await using (var db = _factory.CreateContext())
        {
            var (_, version) = await DataSeeder.SeedGameAsync(db);
            version.PspParTouchdown = 7;
            version.PspBonusMvp = 11;
            await db.SaveChangesAsync();
            versionId = version.Id;
        }

        await using (var db = _factory.CreateContext())
            await new DataEditService(db, NullLogger<DataEditService>.Instance)
                .ModifierBaremePointsAsync(versionId, BaremeReference());

        await using (var db = _factory.CreateContext())
        {
            var v = await db.RulesVersions.FindAsync(versionId);
            Assert.Equal(2000, v!.PointsVictoire);
            Assert.Equal(7, v.PspParTouchdown);
            Assert.Equal(11, v.PspBonusMvp);
        }
    }

    private class StubAuth : IAuthorizationService
    {
        public Task<bool> EstAdminAsync(string userId) => Task.FromResult(true);
        public Task<bool> EstGrandCommissaireAsync(string userId) => Task.FromResult(true);
        public Task<bool> EstCommissaireDeLigueAsync(string userId, int ligueId) => Task.FromResult(true);
        public Task<bool> PeutGererLigueAsync(string userId, int ligueId) => Task.FromResult(true);
        public Task<bool> PeutEditerDonneesAsync(string userId) => Task.FromResult(true);
        public Task<bool> PeutGererSettingsAsync(string userId) => Task.FromResult(true);
    }
}
