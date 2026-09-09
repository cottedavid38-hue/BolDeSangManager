using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using BolDeSangManager.Data;
using BolDeSangManager.Data.Enums;
using BolDeSangManager.Data.Models;
using BolDeSangManager.Data.Seeding;
using BolDeSangManager.Helpers;
using Microsoft.EntityFrameworkCore;

namespace BolDeSangManager.Services;

public class GameDataExportService(ApplicationDbContext db, ILogger<GameDataExportService> logger)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // ── Export ───────────────────────────────────────────────────────────────

    public async Task<byte[]> ExportAsync(int rulesVersionId)
    {
        var version = await db.RulesVersions
            .Include(v => v.Game)
            .FirstOrDefaultAsync(v => v.Id == rulesVersionId)
            ?? throw new InvalidOperationException("Version de règles introuvable");

        var skills = await db.Skills
            .Include(s => s.SkillCategoryDef)
            .Where(s => s.RulesVersionId == rulesVersionId)
            .OrderBy(s => s.SkillCategoryDef.Nom).ThenBy(s => s.Nom)
            .ToListAsync();

        var teamTypes = await db.TeamTypes
            .Include(tt => tt.Postes).ThenInclude(p => p.CompetencesDepart).ThenInclude(pps => pps.Skill)
            .Include(tt => tt.Postes).ThenInclude(p => p.AccesCategories).ThenInclude(a => a.SkillCategoryDef)
            .Include(tt => tt.LimitesMotsCles)
            .Include(tt => tt.ReglesSpecialesListe).ThenInclude(l => l.SpecialRule)
            .Where(tt => tt.RulesVersionId == rulesVersionId)
            .OrderBy(tt => tt.Nom)
            .ToListAsync();

        var reserve = await db.PoolPositions
            .Include(p => p.CompetencesDepart).ThenInclude(pps => pps.Skill)
            .Include(p => p.AccesCategories).ThenInclude(a => a.SkillCategoryDef)
            .Where(p => p.RulesVersionId == rulesVersionId)
            .OrderBy(p => p.Nom)
            .ToListAsync();

        var categories = await db.SkillCategories
            .Where(c => c.RulesVersionId == rulesVersionId)
            .OrderBy(c => c.Nom)
            .ToListAsync();

        var staffTypes = await db.StaffTypes
            .Where(s => s.RulesVersionId == rulesVersionId)
            .OrderBy(s => s.Ordre).ThenBy(s => s.Nom)
            .ToListAsync();

        // Catalogue de règles spéciales de la version (LRB p.93-94).
        var reglesSpeciales = await db.SpecialRules
            .Where(r => r.RulesVersionId == rulesVersionId)
            .OrderBy(r => r.Ordre).ThenBy(r => r.Nom)
            .ToListAsync();

        // Barème des améliorations : les 6 paliers de coût PSP de la version.
        var paliersAmelioration = await db.PaliersAmelioration
            .Where(p => p.RulesVersionId == rulesVersionId)
            .OrderBy(p => p.Rang)
            .ToListAsync();

        // F3 : chaque export produit une nouvelle révision, persistée sur la
        // version. Sans persistance le numéro repartirait à 1 à chaque fois et
        // ne prouverait rien.
        version.Revision++;
        version.DernierExportLe = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var dto = new GameDataExportDto(
            Jeu: version.Game.Nom,
            Version: version.Nom,
            Ordre: version.Ordre,
            EstActive: version.EstActive,
            Revision: version.Revision,
            ExporteLe: version.DernierExportLe,
            Skills: skills.Select(s => new SkillGdDto(
                s.Nom, s.Categorie, s.Description, s.EstElite, s.EstTrait,
                CategorieNom: s.SkillCategoryDef?.Nom)).ToList(),
            TypesEquipes: teamTypes.Select(tt => new TeamTypeGdDto(
                tt.Nom,
                tt.Categorie,
                tt.CoutRelance,
                tt.ReglesSpeciales,
                tt.LiguesTexteObsolete,
                tt.Postes.OrderBy(p => p.Cout).Select(p => new PlayerPositionGdDto(
                    p.Nom,
                    p.QuantiteMax,
                    p.Cout,
                    p.Mouvement,
                    p.Force,
                    p.Agilite,
                    p.CapacitePasse,
                    p.Armure,
                    p.CompetencesPrincipales,
                    p.CompetencesSecondaires,
                    p.MotsCles,
                    p.CompetencesDepart.Select(pps => pps.Skill.Nom).OrderBy(n => n).ToList(),
                    p.AccesCategories.Where(a => a.EstPrincipale).Select(a => a.SkillCategoryDef.Nom).OrderBy(n => n).ToList(),
                    p.AccesCategories.Where(a => !a.EstPrincipale).Select(a => a.SkillCategoryDef.Nom).OrderBy(n => n).ToList()
                )).ToList(),
                tt.LimitesMotsCles.Select(l => new KeywordLimitGdDto(l.MotCle, l.Max)).ToList(),
                tt.ReglesSpecialesListe
                    .OrderBy(l => l.SpecialRule.Ordre).ThenBy(l => l.SpecialRule.Nom)
                    .Select(l => new TeamTypeSpecialRuleGdDto(l.SpecialRule.Nom, l.OptionsChoix, l.LimiteParApresMatch))
                    .ToList()
            )).ToList(),
            Reserve: reserve.Select(p => new PlayerPositionGdDto(
                p.Nom, p.QuantiteMax, p.Cout, p.Mouvement, p.Force, p.Agilite,
                p.CapacitePasse, p.Armure, p.CompetencesPrincipales, p.CompetencesSecondaires,
                p.MotsCles,
                p.CompetencesDepart.Select(pps => pps.Skill.Nom).OrderBy(n => n).ToList(),
                p.AccesCategories.Where(a => a.EstPrincipale).Select(a => a.SkillCategoryDef.Nom).OrderBy(n => n).ToList(),
                p.AccesCategories.Where(a => !a.EstPrincipale).Select(a => a.SkillCategoryDef.Nom).OrderBy(n => n).ToList()
            )).ToList(),
            Categories: categories.Select(c => new SkillCategoryGdDto(c.Nom, c.Code)).ToList(),
            XpParTouchdown: version.XpParTouchdown,
            XpParPasse: version.XpParPasse,
            XpParInterception: version.XpParInterception,
            XpParElimination: version.XpParElimination,
            XpBonusMvp: version.XpBonusMvp,
            XpParDeviation: version.XpParDeviation,
            XpParAgression: version.XpParAgression,
            PointsVictoire: version.PointsVictoire,
            PointsNul: version.PointsNul,
            PointsDefaite: version.PointsDefaite,
            PointsParTouchdown: version.PointsParTouchdown,
            PointsParElimination: version.PointsParElimination,
            PointsParInterception: version.PointsParInterception,
            PointsParPasse: version.PointsParPasse,
            PointsParDeviation: version.PointsParDeviation,
            PointsParAgression: version.PointsParAgression,
            Staff: staffTypes.Select(s => new StaffTypeGdDto(
                s.Nom, s.Description, s.Ordre, s.EstActif, s.Cout,
                s.CoutDepuisTypeEquipe, s.MinCreation, s.MaxCreation, s.MaxLigue,
                s.CompteDansVea
            )).ToList(),
            ReglesSpeciales: reglesSpeciales.Select(r => new SpecialRuleGdDto(
                r.Nom, r.Description, r.Ordre, r.Code
            )).ToList(),
            HaussePrincipale: version.HaussePrincipale,
            HausseSecondaire: version.HausseSecondaire,
            HausseArmure: version.HausseArmure,
            HausseMouvement: version.HausseMouvement,
            HausseCapacitePasse: version.HausseCapacitePasse,
            HausseAgilite: version.HausseAgilite,
            HausseForce: version.HausseForce,
            SurcoutElite: version.SurcoutElite,
            PaliersAmelioration: paliersAmelioration.Select(p => new PalierAmeliorationGdDto(
                p.Rang, p.CoutAleaPrincipale, p.CoutChoixPrincipale,
                p.CoutSecondaire, p.CoutCaracteristique
            )).ToList()
        );

        var json = JsonSerializer.SerializeToUtf8Bytes(dto, JsonOpts);
        logger.LogInformation("Export game data : version '{V}' ({NbTT} types, {NbS} compétences)",
            version.Nom, teamTypes.Count, skills.Count);
        return json;
    }

    // ── Import ───────────────────────────────────────────────────────────────

    public async Task<(bool Success, List<string> Errors)> ImportAsync(
        Stream stream, int gameId, string versionNom)
    {
        var errors = new List<string>();

        GameDataExportDto dto;
        try
        {
            dto = await JsonSerializer.DeserializeAsync<GameDataExportDto>(stream, JsonOpts)
                ?? throw new InvalidOperationException("Fichier JSON invalide");
        }
        catch (Exception ex)
        {
            return (false, [$"Impossible de lire le fichier : {ex.Message}"]);
        }

        var game = await db.Games.FindAsync(gameId);
        if (game is null)
        {
            errors.Add($"Jeu id={gameId} introuvable");
            return (false, errors);
        }

        var dejaExistante = await db.RulesVersions
            .AnyAsync(v => v.GameId == gameId && v.Nom == versionNom);
        if (dejaExistante)
        {
            errors.Add($"Une version nommée « {versionNom} » existe déjà pour ce jeu.");
            return (false, errors);
        }

        await using var tx = await db.Database.BeginTransactionAsync();
        try
        {
            // Repli du barème d'amélioration pour tout champ absent du fichier.
            var baremeDefaut = BaremeAmelioration.ParDefaut();

            // 1. Créer la version
            var nextOrdre = await db.RulesVersions
                .Where(v => v.GameId == gameId)
                .MaxAsync(v => (int?)v.Ordre) ?? 0;

            var version = new RulesVersion
            {
                GameId = gameId,
                Nom = versionNom,
                Ordre = nextOrdre + 1,
                EstActive = false,
                // F3 : la version importée hérite de la révision du fichier, pour
                // qu'on sache de quelle livraison elle provient. Sans ça la
                // traçabilité serait perdue au clonage.
                Revision = dto.Revision ?? 0,
                DernierExportLe = dto.ExporteLe,
                // Barème d'XP (R6) — un export antérieur n'a pas ces champs :
                // on retombe alors sur les valeurs par défaut du jeu.
                XpParTouchdown    = dto.XpParTouchdown    ?? XpBareme.ParDefaut(game.Type).ParTouchdown,
                XpParPasse        = dto.XpParPasse        ?? 1,
                XpParInterception = dto.XpParInterception ?? 2,
                XpParElimination  = dto.XpParElimination  ?? 2,
                XpBonusMvp        = dto.XpBonusMvp        ?? 4,
                XpParDeviation    = dto.XpParDeviation    ?? 0,
                XpParAgression    = dto.XpParAgression    ?? 0,
                // Barème de points de classement : un export antérieur n'a pas
                // ces champs, on reprend alors le barème par défaut (3/1/0 sans
                // bonus) — surtout PAS des zéros, qui vaudraient « aucun point
                // par victoire ».
                PointsVictoire        = dto.PointsVictoire        ?? BaremePoints.ParDefaut().Victoire,
                PointsNul             = dto.PointsNul             ?? BaremePoints.ParDefaut().Nul,
                PointsDefaite         = dto.PointsDefaite         ?? BaremePoints.ParDefaut().Defaite,
                PointsParTouchdown    = dto.PointsParTouchdown    ?? 0,
                PointsParElimination  = dto.PointsParElimination  ?? 0,
                PointsParInterception = dto.PointsParInterception ?? 0,
                PointsParPasse        = dto.PointsParPasse        ?? 0,
                PointsParDeviation    = dto.PointsParDeviation    ?? 0,
                PointsParAgression    = dto.PointsParAgression    ?? 0,
                // Barème des améliorations : absent d'un export antérieur, on
                // reprend le barème LRB — jamais des zéros, qui feraient valoir
                // 0 po toutes les améliorations déjà prises par les joueurs.
                HaussePrincipale    = dto.HaussePrincipale    ?? baremeDefaut.HaussePrincipale,
                HausseSecondaire    = dto.HausseSecondaire    ?? baremeDefaut.HausseSecondaire,
                HausseArmure        = dto.HausseArmure        ?? baremeDefaut.HausseArmure,
                HausseMouvement     = dto.HausseMouvement     ?? baremeDefaut.HausseMouvement,
                HausseCapacitePasse = dto.HausseCapacitePasse ?? baremeDefaut.HausseCapacitePasse,
                HausseAgilite       = dto.HausseAgilite       ?? baremeDefaut.HausseAgilite,
                HausseForce         = dto.HausseForce         ?? baremeDefaut.HausseForce,
                SurcoutElite        = dto.SurcoutElite        ?? baremeDefaut.SurcoutElite
            };
            db.RulesVersions.Add(version);
            await db.SaveChangesAsync();

            // Paliers de coût PSP. Même règle : un fichier sans bloc barème donne
            // le tableau LRB, pas une version sans palier (qui proposerait 0 PSP).
            var paliersDto = dto.PaliersAmelioration is { Count: > 0 }
                ? dto.PaliersAmelioration
                : BaremeAmelioration.PaliersLrbParDefaut()
                    .Select(p => new PalierAmeliorationGdDto(
                        p.Rang, p.CoutAleaPrincipale, p.CoutChoixPrincipale,
                        p.CoutSecondaire, p.CoutCaracteristique))
                    .ToList();

            foreach (var p in paliersDto.OrderBy(p => p.Rang))
                db.PaliersAmelioration.Add(new PalierAmeliorationPsp
                {
                    RulesVersionId      = version.Id,
                    Rang                = p.Rang,
                    CoutAleaPrincipale  = p.CoutAleaPrincipale,
                    CoutChoixPrincipale = p.CoutChoixPrincipale,
                    CoutSecondaire      = p.CoutSecondaire,
                    CoutCaracteristique = p.CoutCaracteristique
                });
            await db.SaveChangesAsync();

            // 2. Catégories de compétence
            // Fichier récent → on reprend ses catégories. Fichier antérieur à R2
            // (Categories absent) → on matérialise les 6 catégories standard, et les
            // compétences sont rattachées via leur ancien enum.
            var categorieMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var categoriesCreees = new List<SkillCategoryDef>();
            var categoriesDto = dto.Categories is { Count: > 0 }
                ? dto.Categories
                : StandardSkillCategories.Toutes
                    .Select(t => new SkillCategoryGdDto(t.Nom, t.Code)).ToList();

            foreach (var c in categoriesDto)
            {
                var cat = new SkillCategoryDef
                {
                    RulesVersionId = version.Id,
                    Nom = c.Nom,
                    Code = c.Code
                };
                db.SkillCategories.Add(cat);
                await db.SaveChangesAsync();
                categorieMap[c.Nom] = cat.Id;
                categoriesCreees.Add(cat);
            }

            // 3. Compétences
            var skillMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in dto.Skills)
            {
                // Résolution par nom de catégorie ; repli sur l'ancien enum pour les
                // fichiers exportés avant R2.
                var nomCategorie = s.CategorieNom ?? StandardSkillCategories.Nom(s.Categorie);
                if (!categorieMap.TryGetValue(nomCategorie, out var categorieId))
                {
                    errors.Add($"Catégorie « {nomCategorie} » introuvable pour la compétence « {s.Nom} ».");
                    continue;
                }

                var skill = new Skill
                {
                    RulesVersionId = version.Id,
                    Nom = s.Nom,
                    Categorie = s.Categorie,
                    SkillCategoryDefId = categorieId,
                    Description = s.Description,
                    EstElite = s.EstElite,
                    EstTrait = s.EstTrait
                };
                db.Skills.Add(skill);
                await db.SaveChangesAsync();
                skillMap[s.Nom] = skill.Id;
            }

            // 3 bis. Catalogue de règles spéciales, AVANT les fiches d'équipe
            // qui les référencent par nom.
            var regleMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in dto.ReglesSpeciales ?? [])
            {
                var regle = new SpecialRule
                {
                    RulesVersionId = version.Id,
                    Nom = r.Nom,
                    Description = r.Description,
                    Ordre = r.Ordre,
                    Code = r.Code
                };
                db.SpecialRules.Add(regle);
                await db.SaveChangesAsync();
                regleMap[r.Nom] = regle.Id;
            }

            // 4. TypesEquipes + Postes + Limites
            foreach (var ttDto in dto.TypesEquipes)
            {
                var tt = new TeamType
                {
                    GameId = gameId,
                    RulesVersionId = version.Id,
                    Nom = ttDto.Nom,
                    Categorie = ttDto.CategorieLrb,
                    CoutRelance = ttDto.CoutRelance,
                    ReglesSpeciales = ttDto.ReglesSpeciales,
                    LiguesTexteObsolete = ttDto.Ligues
                };
                db.TeamTypes.Add(tt);
                await db.SaveChangesAsync();

                foreach (var pDto in ttDto.Postes)
                {
                    var pos = new PlayerPosition
                    {
                        TeamTypeId = tt.Id,
                        Nom = pDto.Nom,
                        QuantiteMax = pDto.QuantiteMax,
                        Cout = pDto.Cout,
                        Mouvement = pDto.Mouvement,
                        Force = pDto.Force,
                        Agilite = pDto.Agilite,
                        CapacitePasse = pDto.CapacitePasse,
                        Armure = pDto.Armure,
                        CompetencesPrincipales = pDto.CompetencesPrincipales,
                        CompetencesSecondaires = pDto.CompetencesSecondaires,
                        MotsCles = pDto.MotsCles
                    };
                    db.PlayerPositions.Add(pos);
                    await db.SaveChangesAsync();

                    foreach (var nomSkill in pDto.CompetencesDepart)
                    {
                        if (!skillMap.TryGetValue(nomSkill, out var skillId))
                        {
                            errors.Add($"Compétence de départ introuvable : « {nomSkill} » (poste : {pDto.Nom})");
                            continue;
                        }
                        db.PlayerPositionSkills.Add(new PlayerPositionSkill
                        {
                            PlayerPositionId = pos.Id,
                            SkillId = skillId
                        });
                    }
                    await db.SaveChangesAsync();

                    var (accP, accS) = ResoudreAccesImport(pDto, categoriesCreees);
                    foreach (var catId in accP)
                        db.PlayerPositionCategoryAccesses.Add(new PlayerPositionCategoryAccess
                        { PlayerPositionId = pos.Id, SkillCategoryDefId = catId, EstPrincipale = true });
                    foreach (var catId in accS)
                        db.PlayerPositionCategoryAccesses.Add(new PlayerPositionCategoryAccess
                        { PlayerPositionId = pos.Id, SkillCategoryDefId = catId, EstPrincipale = false });
                    await db.SaveChangesAsync();
                }

                foreach (var lim in ttDto.Limites)
                {
                    db.TeamTypeKeywordLimits.Add(new TeamTypeKeywordLimit
                    {
                        TeamTypeId = tt.Id,
                        MotCle = lim.MotCle,
                        Max = lim.Max
                    });
                }
                await db.SaveChangesAsync();

                // Rattachement des règles spéciales, résolues par NOM comme le
                // reste de cet export. Une règle absente du fichier est signalée
                // sans interrompre l'import : le reste de la fiche est valide.
                foreach (var rDto in ttDto.ReglesSpecialesRattachees ?? [])
                {
                    if (!regleMap.TryGetValue(rDto.RegleNom, out var regleId))
                    {
                        errors.Add($"Règle spéciale introuvable : « {rDto.RegleNom} » (équipe : {ttDto.Nom})");
                        continue;
                    }
                    db.TeamTypeSpecialRules.Add(new TeamTypeSpecialRule
                    {
                        TeamTypeId = tt.Id,
                        SpecialRuleId = regleId,
                        OptionsChoix = rDto.OptionsChoix,
                        LimiteParApresMatch = rDto.LimiteParApresMatch ?? 1
                    });
                }
                await db.SaveChangesAsync();
            }

            // Réserve (PoolPosition) — skills de départ résolus par nom
            foreach (var pDto in dto.Reserve ?? [])
            {
                var pool = new PoolPosition
                {
                    RulesVersionId = version.Id,
                    Nom = pDto.Nom, QuantiteMax = pDto.QuantiteMax, Cout = pDto.Cout,
                    Mouvement = pDto.Mouvement, Force = pDto.Force, Agilite = pDto.Agilite,
                    CapacitePasse = pDto.CapacitePasse, Armure = pDto.Armure,
                    CompetencesPrincipales = pDto.CompetencesPrincipales,
                    CompetencesSecondaires = pDto.CompetencesSecondaires, MotsCles = pDto.MotsCles
                };
                db.PoolPositions.Add(pool);
                await db.SaveChangesAsync();

                foreach (var nomSkill in pDto.CompetencesDepart)
                {
                    if (!skillMap.TryGetValue(nomSkill, out var skillId))
                    {
                        errors.Add($"Compétence de départ (réserve) introuvable : « {nomSkill} » (poste : {pDto.Nom})");
                        continue;
                    }
                    db.PoolPositionSkills.Add(new PoolPositionSkill { PoolPositionId = pool.Id, SkillId = skillId });
                }

                var (poolP, poolS) = ResoudreAccesImport(pDto, categoriesCreees);
                foreach (var catId in poolP)
                    db.PoolPositionCategoryAccesses.Add(new PoolPositionCategoryAccess
                    { PoolPositionId = pool.Id, SkillCategoryDefId = catId, EstPrincipale = true });
                foreach (var catId in poolS)
                    db.PoolPositionCategoryAccesses.Add(new PoolPositionCategoryAccess
                    { PoolPositionId = pool.Id, SkillCategoryDefId = catId, EstPrincipale = false });
                await db.SaveChangesAsync();
            }

            // Staff configurable. Absent d'un JSON antérieur : la version reprend
            // alors le staff standard, matérialisé par la migration.
            var staffDto = dto.Staff is { Count: > 0 } ? dto.Staff : StaffParDefaut();

            foreach (var s in staffDto)
                db.StaffTypes.Add(new StaffDefinition
                {
                    RulesVersionId       = version.Id,
                    Nom                  = s.Nom,
                    Description          = s.Description,
                    Ordre                = s.Ordre,
                    EstActif             = s.EstActif,
                    Cout                 = s.Cout,
                    CoutDepuisTypeEquipe = s.CoutDepuisTypeEquipe,
                    MinCreation          = s.MinCreation,
                    MaxCreation          = s.MaxCreation,
                    MaxLigue             = s.MaxLigue,
                    // Absent d'un fichier antérieur : on retombe sur la règle
                    // métier plutôt que sur « true », qui remettrait les fans
                    // dans la VEA.
                    CompteDansVea        = s.CompteDansVea
                                           ?? !string.Equals(s.Nom, StaffService.NomFans,
                                                             StringComparison.OrdinalIgnoreCase)
                });
            await db.SaveChangesAsync();

            await tx.CommitAsync();
            logger.LogInformation("Import game data '{V}' : {NbTT} types, {NbS} skills, {NbErr} avertissements",
                versionNom, dto.TypesEquipes.Count, dto.Skills.Count, errors.Count);
            return (true, errors);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            logger.LogError(ex, "Import game data échoué pour version '{V}'", versionNom);
            return (false, [$"Erreur lors de l'import : {ex.Message}"]);
        }
    }

    /// <summary>
    /// Staff standard, utilisé quand un JSON antérieur à cette fonctionnalité
    /// est importé. Mêmes valeurs que le backfill de la migration.
    /// </summary>
    private static List<StaffTypeGdDto> StaffParDefaut() =>
    [
        new("Fans dévoués", "Public fidèle de l'équipe. Influence l'affluence et les gains de match.", 1, true, 10_000, false, 1, 9, null, false),
        new("Relances", "Relances d'équipe disponibles au début de chaque match. Leur prix dépend de la race.", 2, true, 0, true, 0, 8, 8, true),
        new("Coachs assistants", "Chaque coach assistant aide à récupérer l'avantage de terrain.", 3, true, 10_000, false, 0, 6, null, true),
        new("Cheerleaders", "Chaque cheerleader aide à récupérer l'avantage de terrain.", 4, true, 10_000, false, 0, 6, null, true),
        new("Apothicaire", "Permet de relancer un jet de blessure une fois par match.", 5, true, 50_000, false, 0, 1, 1, true),
    ];

    // ── Réserve seule ─────────────────────────────────────────────────────────

    public async Task<byte[]> ExportReserveAsync(int rulesVersionId)
    {
        var version = await db.RulesVersions
            .Include(v => v.Game)
            .FirstOrDefaultAsync(v => v.Id == rulesVersionId)
            ?? throw new InvalidOperationException("Version de règles introuvable");

        var reserve = await db.PoolPositions
            .Include(p => p.CompetencesDepart).ThenInclude(pps => pps.Skill)
            .Include(p => p.AccesCategories).ThenInclude(a => a.SkillCategoryDef)
            .Where(p => p.RulesVersionId == rulesVersionId)
            .OrderBy(p => p.Nom)
            .ToListAsync();

        // F3 : la Réserve fait partie des données de la version — un export
        // Réserve compte donc comme une révision de cette version.
        version.Revision++;
        version.DernierExportLe = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var dto = new ReserveExportDto(
            Jeu: version.Game.Nom,
            Version: version.Nom,
            Reserve: reserve.Select(p => new PlayerPositionGdDto(
                p.Nom, p.QuantiteMax, p.Cout, p.Mouvement, p.Force, p.Agilite,
                p.CapacitePasse, p.Armure, p.CompetencesPrincipales, p.CompetencesSecondaires,
                p.MotsCles,
                p.CompetencesDepart.Select(pps => pps.Skill.Nom).OrderBy(n => n).ToList(),
                p.AccesCategories.Where(a => a.EstPrincipale).Select(a => a.SkillCategoryDef.Nom).OrderBy(n => n).ToList(),
                p.AccesCategories.Where(a => !a.EstPrincipale).Select(a => a.SkillCategoryDef.Nom).OrderBy(n => n).ToList()
            )).ToList(),
            Revision: version.Revision,
            ExporteLe: version.DernierExportLe
        );

        logger.LogInformation("Export réserve : version '{V}' ({N} postes)", version.Nom, reserve.Count);
        return JsonSerializer.SerializeToUtf8Bytes(dto, JsonOpts);
    }

    /// <summary>
    /// Importe une réserve dans une version CIBLE. Mode AJOUT (append, pas de remplacement).
    /// Les compétences de départ sont résolues par nom parmi les skills de la version cible ;
    /// un nom introuvable est signalé mais n'interrompt pas l'import.
    /// </summary>
    public async Task<(bool Success, int Imported, List<string> Errors)> ImportReserveAsync(
        Stream stream, int rulesVersionId, bool confirmerMalgreRevision = false)
    {
        var errors = new List<string>();

        ReserveExportDto dto;
        try
        {
            dto = await JsonSerializer.DeserializeAsync<ReserveExportDto>(stream, JsonOpts)
                ?? throw new InvalidOperationException("Fichier JSON invalide");
        }
        catch (Exception ex)
        {
            return (false, 0, [$"Impossible de lire le fichier : {ex.Message}"]);
        }

        var version = await db.RulesVersions.FindAsync(rulesVersionId);
        if (version is null)
            return (false, 0, [$"Version id={rulesVersionId} introuvable"]);

        // F3 : refuser d'appliquer un fichier plus ancien que la base sans que
        // l'utilisateur l'ait explicitement confirmé.
        var controle = TracabiliteImport.Verifier(dto.Revision, version.Revision, dto.ExporteLe);
        if (controle.DemandeConfirmation && !confirmerMalgreRevision)
            return (false, 0, [controle.Message]);
        if (controle.Verdict != TracabiliteImport.Verdict.PlusRecent)
            logger.LogWarning("Import Réserve sur version {V} : {Message}", version.Nom, controle.Message);

        // skills de la version cible, indexés par nom
        var skillMap = await db.Skills
            .Where(s => s.RulesVersionId == rulesVersionId)
            .ToDictionaryAsync(s => s.Nom, s => s.Id, StringComparer.OrdinalIgnoreCase);

        // catégories de la version cible, pour résoudre les accès des postes importés
        var categoriesCible = await db.SkillCategories
            .Where(c => c.RulesVersionId == rulesVersionId)
            .ToListAsync();

        await using var tx = await db.Database.BeginTransactionAsync();
        try
        {
            int imported = 0;
            foreach (var pDto in dto.Reserve)
            {
                var pool = new PoolPosition
                {
                    RulesVersionId = rulesVersionId,
                    Nom = pDto.Nom, QuantiteMax = pDto.QuantiteMax, Cout = pDto.Cout,
                    Mouvement = pDto.Mouvement, Force = pDto.Force, Agilite = pDto.Agilite,
                    CapacitePasse = pDto.CapacitePasse, Armure = pDto.Armure,
                    CompetencesPrincipales = pDto.CompetencesPrincipales,
                    CompetencesSecondaires = pDto.CompetencesSecondaires, MotsCles = pDto.MotsCles
                };
                db.PoolPositions.Add(pool);
                await db.SaveChangesAsync();

                foreach (var nomSkill in pDto.CompetencesDepart)
                {
                    if (!skillMap.TryGetValue(nomSkill, out var skillId))
                    {
                        errors.Add($"Compétence introuvable dans la version cible : « {nomSkill} » (poste : {pDto.Nom})");
                        continue;
                    }
                    db.PoolPositionSkills.Add(new PoolPositionSkill { PoolPositionId = pool.Id, SkillId = skillId });
                }

                var (poolP, poolS) = ResoudreAccesImport(pDto, categoriesCible);
                foreach (var catId in poolP)
                    db.PoolPositionCategoryAccesses.Add(new PoolPositionCategoryAccess
                    { PoolPositionId = pool.Id, SkillCategoryDefId = catId, EstPrincipale = true });
                foreach (var catId in poolS)
                    db.PoolPositionCategoryAccesses.Add(new PoolPositionCategoryAccess
                    { PoolPositionId = pool.Id, SkillCategoryDefId = catId, EstPrincipale = false });
                await db.SaveChangesAsync();
                imported++;
            }

            await tx.CommitAsync();
            logger.LogInformation("Import réserve : {N} postes importés dans version {V} ({E} avertissements)",
                imported, rulesVersionId, errors.Count);
            return (true, imported, errors);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            return (false, 0, [$"Erreur lors de l'import : {ex.Message}"]);
        }
    }

    /// <summary>
    /// Résout les accès de catégorie d'un poste importé. Priorité aux listes par NOM
    /// (exports R2b) ; repli sur les codes historiques « GAF » pour les fichiers antérieurs.
    /// Renvoie (principales, secondaires) sous forme d'identifiants de catégorie.
    /// </summary>
    private static (List<int> principales, List<int> secondaires) ResoudreAccesImport(
        PlayerPositionGdDto dto, List<SkillCategoryDef> categories)
    {
        List<int> ParNoms(List<string> noms) => noms
            .Select(n => categories.FirstOrDefault(c => string.Equals(c.Nom, n, StringComparison.OrdinalIgnoreCase)))
            .Where(c => c is not null)
            .Select(c => c!.Id)
            .Distinct()
            .ToList();

        var principales = dto.AccesPrincipal is { Count: > 0 }
            ? ParNoms(dto.AccesPrincipal)
            : CategoryAccessHelpers.ResoudreCodesHistoriques(dto.CompetencesPrincipales, categories).Select(c => c.Id).ToList();

        var secondaires = dto.AccesSecondaire is { Count: > 0 }
            ? ParNoms(dto.AccesSecondaire)
            : CategoryAccessHelpers.ResoudreCodesHistoriques(dto.CompetencesSecondaires, categories).Select(c => c.Id).ToList();

        return (principales, secondaires.Where(s => !principales.Contains(s)).ToList());
    }

    // ── Catalogue de règles spéciales seul ───────────────────────────────────
    //
    // L'export global crée une NOUVELLE version à l'import : inutilisable pour
    // une instance déjà en service, qui a ses ligues et ses équipes en cours.
    // Ce format-ci transporte le seul catalogue et le FUSIONNE dans une version
    // existante, sans toucher aux races, aux postes ni aux compétences.

    /// <summary>
    /// Exporte le catalogue de règles d'une version, avec ses rattachements aux
    /// fiches d'équipe. Races et règles sont référencées par NOM, pour que le
    /// fichier reste portable d'une instance à l'autre.
    /// </summary>
    public async Task<byte[]> ExportReglesSpecialesAsync(int rulesVersionId)
    {
        var version = await db.RulesVersions
            .Include(v => v.Game)
            .FirstOrDefaultAsync(v => v.Id == rulesVersionId)
            ?? throw new InvalidOperationException("Version de règles introuvable");

        var regles = await db.SpecialRules
            .Where(r => r.RulesVersionId == rulesVersionId)
            .Include(r => r.TeamTypes).ThenInclude(l => l.TeamType)
            .OrderBy(r => r.Ordre).ThenBy(r => r.Nom)
            .ToListAsync();

        var dto = new ReglesSpecialesExportDto(
            Jeu: version.Game?.Nom ?? "",
            Version: version.Nom,
            Regles: regles.Select(r => new SpecialRulePortableDto(
                r.Nom, r.Description, r.Ordre, r.Code,
                r.TeamTypes
                    .Where(l => l.TeamType is not null)
                    .OrderBy(l => l.TeamType.Nom)
                    .Select(l => new RattachementPortableDto(l.TeamType.Nom, l.OptionsChoix, l.LimiteParApresMatch))
                    .ToList()
            )).ToList()
        );

        logger.LogInformation(
            "Export règles spéciales : version '{V}' ({Nb} règles)", version.Nom, regles.Count);

        return JsonSerializer.SerializeToUtf8Bytes(dto, JsonOpts);
    }

    /// <summary>
    /// Fusionne un catalogue dans une version EXISTANTE.
    ///
    /// Idempotent et non destructif : une règle déjà présente (même nom) est
    /// MISE À JOUR plutôt que dupliquée — c'est ainsi qu'on propage un
    /// correctif de description ou l'ajout d'un comportement automatique sur
    /// une instance en service. Aucune règle absente du fichier n'est
    /// supprimée : un catalogue local enrichi n'est jamais écrasé.
    ///
    /// Une race inconnue de la cible n'interrompt pas l'import : les autres
    /// rattachements passent et le nom manquant est signalé au commissaire.
    /// </summary>
    public async Task<(bool Success, List<string> Errors)> ImportReglesSpecialesAsync(
        int rulesVersionId, Stream stream)
    {
        var avertissements = new List<string>();

        ReglesSpecialesExportDto dto;
        try
        {
            dto = await JsonSerializer.DeserializeAsync<ReglesSpecialesExportDto>(stream, JsonOpts)
                ?? throw new InvalidOperationException("Fichier JSON invalide");
        }
        catch (Exception ex)
        {
            return (false, [$"Impossible de lire le fichier : {ex.Message}"]);
        }

        if (dto.Regles is null)
            return (false, ["Ce fichier ne contient pas de règles spéciales."]);

        var version = await db.RulesVersions.FirstOrDefaultAsync(v => v.Id == rulesVersionId);
        if (version is null)
            return (false, ["Version de règles introuvable."]);

        await using var transaction = await db.Database.BeginTransactionAsync();
        try
        {
            var reglesExistantes = await db.SpecialRules
                .Where(r => r.RulesVersionId == rulesVersionId)
                .ToListAsync();

            var racesCibles = await db.TeamTypes
                .Where(t => t.RulesVersionId == rulesVersionId)
                .ToListAsync();

            foreach (var regleDto in dto.Regles)
            {
                var regle = reglesExistantes
                    .FirstOrDefault(r => r.Nom.Equals(regleDto.Nom, StringComparison.OrdinalIgnoreCase));

                if (regle is null)
                {
                    regle = new SpecialRule { RulesVersionId = rulesVersionId, Nom = regleDto.Nom };
                    db.SpecialRules.Add(regle);
                    reglesExistantes.Add(regle);
                }

                regle.Description = regleDto.Description;
                regle.Ordre = regleDto.Ordre;
                regle.Code = regleDto.Code ?? "";
                await db.SaveChangesAsync();

                foreach (var lienDto in regleDto.Rattachements ?? [])
                {
                    var race = racesCibles
                        .FirstOrDefault(t => t.Nom.Equals(lienDto.EquipeNom, StringComparison.OrdinalIgnoreCase));

                    if (race is null)
                    {
                        avertissements.Add(
                            $"« {regleDto.Nom} » : équipe « {lienDto.EquipeNom} » absente de cette version, rattachement ignoré.");
                        continue;
                    }

                    var lien = await db.TeamTypeSpecialRules
                        .FirstOrDefaultAsync(l => l.TeamTypeId == race.Id && l.SpecialRuleId == regle.Id);

                    if (lien is null)
                    {
                        db.TeamTypeSpecialRules.Add(new TeamTypeSpecialRule
                        {
                            TeamTypeId = race.Id, SpecialRuleId = regle.Id,
                            OptionsChoix = lienDto.OptionsChoix ?? "",
                            LimiteParApresMatch = lienDto.LimiteParApresMatch ?? 1
                        });
                    }
                    else
                    {
                        lien.OptionsChoix = lienDto.OptionsChoix ?? "";
                        lien.LimiteParApresMatch = lienDto.LimiteParApresMatch ?? lien.LimiteParApresMatch;
                    }
                }

                await db.SaveChangesAsync();
            }

            await transaction.CommitAsync();
            logger.LogInformation(
                "Import règles spéciales dans la version id={Id} : {Nb} règles, {NbAvert} avertissement(s)",
                rulesVersionId, dto.Regles.Count, avertissements.Count);

            return (true, avertissements);
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            return (false, [$"Import interrompu : {ex.Message}"]);
        }
    }

    // ── Catalogue « coups de pouce + star players + ligues » ─────────────────

    /// <summary>
    /// Exporte le catalogue informatif d'une version : ligues thématiques,
    /// coups de pouce et star players avec leurs ligues d'accès.
    ///
    /// Tout est référencé par NOM pour rester portable d'une instance à
    /// l'autre : les identifiants d'une base ne valent rien dans une autre.
    /// </summary>
    public async Task<byte[]> ExportCatalogueAsync(int rulesVersionId)
    {
        var version = await db.RulesVersions
            .Include(v => v.Game)
            .FirstOrDefaultAsync(v => v.Id == rulesVersionId)
            ?? throw new InvalidOperationException("Version de règles introuvable");

        var ligues = await db.ThemedLeagues
            .Where(l => l.RulesVersionId == rulesVersionId)
            .OrderBy(l => l.Nom)
            .ToListAsync();

        var coupsDePouce = await db.Inducements
            .Where(i => i.RulesVersionId == rulesVersionId)
            .OrderBy(i => i.Ordre).ThenBy(i => i.Nom)
            .ToListAsync();

        var stars = await db.StarPlayers
            .Where(sp => sp.RulesVersionId == rulesVersionId)
            .Include(sp => sp.Ligues).ThenInclude(x => x.ThemedLeague)
            .OrderBy(sp => sp.Ordre).ThenBy(sp => sp.Nom)
            .ToListAsync();

        var dto = new CataloguePortableDto(
            Jeu: version.Game?.Nom ?? "",
            Version: version.Nom,
            Ligues: ligues.Select(l => new LiguePortableDto(l.Nom)).ToList(),
            CoupsDePouce: coupsDePouce.Select(i => new CoupDePoucePortableDto(
                i.Nom, i.Description, i.Cout, i.QuantiteMax, i.Restriction)).ToList(),
            StarPlayers: stars.Select(sp => new StarPlayerPortableDto(
                sp.Nom, sp.Cout, sp.Mouvement, sp.Force, sp.Agilite, sp.CapacitePasse,
                sp.Armure, sp.Competences, sp.ReglesSpeciales,
                sp.Ligues.Where(x => x.ThemedLeague is not null)
                         .Select(x => x.ThemedLeague.Nom).OrderBy(n => n).ToList())).ToList()
        );

        logger.LogInformation(
            "Export catalogue : version '{V}' ({L} ligues, {C} coups de pouce, {S} star players)",
            version.Nom, ligues.Count, coupsDePouce.Count, stars.Count);

        return JsonSerializer.SerializeToUtf8Bytes(dto, JsonOpts);
    }

    /// <summary>
    /// Fusionne un catalogue dans une version EXISTANTE.
    ///
    /// Idempotent et non destructif, comme l'import des règles spéciales :
    /// une entrée déjà présente (même nom) est MISE À JOUR plutôt que
    /// dupliquée, et rien n'est supprimé — un catalogue local enrichi n'est
    /// jamais écrasé.
    ///
    /// Une ligue citée par un star player mais absente est CRÉÉE plutôt
    /// qu'ignorée : la laisser manquante rendrait le joueur accessible à
    /// toutes les équipes, l'inverse exact de la restriction voulue.
    /// </summary>
    public async Task<(bool Success, List<string> Errors)> ImportCatalogueAsync(
        int rulesVersionId, Stream stream)
    {
        var avertissements = new List<string>();

        CataloguePortableDto dto;
        try
        {
            dto = await JsonSerializer.DeserializeAsync<CataloguePortableDto>(stream, JsonOpts)
                ?? throw new InvalidOperationException("Fichier JSON invalide");
        }
        catch (Exception ex)
        {
            return (false, [$"Lecture du fichier impossible : {ex.Message}"]);
        }

        var version = await db.RulesVersions.FindAsync(rulesVersionId);
        if (version is null) return (false, ["Version de règles cible introuvable."]);

        using var transaction = await db.Database.BeginTransactionAsync();
        try
        {
            // ── Ligues ──────────────────────────────────────────────────────
            var ligues = await db.ThemedLeagues
                .Where(l => l.RulesVersionId == rulesVersionId)
                .ToListAsync();

            async Task<ThemedLeague> LigueOuCreee(string nom)
            {
                var existante = ligues.FirstOrDefault(l =>
                    string.Equals(l.Nom, nom, StringComparison.OrdinalIgnoreCase));
                if (existante is not null) return existante;

                var nouvelle = new ThemedLeague { RulesVersionId = rulesVersionId, Nom = nom };
                db.ThemedLeagues.Add(nouvelle);
                await db.SaveChangesAsync();
                ligues.Add(nouvelle);
                return nouvelle;
            }

            foreach (var l in dto.Ligues ?? [])
                if (!string.IsNullOrWhiteSpace(l.Nom))
                    await LigueOuCreee(l.Nom.Trim());

            // ── Coups de pouce ──────────────────────────────────────────────
            var cpExistants = await db.Inducements
                .Where(i => i.RulesVersionId == rulesVersionId)
                .ToListAsync();

            foreach (var c in dto.CoupsDePouce ?? [])
            {
                if (string.IsNullOrWhiteSpace(c.Nom)) continue;

                var cible = cpExistants.FirstOrDefault(i =>
                    string.Equals(i.Nom, c.Nom, StringComparison.OrdinalIgnoreCase));

                if (cible is null)
                {
                    cible = new Inducement { RulesVersionId = rulesVersionId, Nom = c.Nom.Trim() };
                    db.Inducements.Add(cible);
                    cpExistants.Add(cible);
                }

                cible.Description = c.Description ?? "";
                cible.Cout = Math.Max(0, c.Cout);
                cible.QuantiteMax = Math.Max(0, c.QuantiteMax);
                cible.Restriction = c.Restriction ?? "";
            }
            await db.SaveChangesAsync();

            // ── Star players ────────────────────────────────────────────────
            var spExistants = await db.StarPlayers
                .Where(sp => sp.RulesVersionId == rulesVersionId)
                .Include(sp => sp.Ligues)
                .ToListAsync();

            foreach (var s in dto.StarPlayers ?? [])
            {
                if (string.IsNullOrWhiteSpace(s.Nom)) continue;

                var cible = spExistants.FirstOrDefault(sp =>
                    string.Equals(sp.Nom, s.Nom, StringComparison.OrdinalIgnoreCase));

                if (cible is null)
                {
                    cible = new StarPlayer { RulesVersionId = rulesVersionId, Nom = s.Nom.Trim() };
                    db.StarPlayers.Add(cible);
                    spExistants.Add(cible);
                }

                cible.Cout = Math.Max(0, s.Cout);
                cible.Mouvement = s.Mouvement;
                cible.Force = s.Force;
                cible.Agilite = s.Agilite ?? "3+";
                cible.CapacitePasse = s.CapacitePasse ?? "-";
                cible.Armure = s.Armure ?? "9+";
                cible.Competences = s.Competences ?? "";
                cible.ReglesSpeciales = s.ReglesSpeciales ?? "";
                await db.SaveChangesAsync();   // besoin de l'Id pour les liaisons

                // Ligues : on remplace la sélection par celle du fichier.
                var actuelles = await db.Set<StarPlayerThemedLeague>()
                    .Where(x => x.StarPlayerId == cible.Id)
                    .ToListAsync();
                db.Set<StarPlayerThemedLeague>().RemoveRange(actuelles);

                foreach (var nomLigue in s.Ligues ?? [])
                {
                    if (string.IsNullOrWhiteSpace(nomLigue)) continue;
                    var ligue = await LigueOuCreee(nomLigue.Trim());
                    db.Set<StarPlayerThemedLeague>().Add(new StarPlayerThemedLeague
                    {
                        StarPlayerId = cible.Id, ThemedLeagueId = ligue.Id
                    });
                }
                await db.SaveChangesAsync();
            }

            await transaction.CommitAsync();

            logger.LogInformation(
                "Import catalogue dans '{V}' : {L} ligues, {C} coups de pouce, {S} star players",
                version.Nom, dto.Ligues?.Count ?? 0, dto.CoupsDePouce?.Count ?? 0,
                dto.StarPlayers?.Count ?? 0);

            return (true, avertissements);
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            return (false, [$"Import interrompu : {ex.Message}"]);
        }
    }
}

