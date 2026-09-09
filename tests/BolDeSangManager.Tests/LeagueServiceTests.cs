using BolDeSangManager.Data;
using BolDeSangManager.Data.Enums;
using BolDeSangManager.Data.Models;
using BolDeSangManager.Helpers;
using BolDeSangManager.Services;
using BolDeSangManager.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace BolDeSangManager.Tests;

public class LeagueServiceTests : IDisposable
{
    private readonly TestDbFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private LeagueService CreateService(ApplicationDbContext db) =>
        new(db, NullLogger<LeagueService>.Instance, new StubAuthorizationService(),
            new StaffService(db, NullLogger<StaffService>.Instance));

    // ─── Setup helpers ────────────────────────────────────────────────────────

    private async Task<(ApplicationUser commissaire, Game game, RulesVersion rv)> SetupAsync()
    {
        await using var db = _factory.CreateContext();
        var commissaire = DataSeeder.CreateUser("commissaire");
        db.Users.Add(commissaire);
        await db.SaveChangesAsync();
        var (game, rv) = await DataSeeder.SeedGameAsync(db);
        return (commissaire, game, rv);
    }

    // ─── CreerLigueAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task CreerLigue_SetStatutCreation()
    {
        var (commissaire, game, rv) = await SetupAsync();
        await using var db = _factory.CreateContext();
        var svc = CreateService(db);

        var ligue = new League { Nom = "Test", GameId = game.Id, RulesVersionId = rv.Id };
        var result = await svc.CreerLigueAsync(ligue, commissaire.Id);

        Assert.Equal(LeagueStatus.Creation, result.Statut);
    }

    [Fact]
    public async Task CreerLigue_SetCommissaireId()
    {
        var (commissaire, game, rv) = await SetupAsync();
        await using var db = _factory.CreateContext();
        var svc = CreateService(db);

        var ligue = new League { Nom = "Test", GameId = game.Id, RulesVersionId = rv.Id };
        var result = await svc.CreerLigueAsync(ligue, commissaire.Id);

        Assert.Equal(commissaire.Id, result.CommissaireId);
    }

    [Fact]
    public async Task CreerLigue_EstPersistee_EnBase()
    {
        var (commissaire, game, rv) = await SetupAsync();
        await using var db = _factory.CreateContext();
        var svc = CreateService(db);

        var ligue = new League { Nom = "Ligue Persistée", GameId = game.Id, RulesVersionId = rv.Id };
        await svc.CreerLigueAsync(ligue, commissaire.Id);

        await using var db2 = _factory.CreateContext();
        var found = await db2.Leagues.FirstOrDefaultAsync(l => l.Nom == "Ligue Persistée");
        Assert.NotNull(found);
    }

    // ─── DemarrerInscriptionsAsync ────────────────────────────────────────────

    [Fact]
    public async Task DemarrerInscriptions_PasseAuStatutInscription()
    {
        var (commissaire, game, rv) = await SetupAsync();
        await using var db = _factory.CreateContext();
        var svc = CreateService(db);

        var ligue = await DataSeeder.SeedLeagueAsync(db, game.Id, rv.Id, commissaire.Id);
        await svc.DemarrerInscriptionsAsync(ligue.Id);

        await using var db2 = _factory.CreateContext();
        var updated = await db2.Leagues.FindAsync(ligue.Id);
        Assert.Equal(LeagueStatus.Inscription, updated!.Statut);
    }

