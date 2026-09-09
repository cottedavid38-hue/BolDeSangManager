using BolDeSangManager.Data;
using BolDeSangManager.Data.Models;
using BolDeSangManager.Services;
using BolDeSangManager.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace BolDeSangManager.Tests;

/// <summary>
/// Export / import du catalogue informatif (ligues, coups de pouce, star
/// players).
///
/// Même principe que le catalogue de règles spéciales : l'import global crée
/// une NOUVELLE version, ce qui obligerait à remigrer les ligues en cours.
/// Celui-ci FUSIONNE dans une version existante — c'est ce qui le rend
/// utilisable sur une instance déjà en service.
/// </summary>
public class CataloguePortableTests
{
    private static GameDataExportService Svc(ApplicationDbContext db) =>
        new(db, NullLogger<GameDataExportService>.Instance);

    /// <summary>Version peuplée : 2 ligues, 1 coup de pouce, 2 star players.</summary>
    private static async Task<int> SeedSourceAsync(TestDbFactory factory)
    {
        using var db = factory.CreateContext();
        var (game, version) = await DataSeeder.SeedGameAsync(db);

        var badlands = new ThemedLeague { RulesVersionId = version.Id, Nom = "Bagarre des Terres Arides" };
        var sylvestre = new ThemedLeague { RulesVersionId = version.Id, Nom = "Ligue Sylvestre" };
        db.ThemedLeagues.AddRange(badlands, sylvestre);

        db.Inducements.Add(new Inducement
        {
            RulesVersionId = version.Id,
            Nom = "Pots-de-vin",
            Description = "Texte complet de l'effet.",
            Cout = 100_000,
            QuantiteMax = 3,
            Restriction = "0-6 pour certaines équipes"
        });
        await db.SaveChangesAsync();

        var restreint = new StarPlayer
        {
            RulesVersionId = version.Id,
            Nom = "Varag Mâche Goule",
            Cout = 260_000,
            Mouvement = 6, Force = 5, Agilite = "3+", CapacitePasse = "5+", Armure = "10+",
            Competences = "Blocage, Châtaigne",
            ReglesSpeciales = "'Krazer et 'Klater : texte complet de la règle."
        };
        var ouvert = new StarPlayer
        {
            RulesVersionId = version.Id,
            Nom = "Morg 'n' Thorg",
            Cout = 340_000,
            Mouvement = 6, Force = 6, Agilite = "3+", CapacitePasse = "4+", Armure = "11+",
            Competences = "Blocage",
            ReglesSpeciales = "La Baliste : texte complet."
        };
        db.StarPlayers.AddRange(restreint, ouvert);
        await db.SaveChangesAsync();

        // Varag n'est accessible qu'en Bagarre des Terres Arides ; Morg n'a
        // aucune ligue, il est donc ouvert à toutes les équipes.
        db.Set<StarPlayerThemedLeague>().Add(new StarPlayerThemedLeague
        {
            StarPlayerId = restreint.Id, ThemedLeagueId = badlands.Id
        });
        await db.SaveChangesAsync();

        return version.Id;
    }

    /// <summary>Version vide, destination de l'import.</summary>
    private static async Task<int> SeedCibleAsync(TestDbFactory factory)
    {
        using var db = factory.CreateContext();
        var (_, version) = await DataSeeder.SeedGameAsync(db);
        return version.Id;
    }

    [Fact]
    public async Task LExportContientLesTroisCatalogues()
    {
        using var factory = new TestDbFactory();
        var source = await SeedSourceAsync(factory);

        byte[] fichier;
        using (var db = factory.CreateContext())
            fichier = await Svc(db).ExportCatalogueAsync(source);

        var json = System.Text.Encoding.UTF8.GetString(fichier);

        Assert.Contains("Bagarre des Terres Arides", json);
        Assert.Contains("Pots-de-vin", json);
        Assert.Contains("Varag", json);
        // Les textes complets doivent voyager : c'est tout l'intérêt du fichier.
        Assert.Contains("texte complet de la règle", json);
    }

    [Fact]
    public async Task LImportFusionneDansUneVersionExistanteSansEnCreer()
    {
        using var factory = new TestDbFactory();
        var source = await SeedSourceAsync(factory);
        var cible = await SeedCibleAsync(factory);

        int versionsAvant;
        using (var db = factory.CreateContext())
            versionsAvant = await db.RulesVersions.CountAsync();

        byte[] fichier;
        using (var db = factory.CreateContext())
            fichier = await Svc(db).ExportCatalogueAsync(source);

        using (var db = factory.CreateContext())
        {
            var (ok, erreurs) = await Svc(db).ImportCatalogueAsync(cible, new MemoryStream(fichier));
            Assert.True(ok, string.Join(" / ", erreurs));
        }

        using (var db = factory.CreateContext())
        {
            // Aucune version créée : c'est la différence avec l'import global.
            Assert.Equal(versionsAvant, await db.RulesVersions.CountAsync());

            Assert.Equal(2, await db.ThemedLeagues.CountAsync(l => l.RulesVersionId == cible));
            Assert.Equal(1, await db.Inducements.CountAsync(i => i.RulesVersionId == cible));
            Assert.Equal(2, await db.StarPlayers.CountAsync(s => s.RulesVersionId == cible));
        }
    }

