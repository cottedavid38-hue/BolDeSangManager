using System.Text;
using BolDeSangManager.Data;
using BolDeSangManager.Data.Enums;
using BolDeSangManager.Data.Models;
using BolDeSangManager.Services;
using BolDeSangManager.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace BolDeSangManager.Tests;

public class LeagueExportServiceTests : IDisposable
{
    private readonly TestDbFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private LeagueExportService CreateService(ApplicationDbContext db) =>
        new(db, NullLogger<LeagueExportService>.Instance, new StaffService(db, NullLogger<StaffService>.Instance));

    // ─── Setup ────────────────────────────────────────────────────────────────

    private async Task<(League ligue, ApplicationUser commissaire)> SetupLigueAvecEquipesAsync()
    {
        await using var db = _factory.CreateContext();

        var commissaire = DataSeeder.CreateUser("expcomm");
        var coach1 = DataSeeder.CreateUser("expc1");
        var coach2 = DataSeeder.CreateUser("expc2");
        db.Users.AddRange(commissaire, coach1, coach2);
        await db.SaveChangesAsync();

        var (game, rv) = await DataSeeder.SeedGameAsync(db);
        var (teamType, position) = await DataSeeder.SeedTeamTypeAsync(db, game.Id);
        var ligue = await DataSeeder.SeedLeagueAsync(db, game.Id, rv.Id, commissaire.Id);

        var t1 = await DataSeeder.SeedTeamAsync(db, ligue.Id, coach1.Id, teamType.Id, "Équipe Alpha");
        var t2 = await DataSeeder.SeedTeamAsync(db, ligue.Id, coach2.Id, teamType.Id, "Équipe Bêta");

        await DataSeeder.SeedPlayerAsync(db, t1.Id, position.Id, "Gromag", 1);
        await DataSeeder.SeedPlayerAsync(db, t2.Id, position.Id, "Skulkar", 1);

        return (ligue, commissaire);
    }

    // ─── Staff configurable : aller-retour export → import ────────────────────