// ── DTOs ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Fichier de catalogue de règles spéciales SEUL, destiné à être fusionné dans
/// une version existante. <c>Jeu</c> et <c>Version</c> sont informatifs (ils
/// disent d'où vient le fichier) : la cible est choisie à l'import.
/// </summary>
record ReglesSpecialesExportDto(
    string Jeu,
    string Version,
    List<SpecialRulePortableDto> Regles
);

/// <summary>Règle + ses rattachements, tous référencés par NOM.</summary>
record SpecialRulePortableDto(
    string Nom,
    string Description,
    int Ordre,
    string? Code,
    List<RattachementPortableDto>? Rattachements
);

record RattachementPortableDto(
    string EquipeNom,
    string? OptionsChoix,
    // Plafond de recrues offertes par apres-match. Nullable : un fichier
    // exporte AVANT cette version n'a pas le champ, on retombe alors sur 1
    // (la valeur du livre de regles) plutot que sur 0 = illimite.
    int? LimiteParApresMatch = null
);

/// <summary>
/// Fichier de catalogue « coups de pouce + star players + ligues » SEUL,
/// destiné à être fusionné dans une version existante — même principe que le
/// catalogue de règles spéciales.
///
/// C'est ce fichier qui porte les textes complets saisis en local vers le VPS :
/// ils vivent en base et dans ce transport, jamais dans le dépôt.
/// </summary>
record CataloguePortableDto(
    string Jeu,
    string Version,
    List<LiguePortableDto> Ligues,
    List<CoupDePoucePortableDto> CoupsDePouce,
    List<StarPlayerPortableDto> StarPlayers
);