    /// <summary>
    /// Le rattachement aux ligues doit survivre au transport : sans lui, un
    /// star player restreint deviendrait accessible à TOUTES les équipes.
    /// </summary>
    [Fact]
    public async Task LesLiguesDAccesSontPreservees()
    {
        using var factory = new TestDbFactory();
        var source = await SeedSourceAsync(factory);
        var cible = await SeedCibleAsync(factory);

        byte[] fichier;
        using (var db = factory.CreateContext())
            fichier = await Svc(db).ExportCatalogueAsync(source);

        using (var db = factory.CreateContext())
            await Svc(db).ImportCatalogueAsync(cible, new MemoryStream(fichier));

        using (var db = factory.CreateContext())
        {
            var varag = await db.StarPlayers
                .Include(s => s.Ligues).ThenInclude(x => x.ThemedLeague)
                .FirstAsync(s => s.RulesVersionId == cible && s.Nom.StartsWith("Varag"));

            var morg = await db.StarPlayers
                .Include(s => s.Ligues)
                .FirstAsync(s => s.RulesVersionId == cible && s.Nom.StartsWith("Morg"));

            Assert.Single(varag.Ligues);
            Assert.Equal("Bagarre des Terres Arides", varag.Ligues.First().ThemedLeague.Nom);

            // Aucune ligue = ouvert à tous, et ça doit le rester.
            Assert.Empty(morg.Ligues);
        }
    }

    /// <summary>
    /// Les textes complets sont la raison d'être de ce transport : ils vivent
    /// en base, pas dans le dépôt.
    /// </summary>
    [Fact]
    public async Task LesTextesCompletsSontTransportes()
    {
        using var factory = new TestDbFactory();
        var source = await SeedSourceAsync(factory);
        var cible = await SeedCibleAsync(factory);

        byte[] fichier;
        using (var db = factory.CreateContext())
            fichier = await Svc(db).ExportCatalogueAsync(source);

        using (var db = factory.CreateContext())
            await Svc(db).ImportCatalogueAsync(cible, new MemoryStream(fichier));

        using (var db = factory.CreateContext())
        {
            var cp = await db.Inducements.FirstAsync(i => i.RulesVersionId == cible);
            Assert.Equal("Texte complet de l'effet.", cp.Description);
            Assert.Equal(3, cp.QuantiteMax);
            Assert.Equal("0-6 pour certaines équipes", cp.Restriction);

            var star = await db.StarPlayers
                .FirstAsync(s => s.RulesVersionId == cible && s.Nom.StartsWith("Varag"));
            Assert.Contains("texte complet de la règle", star.ReglesSpeciales);
            Assert.Equal(260_000, star.Cout);
            Assert.Equal("10+", star.Armure);
        }
    }

    /// <summary>
    /// Rejouer l'import ne doit RIEN dupliquer : c'est ainsi qu'on propage un
    /// correctif sur une instance en service.
    /// </summary>
    [Fact]
    public async Task UnSecondImportMetAJourSansDupliquer()
    {
        using var factory = new TestDbFactory();
        var source = await SeedSourceAsync(factory);
        var cible = await SeedCibleAsync(factory);

        byte[] fichier;
        using (var db = factory.CreateContext())
            fichier = await Svc(db).ExportCatalogueAsync(source);

        using (var db = factory.CreateContext())
            await Svc(db).ImportCatalogueAsync(cible, new MemoryStream(fichier));

        // Second import du MÊME fichier : il doit réussir, pas partir en
        // rollback sur un index unique.
        using (var db = factory.CreateContext())
        {
            var (ok, erreurs) = await Svc(db).ImportCatalogueAsync(cible, new MemoryStream(fichier));
            Assert.True(ok, string.Join(" / ", erreurs));
        }

        using (var db = factory.CreateContext())
        {
            Assert.Equal(2, await db.ThemedLeagues.CountAsync(l => l.RulesVersionId == cible));
            Assert.Equal(1, await db.Inducements.CountAsync(i => i.RulesVersionId == cible));
            Assert.Equal(2, await db.StarPlayers.CountAsync(s => s.RulesVersionId == cible));

            // Et pas de liaison en double non plus.
            var varag = await db.StarPlayers
                .Include(s => s.Ligues)
                .FirstAsync(s => s.RulesVersionId == cible && s.Nom.StartsWith("Varag"));
            Assert.Single(varag.Ligues);
        }
    }