    [Fact]
    public async Task ExportImport_PreserveLeStaffPersonnaliseDeLAssociation()
    {
        // Oubli classique du projet : LeagueExportService ne connaissait pas le
        // staff. Les 5 staff standard survivaient via les colonnes historiques,
        // mais un staff créé par l'association (« Chef de bande ») était perdu.
        var (ligue, commissaire) = await SetupLigueAvecEquipesAsync();

        await using (var setup = _factory.CreateContext())
        {
            var staff = new StaffService(setup, NullLogger<StaffService>.Instance);

            // Staff maison, ajouté dans les RÈGLES puis recopié dans la ligue.
            var rvId = (await setup.Leagues.FindAsync(ligue.Id))!.RulesVersionId;
            await staff.AjouterStaffTypeAsync(new StaffDefinition
            {
                RulesVersionId = rvId, Nom = "Chef de bande",
                Description = "Relance un jet une fois par mi-temps.",
                Cout = 25_000, MinCreation = 0, MaxCreation = 2, MaxLigue = 3,
                Ordre = 60, EstActif = true
            });

            // Le seed de test ne crée pas les staff standard : on ajoute celui
            // qu'on veut comparer, pour distinguer « staff maison » et « standard ».
            await staff.AjouterStaffTypeAsync(new StaffDefinition
            {
                RulesVersionId = rvId, Nom = StaffService.NomFans,
                Cout = 10_000, MinCreation = 1, MaxCreation = 9, MaxLigue = null,
                Ordre = 10, EstActif = true
            });

            // La ligue de test a été semée avant : on lui recopie le staff.
            var anciens = await setup.LeagueStaffTypes
                .Where(l => l.LeagueId == ligue.Id).ToListAsync();
            setup.LeagueStaffTypes.RemoveRange(anciens);
            await setup.SaveChangesAsync();
            await staff.CopierVersLigueAsync(ligue.Id, rvId);

            // On en achète pour l'équipe Alpha.
            var alpha = await setup.Teams.FirstAsync(t => t.Nom == "Équipe Alpha");
            var chef = await setup.LeagueStaffTypes
                .FirstAsync(l => l.LeagueId == ligue.Id && l.Nom == "Chef de bande");
            var fans = await setup.LeagueStaffTypes
                .FirstAsync(l => l.LeagueId == ligue.Id && l.Nom == StaffService.NomFans);

            setup.TeamStaffs.Add(new TeamStaff { TeamId = alpha.Id, LeagueStaffTypeId = chef.Id, Quantite = 2 });
            setup.TeamStaffs.Add(new TeamStaff { TeamId = alpha.Id, LeagueStaffTypeId = fans.Id, Quantite = 4 });
            await setup.SaveChangesAsync();
        }

        byte[] bytes;
        await using (var dbExport = _factory.CreateContext())
            bytes = await CreateService(dbExport).ExportAsync(ligue.Id);

        var json = Encoding.UTF8.GetString(bytes);
        Assert.Contains("Chef de bande", json);

        League importee;
        await using (var dbImport = _factory.CreateContext())
        {
            using var flux = new MemoryStream(bytes);
            importee = await CreateService(dbImport).ImportAsync(flux, commissaire.Id);
        }

        await using var verif = _factory.CreateContext();

        // La ligue importée a bien son staff configuré…
        var staffLigue = await verif.LeagueStaffTypes
            .Where(l => l.LeagueId == importee.Id).ToListAsync();
        Assert.Contains(staffLigue, s => s.Nom == "Chef de bande");

        // …et l'équipe a retrouvé ses quantités, staff maison compris.
        var alphaImportee = await verif.Teams
            .FirstAsync(t => t.LeagueId == importee.Id && t.Nom == "Équipe Alpha");
        var quantites = await verif.TeamStaffs
            .Where(ts => ts.TeamId == alphaImportee.Id)
            .Join(verif.LeagueStaffTypes, ts => ts.LeagueStaffTypeId, l => l.Id,
                  (ts, l) => new { l.Nom, ts.Quantite })
            .ToListAsync();

        Assert.Equal(2, Assert.Single(quantites.Where(q => q.Nom == "Chef de bande")).Quantite);
        Assert.Equal(4, Assert.Single(quantites.Where(q => q.Nom == StaffService.NomFans)).Quantite);
    }

