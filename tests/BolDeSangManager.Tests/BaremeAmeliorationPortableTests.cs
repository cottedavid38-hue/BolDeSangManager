using System.Text;
using System.Text.Json;
using BolDeSangManager.Data.Models;
using BolDeSangManager.Services;
using BolDeSangManager.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace BolDeSangManager.Tests;

/// <summary>
/// Propagation du barème d'AMÉLIORATION (8 hausses de valeur + 6 paliers de coût
/// PSP) dans les chaînes de copie du projet : clonage de version, export/import
/// de données de jeu, suppression de version.
///
/// Ces trois-là sont les oublis classiques d'une entité neuve rattachée à une
/// <c>RulesVersion</c> : les manquer casse en silence la maintenabilité des
/// éditions (l'association doit faire évoluer ses règles sans développeur).
/// </summary>
public class BaremeAmeliorationPortableTests : IDisposable
{
    private readonly TestDbFactory _factory = new();
    public void Dispose() => _factory.Dispose();

    /// <summary>Barème volontairement DIFFÉRENT du LRB : sinon un test passerait
    /// aussi bien avec un repli sur les valeurs par défaut, sans rien prouver.</summary>
    private static BaremeAmelioration BaremeReference() => new()
    {
        HaussePrincipale    = 21_000,
        HausseSecondaire    = 41_000,
        HausseArmure        = 11_000,
        HausseMouvement     = 22_000,
        HausseCapacitePasse = 23_000,
        HausseAgilite       = 33_000,
        HausseForce         = 66_000,
        SurcoutElite        =  7_000,
        Paliers = Enumerable.Range(1, 6).Select(r => new PalierAmeliorationPsp
        {
            Rang                = r,
            CoutAleaPrincipale  = r,
            CoutChoixPrincipale = r * 2,
            CoutSecondaire      = r * 3,
            CoutCaracteristique = r * 4
        }).ToList()
    };

    private static async Task<(int gameId, int versionId)> SeedAvecBaremeAsync(TestDbFactory factory)
    {
        await using var db = factory.CreateContext();
        var (game, version) = await DataSeeder.SeedGameAsync(db);

        var svc = new DataEditService(db, NullLogger<DataEditService>.Instance);
        await svc.ModifierBaremeAmeliorationAsync(version.Id, BaremeReference());

        return (game.Id, version.Id);
    }

    private static void AssertBaremeReference(RulesVersion v, IReadOnlyList<PalierAmeliorationPsp> paliers)
    {
        Assert.Equal(21_000, v.HaussePrincipale);
        Assert.Equal(41_000, v.HausseSecondaire);
        Assert.Equal(11_000, v.HausseArmure);
        Assert.Equal(22_000, v.HausseMouvement);
        Assert.Equal(23_000, v.HausseCapacitePasse);
        Assert.Equal(33_000, v.HausseAgilite);
        Assert.Equal(66_000, v.HausseForce);
        Assert.Equal( 7_000, v.SurcoutElite);

        Assert.Equal(6, paliers.Count);
        foreach (var p in paliers)
        {
            Assert.Equal(p.Rang,     p.CoutAleaPrincipale);
            Assert.Equal(p.Rang * 2, p.CoutChoixPrincipale);
            Assert.Equal(p.Rang * 3, p.CoutSecondaire);
            Assert.Equal(p.Rang * 4, p.CoutCaracteristique);
        }
        Assert.Equal([1, 2, 3, 4, 5, 6], paliers.Select(p => p.Rang).OrderBy(r => r).ToArray());
    }

    // ── Clonage de version ────────────────────────────────────────────────────

    [Fact]
    public async Task ClonerVersion_ReprendLesHaussesEtLesSixPaliers()
    {
        var (gameId, sourceId) = await SeedAvecBaremeAsync(_factory);

        int cloneId;
        await using (var db = _factory.CreateContext())
        {
            var svc = new DataEditService(db, NullLogger<DataEditService>.Instance);
            cloneId = (await svc.CreerVersionAsync(gameId, "Clone", sourceId)).Id;
        }

        await using (var db = _factory.CreateContext())
        {
            var v = await db.RulesVersions.FindAsync(cloneId);
            var paliers = await db.PaliersAmelioration
                .Where(p => p.RulesVersionId == cloneId).OrderBy(p => p.Rang).ToListAsync();
            AssertBaremeReference(v!, paliers);
        }
    }