record LiguePortableDto(string Nom);

record CoupDePoucePortableDto(
    string Nom,
    string Description,
    int Cout,
    int QuantiteMax,
    string? Restriction
);

/// <summary>Star player + les ligues qui y donnent accès, référencées par NOM.</summary>
record StarPlayerPortableDto(
    string Nom,
    int Cout,
    int Mouvement,
    int Force,
    string Agilite,
    string CapacitePasse,
    string Armure,
    string Competences,
    string? ReglesSpeciales,
    List<string>? Ligues
);

record GameDataExportDto(
    string Jeu,
    string Version,
    int Ordre,
    bool EstActive,
    List<SkillGdDto> Skills,
    List<TeamTypeGdDto> TypesEquipes,
    List<PlayerPositionGdDto>? Reserve = null,  // ← AJOUT optionnel (rétrocompat)
    List<SkillCategoryGdDto>? Categories = null, // ← AJOUT optionnel (rétrocompat, R2)
    // Barème d'XP de la version (R6). Optionnels : un export antérieur reprend
    // les valeurs par défaut du jeu à l'import.
    // Traçabilité (F3). Optionnels : un JSON exporté avant cette version n'en a
    // pas, et doit rester importable tel quel.
    int? Revision = null,
    DateTime? ExporteLe = null,
    int? XpParTouchdown = null,
    int? XpParPasse = null,
    int? XpParInterception = null,
    int? XpParElimination = null,
    int? XpBonusMvp = null,
    int? XpParDeviation = null,
    int? XpParAgression = null,
    // Barème de points de CLASSEMENT de la version. Optionnels : un export
    // antérieur reprend le barème par défaut (3 / 1 / 0, aucun bonus).
    int? PointsVictoire = null,
    int? PointsNul = null,
    int? PointsDefaite = null,
    int? PointsParTouchdown = null,
    int? PointsParElimination = null,
    int? PointsParInterception = null,
    int? PointsParPasse = null,
    int? PointsParDeviation = null,
    int? PointsParAgression = null,
    // Staff configurable. Optionnel : un JSON exporté avant cette fonctionnalité
    // reste importable, la version reprend alors le staff standard.
    List<StaffTypeGdDto>? Staff = null,
    // Catalogue de règles spéciales (LRB p.93-94). Optionnel pour la même
    // raison : un export antérieur s'importe sans règles spéciales.
    List<SpecialRuleGdDto>? ReglesSpeciales = null,
    // Barème des AMÉLIORATIONS de joueur (hausse de valeur). Optionnels : un
    // export antérieur retombe sur le barème LRB (BaremeAmelioration.ParDefaut),
    // surtout PAS sur des zéros — une amélioration ne vaudrait alors plus rien.
    int? HaussePrincipale = null,
    int? HausseSecondaire = null,
    int? HausseArmure = null,
    int? HausseMouvement = null,
    int? HausseCapacitePasse = null,
    int? HausseAgilite = null,
    int? HausseForce = null,
    int? SurcoutElite = null,
    // Coût en PSP par rang. Absent d'un export antérieur ⇒ tableau LRB.
    List<PalierAmeliorationGdDto>? PaliersAmelioration = null
);