    /// <summary>
    /// Une ligue citée par un star player mais absente du bloc « Ligues » doit
    /// être créée : l'ignorer rendrait le joueur accessible à toutes les
    /// équipes, l'inverse exact de la restriction voulue.
    /// </summary>
    [Fact]
    public async Task UneLigueManquanteEstCreeePlutotQuIgnoree()
    {
        using var factory = new TestDbFactory();
        var cible = await SeedCibleAsync(factory);

        var json = """
        {
          "jeu": "Blood Bowl",
          "version": "Test",
          "ligues": [],
          "coupsDePouce": [],
          "starPlayers": [
            {
              "nom": "Griff Oberwald",
              "cout": 300000,
              "mouvement": 7, "force": 4,
              "agilite": "2+", "capacitePasse": "3+", "armure": "9+",
              "competences": "Blocage",
              "reglesSpeciales": "Grand Professionnel : texte.",
              "ligues": ["Classique du Vieux Monde"]
            }
          ]
        }
        """;

        using (var db = factory.CreateContext())
        {
            var flux = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
            var (ok, erreurs) = await Svc(db).ImportCatalogueAsync(cible, flux);
            Assert.True(ok, string.Join(" / ", erreurs));
        }

        using (var db = factory.CreateContext())
        {
            var ligue = await db.ThemedLeagues
                .FirstOrDefaultAsync(l => l.RulesVersionId == cible && l.Nom == "Classique du Vieux Monde");
            Assert.NotNull(ligue);

            var star = await db.StarPlayers
                .Include(s => s.Ligues)
                .FirstAsync(s => s.RulesVersionId == cible);
            Assert.Single(star.Ligues);
        }
    }

    // ── Feuille imprimée ─────────────────────────────────────────────────────

    /// <summary>
    /// Les deux sections sont OPTIONNELLES : une feuille sans elles ne doit pas
    /// les imprimer, sinon l'option à cocher ne servirait à rien.
    /// </summary>
    [Fact]
    public void SansLesOptions_LeContenuNestPasImprime()
    {
        var equipe = EquipeMinimale();

        var texte = LireTextePdf(new PdfService().GenererFeuilleEquipe(equipe, false));

        Assert.DoesNotContain("Coups de Pouce", texte);
        Assert.DoesNotContain("Star Players disponibles", texte);
    }

    [Fact]
    public void AvecLesOptions_LesDeuxSectionsSontImprimees()
    {
        var equipe = EquipeMinimale();

        var coupsDePouce = new List<Inducement>
        {
            new() { Nom = "Pots-de-vin", Description = "Effet du coup de pouce.",
                    Cout = 100_000, QuantiteMax = 3 }
        };
        var stars = new List<StarPlayer>
        {
            new() { Nom = "Griff Oberwald", Cout = 300_000,
                    Mouvement = 7, Force = 4, Agilite = "2+", CapacitePasse = "3+", Armure = "9+",
                    Competences = "Blocage, Esquive",
                    ReglesSpeciales = "Grand Professionnel : effet de la règle." }
        };

        var texte = LireTextePdf(new PdfService().GenererFeuilleEquipe(
            equipe, false, null, null, false, coupsDePouce, stars));

        Assert.Contains("Coups de Pouce", texte);
        Assert.Contains("Pots-de-vin", texte);
        Assert.Contains("0-3", texte);

        Assert.Contains("Star Players disponibles", texte);
        // « Oberwald » et non « Griff Oberwald » : QuestPDF rend « ff » comme une
        // LIGATURE, que l'extracteur de texte restitue en \0 (« Bretteurs » sort
        // en « Bre\0eurs »). Vérifier le nom complet ferait échouer le test sur
        // un artefact d'extraction, pas sur un vrai défaut.
        Assert.Contains("Oberwald", texte);
        // Le texte complet de la règle doit figurer sur la feuille : c'est une
        // règle de jeu, le coach en a besoin à la table.
        // Portion SANS ligature (« effet » en contient une, comme « Griff ») :
        // sinon le test échouerait sur l'extraction, pas sur le PDF.
        Assert.Contains("de la règle", texte);
        Assert.Contains("Grand Pro", texte);
    }

    private static Team EquipeMinimale()
    {
        var tt = new TeamType { Nom = "Humains" };
        var poste = new PlayerPosition { Nom = "Trois-quart", TeamType = tt, Cout = 50_000 };
        var equipe = new Team { Nom = "Les Bretteurs", TeamType = tt };
        equipe.Joueurs.Add(new TeamPlayer
        {
            Nom = "Marcus", Numero = 1, PlayerPosition = poste
        });
        return equipe;
    }

    private static string LireTextePdf(byte[] pdf)
    {
        using var doc = UglyToad.PdfPig.PdfDocument.Open(pdf);
        return string.Join("\n", doc.GetPages().Select(pg => pg.Text));
    }
}