    [Fact]
    public async Task ClonerVersion_SourceSansPalier_PoseLeTableauLrb()
    {
        // Une version jamais paramétrée n'a aucun palier : le clone doit repartir
        // du LRB, jamais d'une table vide (qui proposerait 0 PSP partout).
        int gameId, sourceId;
        await using (var db = _factory.CreateContext())
        {
            var (game, version) = await DataSeeder.SeedGameAsync(db);
            (gameId, sourceId) = (game.Id, version.Id);
        }

        int cloneId;
        await using (var db = _factory.CreateContext())
        {
            var svc = new DataEditService(db, NullLogger<DataEditService>.Instance);
            cloneId = (await svc.CreerVersionAsync(gameId, "Clone", sourceId)).Id;
        }

        await using (var db = _factory.CreateContext())
        {
            var paliers = await db.PaliersAmelioration
                .Where(p => p.RulesVersionId == cloneId).OrderBy(p => p.Rang).ToListAsync();
            Assert.Equal(6, paliers.Count);
            Assert.Equal(3, paliers[0].CoutAleaPrincipale);
            Assert.Equal(38, paliers[5].CoutCaracteristique);
        }
    }

    // ── Export / import de version de règles ──────────────────────────────────

    [Fact]
    public async Task ExportImportVersion_RestitueLeBaremeAlIdentique()
    {
        var (gameId, versionId) = await SeedAvecBaremeAsync(_factory);

        byte[] json;
        await using (var db = _factory.CreateContext())
            json = await new GameDataExportService(db, NullLogger<GameDataExportService>.Instance)
                .ExportAsync(versionId);

        await using (var db = _factory.CreateContext())
        {
            var (ok, erreurs) = await new GameDataExportService(db, NullLogger<GameDataExportService>.Instance)
                .ImportAsync(new MemoryStream(json), gameId, "Réimportée");
            Assert.True(ok, string.Join(" / ", erreurs));
        }

        await using (var db = _factory.CreateContext())
        {
            var v = await db.RulesVersions.FirstAsync(x => x.Nom == "Réimportée");
            var paliers = await db.PaliersAmelioration
                .Where(p => p.RulesVersionId == v.Id).OrderBy(p => p.Rang).ToListAsync();
            AssertBaremeReference(v, paliers);
        }
    }