/// <summary>Coût en PSP d'une amélioration pour un rang donné (1 à 6).</summary>
record PalierAmeliorationGdDto(
    int Rang,
    int CoutAleaPrincipale,
    int CoutChoixPrincipale,
    int CoutSecondaire,
    int CoutCaracteristique
);

/// <summary>
/// Règle spéciale exportée. Le rattachement aux fiches d'équipe se fait par NOM
/// de règle, côté <c>TeamTypeGdDto.ReglesSpecialesRattachees</c> — comme partout
/// ailleurs dans cet export, pour qu'un fichier reste portable entre instances.
/// </summary>
record SpecialRuleGdDto(
    string Nom,
    string Description,
    int Ordre,
    string Code
);

/// <summary>Rattachement d'une règle à une fiche d'équipe, référencée par nom.</summary>
record TeamTypeSpecialRuleGdDto(
    string RegleNom,
    string OptionsChoix,
    // Nullable : un export antérieur à cette version n'a pas le champ, on
    // retombe alors sur 1 (valeur du livre de règles) et non sur 0 = illimité.
    int? LimiteParApresMatch = null
);

/// <summary>Définition de staff exportée. Référencée par NOM, comme le reste.</summary>
record StaffTypeGdDto(
    string Nom,
    string Description,
    int Ordre,
    bool EstActif,
    int Cout,
    bool CoutDepuisTypeEquipe,
    int MinCreation,
    int MaxCreation,
    int? MaxLigue,
    // Nullable : un fichier antérieur à ce champ n'en a pas. Le repli vise la
    // valeur MÉTIER — les fans hors VEA, tout le reste dedans — sinon
    // réimporter un ancien export rouvrirait le trou qu'on vient de fermer.
    bool? CompteDansVea = null
);

