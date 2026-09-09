using System.Text;
using BolDeSangManager.Services;
using BolDeSangManager.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace BolDeSangManager.Tests;

/// <summary>
/// Le renommage XP → PSP a touché des DTO qui sont un FORMAT DE FICHIER : les
/// commissaires ont déjà des exports de version sur leur disque. Renommer le
/// champ C# sans figer le nom JSON aurait rendu ces fichiers silencieusement
/// dégradés — le barème serait retombé aux valeurs par défaut, SANS erreur ni
/// message, et personne ne l'aurait vu avant la première feuille de match.
///
/// Ce test importe pour de vrai un JSON écrit à l'ANCIEN format (clés « xp… »).
/// </summary>
public class RetrocompatJsonPspTests : IDisposable
{
    private readonly TestDbFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    /// <summary>
    /// Barème volontairement DIFFÉRENT des valeurs par défaut du jeu
    /// (3/1/2/2/4) : si l'alias JSON disparaissait, l'import retomberait sur
    /// les défauts et le test passerait pour de mauvaises raisons.
    /// </summary>
    [Fact]
    public async Task ImportDeVersion_AuFormatXp_ConserveLeBaremePsp()
    {
        const string ancienJson = """
        {
          "jeu": "Blood Bowl",
          "version": "Import Retrocompat",
          "ordre": 99,
          "estActive": false,
          "skills": [],
          "typesEquipes": [],
          "xpParTouchdown": 7,
          "xpParPasse": 5,
          "xpParInterception": 6,
          "xpParElimination": 8,
          "xpBonusMvp": 9,
          "xpParDeviation": 4,
          "xpParAgression": 3
        }
        """;

        await using var db = _factory.CreateContext();
        var jeu = new Data.Models.Game { Nom = "Blood Bowl" };
        db.Games.Add(jeu);
        await db.SaveChangesAsync();

        var svc = new GameDataExportService(db, NullLogger<GameDataExportService>.Instance);

        using var flux = new MemoryStream(Encoding.UTF8.GetBytes(ancienJson));
        var (ok, erreurs) = await svc.ImportAsync(flux, jeu.Id, "Import Retrocompat");

        Assert.True(ok, string.Join(" | ", erreurs));

        var version = await db.RulesVersions
            .AsNoTracking()
            .FirstAsync(v => v.Nom == "Import Retrocompat");

        // Sans [JsonPropertyName("xp…")], ces champs vaudraient null à la
        // lecture et l'import aurait posé les valeurs par défaut.
        Assert.Equal(7, version.PspParTouchdown);
        Assert.Equal(5, version.PspParPasse);
        Assert.Equal(6, version.PspParInterception);
        Assert.Equal(8, version.PspParElimination);
        Assert.Equal(9, version.PspBonusMvp);
        Assert.Equal(4, version.PspParDeviation);
        Assert.Equal(3, version.PspParAgression);
    }

    /// <summary>
    /// Un export produit AUJOURD'HUI doit rester relisible par une instance
    /// restée en arrière : le VPS de l'association n'est pas mis à jour en même
    /// temps que le poste du commissaire qui exporte.
    /// </summary>
    [Fact]
    public async Task ExportDeVersion_EcritToujoursLesClesEnXp()
    {
        await using var db = _factory.CreateContext();
        var jeu = new Data.Models.Game { Nom = "Blood Bowl" };
        db.Games.Add(jeu);
        await db.SaveChangesAsync();

        var version = new Data.Models.RulesVersion
        {
            Nom = "Export Retrocompat",
            GameId = jeu.Id,
            Ordre = 98,
            PspParTouchdown = 7,
            PspBonusMvp = 9
        };
        db.RulesVersions.Add(version);
        await db.SaveChangesAsync();

        var svc = new GameDataExportService(db, NullLogger<GameDataExportService>.Instance);
        var json = Encoding.UTF8.GetString(await svc.ExportAsync(version.Id));

        Assert.Contains("\"xpParTouchdown\": 7", json);
        Assert.Contains("\"xpBonusMvp\": 9", json);
        Assert.DoesNotContain("pspParTouchdown", json);
    }
}