    [Fact]
    public async Task Import_JsonSansStaff_RetombeSurLesColonnesHistoriques()
    {
        // Rétrocompatibilité : un JSON exporté avant le staff configurable doit
        // rester importable, en reconstruisant le staff standard.
        var (ligue, commissaire) = await SetupLigueAvecEquipesAsync();

        await using (var setup = _factory.CreateContext())
        {
            // Le seed de test ne crée pas les staff standard : on les ajoute pour
            // que la ligue importée ait de quoi rattacher les colonnes historiques.
            var staff = new StaffService(setup, NullLogger<StaffService>.Instance);
            var rvId = (await setup.Leagues.FindAsync(ligue.Id))!.RulesVersionId;
            foreach (var (nom, ordre) in new[]
                     {
                         (StaffService.NomFans, 10), (StaffService.NomRelances, 20),
                         (StaffService.NomApothicaire, 50)
                     })
            {
                await staff.AjouterStaffTypeAsync(new StaffDefinition
                {
                    RulesVersionId = rvId, Nom = nom, Cout = 10_000,
                    MinCreation = 0, MaxCreation = 9, MaxLigue = null,
                    Ordre = ordre, EstActif = true
                });
            }

            var anciens = await setup.LeagueStaffTypes
                .Where(l => l.LeagueId == ligue.Id).ToListAsync();
            setup.LeagueStaffTypes.RemoveRange(anciens);
            await setup.SaveChangesAsync();
            await staff.CopierVersLigueAsync(ligue.Id, rvId);

            var alpha = await setup.Teams.FirstAsync(t => t.Nom == "Équipe Alpha");
            alpha.FansDevoues = 5;
            alpha.NombreRelances = 3;
            alpha.Apothicaire = true;
            await setup.SaveChangesAsync();
        }

        byte[] bytes;
        await using (var dbExport = _factory.CreateContext())
            bytes = await CreateService(dbExport).ExportAsync(ligue.Id);

        // On retire le bloc Staff du JSON pour simuler un ancien export.
        // La virgule qui précède part avec, sinon le JSON devient invalide.
        var json = Encoding.UTF8.GetString(bytes);
        var sansStaff = System.Text.RegularExpressions.Regex.Replace(
            json, ",\\s*\"staff\"\\s*:\\s*\\{[^{}]*\\}", "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        Assert.DoesNotContain("\"staff\"", sansStaff, StringComparison.OrdinalIgnoreCase);

        League importee;
        await using (var dbImport = _factory.CreateContext())
        {
            using var flux = new MemoryStream(Encoding.UTF8.GetBytes(sansStaff));
            importee = await CreateService(dbImport).ImportAsync(flux, commissaire.Id);
        }

        await using var verif = _factory.CreateContext();
        var alphaImportee = await verif.Teams
            .FirstAsync(t => t.LeagueId == importee.Id && t.Nom == "Équipe Alpha");
        var quantites = await verif.TeamStaffs
            .Where(ts => ts.TeamId == alphaImportee.Id)
            .Join(verif.LeagueStaffTypes, ts => ts.LeagueStaffTypeId, l => l.Id,
                  (ts, l) => new { l.Nom, ts.Quantite })
            .ToListAsync();

        Assert.Equal(5, Assert.Single(quantites.Where(q => q.Nom == StaffService.NomFans)).Quantite);
        Assert.Equal(3, Assert.Single(quantites.Where(q => q.Nom == StaffService.NomRelances)).Quantite);
        Assert.Equal(1, Assert.Single(quantites.Where(q => q.Nom == StaffService.NomApothicaire)).Quantite);
    }

    // ─── ExportAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Export_ProduitsOctets()
    {
        var (ligue, _) = await SetupLigueAvecEquipesAsync();
        await using var db = _factory.CreateContext();
        var svc = CreateService(db);

        var bytes = await svc.ExportAsync(ligue.Id);

        Assert.NotEmpty(bytes);
    }

    [Fact]
    public async Task Export_JsonContientNomLigue()
    {
        var (ligue, _) = await SetupLigueAvecEquipesAsync();
        await using var db = _factory.CreateContext();
        var svc = CreateService(db);

        var bytes = await svc.ExportAsync(ligue.Id);
        var json = Encoding.UTF8.GetString(bytes);

        Assert.Contains(ligue.Nom, json);
    }

    [Fact]
    public async Task Export_JsonContientNomsEquipes()
    {
        var (ligue, _) = await SetupLigueAvecEquipesAsync();
        await using var db = _factory.CreateContext();
        var svc = CreateService(db);

        var bytes = await svc.ExportAsync(ligue.Id);
        var json = Encoding.UTF8.GetString(bytes);

        Assert.Contains("Équipe Alpha", json);
        Assert.Contains("Équipe Bêta", json);
    }

    [Fact]
    public async Task Export_JsonContientNomsJoueurs()
    {
        var (ligue, _) = await SetupLigueAvecEquipesAsync();
        await using var db = _factory.CreateContext();
        var svc = CreateService(db);

        var bytes = await svc.ExportAsync(ligue.Id);
        var json = Encoding.UTF8.GetString(bytes);

        Assert.Contains("Gromag", json);
        Assert.Contains("Skulkar", json);
    }