record ReserveExportDto(
    string Jeu,
    string Version,
    List<PlayerPositionGdDto> Reserve,
    // Traçabilité (F3), optionnelle pour rester rétrocompatible.
    int? Revision = null,
    DateTime? ExporteLe = null
);

record SkillGdDto(
    string Nom,
    SkillCategory Categorie,
    string Description,
    bool EstElite,
    bool EstTrait,
    // Nom de la catégorie (catégories devenues éditables). Null sur les exports
    // antérieurs : on retombe alors sur le champ Categorie (ancien enum).
    string? CategorieNom = null
);

/// <summary>Catégorie de compétence exportée. Absent des fichiers antérieurs à R2.</summary>
record SkillCategoryGdDto(
    string Nom,
    string Code
);

record TeamTypeGdDto(
    string Nom,
    // Nom de champ JSON volontairement DIFFÉRENT de l'ancien « Categorie ».
    // L'ancien champ portait le style de jeu maison (0=Bashy … 3=Specialist),
    // sérialisé en int : réutiliser le même nom ferait relire « 2 » (Agile)
    // comme « catégorie LRB 2 » dans tout export antérieur — une donnée fausse
    // et silencieuse. Un ancien fichier importe donc CategorieLrb = 0
    // (« à renseigner »), ce qui est exact.
    int CategorieLrb,
    int CoutRelance,
    string ReglesSpeciales,
    string Ligues,
    List<PlayerPositionGdDto> Postes,
    List<KeywordLimitGdDto> Limites,
    // Règles spéciales rattachées à cette fiche, référencées par nom.
    // Optionnel : un export antérieur au catalogue s'importe sans rattachement.
    List<TeamTypeSpecialRuleGdDto>? ReglesSpecialesRattachees = null
);

record PlayerPositionGdDto(
    string Nom,
    int QuantiteMax,
    int Cout,
    int Mouvement,
    int Force,
    string Agilite,
    string CapacitePasse,
    string Armure,
    // Codes historiques (« GAF »). Conservés pour relire les exports antérieurs à R2b ;
    // ignorés dès que AccesPrincipal / AccesSecondaire sont présents.
    string CompetencesPrincipales,
    string CompetencesSecondaires,
    string MotsCles,
    List<string> CompetencesDepart,
    // Accès par NOM de catégorie (R2b) : seule forme compatible avec des codes à 2 caractères.
    List<string>? AccesPrincipal = null,
    List<string>? AccesSecondaire = null
);

record KeywordLimitGdDto(string MotCle, int Max);