    [Fact]
    public async Task DemarrerInscriptions_LigueInexistante_ThrowsException()
    {
        await using var db = _factory.CreateContext();
        var svc = CreateService(db);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.DemarrerInscriptionsAsync(99999));
    }

    // ─── LancerSaisonAsync + génération round-robin ───────────────────────────

    [Fact]
    public async Task LancerSaison_MoinsDeDeuxEquipes_ThrowsException()
    {
        var (commissaire, game, rv) = await SetupAsync();
        await using var db = _factory.CreateContext();
        var (teamType, _) = await DataSeeder.SeedTeamTypeAsync(db, game.Id);
        var coach = DataSeeder.CreateUser("coach1");
        db.Users.Add(coach);
        await db.SaveChangesAsync();
        var svc = CreateService(db);

        var ligue = await DataSeeder.SeedLeagueAsync(db, game.Id, rv.Id, commissaire.Id);
        await DataSeeder.SeedTeamAsync(db, ligue.Id, coach.Id, teamType.Id, "Équipe A");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.LancerSaisonAsync(ligue.Id));
    }

    [Fact]
    public async Task LancerSaison_Avec2Equipes_GenereUnMatch()
    {
        var (commissaire, game, rv) = await SetupAsync();
        await using var db = _factory.CreateContext();
        var (teamType, _) = await DataSeeder.SeedTeamTypeAsync(db, game.Id);
        var coach1 = DataSeeder.CreateUser("c1");
        var coach2 = DataSeeder.CreateUser("c2");
        db.Users.AddRange(coach1, coach2);
        await db.SaveChangesAsync();
        var svc = CreateService(db);

        var ligue = await DataSeeder.SeedLeagueAsync(db, game.Id, rv.Id, commissaire.Id);
        await DataSeeder.SeedTeamAsync(db, ligue.Id, coach1.Id, teamType.Id, "Équipe A");
        await DataSeeder.SeedTeamAsync(db, ligue.Id, coach2.Id, teamType.Id, "Équipe B");

        await svc.LancerSaisonAsync(ligue.Id);

        await using var db2 = _factory.CreateContext();
        var divIds = await db2.Divisions.Where(d => d.LeagueId == ligue.Id).Select(d => d.Id).ToListAsync();
        var nbMatchs = await db2.Matches.CountAsync(m => divIds.Contains(m.DivisionId!.Value));
        Assert.Equal(1, nbMatchs);
    }

    [Fact]
    public async Task LancerSaison_Avec4Equipes_Genere6Matchs()
    {
        var (commissaire, game, rv) = await SetupAsync();
        await using var db = _factory.CreateContext();
        var (teamType, _) = await DataSeeder.SeedTeamTypeAsync(db, game.Id);
        var ligue = await DataSeeder.SeedLeagueAsync(db, game.Id, rv.Id, commissaire.Id);
        for (int i = 1; i <= 4; i++)
        {
            var c = DataSeeder.CreateUser($"rr4_{i}");
            db.Users.Add(c);
            await db.SaveChangesAsync();
            await DataSeeder.SeedTeamAsync(db, ligue.Id, c.Id, teamType.Id, $"T{i}");
        }
        var svc = CreateService(db);
        await svc.LancerSaisonAsync(ligue.Id);

        await using var db2 = _factory.CreateContext();
        var divIds = await db2.Divisions.Where(d => d.LeagueId == ligue.Id).Select(d => d.Id).ToListAsync();
        var nbMatchs = await db2.Matches.CountAsync(m => divIds.Contains(m.DivisionId!.Value));
        Assert.Equal(6, nbMatchs);  // 4*(4-1)/2 = 6
    }

    // ─── Nombre impair d'équipes ──────────────────────────────────────────────

    [Fact]
    public async Task LancerSaison_Avec3Equipes_Genere3Matchs()
    {
        var (commissaire, game, rv) = await SetupAsync();
        await using var db = _factory.CreateContext();
        var (teamType, _) = await DataSeeder.SeedTeamTypeAsync(db, game.Id);
        var ligue = await DataSeeder.SeedLeagueAsync(db, game.Id, rv.Id, commissaire.Id);
        for (int i = 1; i <= 3; i++)
        {
            var c = DataSeeder.CreateUser($"rr3_{i}");
            db.Users.Add(c);
            await db.SaveChangesAsync();
            await DataSeeder.SeedTeamAsync(db, ligue.Id, c.Id, teamType.Id, $"T{i}");
        }
        var svc = CreateService(db);
        await svc.LancerSaisonAsync(ligue.Id);

        await using var db2 = _factory.CreateContext();
        var divIds = await db2.Divisions.Where(d => d.LeagueId == ligue.Id).Select(d => d.Id).ToListAsync();
        var matchs = await db2.Matches.Where(m => divIds.Contains(m.DivisionId!.Value)).ToListAsync();

        // 3*(3-1)/2 = 3 matchs
        Assert.Equal(3, matchs.Count);
    }

    [Fact]
    public async Task LancerSaison_Avec3Equipes_ChaqueEquipeJoueContreLesDeuxAutres()
    {
        var (commissaire, game, rv) = await SetupAsync();
        await using var db = _factory.CreateContext();
        var (teamType, _) = await DataSeeder.SeedTeamTypeAsync(db, game.Id);
        var ligue = await DataSeeder.SeedLeagueAsync(db, game.Id, rv.Id, commissaire.Id);
        var ids = new List<int>();
        for (int i = 1; i <= 3; i++)
        {
            var c = DataSeeder.CreateUser($"rr3p_{i}");
            db.Users.Add(c);
            await db.SaveChangesAsync();
            var t = await DataSeeder.SeedTeamAsync(db, ligue.Id, c.Id, teamType.Id, $"T{i}");
            ids.Add(t.Id);
        }
        var svc = CreateService(db);
        await svc.LancerSaisonAsync(ligue.Id);

        await using var db2 = _factory.CreateContext();
        var divIds = await db2.Divisions.Where(d => d.LeagueId == ligue.Id).Select(d => d.Id).ToListAsync();
        var matchs = await db2.Matches.Where(m => divIds.Contains(m.DivisionId!.Value)).ToListAsync();

        // Toutes les paires (A,B), (A,C), (B,C) doivent apparaître exactement une fois
        var paires = matchs
            .Select(m => (Math.Min(m.EquipeDomicileId, m.EquipeExterieurId),
                          Math.Max(m.EquipeDomicileId, m.EquipeExterieurId)))
            .ToList();
        Assert.Equal(3, paires.Distinct().Count()); // 3 paires distinctes
        Assert.Equal(3, paires.Count);              // sans doublons

        // Chaque équipe joue exactement 2 fois
        foreach (var id in ids)
        {
            var nbMatchsEquipe = matchs.Count(m => m.EquipeDomicileId == id || m.EquipeExterieurId == id);
            Assert.Equal(2, nbMatchsEquipe);
        }
    }

    [Fact]
    public async Task LancerSaison_Avec3Equipes_Genere3Rondes()
    {
        var (commissaire, game, rv) = await SetupAsync();
        await using var db = _factory.CreateContext();
        var (teamType, _) = await DataSeeder.SeedTeamTypeAsync(db, game.Id);
        var ligue = await DataSeeder.SeedLeagueAsync(db, game.Id, rv.Id, commissaire.Id);
        for (int i = 1; i <= 3; i++)
        {
            var c = DataSeeder.CreateUser($"rr3r_{i}");
            db.Users.Add(c);
            await db.SaveChangesAsync();
            await DataSeeder.SeedTeamAsync(db, ligue.Id, c.Id, teamType.Id, $"T{i}");
        }
        var svc = CreateService(db);
        await svc.LancerSaisonAsync(ligue.Id);

        await using var db2 = _factory.CreateContext();
        var divIds = await db2.Divisions.Where(d => d.LeagueId == ligue.Id).Select(d => d.Id).ToListAsync();
        var rondes = await db2.Matches
            .Where(m => divIds.Contains(m.DivisionId!.Value))
            .Select(m => m.Ronde)
            .Distinct()
            .ToListAsync();

        // 3 rondes avec 1 match chacune (1 équipe a un bye par ronde)
        Assert.Equal(3, rondes.Count);
        Assert.Equal(1, rondes.Min()); // rondes numérotées à partir de 1
    }

    [Fact]
    public async Task LancerSaison_Avec5Equipes_Genere10Matchs()
    {
        var (commissaire, game, rv) = await SetupAsync();
        await using var db = _factory.CreateContext();
        var (teamType, _) = await DataSeeder.SeedTeamTypeAsync(db, game.Id);
        var ligue = await DataSeeder.SeedLeagueAsync(db, game.Id, rv.Id, commissaire.Id);
        for (int i = 1; i <= 5; i++)
        {
            var c = DataSeeder.CreateUser($"rr5_{i}");
            db.Users.Add(c);
            await db.SaveChangesAsync();
            await DataSeeder.SeedTeamAsync(db, ligue.Id, c.Id, teamType.Id, $"T{i}");
        }
        var svc = CreateService(db);
        await svc.LancerSaisonAsync(ligue.Id);

        await using var db2 = _factory.CreateContext();
        var divIds = await db2.Divisions.Where(d => d.LeagueId == ligue.Id).Select(d => d.Id).ToListAsync();
        var matchs = await db2.Matches.Where(m => divIds.Contains(m.DivisionId!.Value)).ToListAsync();

        // 5*(5-1)/2 = 10 matchs, aucun doublon
        Assert.Equal(10, matchs.Count);
        var paires = matchs
            .Select(m => (Math.Min(m.EquipeDomicileId, m.EquipeExterieurId),
                          Math.Max(m.EquipeDomicileId, m.EquipeExterieurId)))
            .ToList();
        Assert.Equal(10, paires.Distinct().Count());
    }

    [Fact]
    public async Task LancerSaison_PasseStatutEnCours()
    {
        var (commissaire, game, rv) = await SetupAsync();
        await using var db = _factory.CreateContext();
        var (teamType, _) = await DataSeeder.SeedTeamTypeAsync(db, game.Id);
        var coach1 = DataSeeder.CreateUser("lc1");
        var coach2 = DataSeeder.CreateUser("lc2");
        db.Users.AddRange(coach1, coach2);
        await db.SaveChangesAsync();
        var ligue = await DataSeeder.SeedLeagueAsync(db, game.Id, rv.Id, commissaire.Id);
        await DataSeeder.SeedTeamAsync(db, ligue.Id, coach1.Id, teamType.Id, "A");
        await DataSeeder.SeedTeamAsync(db, ligue.Id, coach2.Id, teamType.Id, "B");
        var svc = CreateService(db);

        await svc.LancerSaisonAsync(ligue.Id);

        await using var db2 = _factory.CreateContext();
        var updated = await db2.Leagues.FindAsync(ligue.Id);
        Assert.Equal(LeagueStatus.EnCours, updated!.Statut);
    }

    // ─── GetVersionsAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetVersionsAsync_RetourneToutesLesVersions_TrieesParOrdre()
    {
        await using var db = _factory.CreateContext();
        var game = new Game { Nom = "TestGame", Type = GameType.BloodBowl };
        db.Games.Add(game);
        await db.SaveChangesAsync();
        db.RulesVersions.AddRange(
            new RulesVersion { GameId = game.Id, Nom = "V3", EstActive = true, Ordre = 3 },
            new RulesVersion { GameId = game.Id, Nom = "V1", EstActive = false, Ordre = 1 },
            new RulesVersion { GameId = game.Id, Nom = "V2", EstActive = false, Ordre = 2 }
        );
        await db.SaveChangesAsync();
        var svc = CreateService(db);

        var versions = await svc.GetVersionsAsync(game.Id);

        Assert.Equal(3, versions.Count);
        Assert.Equal("V1", versions[0].Nom);
        Assert.Equal("V2", versions[1].Nom);
        Assert.Equal("V3", versions[2].Nom);
    }

    [Fact]
    public async Task GetVersionsAsync_IncludeVersionsInactives()
    {
        await using var db = _factory.CreateContext();
        var game = new Game { Nom = "GameV", Type = GameType.BloodBowl };
        db.Games.Add(game);
        await db.SaveChangesAsync();
        db.RulesVersions.AddRange(
            new RulesVersion { GameId = game.Id, Nom = "Ancienne", EstActive = false, Ordre = 1 },
            new RulesVersion { GameId = game.Id, Nom = "Actuelle", EstActive = true, Ordre = 2 }
        );
        await db.SaveChangesAsync();
        var svc = CreateService(db);

        var versions = await svc.GetVersionsAsync(game.Id);

        Assert.Equal(2, versions.Count);
        Assert.Contains(versions, v => v.EstActive == false);
    }

    // ─── EstCommissaireAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task EstCommissaire_RetourneVraiPourLeCommissaire()
    {
        var (commissaire, game, rv) = await SetupAsync();
        await using var db = _factory.CreateContext();
        var ligue = await DataSeeder.SeedLeagueAsync(db, game.Id, rv.Id, commissaire.Id);
        // Stub configured to grant access for the commissaire on this specific league
        var stub = new StubAuthorizationService(peutGerer: (commissaire.Id, ligue.Id));
        var svc = new LeagueService(db, NullLogger<LeagueService>.Instance, stub, new StaffService(db, NullLogger<StaffService>.Instance));

        var result = await svc.EstCommissaireAsync(ligue.Id, commissaire.Id);

        Assert.True(result);
    }

    [Fact]
    public async Task EstCommissaire_RetourneFauxPourAutreUtilisateur()
    {
        var (commissaire, game, rv) = await SetupAsync();
        await using var db = _factory.CreateContext();
        var ligue = await DataSeeder.SeedLeagueAsync(db, game.Id, rv.Id, commissaire.Id);
        // Default stub returns false for all — "autre-id" has no access
        var svc = CreateService(db);

        var result = await svc.EstCommissaireAsync(ligue.Id, "autre-id");

        Assert.False(result);
    }

    // ─── LancerPhaseDeReposAsync ──────────────────────────────────────────────

    [Fact]
    public async Task LancerPhaseDeRepos_ChangeStatutEtResetRPM()
    {
        await using var db = _factory.CreateContext();
        var (game, version) = await DataSeeder.SeedGameAsync(db);
        var (teamType, position) = await DataSeeder.SeedTeamTypeAsync(db, game.Id);
        var commissaire = DataSeeder.CreateUser("c");
        var coach = DataSeeder.CreateUser("co");
        db.Users.AddRange(commissaire, coach);
        await db.SaveChangesAsync();

        var ligue = await DataSeeder.SeedLeagueAsync(db, game.Id, version.Id, commissaire.Id);
        ligue.Statut = LeagueStatus.EnCours;
        var equipe = await DataSeeder.SeedTeamAsync(db, ligue.Id, coach.Id, teamType.Id, "Test");
        var j1 = new TeamPlayer { TeamId = equipe.Id, PlayerPositionId = position.Id, Nom = "j1", Numero = 1, ManqueSuivantMatch = true };
        var j2 = new TeamPlayer { TeamId = equipe.Id, PlayerPositionId = position.Id, Nom = "j2", Numero = 2, ManqueSuivantMatch = true };
        db.TeamPlayers.AddRange(j1, j2);
        await db.SaveChangesAsync();

        var service = new LeagueService(db, NullLogger<LeagueService>.Instance, new StubAuthorizationService(), new StaffService(db, NullLogger<StaffService>.Instance));
        await service.LancerPhaseDeReposAsync(ligue.Id);

        var maj = await db.Leagues.FindAsync(ligue.Id);
        var joueurs = await db.TeamPlayers.Where(j => j.TeamId == equipe.Id).ToListAsync();

        Assert.Equal(LeagueStatus.PhaseDeRepos, maj!.Statut);
        Assert.All(joueurs, j => Assert.False(j.ManqueSuivantMatch));
    }

    // ─── ValiderApresMatchReposAsync ─────────────────────────────────────────

    [Fact]
    public async Task ValiderApresMatchRepos_CreeValidationEtAppliqueAchats()
    {
        await using var db = _factory.CreateContext();
        var (game, version) = await DataSeeder.SeedGameAsync(db);
        var (teamType, position) = await DataSeeder.SeedTeamTypeAsync(db, game.Id);
        var commissaire = DataSeeder.CreateUser("c");
        var coach = DataSeeder.CreateUser("co");
        db.Users.AddRange(commissaire, coach);
        await db.SaveChangesAsync();

        var ligue = await DataSeeder.SeedLeagueAsync(db, game.Id, version.Id, commissaire.Id);
        ligue.Statut = LeagueStatus.PhaseDeRepos;
        var equipe = await DataSeeder.SeedTeamAsync(db, ligue.Id, coach.Id, teamType.Id, "Test");
        equipe.Tresorerie = 200_000;
        equipe.NombreRelances = 0;
        var joueur = new TeamPlayer
        {
            TeamId = equipe.Id,
            PlayerPositionId = position.Id,
            Nom = "J1", Numero = 1, PointsStarPlayer = 6
        };
        db.TeamPlayers.Add(joueur);
        var vId = (await db.RulesVersions.FirstAsync()).Id;
        var catId = await DataSeeder.GetOrCreateCategorieAsync(db, vId);
        var skill = new Skill { Nom = "Blocage", Categorie = SkillCategory.Generale, SkillCategoryDefId = catId, RulesVersionId = vId };
        db.Skills.Add(skill);
        await db.SaveChangesAsync();

        var teamService = new TeamService(db, NullLogger<TeamService>.Instance);
        var service = new LeagueService(db, NullLogger<LeagueService>.Instance, new StubAuthorizationService(), new StaffService(db, NullLogger<StaffService>.Instance));

        await service.ValiderApresMatchReposAsync(
            ligueId: ligue.Id,
            teamId: equipe.Id,
            competences: [(joueur.Id, skill.Id, estPrincipale: true, pspDepensee: 6)],
            nouveauxJoueurs: [],
            nouvellesRelances: 1,
            teamService: teamService);

        var validation = await db.PhaseDeReposValidations.FirstOrDefaultAsync(v => v.LeagueId == ligue.Id && v.TeamId == equipe.Id);
        Assert.NotNull(validation);

        var equipeMaj = await db.Teams.FindAsync(equipe.Id);
        Assert.Equal(1, equipeMaj!.NombreRelances);
        Assert.Equal(200_000 - 100_000, equipeMaj.Tresorerie);

        var jMaj = await db.TeamPlayers.Include(j => j.Improvements).FirstAsync(j => j.Id == joueur.Id);
        Assert.Single(jMaj.Improvements);
    }

    [Fact]
    public async Task ValiderApresMatchRepos_DejaValide_LeveException()
    {
        await using var db = _factory.CreateContext();
        var (game, version) = await DataSeeder.SeedGameAsync(db);
        var (teamType, _) = await DataSeeder.SeedTeamTypeAsync(db, game.Id);
        var commissaire = DataSeeder.CreateUser("c2");
        var coach = DataSeeder.CreateUser("co2");
        db.Users.AddRange(commissaire, coach);
        await db.SaveChangesAsync();
        var ligue = await DataSeeder.SeedLeagueAsync(db, game.Id, version.Id, commissaire.Id);
        ligue.Statut = LeagueStatus.PhaseDeRepos;
        var equipe = await DataSeeder.SeedTeamAsync(db, ligue.Id, coach.Id, teamType.Id, "Test2");

        db.PhaseDeReposValidations.Add(new PhaseDeReposValidation { LeagueId = ligue.Id, TeamId = equipe.Id });
        await db.SaveChangesAsync();

        var teamService = new TeamService(db, NullLogger<TeamService>.Instance);
        var service = new LeagueService(db, NullLogger<LeagueService>.Instance, new StubAuthorizationService(), new StaffService(db, NullLogger<StaffService>.Instance));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ValiderApresMatchReposAsync(ligue.Id, equipe.Id, [], [], 0, teamService));
    }

    [Fact]
    public async Task ValiderApresMatchRepos_LigueHorsPhase_LeveException()
    {
        await using var db = _factory.CreateContext();
        var (game, version) = await DataSeeder.SeedGameAsync(db);
        var (teamType, _) = await DataSeeder.SeedTeamTypeAsync(db, game.Id);
        var commissaire = DataSeeder.CreateUser("c3");
        var coach = DataSeeder.CreateUser("co3");
        db.Users.AddRange(commissaire, coach);
        await db.SaveChangesAsync();
        var ligue = await DataSeeder.SeedLeagueAsync(db, game.Id, version.Id, commissaire.Id);
        ligue.Statut = LeagueStatus.EnCours; // pas en repos
        var equipe = await DataSeeder.SeedTeamAsync(db, ligue.Id, coach.Id, teamType.Id, "Test3");
        await db.SaveChangesAsync();

        var teamService = new TeamService(db, NullLogger<TeamService>.Instance);
        var service = new LeagueService(db, NullLogger<LeagueService>.Instance, new StubAuthorizationService(), new StaffService(db, NullLogger<StaffService>.Instance));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ValiderApresMatchReposAsync(ligue.Id, equipe.Id, [], [], 0, teamService));
    }

    // ─── GetTop*Async ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetTopJoueursParPsp_RetourneJoueursDeLaLigueOrdonnesParPSP()
    {
        await using var db = _factory.CreateContext();
        var (game, version) = await DataSeeder.SeedGameAsync(db);
        var (teamType, position) = await DataSeeder.SeedTeamTypeAsync(db, game.Id);
        var commissaire = DataSeeder.CreateUser("c");
        var coach = DataSeeder.CreateUser("co");
        db.Users.AddRange(commissaire, coach);
        await db.SaveChangesAsync();
        var ligue = await DataSeeder.SeedLeagueAsync(db, game.Id, version.Id, commissaire.Id);
        var equipe = await DataSeeder.SeedTeamAsync(db, ligue.Id, coach.Id, teamType.Id, "T");

        db.TeamPlayers.AddRange(
            new TeamPlayer { TeamId = equipe.Id, PlayerPositionId = position.Id, Nom = "Bas", Numero = 1, PointsStarPlayer = 3 },
            new TeamPlayer { TeamId = equipe.Id, PlayerPositionId = position.Id, Nom = "Haut", Numero = 2, PointsStarPlayer = 25 },
            new TeamPlayer { TeamId = equipe.Id, PlayerPositionId = position.Id, Nom = "Moyen", Numero = 3, PointsStarPlayer = 10 }
        );
        await db.SaveChangesAsync();

        var service = new LeagueService(db, NullLogger<LeagueService>.Instance, new StubAuthorizationService(), new StaffService(db, NullLogger<StaffService>.Instance));
        var top = await service.GetTopJoueursParPspAsync(ligue.Id, limit: 2);

        Assert.Equal(2, top.Count);
        Assert.Equal("Haut", top[0].Nom);
        Assert.Equal("Moyen", top[1].Nom);
    }

    // ─── AttribuerAwardAsync + GetAwardsAsync ─────────────────────────────────

    [Fact]
    public async Task AttribuerAward_CreeLeagueAward()
    {
        await using var db = _factory.CreateContext();
        var (game, version) = await DataSeeder.SeedGameAsync(db);
        var (teamType, position) = await DataSeeder.SeedTeamTypeAsync(db, game.Id);
        var commissaire = DataSeeder.CreateUser("c");
        var coach = DataSeeder.CreateUser("co");
        db.Users.AddRange(commissaire, coach);
        await db.SaveChangesAsync();
        var ligue = await DataSeeder.SeedLeagueAsync(db, game.Id, version.Id, commissaire.Id);
        var equipe = await DataSeeder.SeedTeamAsync(db, ligue.Id, coach.Id, teamType.Id, "T");
        var joueur = new TeamPlayer { TeamId = equipe.Id, PlayerPositionId = position.Id, Nom = "Star", Numero = 1, PointsStarPlayer = 50 };
        db.TeamPlayers.Add(joueur);
        await db.SaveChangesAsync();

        var service = new LeagueService(db, NullLogger<LeagueService>.Instance, new StubAuthorizationService(), new StaffService(db, NullLogger<StaffService>.Instance));
        await service.AttribuerAwardAsync(ligue.Id, AwardType.MVP, teamPlayerId: joueur.Id);

        var awards = await service.GetAwardsAsync(ligue.Id);
        Assert.Single(awards);
        Assert.Equal(AwardType.MVP, awards[0].Type);
        Assert.Equal(joueur.Id, awards[0].TeamPlayerId);
    }

    // ─── SupprimerLigueAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task SupprimerLigue_EffaceToutesLesDonnees()
    {
        var (commissaire, game, rv) = await SetupAsync();
        await using var db = _factory.CreateContext();
        var (teamType, position) = await DataSeeder.SeedTeamTypeAsync(db, game.Id);
        var coach1 = DataSeeder.CreateUser("sd1");
        var coach2 = DataSeeder.CreateUser("sd2");
        db.Users.AddRange(coach1, coach2);
        await db.SaveChangesAsync();

        var ligue = await DataSeeder.SeedLeagueAsync(db, game.Id, rv.Id, commissaire.Id);
        var t1 = await DataSeeder.SeedTeamAsync(db, ligue.Id, coach1.Id, teamType.Id, "S1");
        var t2 = await DataSeeder.SeedTeamAsync(db, ligue.Id, coach2.Id, teamType.Id, "S2");
        await DataSeeder.SeedPlayerAsync(db, t1.Id, position.Id, "Joueur X");

        var svc = CreateService(db);
        await svc.SupprimerLigueAsync(ligue.Id);

        await using var db2 = _factory.CreateContext();
        Assert.False(await db2.Leagues.AnyAsync(l => l.Id == ligue.Id));
        Assert.False(await db2.Teams.AnyAsync(t => t.LeagueId == ligue.Id));
        Assert.False(await db2.TeamPlayers.AnyAsync(j => j.TeamId == t1.Id));
    }

    // ─── StubAuthorizationService ─────────────────────────────────────────────

    // ─── Format Libre : calendrier composé par le commissaire ─────────────────

    /// <summary>Crée une ligue au format Libre avec N équipes, et la lance.</summary>
    private async Task<(int ligueId, List<int> equipeIds)> SetupLigueLibreAsync(
        int nbEquipes, LeagueFormat format = LeagueFormat.Libre, bool lancer = true)
    {
        await using var db = _factory.CreateContext();

        // commissaire unique : ce helper peut être appelé plusieurs fois dans
        // un même test (AspNetUsers.NormalizedUserName est unique).
        var commissaire = DataSeeder.CreateUser($"comlibre_{Guid.NewGuid():N}");
        db.Users.Add(commissaire);
        await db.SaveChangesAsync();
        var (game, rv) = await DataSeeder.SeedGameAsync(db);

        var (teamType, _) = await DataSeeder.SeedTeamTypeAsync(db, game.Id);
        var ligue = await DataSeeder.SeedLeagueAsync(db, game.Id, rv.Id, commissaire.Id,
            format: format);

        var equipeIds = new List<int>();
        for (int i = 1; i <= nbEquipes; i++)
        {
            var c = DataSeeder.CreateUser($"libre_{Guid.NewGuid():N}");
            db.Users.Add(c);
            await db.SaveChangesAsync();
            var t = await DataSeeder.SeedTeamAsync(db, ligue.Id, c.Id, teamType.Id, $"Équipe {i}");
            equipeIds.Add(t.Id);
        }

        if (lancer)
        {
            var svc = CreateService(db);
            await svc.LancerSaisonAsync(ligue.Id);
        }
        return (ligue.Id, equipeIds);
    }

    private async Task<int> CompterMatchsAsync(int ligueId)
    {
        await using var db = _factory.CreateContext();
        var divIds = await db.Divisions.Where(d => d.LeagueId == ligueId).Select(d => d.Id).ToListAsync();
        return await db.Matches.CountAsync(m => divIds.Contains(m.DivisionId!.Value));
    }

    [Fact]
    public async Task LancerSaison_FormatLibre_NeGenereAucunMatch()
    {
        var (ligueId, _) = await SetupLigueLibreAsync(4);

        await using var db = _factory.CreateContext();
        Assert.Equal(LeagueStatus.EnCours, (await db.Leagues.FindAsync(ligueId))!.Statut);
        Assert.Equal(0, await CompterMatchsAsync(ligueId));
    }

    [Fact]
    public async Task LancerSaison_FormatLibre_CreeQuandMemeLaDivision()
    {
        // le commissaire a besoin d'une division pour y rattacher ses matchs
        var (ligueId, _) = await SetupLigueLibreAsync(4);

        await using var db = _factory.CreateContext();
        Assert.Single(await db.Divisions.Where(d => d.LeagueId == ligueId).ToListAsync());
    }

    [Fact]
    public async Task DefinirRonde_CreeLesRencontresDemandees()
    {
        var (ligueId, ids) = await SetupLigueLibreAsync(4);

        await using var db = _factory.CreateContext();
        var svc = CreateService(db);
        await svc.DefinirRondeAsync(ligueId, 1, [(ids[0], ids[1]), (ids[2], ids[3])]);

        await using var db2 = _factory.CreateContext();
        var matchs = await db2.Matches
            .Where(m => m.Division!.LeagueId == ligueId && m.Ronde == 1)
            .ToListAsync();
        Assert.Equal(2, matchs.Count);
        Assert.All(matchs, m => Assert.Equal(MatchStatus.Programme, m.Statut));
        Assert.All(matchs, m => Assert.False(m.EstPlayoff));
    }

    [Fact]
    public async Task DefinirRonde_RefuseSurUneLigueNonLibre()
    {
        var (commissaire, game, rv) = await SetupAsync();
        await using var db = _factory.CreateContext();
        var (teamType, _) = await DataSeeder.SeedTeamTypeAsync(db, game.Id);
        var ligue = await DataSeeder.SeedLeagueAsync(db, game.Id, rv.Id, commissaire.Id);
        var c1 = DataSeeder.CreateUser("nl1"); var c2 = DataSeeder.CreateUser("nl2");
        db.Users.AddRange(c1, c2); await db.SaveChangesAsync();
        var t1 = await DataSeeder.SeedTeamAsync(db, ligue.Id, c1.Id, teamType.Id, "A");
        var t2 = await DataSeeder.SeedTeamAsync(db, ligue.Id, c2.Id, teamType.Id, "B");

        var svc = CreateService(db);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.DefinirRondeAsync(ligue.Id, 1, [(t1.Id, t2.Id)]));
    }

    [Fact]
    public async Task DefinirRonde_RefuseUneEquipeDeuxFoisDansLaMemeRonde()
    {
        var (ligueId, ids) = await SetupLigueLibreAsync(4);

        await using var db = _factory.CreateContext();
        var svc = CreateService(db);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.DefinirRondeAsync(ligueId, 1, [(ids[0], ids[1]), (ids[0], ids[2])]));
        Assert.Contains("deux fois", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DefinirRonde_RefuseUneEquipeContreEllememe()
    {
        var (ligueId, ids) = await SetupLigueLibreAsync(4);

        await using var db = _factory.CreateContext();
        var svc = CreateService(db);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.DefinirRondeAsync(ligueId, 1, [(ids[0], ids[0])]));
    }

    [Fact]
    public async Task DefinirRonde_RefuseUneEquipeDuneAutreLigue()
    {
        var (ligueId, ids) = await SetupLigueLibreAsync(2);
        var (_, autresIds) = await SetupLigueLibreAsync(2);

        await using var db = _factory.CreateContext();
        var svc = CreateService(db);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.DefinirRondeAsync(ligueId, 1, [(ids[0], autresIds[0])]));
    }

    [Fact]
    public async Task DefinirRonde_RefuseUnNumeroDeRondeInvalide()
    {
        var (ligueId, ids) = await SetupLigueLibreAsync(2);

        await using var db = _factory.CreateContext();
        var svc = CreateService(db);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.DefinirRondeAsync(ligueId, 0, [(ids[0], ids[1])]));
    }

    [Fact]
    public async Task DefinirRonde_AutoriseLeRepos_UneEquipeNonCitee()
    {
        // 3 équipes : une seule rencontre, la troisième se repose
        var (ligueId, ids) = await SetupLigueLibreAsync(3);

        await using var db = _factory.CreateContext();
        var svc = CreateService(db);
        await svc.DefinirRondeAsync(ligueId, 1, [(ids[0], ids[1])]);

        Assert.Equal(1, await CompterMatchsAsync(ligueId));
    }

    [Fact]
    public async Task DefinirRonde_AutoriseLaMemePaireDansUneAutreRonde()
    {
        // organisation entièrement libre : les matchs aller-retour sont permis
        var (ligueId, ids) = await SetupLigueLibreAsync(2);

        await using var db = _factory.CreateContext();
        var svc = CreateService(db);
        await svc.DefinirRondeAsync(ligueId, 1, [(ids[0], ids[1])]);
        await svc.DefinirRondeAsync(ligueId, 2, [(ids[1], ids[0])]);   // retour

        Assert.Equal(2, await CompterMatchsAsync(ligueId));
    }

    [Fact]
    public async Task DefinirRonde_RedefinirUneRondeNonJouee_RemplaceLesMatchs()
    {
        var (ligueId, ids) = await SetupLigueLibreAsync(4);

        await using var db = _factory.CreateContext();
        var svc = CreateService(db);
        await svc.DefinirRondeAsync(ligueId, 1, [(ids[0], ids[1])]);
        await svc.DefinirRondeAsync(ligueId, 1, [(ids[2], ids[3]), (ids[0], ids[1])]);

        Assert.Equal(2, await CompterMatchsAsync(ligueId));
    }

    [Fact]
    public async Task DefinirRonde_RefuseDeModifierUneRondeDejaJouee()
    {
        var (ligueId, ids) = await SetupLigueLibreAsync(4);

        await using (var db = _factory.CreateContext())
        {
            var svc = CreateService(db);
            await svc.DefinirRondeAsync(ligueId, 1, [(ids[0], ids[1])]);
        }

        // le match est joué
        await using (var db = _factory.CreateContext())
        {
            var m = await db.Matches.FirstAsync(x => x.Ronde == 1);
            m.Statut = MatchStatus.Termine;
            m.ScoreDomicile = 2; m.ScoreExterieur = 1;
            await db.SaveChangesAsync();
        }

        await using (var db = _factory.CreateContext())
        {
            var svc = CreateService(db);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => svc.DefinirRondeAsync(ligueId, 1, [(ids[2], ids[3])]));
            Assert.Contains("déjà", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task SupprimerRonde_RetireLesMatchsNonJoues()
    {
        var (ligueId, ids) = await SetupLigueLibreAsync(4);

        await using var db = _factory.CreateContext();
        var svc = CreateService(db);
        await svc.DefinirRondeAsync(ligueId, 1, [(ids[0], ids[1])]);
        await svc.DefinirRondeAsync(ligueId, 2, [(ids[2], ids[3])]);

        await svc.SupprimerRondeAsync(ligueId, 2);

        Assert.Equal(1, await CompterMatchsAsync(ligueId));
    }

    [Fact]
    public async Task SupprimerRonde_RefuseSiUnMatchEstJoue()
    {
        var (ligueId, ids) = await SetupLigueLibreAsync(2);

        await using (var db = _factory.CreateContext())
        {
            var svc = CreateService(db);
            await svc.DefinirRondeAsync(ligueId, 1, [(ids[0], ids[1])]);
        }
        await using (var db = _factory.CreateContext())
        {
            var m = await db.Matches.FirstAsync();
            m.Statut = MatchStatus.Termine;
            await db.SaveChangesAsync();
        }

        await using (var db = _factory.CreateContext())
        {
            var svc = CreateService(db);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => svc.SupprimerRondeAsync(ligueId, 1));
        }
    }

    [Fact]
    public async Task LancerSaison_RoundRobin_GenereToujoursLeCalendrier()
    {
        // non-régression : les formats existants ne changent pas
        var (commissaire, game, rv) = await SetupAsync();
        await using var db = _factory.CreateContext();
        var (teamType, _) = await DataSeeder.SeedTeamTypeAsync(db, game.Id);
        var ligue = await DataSeeder.SeedLeagueAsync(db, game.Id, rv.Id, commissaire.Id,
            format: LeagueFormat.RoundRobin);
        for (int i = 1; i <= 4; i++)
        {
            var c = DataSeeder.CreateUser($"nonreg_{i}");
            db.Users.Add(c); await db.SaveChangesAsync();
            await DataSeeder.SeedTeamAsync(db, ligue.Id, c.Id, teamType.Id, $"T{i}");
        }
        var svc = CreateService(db);
        await svc.LancerSaisonAsync(ligue.Id);

        Assert.Equal(6, await CompterMatchsAsync(ligue.Id));
    }

    [Fact]
    public void DisplayHelpers_ConnaitLesFormatsLibres()
    {
        Assert.Equal("Libre", DisplayHelpers.LeagueFormatLabel(LeagueFormat.Libre));
        Assert.Equal("Libre + Play-offs", DisplayHelpers.LeagueFormatLabel(LeagueFormat.LibreAvecPlayoffs));

        Assert.True(DisplayHelpers.EstFormatLibre(LeagueFormat.Libre));
        Assert.True(DisplayHelpers.EstFormatLibre(LeagueFormat.LibreAvecPlayoffs));
        Assert.False(DisplayHelpers.EstFormatLibre(LeagueFormat.RoundRobin));

        Assert.True(DisplayHelpers.AvecPlayoffs(LeagueFormat.LibreAvecPlayoffs));
        Assert.True(DisplayHelpers.AvecPlayoffs(LeagueFormat.RoundRobinAvecPlayoffs));
        Assert.False(DisplayHelpers.AvecPlayoffs(LeagueFormat.Libre));
    }

    [Fact]
    public void LeagueFormat_ValeursPersistees_NeDoiventPasBouger()
    {
        // ces entiers sont stockés en base : les réordonner casserait les ligues
        Assert.Equal(0, (int)LeagueFormat.RoundRobin);
        Assert.Equal(1, (int)LeagueFormat.RoundRobinAvecPlayoffs);
        Assert.Equal(2, (int)LeagueFormat.Libre);
        Assert.Equal(3, (int)LeagueFormat.LibreAvecPlayoffs);
    }

    [Fact]
    public async Task SupprimerRonde_NeSupprimeQueLaRondeVisee()
    {
        // Bug signalé : supprimer la dernière ronde effaçait tout le calendrier.
        var (ligueId, ids) = await SetupLigueLibreAsync(4);

        await using var db = _factory.CreateContext();
        var svc = CreateService(db);
        await svc.DefinirRondeAsync(ligueId, 1, [(ids[0], ids[1]), (ids[2], ids[3])]);
        await svc.DefinirRondeAsync(ligueId, 2, [(ids[0], ids[2]), (ids[1], ids[3])]);
        await svc.DefinirRondeAsync(ligueId, 3, [(ids[0], ids[3]), (ids[1], ids[2])]);
        Assert.Equal(6, await CompterMatchsAsync(ligueId));

        await svc.SupprimerRondeAsync(ligueId, 3);   // la dernière

        // les rondes 1 et 2 doivent rester intactes
        await using var db2 = _factory.CreateContext();
        var restants = await db2.Matches
            .Where(m => m.Division!.LeagueId == ligueId)
            .Select(m => m.Ronde)
            .ToListAsync();
        Assert.Equal(4, restants.Count);
        Assert.Equal([1, 1, 2, 2], restants.OrderBy(r => r).ToList());
    }

    [Fact]
    public async Task DefinirEcheanceRonde_EnregistreEtModifieLaDate()
    {
        var (ligueId, ids) = await SetupLigueLibreAsync(2);

        await using var db = _factory.CreateContext();
        var svc = CreateService(db);
        await svc.DefinirRondeAsync(ligueId, 1, [(ids[0], ids[1])]);

        var date = new DateTime(2026, 9, 15, 0, 0, 0, DateTimeKind.Utc);
        await svc.DefinirEcheanceRondeAsync(ligueId, 1, date);
        Assert.Equal(date.Date, (await svc.GetEcheancesRondesAsync(ligueId))[1].Date);

        var nouvelle = new DateTime(2026, 9, 22, 0, 0, 0, DateTimeKind.Utc);
        await svc.DefinirEcheanceRondeAsync(ligueId, 1, nouvelle);
        var echeances = await svc.GetEcheancesRondesAsync(ligueId);
        Assert.Single(echeances);                 // pas de doublon
        Assert.Equal(nouvelle.Date, echeances[1].Date);
    }

    [Fact]
    public async Task DefinirEcheanceRonde_AvecNull_RetireLecheance()
    {
        var (ligueId, ids) = await SetupLigueLibreAsync(2);

        await using var db = _factory.CreateContext();
        var svc = CreateService(db);
        await svc.DefinirRondeAsync(ligueId, 1, [(ids[0], ids[1])]);
        await svc.DefinirEcheanceRondeAsync(ligueId, 1, new DateTime(2026, 9, 15, 0, 0, 0, DateTimeKind.Utc));

        await svc.DefinirEcheanceRondeAsync(ligueId, 1, null);

        Assert.Empty(await svc.GetEcheancesRondesAsync(ligueId));
    }

    [Fact]
    public async Task SupprimerRonde_RetireAussiSonEcheance()
    {
        var (ligueId, ids) = await SetupLigueLibreAsync(4);

        await using var db = _factory.CreateContext();
        var svc = CreateService(db);
        await svc.DefinirRondeAsync(ligueId, 1, [(ids[0], ids[1])]);
        await svc.DefinirRondeAsync(ligueId, 2, [(ids[2], ids[3])]);
        await svc.DefinirEcheanceRondeAsync(ligueId, 1, new DateTime(2026, 9, 15, 0, 0, 0, DateTimeKind.Utc));
        await svc.DefinirEcheanceRondeAsync(ligueId, 2, new DateTime(2026, 9, 22, 0, 0, 0, DateTimeKind.Utc));

        await svc.SupprimerRondeAsync(ligueId, 2);

        var echeances = await svc.GetEcheancesRondesAsync(ligueId);
        Assert.Single(echeances);
        Assert.True(echeances.ContainsKey(1));     // celle de la ronde 1 est conservée
    }

    [Fact]
    public async Task ProposerAppariements_VarieLesRencontresEntreLesRondes()
    {
        // Le reproche initial : « Compléter » proposait toujours les mêmes matchs.
        var (ligueId, ids) = await SetupLigueLibreAsync(4);

        await using var db = _factory.CreateContext();
        var svc = CreateService(db);

        var r1 = await svc.ProposerAppariementsAsync(ligueId, 1, ids);
        await svc.DefinirRondeAsync(ligueId, 1, r1);

        var r2 = await svc.ProposerAppariementsAsync(ligueId, 2, ids);
        await svc.DefinirRondeAsync(ligueId, 2, r2);

        var r3 = await svc.ProposerAppariementsAsync(ligueId, 3, ids);

        static string Cle((int a, int b) p) => p.a < p.b ? $"{p.a}-{p.b}" : $"{p.b}-{p.a}";
        var paires = r1.Concat(r2).Concat(r3).Select(p => Cle((p.domicileId, p.exterieurId))).ToList();

        // 4 équipes → 3 rondes de 2 matchs couvrent les 6 affrontements possibles,
        // chacun exactement une fois : aucune répétition.
        Assert.Equal(6, paires.Count);
        Assert.Equal(6, paires.Distinct().Count());
    }

    [Fact]
    public async Task ProposerAppariements_AlterneDomicileEtExterieur()
    {
        var (ligueId, ids) = await SetupLigueLibreAsync(2);

        await using var db = _factory.CreateContext();
        var svc = CreateService(db);

        var r1 = (await svc.ProposerAppariementsAsync(ligueId, 1, ids)).Single();
        await svc.DefinirRondeAsync(ligueId, 1, [r1]);

        var r2 = (await svc.ProposerAppariementsAsync(ligueId, 2, ids)).Single();

        // match retour : celui qui recevait se déplace
        Assert.Equal(r1.domicileId, r2.exterieurId);
        Assert.Equal(r1.exterieurId, r2.domicileId);
    }

    [Fact]
    public async Task ProposerAppariements_LaisseUneEquipeAuReposSiNombreImpair()
    {
        var (ligueId, ids) = await SetupLigueLibreAsync(3);

        await using var db = _factory.CreateContext();
        var svc = CreateService(db);
        var propositions = await svc.ProposerAppariementsAsync(ligueId, 1, ids);

        Assert.Single(propositions);   // 3 équipes → 1 match, 1 au repos
    }

    [Fact]
    public async Task ProposerAppariements_TientCompteDesRondesNonEnregistrees()
    {
        // Cas réel : le commissaire enchaîne « Ajouter une ronde » + « Compléter »
        // sans enregistrer entre les deux. Sans dejaComposees, les rondes
        // successives proposaient exactement les mêmes rencontres.
        var (ligueId, ids) = await SetupLigueLibreAsync(4);

        await using var db = _factory.CreateContext();
        var svc = CreateService(db);

        var r1 = await svc.ProposerAppariementsAsync(ligueId, 1, ids);   // rien en base
        var r2 = await svc.ProposerAppariementsAsync(ligueId, 2, ids, r1);

        static string Cle((int a, int b) p) => p.a < p.b ? $"{p.a}-{p.b}" : $"{p.b}-{p.a}";
        var c1 = r1.Select(p => Cle((p.domicileId, p.exterieurId))).ToHashSet();
        var c2 = r2.Select(p => Cle((p.domicileId, p.exterieurId))).ToHashSet();

        Assert.Empty(c1.Intersect(c2));   // aucune rencontre en commun
    }

    [Fact]
    public async Task RenumeroterRondes_ComblLesTrousApresSuppression()
    {
        // Bug signalé : après suppression, il restait « Ronde 1 » puis « Ronde 4 ».
        var (ligueId, ids) = await SetupLigueLibreAsync(4);

        await using var db = _factory.CreateContext();
        var svc = CreateService(db);
        await svc.DefinirRondeAsync(ligueId, 1, [(ids[0], ids[1])]);
        await svc.DefinirRondeAsync(ligueId, 2, [(ids[2], ids[3])]);
        await svc.DefinirRondeAsync(ligueId, 3, [(ids[0], ids[2])]);
        await svc.DefinirEcheanceRondeAsync(ligueId, 3, new DateTime(2026, 9, 15, 0, 0, 0, DateTimeKind.Utc));

        await svc.SupprimerRondeAsync(ligueId, 2);      // trou : 1, 3
        Assert.True(await svc.RenumeroterRondesAsync(ligueId));

        await using var db2 = _factory.CreateContext();
        var rondes = await db2.Matches
            .Where(m => m.Division!.LeagueId == ligueId)
            .Select(m => m.Ronde).Distinct().OrderBy(r => r).ToListAsync();
        Assert.Equal([1, 2], rondes);

        // l'échéance suit sa ronde (3 → 2)
        var echeances = await svc.GetEcheancesRondesAsync(ligueId);
        Assert.True(echeances.ContainsKey(2));
    }

    /// <summary>
    /// Bug signalé : avant le lancement, une ligue n'a AUCUN match — les rondes
    /// n'existent que par leurs échéances de date. La renumérotation sortait
    /// alors immédiatement (« pas de match, rien à faire ») et l'écran gardait
    /// son trou : « Ronde 1, Ronde 2, Ronde 4 » après suppression de la 3.
    /// </summary>
    [Fact]
    public async Task RenumeroterRondes_CombleLesTrous_AvantLancement_SansAucunMatch()
    {
        var (ligueId, _) = await SetupLigueLibreAsync(4);

        await using var db = _factory.CreateContext();
        var svc = CreateService(db);
        await svc.DefinirEcheanceRondeAsync(ligueId, 1, new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc));
        await svc.DefinirEcheanceRondeAsync(ligueId, 2, new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc));
        await svc.DefinirEcheanceRondeAsync(ligueId, 4, new DateTime(2027, 2, 16, 0, 0, 0, DateTimeKind.Utc));

        // Aucun match : c'est précisément le cas qui sortait trop tôt.
        Assert.Empty(await db.Matches.Where(m => m.Division!.LeagueId == ligueId).ToListAsync());

        Assert.True(await svc.RenumeroterRondesAsync(ligueId));

        await using var db2 = _factory.CreateContext();
        var echeances = await CreateService(db2).GetEcheancesRondesAsync(ligueId);

        Assert.Equal([1, 2, 3], echeances.Keys.OrderBy(k => k));
        // La date de l'ex-ronde 4 suit son nouveau numéro, elle n'est pas perdue.
        Assert.Equal(new DateTime(2027, 2, 16), echeances[3].Date);
    }

    [Fact]
    public async Task RenumeroterRondes_NeToucheRienSiDejaCompact()
    {
        var (ligueId, ids) = await SetupLigueLibreAsync(4);

        await using var db = _factory.CreateContext();
        var svc = CreateService(db);
        await svc.DefinirRondeAsync(ligueId, 1, [(ids[0], ids[1])]);
        await svc.DefinirRondeAsync(ligueId, 2, [(ids[2], ids[3])]);

        Assert.True(await svc.RenumeroterRondesAsync(ligueId));

        await using var db2 = _factory.CreateContext();
        var rondes = await db2.Matches
            .Where(m => m.Division!.LeagueId == ligueId)
            .Select(m => m.Ronde).Distinct().OrderBy(r => r).ToListAsync();
        Assert.Equal([1, 2], rondes);
    }

    [Fact]
    public async Task RenumeroterRondes_RefuseDeDecalerUneRondeCommencee()
    {
        var (ligueId, ids) = await SetupLigueLibreAsync(4);

        await using (var db = _factory.CreateContext())
        {
            var svc = CreateService(db);
            await svc.DefinirRondeAsync(ligueId, 2, [(ids[0], ids[1])]);   // pas de ronde 1
        }
        await using (var db = _factory.CreateContext())
        {
            var m = await db.Matches.FirstAsync();
            m.Statut = MatchStatus.Termine;
            await db.SaveChangesAsync();
        }

        await using (var db = _factory.CreateContext())
        {
            var svc = CreateService(db);
            Assert.False(await svc.RenumeroterRondesAsync(ligueId));   // 2 → 1 interdit
            Assert.Equal(2, (await db.Matches.FirstAsync()).Ronde);
        }
    }

    [Fact]
    public async Task DefinirEcheanceRonde_ConserveLaDateSaisieQuelQueSoitLeFuseau()
    {
        // Piège : une date stockée à minuit bascule d'un jour selon le fuseau
        // et l'heure d'été. Elle est donc normalisée à midi UTC.
        var (ligueId, ids) = await SetupLigueLibreAsync(2);

        await using var db = _factory.CreateContext();
        var svc = CreateService(db);
        await svc.DefinirRondeAsync(ligueId, 1, [(ids[0], ids[1])]);

        // saisie « 25 août » en heure locale non spécifiée, comme le MudDatePicker
        await svc.DefinirEcheanceRondeAsync(ligueId, 1, new DateTime(2026, 8, 25));

        var stockee = (await svc.GetEcheancesRondesAsync(ligueId))[1];
        Assert.Equal(new DateTime(2026, 8, 25), stockee.Date);   // toujours le 25
        Assert.Equal(12, stockee.Hour);                          // midi : marge de 12h
    }

    [Fact]
    public void LabelMvp_EstLeTermeFrancaisJPV()
    {
        // L'association dit « JPV », pas « MVP ». Le libellé est centralisé
        // dans DisplayHelpers : c'est le seul point à traduire.
        Assert.Equal("JPV", DisplayHelpers.LabelMvp);
        Assert.Contains("JPV", DisplayHelpers.LabelMvpLong);
        Assert.Contains("Valeureux", DisplayHelpers.LabelMvpLong);
    }

    // ─── Dev 3 : préparation du calendrier avant le lancement ─────────────────

    [Fact]
    public async Task DefinirEcheanceRonde_EstPossibleAvantLeLancementDeLaSaison()
    {
        // Le commissaire prépare son planning dès la configuration de la ligue,
        // avant même que les équipes soient inscrites : à ce stade aucune ronde
        // n'existe encore (le pool de matchs est généré au lancement).
        var (ligueId, _) = await SetupLigueLibreAsync(2, lancer: false);

        await using var db = _factory.CreateContext();
        var svc = CreateService(db);

        // Phase Creation : la ligue est encore en cours de configuration.
        var ligue = await db.Leagues.FindAsync(ligueId);
        ligue!.Statut = LeagueStatus.Creation;
        await db.SaveChangesAsync();

        await svc.DefinirEcheanceRondeAsync(ligueId, 1, new DateTime(2026, 9, 15));
        Assert.Equal(new DateTime(2026, 9, 15), (await svc.GetEcheancesRondesAsync(ligueId))[1].Date);

        // Phase Inscription : les coaches arrivent, le planning reste éditable.
        await svc.DemarrerInscriptionsAsync(ligueId);
        await svc.DefinirEcheanceRondeAsync(ligueId, 2, new DateTime(2026, 9, 22));

        var echeances = await svc.GetEcheancesRondesAsync(ligueId);
        Assert.Equal(2, echeances.Count);
        Assert.Equal(new DateTime(2026, 9, 22), echeances[2].Date);
    }

    [Fact]
    public async Task LancerSaison_ConserveLesEcheancesDesRondesReellementCreees()
    {
        // Les dates préparées en amont ne doivent pas être perdues au lancement.
        var (ligueId, _) = await SetupLigueLibreAsync(4, format: LeagueFormat.RoundRobin, lancer: false);

        await using var db = _factory.CreateContext();
        var svc = CreateService(db);
        await svc.DefinirEcheanceRondeAsync(ligueId, 1, new DateTime(2026, 9, 15));

        await svc.LancerSaisonAsync(ligueId);

        var echeances = await svc.GetEcheancesRondesAsync(ligueId);
        Assert.Equal(new DateTime(2026, 9, 15), echeances[1].Date);
    }

    [Fact]
    public async Task LancerSaison_NettoieLesEcheancesDesRondesQuiNexistentPas()
    {
        // Option A : la saisie des rondes est libre avant le lancement, on ne
        // connaît pas encore le nombre réel de rondes. Celles qui dépassent le
        // calendrier finalement généré sont retirées pour ne pas afficher des
        // échéances fantômes.
        var (ligueId, _) = await SetupLigueLibreAsync(4, format: LeagueFormat.RoundRobin, lancer: false);

        await using var db = _factory.CreateContext();
        var svc = CreateService(db);
        await svc.DefinirEcheanceRondeAsync(ligueId, 1, new DateTime(2026, 9, 15));
        await svc.DefinirEcheanceRondeAsync(ligueId, 99, new DateTime(2027, 1, 1));

        var orphelines = await svc.LancerSaisonAsync(ligueId);

        var echeances = await svc.GetEcheancesRondesAsync(ligueId);
        Assert.True(echeances.ContainsKey(1));
        Assert.False(echeances.ContainsKey(99));
        Assert.Contains(99, orphelines);
    }

    [Fact]
    public async Task LancerSaison_FormatLibre_ConserveToutesLesEcheances()
    {
        // En format Libre le commissaire compose ses rondes APRÈS le lancement :
        // aucune ronde n'existe à cet instant, donc rien ne doit être nettoyé —
        // sinon on effacerait tout le planning préparé en amont.
        var (ligueId, _) = await SetupLigueLibreAsync(4, format: LeagueFormat.Libre, lancer: false);

        await using var db = _factory.CreateContext();
        var svc = CreateService(db);
        await svc.DefinirEcheanceRondeAsync(ligueId, 1, new DateTime(2026, 9, 15));
        await svc.DefinirEcheanceRondeAsync(ligueId, 5, new DateTime(2026, 10, 20));

        var orphelines = await svc.LancerSaisonAsync(ligueId);

        var echeances = await svc.GetEcheancesRondesAsync(ligueId);
        Assert.Equal(2, echeances.Count);
        Assert.Empty(orphelines);
    }

    [Fact]
    public void CalendrierEditable_AvantLeLancement_QuelQueSoitLeFormat()
    {
        // Dater son planning à l'avance a du sens aussi en Round Robin, où le
        // calendrier sera généré automatiquement au lancement.
        // Open est la seule exception : ce format n'a aucune ronde à dater.
        foreach (var format in Enum.GetValues<LeagueFormat>())
        {
            var attendu = format != LeagueFormat.Open;
            Assert.Equal(attendu, DisplayHelpers.CalendrierEditable(LeagueStatus.Creation, format));
            Assert.Equal(attendu, DisplayHelpers.CalendrierEditable(LeagueStatus.Inscription, format));
        }
    }

    [Fact]
    public void CalendrierEditable_ApresLancement_SeulementEnFormatLibre()
    {
        // Saison lancée : en Round Robin le calendrier est figé, il n'y a plus
        // rien à composer.
        Assert.True(DisplayHelpers.CalendrierEditable(LeagueStatus.EnCours, LeagueFormat.Libre));
        Assert.True(DisplayHelpers.CalendrierEditable(LeagueStatus.EnCours, LeagueFormat.LibreAvecPlayoffs));
        Assert.False(DisplayHelpers.CalendrierEditable(LeagueStatus.EnCours, LeagueFormat.RoundRobin));
        Assert.False(DisplayHelpers.CalendrierEditable(LeagueStatus.PlayOffs, LeagueFormat.Libre));
    }

    [Fact]
    public void AppariementsEditables_JamaisAvantLeLancement()
    {
        // Avant le lancement les équipes ne sont pas toutes inscrites : composer
        // des rencontres n'aurait aucun sens, seules les dates sont éditables.
        Assert.False(DisplayHelpers.AppariementsEditables(LeagueStatus.Creation, LeagueFormat.Libre));
        Assert.False(DisplayHelpers.AppariementsEditables(LeagueStatus.Inscription, LeagueFormat.Libre));
        Assert.True(DisplayHelpers.AppariementsEditables(LeagueStatus.EnCours, LeagueFormat.Libre));
        Assert.False(DisplayHelpers.AppariementsEditables(LeagueStatus.EnCours, LeagueFormat.RoundRobin));
    }

    // ─── Dev 2 : format Open ──────────────────────────────────────────────────

    [Fact]
    public void LeagueFormat_LesEntiersPersistesNeChangentJamais()
    {
        // Les valeurs sont stockées en int en base : insérer une entrée au milieu
        // réaffecterait silencieusement toutes les lignes existantes.
        Assert.Equal(0, (int)LeagueFormat.RoundRobin);
        Assert.Equal(1, (int)LeagueFormat.RoundRobinAvecPlayoffs);
        Assert.Equal(2, (int)LeagueFormat.Libre);
        Assert.Equal(3, (int)LeagueFormat.LibreAvecPlayoffs);
        Assert.Equal(4, (int)LeagueFormat.Open);
    }

    [Fact]
    public void Open_NestPasUnFormatLibre()
    {
        // Sinon Open hériterait de l'UI de composition de rondes, alors qu'il
        // n'a pas de rondes du tout.
        Assert.False(DisplayHelpers.EstFormatLibre(LeagueFormat.Open));
        Assert.False(DisplayHelpers.AvecPlayoffs(LeagueFormat.Open));
        Assert.True(DisplayHelpers.SansCalendrier(LeagueFormat.Open));
        Assert.False(DisplayHelpers.SansCalendrier(LeagueFormat.Libre));
    }

    [Fact]
    public void InscriptionOuverte_EnOpen_MemeSaisonLancee()
    {
        // Le cœur du mode Open : la ligue est simultanément « en cours » et
        // ouverte aux inscriptions, un état que LeagueStatus ne sait pas exprimer.
        Assert.True(DisplayHelpers.InscriptionOuverte(LeagueStatus.EnCours, LeagueFormat.Open));
        Assert.True(DisplayHelpers.InscriptionOuverte(LeagueStatus.Inscription, LeagueFormat.RoundRobin));
        Assert.False(DisplayHelpers.InscriptionOuverte(LeagueStatus.EnCours, LeagueFormat.RoundRobin));

        // Une ligue Open clôturée n'accepte plus personne.
        Assert.False(DisplayHelpers.InscriptionOuverte(LeagueStatus.Termine, LeagueFormat.Open));
    }

    [Fact]
    public async Task LancerSaison_EnOpen_CreeLaDivisionMaisAucunMatch()
    {
        // La division technique est indispensable : SupprimerLigueAsync retrouve
        // les matchs VIA les divisions, un match sans division serait orphelin.
        var (ligueId, _) = await SetupLigueLibreAsync(3, format: LeagueFormat.Open, lancer: false);

        await using var db = _factory.CreateContext();
        var svc = CreateService(db);
        await svc.LancerSaisonAsync(ligueId);

        var ligue = await db.Leagues.Include(l => l.Divisions).FirstAsync(l => l.Id == ligueId);
        Assert.Single(ligue.Divisions);
        Assert.Equal(0, await CompterMatchsAsync(ligueId));
        Assert.Equal(LeagueStatus.EnCours, ligue.Statut);
    }

    [Fact]
    public async Task LancerSaison_EnOpen_NeNettoiePasLesEcheances()
    {
        // Aucune ronde n'existe en Open : le nettoyage effacerait tout.
        var (ligueId, _) = await SetupLigueLibreAsync(2, format: LeagueFormat.Open, lancer: false);

        await using var db = _factory.CreateContext();
        var svc = CreateService(db);
        await svc.DefinirEcheanceRondeAsync(ligueId, 1, new DateTime(2026, 9, 15));

        var orphelines = await svc.LancerSaisonAsync(ligueId);

        Assert.Empty(orphelines);
        Assert.Single(await svc.GetEcheancesRondesAsync(ligueId));
    }

    [Fact]
    public async Task SupprimerLigue_EnOpen_NeLaissePasDeMatchOrphelin()
    {
        // Le piège du dev : sans division technique, les matchs Open resteraient
        // en base pour toujours.
        var (ligueId, ids) = await SetupLigueLibreAsync(2, format: LeagueFormat.Open, lancer: false);

        await using var db = _factory.CreateContext();
        var svc = CreateService(db);
        await svc.LancerSaisonAsync(ligueId);
        await svc.ProposerRencontreAsync(ligueId, ids[0], ids[1]);

        Assert.Equal(1, await CompterMatchsAsync(ligueId));

        await svc.SupprimerLigueAsync(ligueId);

        await using var db2 = _factory.CreateContext();
        Assert.Empty(await db2.Matches.ToListAsync());
        Assert.Empty(await db2.MatchSheets.ToListAsync());
        Assert.Empty(await db2.Teams.Where(t => t.LeagueId == ligueId).ToListAsync());
    }

    [Fact]
    public async Task ProposerRencontre_EnOpen_CreeUnMatchHorsRonde()
    {
        var (ligueId, ids) = await SetupLigueLibreAsync(2, format: LeagueFormat.Open, lancer: false);

        await using var db = _factory.CreateContext();
        var svc = CreateService(db);
        await svc.LancerSaisonAsync(ligueId);

        var matchId = await svc.ProposerRencontreAsync(ligueId, ids[0], ids[1]);

        var match = await db.Matches.FindAsync(matchId);
        Assert.NotNull(match);
        Assert.Equal(0, match!.Ronde);          // convention « hors ronde »
        Assert.NotNull(match.DivisionId);       // rattaché à la division technique
        Assert.Equal(ids[0], match.EquipeDomicileId);
        Assert.Equal(ids[1], match.EquipeExterieurId);
    }

    [Fact]
    public async Task ProposerRencontre_RefuseUneEquipeContreElleMeme()
    {
        var (ligueId, ids) = await SetupLigueLibreAsync(2, format: LeagueFormat.Open, lancer: false);

        await using var db = _factory.CreateContext();
        var svc = CreateService(db);
        await svc.LancerSaisonAsync(ligueId);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ProposerRencontreAsync(ligueId, ids[0], ids[0]));
    }

    [Fact]
    public async Task ProposerRencontre_RefuseHorsFormatOpen()
    {
        // Les autres formats passent par le calendrier, pas par des rencontres
        // proposées à la volée.
        var (ligueId, ids) = await SetupLigueLibreAsync(2, format: LeagueFormat.Libre);

        await using var db = _factory.CreateContext();
        var svc = CreateService(db);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ProposerRencontreAsync(ligueId, ids[0], ids[1]));
    }

    [Fact]
    public void RondeLabel_DistingueLibreRondeEtPlayoff()
    {
        // Trois conventions cohabitent sur Match.Ronde : 0 = hors ronde (Open),
        // >= 100 = play-off, le reste = ronde classique. « Ronde 0 » n'aurait
        // aucun sens pour l'utilisateur.
        Assert.Equal("Rencontre libre", DisplayHelpers.RondeLabel(0));
        Assert.Equal("Ronde 3", DisplayHelpers.RondeLabel(3));
        Assert.Equal("Play-off — Tour 1", DisplayHelpers.RondeLabel(100));
        Assert.Equal("Libre", DisplayHelpers.RondeLabelCourt(0));
        Assert.Equal("Play-off T2", DisplayHelpers.RondeLabelCourt(101));
    }

    [Fact]
    public async Task PhaseDeRepos_LeveLesRateLeProchainMatch_EtTraceLaValidation()
    {
        // La phase de repos existait côté service sans aucun écran pour la
        // déclencher (constaté en QA). On verrouille ici son contrat métier :
        // levée des indisponibilités, puis validation traçée une seule fois.
        var (ligueId, equipeIds) = await SetupLigueLibreAsync(4, LeagueFormat.RoundRobinAvecPlayoffs);

        await using var db = _factory.CreateContext();
        var svc = CreateService(db);

        var ligue = await db.Leagues.FindAsync(ligueId);
        ligue!.Statut = LeagueStatus.EnCours;

        // Le seed de ligue ne crée pas de joueurs : on en ajoute un, sanctionné.
        var position = await db.PlayerPositions.FirstAsync();
        var joueur = new TeamPlayer
        {
            TeamId = equipeIds[0], PlayerPositionId = position.Id,
            Nom = "Blessé", Numero = 1,
            ManqueSuivantMatch = true, RecruteLe = DateTime.UtcNow
        };
        db.TeamPlayers.Add(joueur);
        await db.SaveChangesAsync();

        await svc.LancerPhaseDeReposAsync(ligueId);

        var apres = await db.TeamPlayers.FindAsync(joueur.Id);
        Assert.False(apres!.ManqueSuivantMatch);
        Assert.Equal(LeagueStatus.PhaseDeRepos,
            (await db.Leagues.FindAsync(ligueId))!.Statut);

        // Pas encore validée pour cette équipe…
        Assert.False(await svc.ADejaValideReposAsync(ligueId, equipeIds[0]));

        var teamService = new TeamService(db, NullLogger<TeamService>.Instance);
        await svc.ValiderApresMatchReposAsync(
            ligueId, equipeIds[0], [], [], nouvellesRelances: 0, teamService);

        Assert.True(await svc.ADejaValideReposAsync(ligueId, equipeIds[0]));

        // …et on ne valide pas deux fois.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.ValiderApresMatchReposAsync(
                ligueId, equipeIds[0], [], [], nouvellesRelances: 0, teamService));
    }

    [Fact]
    public void LeagueLabel_CouvreTousLesStatuts()
    {
        // Garde-fou : PhaseDeRepos manquait au switch et s'affichait
        // « PhaseDeRepos » brut à l'écran. Le défaut est passé inaperçu parce que
        // l'état était inatteignable faute d'écran pour le déclencher.
        foreach (var statut in Enum.GetValues<LeagueStatus>())
        {
            var libelle = DisplayHelpers.LeagueLabel(statut);
            Assert.NotEqual(statut.ToString(), libelle);
        }
    }

    [Fact]
    public void MatchLabel_CouvreTousLesStatuts()
    {
        foreach (var statut in Enum.GetValues<MatchStatus>())
        {
            var libelle = DisplayHelpers.MatchLabel(statut);
            Assert.NotEqual(statut.ToString(), libelle);
        }
    }

    [Fact]
    public void NomCoach_CompteAnonymise_AfficheCoachSupprime()
    {
        // Le pseudo a été effacé à l'anonymisation, mais la ligne subsiste pour
        // que les équipes et feuilles de match gardent leur référence. Les
        // autres coaches doivent lire un libellé neutre, pas un identifiant
        // technique du type « compte-supprime-a1b2c3d4 ».
        var supprime = new ApplicationUser
        {
            PseudoCoach = "Coach supprimé",
            UserName = "compte-supprime-a1b2c3d4",
            EstSupprime = true
        };

        Assert.Equal("Coach supprimé", DisplayHelpers.NomCoach(supprime));
    }

    [Fact]
    public void NomCoach_CompteNormal_AfficheLePseudo()
    {
        var actif = new ApplicationUser { PseudoCoach = "Ragnar", UserName = "ragnar@test.fr" };
        Assert.Equal("Ragnar", DisplayHelpers.NomCoach(actif));
    }

    [Fact]
    public void NomCoach_SansPseudo_RetombeSurLIdentifiant()
    {
        var sansPseudo = new ApplicationUser { PseudoCoach = "", UserName = "coach@test.fr" };
        Assert.Equal("coach@test.fr", DisplayHelpers.NomCoach(sansPseudo));
    }

    [Fact]
    public void NomCoach_Null_NeJettePas()
    {
        Assert.Equal("—", DisplayHelpers.NomCoach(null));
    }

    // ─── Promotion commissaire de ligue ───────────────────────────────────────

    /// <summary>
    /// Le cas du bug : équipe inscrite mais SANS division (ligue en Inscription).
    /// L'ancienne UI listait les coaches via Divisions.Equipes → liste vide → le
    /// coach n'apparaissait jamais dans la modale de promotion.
    /// </summary>
    [Fact]
    public async Task CoachesPromouvables_IncluentUnCoachSansDivision()
    {
        var (commissaire, game, rv) = await SetupAsync();
        await using var db = _factory.CreateContext();
        var coach = DataSeeder.CreateUser("coachlibre");
        db.Users.Add(coach);
        await db.SaveChangesAsync();
        var (teamType, _) = await DataSeeder.SeedTeamTypeAsync(db, game.Id);
        var ligue = await DataSeeder.SeedLeagueAsync(db, game.Id, rv.Id, commissaire.Id);
        await DataSeeder.SeedTeamAsync(db, ligue.Id, coach.Id, teamType.Id, "Sans Division");

        await using var db2 = _factory.CreateContext();
        var promouvables = await CreateService(db2).GetCoachesPromouvablesAsync(ligue.Id);

        Assert.Contains(promouvables, c => c.Id == coach.Id);
    }

    [Fact]
    public async Task CoachesPromouvables_ExcluentUnCoachDejaCommissaireDeLigue()
    {
        var (commissaire, game, rv) = await SetupAsync();
        await using var db = _factory.CreateContext();
        var coach = DataSeeder.CreateUser("dejapromu");
        db.Users.Add(coach);
        await db.SaveChangesAsync();
        var (teamType, _) = await DataSeeder.SeedTeamTypeAsync(db, game.Id);
        var ligue = await DataSeeder.SeedLeagueAsync(db, game.Id, rv.Id, commissaire.Id);
        await DataSeeder.SeedTeamAsync(db, ligue.Id, coach.Id, teamType.Id, "Équipe");
        await CreateService(db).PromouvoirCommissaireDeLigueAsync(ligue.Id, coach.Id, commissaire.Id);

        await using var db2 = _factory.CreateContext();
        var promouvables = await CreateService(db2).GetCoachesPromouvablesAsync(ligue.Id);

        Assert.DoesNotContain(promouvables, c => c.Id == coach.Id);
    }

    [Fact]
    public async Task CoachesPromouvables_ExcluentLeCommissaireCreateurEtLesComptesSupprimes()
    {
        var (commissaire, game, rv) = await SetupAsync();
        await using var db = _factory.CreateContext();
        var supprime = DataSeeder.CreateUser("anonyme");
        supprime.EstSupprime = true;
        db.Users.Add(supprime);
        await db.SaveChangesAsync();
        var (teamType, _) = await DataSeeder.SeedTeamTypeAsync(db, game.Id);
        var ligue = await DataSeeder.SeedLeagueAsync(db, game.Id, rv.Id, commissaire.Id);
        await DataSeeder.SeedTeamAsync(db, ligue.Id, commissaire.Id, teamType.Id, "Équipe du commissaire");
        await DataSeeder.SeedTeamAsync(db, ligue.Id, supprime.Id, teamType.Id, "Équipe orpheline");

        await using var db2 = _factory.CreateContext();
        var promouvables = await CreateService(db2).GetCoachesPromouvablesAsync(ligue.Id);

        Assert.DoesNotContain(promouvables, c => c.Id == commissaire.Id);
        Assert.DoesNotContain(promouvables, c => c.Id == supprime.Id);
    }

    [Fact]
    public async Task Promouvoir_RendLeCoachCommissaireDeLigue()
    {
        var (commissaire, game, rv) = await SetupAsync();
        await using var db = _factory.CreateContext();
        var coach = DataSeeder.CreateUser("promu");
        db.Users.Add(coach);
        await db.SaveChangesAsync();
        var (teamType, _) = await DataSeeder.SeedTeamTypeAsync(db, game.Id);
        var ligue = await DataSeeder.SeedLeagueAsync(db, game.Id, rv.Id, commissaire.Id);
        await DataSeeder.SeedTeamAsync(db, ligue.Id, coach.Id, teamType.Id, "Équipe");

        await using var db2 = _factory.CreateContext();
        await CreateService(db2).PromouvoirCommissaireDeLigueAsync(ligue.Id, coach.Id, commissaire.Id);

        await using var db3 = _factory.CreateContext();
        var auth = new AuthorizationService(db3, null!);
        Assert.True(await auth.EstCommissaireDeLigueAsync(coach.Id, ligue.Id));
    }

    /// <summary>
    /// Verrou de régression : `GetLigueAsync` doit charger la FEUILLE des matchs.
    ///
    /// Les cartes de match de la fiche de ligue testent `Match.Feuille` pour
    /// décider d'afficher « Corriger la saisie » (commissaire) et
    /// « Confirmer / En attente adversaire ». Sans ce Include, la feuille est
    /// toujours nulle et ces boutons ne s'affichent JAMAIS — panne silencieuse :
    /// la page se rend normalement, seuls des boutons manquent.
    /// </summary>
    [Fact]
    public async Task GetLigue_ChargeLaFeuilleDesMatchs()
    {
        var (commissaire, game, rv) = await SetupAsync();

        await using var db = _factory.CreateContext();
        var ligue = await DataSeeder.SeedLeagueAsync(db, game.Id, rv.Id, commissaire.Id);
        var (teamType, _) = await DataSeeder.SeedTeamTypeAsync(db, game.Id);

        var div = new Division { LeagueId = ligue.Id, Nom = "D1", Ordre = 1 };
        db.Divisions.Add(div);
        await db.SaveChangesAsync();

        var dom = await DataSeeder.SeedTeamAsync(db, ligue.Id, commissaire.Id, teamType.Id, "Dom");
        var ext = await DataSeeder.SeedTeamAsync(db, ligue.Id, commissaire.Id, teamType.Id, "Ext");
        var match = await DataSeeder.SeedMatchAsync(db, dom.Id, ext.Id, div.Id);

        db.MatchSheets.Add(new MatchSheet
        {
            MatchId = match.Id,
            SaisiParId = commissaire.Id,
            TouchdownsDomicile = 2,
            TouchdownsExterieur = 1,
        });
        await db.SaveChangesAsync();

        await using var db2 = _factory.CreateContext();
        var relue = await CreateService(db2).GetLigueAsync(ligue.Id);

        var matchRelu = relue!.Divisions.Single().Matchs.Single();
        Assert.NotNull(matchRelu.Feuille);
        Assert.Equal(2, matchRelu.Feuille!.TouchdownsDomicile);
    }

    private class StubAuthorizationService(
        (string userId, int ligueId)? peutGerer = null) : IAuthorizationService
    {
        public Task<bool> EstAdminAsync(string userId) => Task.FromResult(false);
        public Task<bool> EstGrandCommissaireAsync(string userId) => Task.FromResult(false);
        public Task<bool> EstCommissaireDeLigueAsync(string userId, int ligueId) => Task.FromResult(false);
        public Task<bool> PeutGererLigueAsync(string userId, int ligueId) =>
            Task.FromResult(peutGerer.HasValue && peutGerer.Value.userId == userId && peutGerer.Value.ligueId == ligueId);
        public Task<bool> PeutEditerDonneesAsync(string userId) => Task.FromResult(false);
        public Task<bool> PeutGererSettingsAsync(string userId) => Task.FromResult(false);
    }
}