    [Fact]
    public async Task Export_LigueInexistante_ThrowsException()
    {
        await using var db = _factory.CreateContext();
        var svc = CreateService(db);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.ExportAsync(99999));
    }

    // ─── ImportAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Import_RecreeLaLigue()
    {
        var (ligue, commissaire) = await SetupLigueAvecEquipesAsync();
        await using var db = _factory.CreateContext();
        var svc = CreateService(db);

        var bytes = await svc.ExportAsync(ligue.Id);
        var importedLigue = await svc.ImportAsync(new MemoryStream(bytes), commissaire.Id);

        Assert.NotNull(importedLigue);
        Assert.NotEqual(ligue.Id, importedLigue.Id);  // Nouvel ID
        Assert.Contains(ligue.Nom, importedLigue.Nom);
    }

    [Fact]
    public async Task Import_PreserveNombreEquipes()
    {
        var (ligue, commissaire) = await SetupLigueAvecEquipesAsync();
        await using var db = _factory.CreateContext();
        var svc = CreateService(db);

        var bytes = await svc.ExportAsync(ligue.Id);
        var importedLigue = await svc.ImportAsync(new MemoryStream(bytes), commissaire.Id);

        await using var db2 = _factory.CreateContext();
        var nbEquipes = await db2.Teams.CountAsync(t => t.LeagueId == importedLigue.Id);
        Assert.Equal(2, nbEquipes);
    }

    [Fact]
    public async Task Import_PreserveJoueurs()
    {
        var (ligue, commissaire) = await SetupLigueAvecEquipesAsync();
        await using var db = _factory.CreateContext();
        var svc = CreateService(db);

        var bytes = await svc.ExportAsync(ligue.Id);
        var importedLigue = await svc.ImportAsync(new MemoryStream(bytes), commissaire.Id);

        await using var db2 = _factory.CreateContext();
        var teamIds = await db2.Teams.Where(t => t.LeagueId == importedLigue.Id)
            .Select(t => t.Id).ToListAsync();
        var nbJoueurs = await db2.TeamPlayers.CountAsync(j => teamIds.Contains(j.TeamId));
        Assert.Equal(2, nbJoueurs);  // 1 joueur par équipe
    }

    [Fact]
    public async Task Import_StatutTermine()
    {
        var (ligue, commissaire) = await SetupLigueAvecEquipesAsync();
        await using var db = _factory.CreateContext();
        var svc = CreateService(db);

        var bytes = await svc.ExportAsync(ligue.Id);
        var importedLigue = await svc.ImportAsync(new MemoryStream(bytes), commissaire.Id);

        Assert.Equal(LeagueStatus.Termine, importedLigue.Statut);
    }

    [Fact]
    public async Task Import_JsonInvalide_ThrowsException()
    {
        await using var db = _factory.CreateContext();
        var svc = CreateService(db);

        var badJson = new MemoryStream(Encoding.UTF8.GetBytes("{ pas du json valide }"));

        await Assert.ThrowsAnyAsync<Exception>(() =>
            svc.ImportAsync(badJson, "any-id"));
    }

    [Fact]
    public async Task Import_JeuInconnu_ThrowsException()
    {
        await using var db = _factory.CreateContext();
        var svc = CreateService(db);

        // JSON valide mais référence un jeu inexistant
        var json = """
            {
              "version": "1.0",
              "exporteeLe": "2025-01-01T00:00:00Z",
              "nomLigue": "Fantôme",
              "description": "",
              "gameNom": "JeuInconnu",
              "rulesVersionNom": null,
              "format": "RoundRobinAvecPlayoffs",
              "budgetDepart": 1000000,
              "nombreEquipesPlayoff": 4,
              "equipes": [],
              "matchs": []
            }
            """;
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.ImportAsync(stream, "any-id"));
    }

    // ─── Cycle export → import (round-trip) ──────────────────────────────────

    // ─── Améliorations : aller-retour export → import ────────────────────────

    /// <summary>
    /// Donne 2 améliorations à Gromag : une compétence (résolue par nom) et une
    /// hausse de caractéristique.
    /// </summary>
    private async Task<(int skillId, string skillNom)> SeedAmeliorationsGromagAsync()
    {
        await using var setup = _factory.CreateContext();
        var (skill, _) = await DataSeeder.SeedSkillsAsync(setup);
        var gromag = await setup.TeamPlayers.FirstAsync(j => j.Nom == "Gromag");

        setup.PlayerImprovements.AddRange(
            new PlayerImprovement
            {
                TeamPlayerId = gromag.Id, Palier = 1,
                Type = ImprovementType.SelectionPrimaire,
                SkillId = skill.Id, PspDepensee = 6, ValeurHausse = 20_000
            },
            new PlayerImprovement
            {
                TeamPlayerId = gromag.Id, Palier = 2,
                Type = ImprovementType.AmeliorationCarac,
                StatAmelioree = AffectedStat.Mouvement,
                PspDepensee = 16, ValeurHausse = 20_000
            });
        await setup.SaveChangesAsync();
        return (skill.Id, skill.Nom);
    }

    [Fact]
    public async Task ExportImport_PreserveLesAmeliorationsDuJoueur()
    {
        // Bug historique : l'export ne portait qu'un COMPTEUR
        // (NombreAmeliorations) que l'import n'appliquait jamais — une ligue
        // réimportée perdait toute la progression de ses joueurs.
        var (ligue, commissaire) = await SetupLigueAvecEquipesAsync();
        var (_, skillNom) = await SeedAmeliorationsGromagAsync();

        byte[] bytes;
        await using (var dbExport = _factory.CreateContext())
            bytes = await CreateService(dbExport).ExportAsync(ligue.Id);

        // Le nom de la compétence doit voyager (pas son id, non portable).
        Assert.Contains(skillNom, Encoding.UTF8.GetString(bytes));

        League importee;
        await using (var dbImport = _factory.CreateContext())
        {
            using var flux = new MemoryStream(bytes);
            importee = await CreateService(dbImport).ImportAsync(flux, commissaire.Id);
        }

        await using var verif = _factory.CreateContext();
        var gromag = await verif.TeamPlayers
            .Include(j => j.Improvements).ThenInclude(i => i.Skill)
            .FirstAsync(j => j.Nom == "Gromag" && j.Team.LeagueId == importee.Id);

        Assert.Equal(2, gromag.Improvements.Count);

        var comp = Assert.Single(gromag.Improvements.Where(i => i.Type == ImprovementType.SelectionPrimaire));
        Assert.Equal(1, comp.Palier);
        Assert.Equal(6, comp.PspDepensee);
        Assert.Null(comp.StatAmelioree);

        var carac = Assert.Single(gromag.Improvements.Where(i => i.Type == ImprovementType.AmeliorationCarac));
        Assert.Equal(2, carac.Palier);
        Assert.Equal(AffectedStat.Mouvement, carac.StatAmelioree);
        Assert.Null(carac.SkillId);
    }

    [Fact]
    public async Task ExportImport_AmeliorationDeCompetence_RetrouveSaCompetenceParNom()
    {
        var (ligue, commissaire) = await SetupLigueAvecEquipesAsync();
        var (skillIdOrigine, skillNom) = await SeedAmeliorationsGromagAsync();

        byte[] bytes;
        await using (var dbExport = _factory.CreateContext())
            bytes = await CreateService(dbExport).ExportAsync(ligue.Id);

        League importee;
        await using (var dbImport = _factory.CreateContext())
        {
            using var flux = new MemoryStream(bytes);
            importee = await CreateService(dbImport).ImportAsync(flux, commissaire.Id);
        }

        await using var verif = _factory.CreateContext();
        var comp = await verif.PlayerImprovements
            .Include(i => i.Skill)
            .FirstAsync(i => i.TeamPlayer.Nom == "Gromag"
                             && i.TeamPlayer.Team.LeagueId == importee.Id
                             && i.SkillId != null);

        Assert.Equal(skillNom, comp.Skill!.Nom);
        // Même version de règles ici, donc le même id : ce qui compte est que la
        // résolution passe par le NOM (cf. test « compétence introuvable »).
        Assert.Equal(skillIdOrigine, comp.SkillId);
    }

    [Fact]
    public async Task Import_JsonSansAmeliorations_ResteImportable()
    {
        // Rétrocompatibilité : un export produit AVANT ce champ doit s'importer
        // sans erreur, le joueur se retrouvant simplement sans amélioration.
        var (ligue, commissaire) = await SetupLigueAvecEquipesAsync();
        await SeedAmeliorationsGromagAsync();

        byte[] bytes;
        await using (var dbExport = _factory.CreateContext())
            bytes = await CreateService(dbExport).ExportAsync(ligue.Id);

        // On retire le tableau « ameliorations » du JSON (et la virgule qui le
        // précède, sinon le document devient invalide).
        var json = Encoding.UTF8.GetString(bytes);
        var sansAmeliorations = System.Text.RegularExpressions.Regex.Replace(
            json, ",\\s*\"ameliorations\"\\s*:\\s*\\[[^\\[\\]]*\\]", "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        sansAmeliorations = System.Text.RegularExpressions.Regex.Replace(
            sansAmeliorations, ",\\s*\"ameliorations\"\\s*:\\s*\\[(?:[^\\[\\]]|\\[[^\\[\\]]*\\])*\\]", "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        Assert.DoesNotContain("\"ameliorations\"", sansAmeliorations, StringComparison.OrdinalIgnoreCase);

        League importee;
        await using (var dbImport = _factory.CreateContext())
        {
            using var flux = new MemoryStream(Encoding.UTF8.GetBytes(sansAmeliorations));
            importee = await CreateService(dbImport).ImportAsync(flux, commissaire.Id);
        }

        await using var verif = _factory.CreateContext();
        var gromag = await verif.TeamPlayers
            .Include(j => j.Improvements)
            .FirstAsync(j => j.Nom == "Gromag" && j.Team.LeagueId == importee.Id);
        Assert.Empty(gromag.Improvements);
    }

    [Fact]
    public async Task Import_CompetenceIntrouvable_NEchouePasEtGardeLAmelioration()
    {
        // Une compétence absente de la version d'accueil ne doit ni faire échouer
        // l'import, ni faire disparaître la ligne : SkillId reste null.
        var (ligue, commissaire) = await SetupLigueAvecEquipesAsync();
        var (_, skillNom) = await SeedAmeliorationsGromagAsync();

        byte[] bytes;
        await using (var dbExport = _factory.CreateContext())
            bytes = await CreateService(dbExport).ExportAsync(ligue.Id);

        var json = Encoding.UTF8.GetString(bytes)
            .Replace(skillNom, "Compétence D'Une Autre Édition");

        League importee;
        await using (var dbImport = _factory.CreateContext())
        {
            using var flux = new MemoryStream(Encoding.UTF8.GetBytes(json));
            importee = await CreateService(dbImport).ImportAsync(flux, commissaire.Id);
        }

        await using var verif = _factory.CreateContext();
        var gromag = await verif.TeamPlayers
            .Include(j => j.Improvements)
            .FirstAsync(j => j.Nom == "Gromag" && j.Team.LeagueId == importee.Id);

        Assert.Equal(2, gromag.Improvements.Count);
        var comp = Assert.Single(gromag.Improvements.Where(i => i.Type == ImprovementType.SelectionPrimaire));
        Assert.Null(comp.SkillId);
        Assert.Equal(6, comp.PspDepensee);
    }

    [Fact]
    public async Task ExportImport_PreserveStatsEquipes()
    {
        var (ligue, commissaire) = await SetupLigueAvecEquipesAsync();

        // Modifier les stats d'une équipe avant export
        await using var db = _factory.CreateContext();
        var equipe = await db.Teams.FirstAsync(t => t.LeagueId == ligue.Id);
        equipe.NombreVictoires = 5;
        equipe.PointsLigue = 15;
        equipe.TouchdownsMarques = 12;
        await db.SaveChangesAsync();

        var svc = CreateService(db);
        var bytes = await svc.ExportAsync(ligue.Id);
        var importedLigue = await svc.ImportAsync(new MemoryStream(bytes), commissaire.Id);

        await using var db2 = _factory.CreateContext();
        var importedEquipe = await db2.Teams
            .FirstAsync(t => t.LeagueId == importedLigue.Id && t.Nom == equipe.Nom);
        Assert.Equal(5, importedEquipe.NombreVictoires);
        Assert.Equal(15, importedEquipe.PointsLigue);
        Assert.Equal(12, importedEquipe.TouchdownsMarques);
    }
}