    [Fact]
    public async Task ImportVersion_SansBlocBareme_DonneLeBaremeLrb_PasDesZeros()
    {
        var (gameId, versionId) = await SeedAvecBaremeAsync(_factory);

        byte[] json;
        await using (var db = _factory.CreateContext())
            json = await new GameDataExportService(db, NullLogger<GameDataExportService>.Instance)
                .ExportAsync(versionId);

        // On retire du JSON tout ce qui touche au barème d'amélioration : c'est
        // exactement la forme d'un export antérieur à cette fonctionnalité.
        var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
        foreach (var cle in doc.Keys.Where(k =>
                     k.StartsWith("hausse", StringComparison.OrdinalIgnoreCase)
                     || k.Equals("surcoutElite", StringComparison.OrdinalIgnoreCase)
                     || k.Equals("paliersAmelioration", StringComparison.OrdinalIgnoreCase)).ToList())
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
            var lrb = BaremeAmelioration.ParDefaut();

            Assert.Equal(lrb.HaussePrincipale,    v.HaussePrincipale);
            Assert.Equal(lrb.HausseSecondaire,    v.HausseSecondaire);
            Assert.Equal(lrb.HausseArmure,        v.HausseArmure);
            Assert.Equal(lrb.HausseMouvement,     v.HausseMouvement);
            Assert.Equal(lrb.HausseCapacitePasse, v.HausseCapacitePasse);
            Assert.Equal(lrb.HausseAgilite,       v.HausseAgilite);
            Assert.Equal(lrb.HausseForce,         v.HausseForce);
            Assert.Equal(lrb.SurcoutElite,        v.SurcoutElite);
            Assert.NotEqual(0, v.HaussePrincipale);

            var paliers = await db.PaliersAmelioration
                .Where(p => p.RulesVersionId == v.Id).OrderBy(p => p.Rang).ToListAsync();
            Assert.Equal(6, paliers.Count);
            Assert.Equal(3,  paliers[0].CoutAleaPrincipale);
            Assert.Equal(6,  paliers[0].CoutChoixPrincipale);
            Assert.Equal(15, paliers[5].CoutAleaPrincipale);
            Assert.Equal(38, paliers[5].CoutCaracteristique);
        }
    }

    // ── Suppression de version ────────────────────────────────────────────────

    [Fact]
    public async Task SupprimerVersion_SupprimeAussiSesPaliers()
    {
        var (_, versionId) = await SeedAvecBaremeAsync(_factory);

        await using (var db = _factory.CreateContext())
        {
            var v = await db.RulesVersions.FindAsync(versionId);
            v!.EstActive = false;                  // la version active est protégée
            await db.SaveChangesAsync();
        }

        await using (var db = _factory.CreateContext())
        {
            var svc = new DataEditService(db, NullLogger<DataEditService>.Instance);
            await svc.SupprimerVersionAsync(versionId);
        }

        await using (var db = _factory.CreateContext())
        {
            Assert.Null(await db.RulesVersions.FindAsync(versionId));
            Assert.Empty(await db.PaliersAmelioration
                .Where(p => p.RulesVersionId == versionId).ToListAsync());
        }
    }

    // ── Édition admin : la copie de travail ne doit rien écraser ──────────────

    [Fact]
    public async Task ModifierUnAutreChamp_NeChangePasLeBaremeDAmelioration()
    {
        // Le piège maison : une modale d'admin qui recopie l'entité champ par
        // champ oublie le champ neuf, qui repart alors à son défaut C#. Ici on
        // exerce les commandes voisines qui écrivent sur la même RulesVersion.
        var (_, versionId) = await SeedAvecBaremeAsync(_factory);

        await using (var db = _factory.CreateContext())
        {
            var svc = new DataEditService(db, NullLogger<DataEditService>.Instance);
            await svc.ModifierBaremeXpAsync(versionId, new BaremePsp { ParTouchdown = 9 });
            await svc.ModifierBaremePointsAsync(versionId, new BaremePoints { Victoire = 7 });
            await svc.RenommerVersionAsync(versionId, "Saison 3 bis");
        }

        await using (var db = _factory.CreateContext())
        {
            var v = await db.RulesVersions.FindAsync(versionId);
            var paliers = await db.PaliersAmelioration
                .Where(p => p.RulesVersionId == versionId).OrderBy(p => p.Rang).ToListAsync();

            Assert.Equal(9, v!.PspParTouchdown);      // le champ visé a bien changé…
            Assert.Equal(7, v.PointsVictoire);
            AssertBaremeReference(v, paliers);        // …et le barème n'a pas bougé
        }
    }

    [Fact]
    public async Task ModifierBaremeAmelioration_MetAJourLesPaliersEnPlace()
    {
        var (_, versionId) = await SeedAvecBaremeAsync(_factory);

        await using (var db = _factory.CreateContext())
        {
            var svc = new DataEditService(db, NullLogger<DataEditService>.Instance);
            var bareme = BaremeReference();
            bareme.Paliers[0].CoutAleaPrincipale = 42;
            await svc.ModifierBaremeAmeliorationAsync(versionId, bareme);
        }

        await using (var db = _factory.CreateContext())
        {
            var paliers = await db.PaliersAmelioration
                .Where(p => p.RulesVersionId == versionId).OrderBy(p => p.Rang).ToListAsync();
            Assert.Equal(6, paliers.Count);   // pas de doublon : mise à jour en place
            Assert.Equal(42, paliers[0].CoutAleaPrincipale);
        }
    }

    [Fact]
    public async Task ModifierBaremeAmelioration_RefuseUnRangHorsBornes()
    {
        var (_, versionId) = await SeedAvecBaremeAsync(_factory);

        await using var db = _factory.CreateContext();
        var svc = new DataEditService(db, NullLogger<DataEditService>.Instance);
        var bareme = BaremeReference();
        bareme.Paliers[0].Rang = 7;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ModifierBaremeAmeliorationAsync(versionId, bareme));
    }
}
