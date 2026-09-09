using BolDeSangManager.Data;
using BolDeSangManager.Data.Enums;
using BolDeSangManager.Data.Models;
using BolDeSangManager.Data.Seeding;
using Microsoft.EntityFrameworkCore;

namespace BolDeSangManager.Services;

/// <summary>
/// CRUD validé pour les données de jeu (TeamType, PlayerPosition, Skill, RulesVersion, KeywordLimit).
/// Réservé Admin/GrandCommissaire (auth gate au layer UI).
/// </summary>
public class DataEditService(ApplicationDbContext db, ILogger<DataEditService> logger)
{
    // ═══════════════════ RulesVersion ═══════════════════

    public async Task<List<RulesVersion>> GetVersionsAsync(int gameId) =>
        await db.RulesVersions
            .Where(v => v.GameId == gameId)
            .OrderBy(v => v.Ordre)
            .ToListAsync();

    /// <summary>
    /// Crée une version de règles. L'ordre et le statut actif ne sont plus
    /// demandés à l'utilisateur :
    /// - l'ordre est calculé automatiquement (dernier + 1 pour ce jeu) ;
    /// - la version n'est active que si c'est la PREMIÈRE du jeu, sinon on
    ///   bascule explicitement via ActiverVersionAsync (bouton dédié).
    /// </summary>
    public async Task<RulesVersion> CreerVersionAsync(int gameId, string nom, int? cloneFromVersionId)
    {
        var nomNet = (nom ?? "").Trim();
        if (string.IsNullOrWhiteSpace(nomNet))
            throw new InvalidOperationException("Le nom de la version est obligatoire.");
        if (nomNet.Length > 100)
            throw new InvalidOperationException("Le nom de la version ne peut pas dépasser 100 caractères.");

        var doublon = await db.RulesVersions
            .AnyAsync(v => v.GameId == gameId && v.Nom.ToLower() == nomNet.ToLower());
        if (doublon)
            throw new InvalidOperationException($"Une autre version de ce jeu s'appelle déjà « {nomNet} ».");

        // Tout est fait dans UNE transaction : si le clonage échoue, la version
        // ne doit pas rester en base à moitié remplie (sinon la liste se pollue
        // de versions vides après chaque erreur).
        await using var tx = await db.Database.BeginTransactionAsync();
        try
        {
            var ordre = (await db.RulesVersions
                .Where(v => v.GameId == gameId)
                .MaxAsync(v => (int?)v.Ordre) ?? 0) + 1;

            // Première version du jeu : elle doit être active, sinon aucune ne
            // le serait et la création de ligue n'aurait pas de valeur par défaut.
            var premiere = !await db.RulesVersions.AnyAsync(v => v.GameId == gameId);

            var nouvelle = new RulesVersion { GameId = gameId, Nom = nomNet, Ordre = ordre, EstActive = premiere };
            db.RulesVersions.Add(nouvelle);
            await db.SaveChangesAsync();

            if (cloneFromVersionId is int srcId)
                await ClonerVersionAsync(srcId, nouvelle.Id);

            await tx.CommitAsync();
            logger.LogInformation("Version créée : {Nom} (id={Id}) sur Game={GameId} (cloneFrom={Clone}, active={Active})", nomNet, nouvelle.Id, gameId, cloneFromVersionId, premiere);
            return nouvelle;
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    private async Task ClonerVersionAsync(int sourceVersionId, int destVersionId)
    {
        // Pas de transaction ici : l'appelant en ouvre une (transactions
        // imbriquées interdites par EF Core sur SQLite).

        // 0. Reprendre le barème d'XP de la version source (R6)
        var vSource = await db.RulesVersions.FirstOrDefaultAsync(v => v.Id == sourceVersionId);
        var vDest   = await db.RulesVersions.FirstOrDefaultAsync(v => v.Id == destVersionId);
        if (vSource is not null && vDest is not null)
        {
            vDest.PspParTouchdown    = vSource.PspParTouchdown;
            vDest.PspParPasse        = vSource.PspParPasse;
            vDest.PspParInterception = vSource.PspParInterception;
            vDest.PspParElimination  = vSource.PspParElimination;
            vDest.PspBonusMvp        = vSource.PspBonusMvp;
            vDest.PspParDeviation    = vSource.PspParDeviation;
            vDest.PspParAgression    = vSource.PspParAgression;
            // Barème de points de classement : mêmes règles, même clonage.
            BaremePoints.DeVersion(vSource).AppliquerA(vDest);
            // Barème des améliorations : les 8 hausses de valeur…
            var haussesSource = new BaremeAmelioration
            {
                HaussePrincipale    = vSource.HaussePrincipale,
                HausseSecondaire    = vSource.HausseSecondaire,
                HausseArmure        = vSource.HausseArmure,
                HausseMouvement     = vSource.HausseMouvement,
                HausseCapacitePasse = vSource.HausseCapacitePasse,
                HausseAgilite       = vSource.HausseAgilite,
                HausseForce         = vSource.HausseForce,
                SurcoutElite        = vSource.SurcoutElite
            };
            haussesSource.AppliquerA(vDest);
            await db.SaveChangesAsync();
        }

        // 0 bis. …et les 6 paliers de coût PSP. Une version source jamais
        // paramétrée n'en a aucun : on pose alors le tableau LRB, jamais rien —
        // sinon la version clonée proposerait 0 PSP partout.
        {
            var paliersSource = await db.PaliersAmelioration
                .Where(p => p.RulesVersionId == sourceVersionId)
                .OrderBy(p => p.Rang)
                .ToListAsync();
            if (paliersSource.Count == 0)
                paliersSource = BaremeAmelioration.PaliersLrbParDefaut();

            foreach (var src in paliersSource)
                db.PaliersAmelioration.Add(new PalierAmeliorationPsp
                {
                    RulesVersionId      = destVersionId,
                    Rang                = src.Rang,
                    CoutAleaPrincipale  = src.CoutAleaPrincipale,
                    CoutChoixPrincipale = src.CoutChoixPrincipale,
                    CoutSecondaire      = src.CoutSecondaire,
                    CoutCaracteristique = src.CoutCaracteristique
                });
            await db.SaveChangesAsync();
        }

        // 1. Cloner les catégories de compétence + map oldId → newId
        var sourceCategories = await db.SkillCategories
            .Where(c => c.RulesVersionId == sourceVersionId)
            .ToListAsync();
        var categorieMap = new Dictionary<int, SkillCategoryDef>();
        foreach (var srcCat in sourceCategories)
        {
            var copieCat = new SkillCategoryDef
            {
                RulesVersionId = destVersionId,
                Nom = srcCat.Nom,
                Code = srcCat.Code
            };
            db.SkillCategories.Add(copieCat);
            categorieMap[srcCat.Id] = copieCat;
        }
        await db.SaveChangesAsync();

        // 2. Cloner les Skills + map oldId → newSkill (en rattachant à la catégorie clonée)
        var sourceSkills = await db.Skills.Where(s => s.RulesVersionId == sourceVersionId).ToListAsync();

        // Repli par NOM : si une compétence source pointe vers une catégorie qui
        // n'appartient pas à la version source (donnée héritée d'un incident), on
        // la rattache à la catégorie clonée portant le nom standard de son enum.
        // Sans cela on recopiait l'id étranger tel quel → FOREIGN KEY constraint failed.
        var categoriesParNom = categorieMap.Values
            .ToDictionary(c => c.Nom, c => c, StringComparer.OrdinalIgnoreCase);

        var skillMap = new Dictionary<int, Skill>();
        foreach (var src in sourceSkills)
        {
            SkillCategoryDef? cible;
            if (!categorieMap.TryGetValue(src.SkillCategoryDefId, out cible))
            {
                var nomStandard = StandardSkillCategories.Nom(src.Categorie);
                if (!categoriesParNom.TryGetValue(nomStandard, out cible))
                    throw new InvalidOperationException(
                        $"La compétence « {src.Nom} » référence une catégorie absente de sa version " +
                        $"et aucune catégorie « {nomStandard} » n'existe pour la remplacer. " +
                        "Corrigez la catégorie de cette compétence avant de cloner.");

                logger.LogWarning(
                    "Clonage : compétence « {Nom} » rattachée à une catégorie étrangère (id={Id}), " +
                    "repli sur « {Cible} » de la version clonée.",
                    src.Nom, src.SkillCategoryDefId, cible.Nom);
            }

            var copie = new Skill
            {
                Nom = src.Nom,
                Categorie = src.Categorie,
                SkillCategoryDefId = cible.Id,
                Description = src.Description,
                EstElite = src.EstElite,
                EstTrait = src.EstTrait,
                RulesVersionId = destVersionId
            };
            db.Skills.Add(copie);
            skillMap[src.Id] = copie;
        }
        await db.SaveChangesAsync();

        // 3. Cloner les TeamTypes + map
        var sourceTypes = await db.TeamTypes
            .Include(t => t.Postes).ThenInclude(p => p.CompetencesDepart)
            .Include(t => t.Postes).ThenInclude(p => p.AccesCategories)
            .Include(t => t.LimitesMotsCles)
            .Where(t => t.RulesVersionId == sourceVersionId)
            .ToListAsync();

        var teamTypeMap = new Dictionary<int, TeamType>();
        foreach (var src in sourceTypes)
        {
            var copie = new TeamType
            {
                GameId = src.GameId,
                RulesVersionId = destVersionId,
                Nom = src.Nom,
                CoutRelance = src.CoutRelance,
                Categorie = src.Categorie,
                ReglesSpeciales = src.ReglesSpeciales,
                LiguesTexteObsolete = src.LiguesTexteObsolete
            };
            db.TeamTypes.Add(copie);
            teamTypeMap[src.Id] = copie;
        }
        await db.SaveChangesAsync();

        // 3 bis. Cloner le catalogue de règles spéciales, puis les
        // rattachements aux fiches d'équipe (remappés via les deux maps).
        // Sans ce bloc, créer une nouvelle édition perdrait toutes les règles
        // spéciales — l'association devrait tout ressaisir à chaque saison.
        var sourceRegles = await db.SpecialRules
            .Where(r => r.RulesVersionId == sourceVersionId)
            .ToListAsync();

        var regleMap = new Dictionary<int, SpecialRule>();
        foreach (var src in sourceRegles)
        {
            var copie = new SpecialRule
            {
                RulesVersionId = destVersionId,
                Nom = src.Nom,
                Description = src.Description,
                Ordre = src.Ordre,
                Code = src.Code
            };
            db.SpecialRules.Add(copie);
            regleMap[src.Id] = copie;
        }
        await db.SaveChangesAsync();

        var sourceLiaisons = await db.TeamTypeSpecialRules
            .Where(l => l.SpecialRule.RulesVersionId == sourceVersionId)
            .ToListAsync();

        foreach (var lien in sourceLiaisons)
        {
            // Une liaison ne se clone que si SES DEUX extrémités ont été
            // clonées : sinon on fabriquerait une FK vers une autre version.
            if (!teamTypeMap.TryGetValue(lien.TeamTypeId, out var destType)) continue;
            if (!regleMap.TryGetValue(lien.SpecialRuleId, out var destRegle)) continue;

            db.TeamTypeSpecialRules.Add(new TeamTypeSpecialRule
            {
                TeamTypeId = destType.Id,
                SpecialRuleId = destRegle.Id,
                OptionsChoix = lien.OptionsChoix
            });
        }
        await db.SaveChangesAsync();

        // 4. Cloner les PlayerPositions + leurs CompetencesDepart (avec mapping skill)
        foreach (var src in sourceTypes)
        {
            var destType = teamTypeMap[src.Id];
            foreach (var pos in src.Postes)
            {
                var copie = new PlayerPosition
                {
                    TeamTypeId = destType.Id,
                    Nom = pos.Nom,
                    QuantiteMax = pos.QuantiteMax,
                    Cout = pos.Cout,
                    Mouvement = pos.Mouvement,
                    Force = pos.Force,
                    Agilite = pos.Agilite,
                    CapacitePasse = pos.CapacitePasse,
                    Armure = pos.Armure,
                    MotsCles = pos.MotsCles
                };
                db.PlayerPositions.Add(copie);
                await db.SaveChangesAsync();

                foreach (var pps in pos.CompetencesDepart)
                {
                    if (skillMap.TryGetValue(pps.SkillId, out var newSkill))
                    {
                        db.PlayerPositionSkills.Add(new PlayerPositionSkill
                        {
                            PlayerPositionId = copie.Id,
                            SkillId = newSkill.Id
                        });
                    }
                }

                // Accès de catégorie : remap vers les catégories clonées
                foreach (var acc in pos.AccesCategories)
                {
                    if (categorieMap.TryGetValue(acc.SkillCategoryDefId, out var newCat))
                    {
                        db.PlayerPositionCategoryAccesses.Add(new PlayerPositionCategoryAccess
                        {
                            PlayerPositionId = copie.Id,
                            SkillCategoryDefId = newCat.Id,
                            EstPrincipale = acc.EstPrincipale
                        });
                    }
                }
            }

            // Limites mot-clé
            foreach (var lim in src.LimitesMotsCles)
            {
                db.TeamTypeKeywordLimits.Add(new TeamTypeKeywordLimit
                {
                    TeamTypeId = destType.Id,
                    MotCle = lim.MotCle,
                    Max = lim.Max
                });
            }
        }
        await db.SaveChangesAsync();

        // Cloner les PoolPositions (Réserve) + leurs compétences de départ (remap skills)
        var sourcePools = await db.PoolPositions
            .Include(p => p.CompetencesDepart)
            .Include(p => p.AccesCategories)
            .Where(p => p.RulesVersionId == sourceVersionId)
            .ToListAsync();

        foreach (var srcPool in sourcePools)
        {
            var copie = new PoolPosition
            {
                RulesVersionId = destVersionId,
                Nom = srcPool.Nom, QuantiteMax = srcPool.QuantiteMax, Cout = srcPool.Cout,
                Mouvement = srcPool.Mouvement, Force = srcPool.Force, Agilite = srcPool.Agilite,
                CapacitePasse = srcPool.CapacitePasse, Armure = srcPool.Armure,
                MotsCles = srcPool.MotsCles
            };
            db.PoolPositions.Add(copie);
            await db.SaveChangesAsync();

            foreach (var pps in srcPool.CompetencesDepart)
                if (skillMap.TryGetValue(pps.SkillId, out var newSkill))
                    db.PoolPositionSkills.Add(new PoolPositionSkill
                    {
                        PoolPositionId = copie.Id,
                        SkillId = newSkill.Id
                    });

            foreach (var acc in srcPool.AccesCategories)
                if (categorieMap.TryGetValue(acc.SkillCategoryDefId, out var newCat))
                    db.PoolPositionCategoryAccesses.Add(new PoolPositionCategoryAccess
                    {
                        PoolPositionId = copie.Id,
                        SkillCategoryDefId = newCat.Id,
                        EstPrincipale = acc.EstPrincipale
                    });
        }
        await db.SaveChangesAsync();

        // Cloner les définitions de staff. Étape obligatoire : sans elle, une
        // nouvelle édition de règles naîtrait sans aucun staff et les ligues
        // créées dessus n'auraient ni fans, ni relances, ni apothicaire.
        var sourceStaff = await db.StaffTypes
            .Where(s => s.RulesVersionId == sourceVersionId)
            .ToListAsync();

        foreach (var src in sourceStaff)
            db.StaffTypes.Add(new StaffDefinition
            {
                RulesVersionId       = destVersionId,
                Nom                  = src.Nom,
                Description          = src.Description,
                Ordre                = src.Ordre,
                EstActif             = src.EstActif,
                Cout                 = src.Cout,
                CoutDepuisTypeEquipe = src.CoutDepuisTypeEquipe,
                MinCreation          = src.MinCreation,
                MaxCreation          = src.MaxCreation,
                MaxLigue             = src.MaxLigue,
                CompteDansVea        = src.CompteDansVea
            });
        await db.SaveChangesAsync();

        logger.LogInformation("Clonage : v{Src} → v{Dest} ({NbSkills} skills, {NbTypes} types, {NbStaff} staff)", sourceVersionId, destVersionId, sourceSkills.Count, sourceTypes.Count, sourceStaff.Count);
    }

    /// <summary>
    /// Renomme une version de règles. Le nom doit être non vide et unique
    /// au sein du même jeu (comparaison insensible à la casse).
    /// Renommer est TOUJOURS autorisé, même sur une version active ou utilisée
    /// par des ligues : tout est lié par id, jamais par libellé.
    /// </summary>
    public async Task RenommerVersionAsync(int id, string nouveauNom)
    {
        var version = await db.RulesVersions.FindAsync(id)
            ?? throw new InvalidOperationException("Version introuvable");

        var nom = (nouveauNom ?? "").Trim();
        if (string.IsNullOrWhiteSpace(nom))
            throw new InvalidOperationException("Le nom de la version est obligatoire.");
        if (nom.Length > 100)
            throw new InvalidOperationException("Le nom de la version ne peut pas dépasser 100 caractères.");

        if (string.Equals(nom, version.Nom, StringComparison.Ordinal))
            return; // aucun changement

        var doublon = await db.RulesVersions
            .AnyAsync(v => v.GameId == version.GameId
                        && v.Id != id
                        && v.Nom.ToLower() == nom.ToLower());
        if (doublon)
            throw new InvalidOperationException(
                $"Une autre version de ce jeu s'appelle déjà « {nom} ».");

        var ancien = version.Nom;
        version.Nom = nom;
        await db.SaveChangesAsync();
        logger.LogInformation("Version renommée : « {Ancien} » → « {Nouveau} » (id={Id})", ancien, nom, id);
    }

    /// <summary>
    /// Rend une version active pour son jeu. Une seule version active par jeu :
    /// les autres sont désactivées dans la même transaction.
    /// C'est la version utilisée par défaut à la création d'une ligue.
    /// </summary>
    public async Task ActiverVersionAsync(int id)
    {
        var version = await db.RulesVersions.FindAsync(id)
            ?? throw new InvalidOperationException("Version introuvable");

        if (version.EstActive)
            return; // déjà active : rien à faire (idempotent)

        await using var tx = await db.Database.BeginTransactionAsync();
        try
        {
            var autres = await db.RulesVersions
                .Where(v => v.GameId == version.GameId && v.EstActive && v.Id != id)
                .ToListAsync();
            foreach (var a in autres) a.EstActive = false;

            version.EstActive = true;
            await db.SaveChangesAsync();

            await tx.CommitAsync();
            logger.LogInformation("Version activée : {Nom} (id={Id}) pour Game={GameId}", version.Nom, id, version.GameId);
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    public async Task SupprimerVersionAsync(int id)
    {
        var version = await db.RulesVersions.FindAsync(id)
            ?? throw new InvalidOperationException("Version introuvable");

        if (version.EstActive)
            throw new InvalidOperationException("Impossible de supprimer la version active. Activez une autre version d'abord.");

        var nbEquipes = await db.Teams.CountAsync(t => t.TeamType.RulesVersionId == id);
        if (nbEquipes > 0)
            throw new InvalidOperationException($"{nbEquipes} équipe(s) utilisent un type de cette version. Supprimez ces équipes d'abord.");

        // League → RulesVersion est en cascade par convention EF : sans ce garde-fou,
        // supprimer une version effacerait silencieusement les ligues qui s'en servent.
        var nbLigues = await db.Leagues.CountAsync(l => l.RulesVersionId == id);
        if (nbLigues > 0)
            throw new InvalidOperationException($"{nbLigues} ligue(s) utilisent cette version. Supprimez ces ligues d'abord.");

        await using var tx = await db.Database.BeginTransactionAsync();
        try
        {
            var teamTypes = await db.TeamTypes.Where(t => t.RulesVersionId == id).ToListAsync();
            db.TeamTypes.RemoveRange(teamTypes);
            await db.SaveChangesAsync();

            // Les règles spéciales partent APRÈS les TeamTypes : la liaison
            // TeamTypeSpecialRule → SpecialRule est en Restrict, donc une règle
            // encore rattachée à une fiche d'équipe ne peut pas être supprimée.
            // (Les liaisons, elles, tombent en cascade avec leur TeamType.)
            var reglesSpeciales = await db.SpecialRules.Where(r => r.RulesVersionId == id).ToListAsync();
            db.SpecialRules.RemoveRange(reglesSpeciales);
            await db.SaveChangesAsync();

            // La Réserve doit partir AVANT les compétences et les catégories :
            // PoolPositionSkill → Skill et PoolPositionCategoryAccess → SkillCategoryDef
            // sont en Restrict avec une FK non nullable.
            var poolPositions = await db.PoolPositions.Where(p => p.RulesVersionId == id).ToListAsync();
            db.PoolPositions.RemoveRange(poolPositions);
            await db.SaveChangesAsync();

            var skills = await db.Skills.Where(s => s.RulesVersionId == id).ToListAsync();
            db.Skills.RemoveRange(skills);
            await db.SaveChangesAsync();

            var categories = await db.SkillCategories.Where(c => c.RulesVersionId == id).ToListAsync();
            db.SkillCategories.RemoveRange(categories);
            await db.SaveChangesAsync();

            // Staff de la version. Les copies déjà prises par les ligues
            // (LeagueStaffType) survivent : leur FK vers StaffType est SetNull,
            // sinon supprimer une version viderait le staff de ligues en cours.
            var staffTypes = await db.StaffTypes.Where(s => s.RulesVersionId == id).ToListAsync();
            db.StaffTypes.RemoveRange(staffTypes);
            await db.SaveChangesAsync();

            // Paliers de coût PSP du barème d'amélioration. La FK est en Cascade,
            // mais on les retire explicitement : c'est le seul moyen de ne pas
            // dépendre de l'état de `pragma foreign_keys` selon le fournisseur,
            // et de garder la liste des enfants visible à la lecture.
            var paliersAmelioration = await db.PaliersAmelioration
                .Where(p => p.RulesVersionId == id).ToListAsync();
            db.PaliersAmelioration.RemoveRange(paliersAmelioration);
            await db.SaveChangesAsync();

            db.RulesVersions.Remove(version);
            await db.SaveChangesAsync();

            await tx.CommitAsync();
            logger.LogInformation("Version supprimée : {Nom} (id={Id})", version.Nom, id);
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    // ═══════════════════ Règles spéciales (LRB p.93-94) ═══════════════════
    //
    // Catalogue porté par la version de règles, éditable par l'association sans
    // dev. Une règle sans Code est purement descriptive : elle s'affiche sur la
    // feuille d'équipe, et c'est tout. Un Code connu (SpecialRuleCodes) branche
    // un comportement écrit une fois dans le code.

    public async Task<List<SpecialRule>> GetReglesSpecialesAsync(int versionId) =>
        await db.SpecialRules
            .Include(r => r.TeamTypes)   // la liste admin affiche le nombre d'équipes rattachées
            .Where(r => r.RulesVersionId == versionId)
            .OrderBy(r => r.Ordre).ThenBy(r => r.Nom)
            .ToListAsync();

    // ── Ligues thématiques ───────────────────────────────────────────────────
    // Catalogue éditable : les races et les star players y pointent tous les
    // deux, ce qui supprime les divergences de saisie du texte libre.

    public async Task<List<ThemedLeague>> GetLiguesAsync(int versionId) =>
        await db.ThemedLeagues
            // Le tableau d'administration affiche le nombre de rattachements :
            // sans ces Include, il afficherait 0 partout.
            .Include(l => l.Equipes)
            .Include(l => l.StarPlayers)
            .Where(l => l.RulesVersionId == versionId)
            .OrderBy(l => l.Ordre).ThenBy(l => l.Nom)
            .ToListAsync();

    public async Task<ThemedLeague> CreerLigueThematiqueAsync(int versionId, string nom, int ordre = 0)
    {
        if (string.IsNullOrWhiteSpace(nom))
            throw new InvalidOperationException("Le nom de la ligue est obligatoire.");

        var existe = await db.ThemedLeagues
            .AnyAsync(l => l.RulesVersionId == versionId && l.Nom.ToLower() == nom.ToLower());
        if (existe)
            throw new InvalidOperationException($"Une ligue « {nom} » existe déjà dans cette version.");

        var ligue = new ThemedLeague { RulesVersionId = versionId, Nom = nom.Trim(), Ordre = ordre };
        db.ThemedLeagues.Add(ligue);
        await db.SaveChangesAsync();
        logger.LogInformation("Ligue thématique créée : {Nom} (id={Id})", ligue.Nom, ligue.Id);
        return ligue;
    }

    public async Task ModifierLigueThematiqueAsync(int id, string nom, int ordre)
    {
        var ligue = await db.ThemedLeagues.FindAsync(id)
            ?? throw new InvalidOperationException("Ligue introuvable.");

        if (string.IsNullOrWhiteSpace(nom))
            throw new InvalidOperationException("Le nom de la ligue est obligatoire.");

        var doublon = await db.ThemedLeagues.AnyAsync(l =>
            l.RulesVersionId == ligue.RulesVersionId && l.Id != id && l.Nom.ToLower() == nom.ToLower());
        if (doublon)
            throw new InvalidOperationException($"Une ligue « {nom} » existe déjà dans cette version.");

        ligue.Nom = nom.Trim();
        ligue.Ordre = ordre;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Supprime une ligue du catalogue. Les rattachements (races et star
    /// players) partent en cascade : c'est voulu, la ligue n'existe plus.
    /// </summary>
    public async Task SupprimerLigueThematiqueAsync(int id)
    {
        var ligue = await db.ThemedLeagues.FindAsync(id);
        if (ligue is null) return;
        db.ThemedLeagues.Remove(ligue);
        await db.SaveChangesAsync();
        logger.LogInformation("Ligue thématique supprimée : {Nom} (id={Id})", ligue.Nom, id);
    }

    /// <summary>Ligues rattachées à une race.</summary>
    public async Task<List<int>> GetLiguesDeLaRaceAsync(int teamTypeId) =>
        await db.Set<TeamTypeThemedLeague>()
            .Where(x => x.TeamTypeId == teamTypeId)
            .Select(x => x.ThemedLeagueId)
            .ToListAsync();

    /// <summary>Remplace les ligues d'une race par la sélection fournie.</summary>
    public async Task DefinirLiguesDeLaRaceAsync(int teamTypeId, IEnumerable<int> ligueIds)
    {
        var actuelles = await db.Set<TeamTypeThemedLeague>()
            .Where(x => x.TeamTypeId == teamTypeId)
            .ToListAsync();

        db.Set<TeamTypeThemedLeague>().RemoveRange(actuelles);

        foreach (var id in ligueIds.Distinct())
            db.Set<TeamTypeThemedLeague>().Add(new TeamTypeThemedLeague
            {
                TeamTypeId = teamTypeId, ThemedLeagueId = id
            });

        await db.SaveChangesAsync();
    }

    /// <summary>Ligues donnant accès à un star player.</summary>
    public async Task<List<int>> GetLiguesDuStarPlayerAsync(int starPlayerId) =>
        await db.Set<StarPlayerThemedLeague>()
            .Where(x => x.StarPlayerId == starPlayerId)
            .Select(x => x.ThemedLeagueId)
            .ToListAsync();

    public async Task DefinirLiguesDuStarPlayerAsync(int starPlayerId, IEnumerable<int> ligueIds)
    {
        var actuelles = await db.Set<StarPlayerThemedLeague>()
            .Where(x => x.StarPlayerId == starPlayerId)
            .ToListAsync();

        db.Set<StarPlayerThemedLeague>().RemoveRange(actuelles);

        foreach (var id in ligueIds.Distinct())
            db.Set<StarPlayerThemedLeague>().Add(new StarPlayerThemedLeague
            {
                StarPlayerId = starPlayerId, ThemedLeagueId = id
            });

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Star players qu'une race peut engager : ceux dont au moins une ligue est
    /// commune avec les siennes, plus ceux qui n'en portent aucune (ouverts à
    /// toutes les équipes).
    ///
    /// Le recoupement se fait sur les identifiants du catalogue, jamais sur des
    /// noms saisis à la main.
    /// </summary>
    public async Task<List<StarPlayer>> GetStarPlayersAccessiblesAsync(int teamTypeId)
    {
        var race = await db.TeamTypes
            .Include(t => t.LiguesListe)
            .FirstOrDefaultAsync(t => t.Id == teamTypeId);

        if (race is null) return [];

        var liguesDeLaRace = race.LiguesListe.Select(x => x.ThemedLeagueId).ToHashSet();

        var candidats = await db.StarPlayers
            .Include(s => s.Ligues)
            .Where(s => s.RulesVersionId == race.RulesVersionId)
            .OrderBy(s => s.Nom)
            .ToListAsync();

        return candidats
            .Where(s => s.Ligues.Count == 0
                        || s.Ligues.Any(x => liguesDeLaRace.Contains(x.ThemedLeagueId)))
            .ToList();
    }

    // ── Coups de pouce et star players ───────────────────────────────────────
    // Deux catalogues INFORMATIFS rattachés à une version de règles. Aucune
    // mécanique : ils s'affichent pour que les coaches comparent les VEA.

    public async Task<List<Inducement>> GetCoupsDePouceAsync(int versionId) =>
        await db.Inducements
            .Where(i => i.RulesVersionId == versionId)
            .OrderBy(i => i.Ordre).ThenBy(i => i.Nom)
            .ToListAsync();

    public async Task<Inducement> CreerCoupDePouceAsync(
        int versionId, string nom, string description, int cout,
        int quantiteMax = 0, string restriction = "", int ordre = 0)
    {
        if (string.IsNullOrWhiteSpace(nom))
            throw new InvalidOperationException("Le nom du coup de pouce est obligatoire.");

        var existe = await db.Inducements
            .AnyAsync(i => i.RulesVersionId == versionId && i.Nom.ToLower() == nom.ToLower());
        if (existe)
            throw new InvalidOperationException($"Un coup de pouce « {nom} » existe déjà dans cette version.");

        var cp = new Inducement
        {
            RulesVersionId = versionId,
            Nom = nom.Trim(),
            Description = description,
            Cout = Math.Max(0, cout),
            QuantiteMax = Math.Max(0, quantiteMax),
            Restriction = restriction?.Trim() ?? "",
            Ordre = ordre
        };
        db.Inducements.Add(cp);
        await db.SaveChangesAsync();
        logger.LogInformation("Coup de pouce créé : {Nom} (id={Id})", cp.Nom, cp.Id);
        return cp;
    }

    public async Task ModifierCoupDePouceAsync(
        int id, string nom, string description, int cout,
        int quantiteMax, string restriction, int ordre)
    {
        var cp = await db.Inducements.FindAsync(id)
            ?? throw new InvalidOperationException("Coup de pouce introuvable.");

        if (string.IsNullOrWhiteSpace(nom))
            throw new InvalidOperationException("Le nom du coup de pouce est obligatoire.");

        var doublon = await db.Inducements.AnyAsync(i =>
            i.RulesVersionId == cp.RulesVersionId && i.Id != id && i.Nom.ToLower() == nom.ToLower());
        if (doublon)
            throw new InvalidOperationException($"Un coup de pouce « {nom} » existe déjà dans cette version.");

        cp.Nom = nom.Trim();
        cp.Description = description;
        cp.Cout = Math.Max(0, cout);
        cp.QuantiteMax = Math.Max(0, quantiteMax);
        cp.Restriction = restriction?.Trim() ?? "";
        cp.Ordre = ordre;
        await db.SaveChangesAsync();
    }

    public async Task SupprimerCoupDePouceAsync(int id)
    {
        var cp = await db.Inducements.FindAsync(id);
        if (cp is null) return;
        db.Inducements.Remove(cp);
        await db.SaveChangesAsync();
        logger.LogInformation("Coup de pouce supprimé : {Nom} (id={Id})", cp.Nom, id);
    }

    public async Task<List<StarPlayer>> GetStarPlayersAsync(int versionId) =>
        await db.StarPlayers
            .Include(s => s.Ligues)
            .Where(s => s.RulesVersionId == versionId)
            .OrderBy(s => s.Ordre).ThenBy(s => s.Nom)
            .ToListAsync();

    public async Task<StarPlayer> CreerStarPlayerAsync(int versionId, StarPlayer modele)
    {
        if (string.IsNullOrWhiteSpace(modele.Nom))
            throw new InvalidOperationException("Le nom du star player est obligatoire.");

        var existe = await db.StarPlayers
            .AnyAsync(s => s.RulesVersionId == versionId && s.Nom.ToLower() == modele.Nom.ToLower());
        if (existe)
            throw new InvalidOperationException($"Un star player « {modele.Nom} » existe déjà dans cette version.");

        var star = new StarPlayer
        {
            RulesVersionId = versionId,
            Nom = modele.Nom.Trim(),
            Cout = Math.Max(0, modele.Cout),
            Mouvement = modele.Mouvement,
            Force = modele.Force,
            Agilite = modele.Agilite,
            CapacitePasse = modele.CapacitePasse,
            Armure = modele.Armure,
            Competences = NormaliserOptions(modele.Competences),
            ReglesSpeciales = modele.ReglesSpeciales?.Trim() ?? "",
            Ordre = modele.Ordre
        };
        db.StarPlayers.Add(star);
        await db.SaveChangesAsync();
        logger.LogInformation("Star player créé : {Nom} (id={Id})", star.Nom, star.Id);
        return star;
    }

    public async Task ModifierStarPlayerAsync(int id, StarPlayer modele)
    {
        var star = await db.StarPlayers.FindAsync(id)
            ?? throw new InvalidOperationException("Star player introuvable.");

        if (string.IsNullOrWhiteSpace(modele.Nom))
            throw new InvalidOperationException("Le nom du star player est obligatoire.");

        var doublon = await db.StarPlayers.AnyAsync(s =>
            s.RulesVersionId == star.RulesVersionId && s.Id != id && s.Nom.ToLower() == modele.Nom.ToLower());
        if (doublon)
            throw new InvalidOperationException($"Un star player « {modele.Nom} » existe déjà dans cette version.");

        star.Nom = modele.Nom.Trim();
        star.Cout = Math.Max(0, modele.Cout);
        star.Mouvement = modele.Mouvement;
        star.Force = modele.Force;
        star.Agilite = modele.Agilite;
        star.CapacitePasse = modele.CapacitePasse;
        star.Armure = modele.Armure;
        star.Competences = NormaliserOptions(modele.Competences);
        star.ReglesSpeciales = modele.ReglesSpeciales?.Trim() ?? "";
        star.Ordre = modele.Ordre;
        await db.SaveChangesAsync();
    }

    public async Task SupprimerStarPlayerAsync(int id)
    {
        var star = await db.StarPlayers.FindAsync(id);
        if (star is null) return;
        db.StarPlayers.Remove(star);
        await db.SaveChangesAsync();
        logger.LogInformation("Star player supprimé : {Nom} (id={Id})", star.Nom, id);
    }

    public async Task<SpecialRule> CreerRegleSpecialeAsync(
        int versionId, string nom, string description, string code = "", int ordre = 0)
    {
        if (string.IsNullOrWhiteSpace(nom))
            throw new InvalidOperationException("Le nom de la règle spéciale est obligatoire.");

        // Insensible à la casse, comme partout ailleurs dans le projet.
        var existe = await db.SpecialRules
            .AnyAsync(r => r.RulesVersionId == versionId && r.Nom.ToLower() == nom.ToLower());
        if (existe)
            throw new InvalidOperationException($"Une règle spéciale « {nom} » existe déjà dans cette version.");

        var regle = new SpecialRule
        {
            RulesVersionId = versionId,
            Nom = nom.Trim(),
            Description = description,
            Code = code.Trim(),
            Ordre = ordre
        };
        db.SpecialRules.Add(regle);
        await db.SaveChangesAsync();
        logger.LogInformation("Règle spéciale créée : {Nom} (id={Id}) sur version {VersionId}", regle.Nom, regle.Id, versionId);
        return regle;
    }

    public async Task ModifierRegleSpecialeAsync(
        int id, string nom, string description, string code, int ordre)
    {
        if (string.IsNullOrWhiteSpace(nom))
            throw new InvalidOperationException("Le nom de la règle spéciale est obligatoire.");

        var regle = await db.SpecialRules.FindAsync(id)
            ?? throw new InvalidOperationException("Règle spéciale introuvable.");

        var doublon = await db.SpecialRules.AnyAsync(r =>
            r.RulesVersionId == regle.RulesVersionId && r.Id != id && r.Nom.ToLower() == nom.ToLower());
        if (doublon)
            throw new InvalidOperationException($"Une autre règle spéciale « {nom} » existe déjà dans cette version.");

        regle.Nom = nom.Trim();
        regle.Description = description;
        regle.Code = code.Trim();
        regle.Ordre = ordre;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Supprime une règle du catalogue. Refuse si elle est encore rattachée à
    /// des fiches d'équipe : la FK est en Restrict, et un message explicite
    /// vaut mieux qu'une exception SQLite incompréhensible.
    /// </summary>
    public async Task SupprimerRegleSpecialeAsync(int id)
    {
        var nbRattachements = await db.TeamTypeSpecialRules.CountAsync(l => l.SpecialRuleId == id);
        if (nbRattachements > 0)
            throw new InvalidOperationException(
                $"{nbRattachements} fiche(s) d'équipe utilisent cette règle. Retirez-la de ces équipes d'abord.");

        var regle = await db.SpecialRules.FindAsync(id)
            ?? throw new InvalidOperationException("Règle spéciale introuvable.");
        db.SpecialRules.Remove(regle);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Rattache une règle à une fiche d'équipe (ou mets à jour ses options).
    /// </summary>
    /// <param name="optionsChoix">
    /// Options proposées à CETTE race, en CSV — voir
    /// <see cref="TeamTypeSpecialRule.OptionsChoix"/>. Une seule valeur =
    /// imposée ; vide = aucun choix à faire.
    /// </param>
    public async Task AssocierRegleSpecialeAsync(int teamTypeId, int regleId, string optionsChoix = "")
    {
        var teamType = await db.TeamTypes.FindAsync(teamTypeId)
            ?? throw new InvalidOperationException("Type d'équipe introuvable.");
        var regle = await db.SpecialRules.FindAsync(regleId)
            ?? throw new InvalidOperationException("Règle spéciale introuvable.");

        // Garde-fou : une FK choisie par l'utilisateur dans une entité scopée
        // par version doit être vérifiée comme appartenant à CETTE version,
        // sinon on recrée la corruption « catégorie d'une autre version ».
        if (regle.RulesVersionId != teamType.RulesVersionId)
            throw new InvalidOperationException(
                $"La règle « {regle.Nom} » appartient à une autre version de règles que l'équipe « {teamType.Nom} ».");

        var lien = await db.TeamTypeSpecialRules
            .FirstOrDefaultAsync(l => l.TeamTypeId == teamTypeId && l.SpecialRuleId == regleId);

        if (lien is null)
        {
            db.TeamTypeSpecialRules.Add(new TeamTypeSpecialRule
            {
                TeamTypeId = teamTypeId,
                SpecialRuleId = regleId,
                OptionsChoix = NormaliserOptions(optionsChoix)
            });
        }
        else
        {
            lien.OptionsChoix = NormaliserOptions(optionsChoix);
        }
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Règle le plafond de recrues offertes par phase d'après-match pour cette
    /// race (« Maîtres de la Non-Vie »). 0 = sans limite.
    /// </summary>
    public async Task DefinirLimiteApresMatchAsync(int teamTypeId, int regleId, int limite)
    {
        var lien = await db.TeamTypeSpecialRules
            .FirstOrDefaultAsync(l => l.TeamTypeId == teamTypeId && l.SpecialRuleId == regleId)
            ?? throw new InvalidOperationException("Cette règle n'est pas rattachée à cette équipe.");

        lien.LimiteParApresMatch = Math.Clamp(limite, 0, 9);
        await db.SaveChangesAsync();
    }

    public async Task DissocierRegleSpecialeAsync(int teamTypeId, int regleId)
    {
        var lien = await db.TeamTypeSpecialRules
            .FirstOrDefaultAsync(l => l.TeamTypeId == teamTypeId && l.SpecialRuleId == regleId);
        if (lien is null) return;

        db.TeamTypeSpecialRules.Remove(lien);
        await db.SaveChangesAsync();
    }

    /// <summary>Nettoie un CSV saisi à la main : espaces parasites, entrées vides.</summary>
    private static string NormaliserOptions(string csv) =>
        string.Join(",", (csv ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    // ═══════════════════ TeamType ═══════════════════

    public async Task<List<TeamType>> GetTeamTypesAsync(int versionId) =>
        await db.TeamTypes
            .Include(t => t.Postes)
            .Where(t => t.RulesVersionId == versionId)
            .OrderBy(t => t.Nom)
            .ToListAsync();

    public async Task<TeamType?> GetTeamTypeAsync(int id) =>
        await db.TeamTypes
            .Include(t => t.Postes).ThenInclude(p => p.CompetencesDepart).ThenInclude(pps => pps.Skill)
            .Include(t => t.Postes).ThenInclude(p => p.AccesCategories).ThenInclude(a => a.SkillCategoryDef)
            .Include(t => t.LimitesMotsCles)
            .Include(t => t.ReglesSpecialesListe).ThenInclude(l => l.SpecialRule)
            .FirstOrDefaultAsync(t => t.Id == id);

    public async Task<TeamType> CreerTeamTypeAsync(int versionId, TeamType data)
    {
        data.RulesVersionId = versionId;
        var gameId = await db.RulesVersions.Where(v => v.Id == versionId).Select(v => v.GameId).FirstAsync();
        data.GameId = gameId;
        db.TeamTypes.Add(data);
        await db.SaveChangesAsync();
        logger.LogInformation("TeamType créé : {Nom} (id={Id}) sur version {VersionId}", data.Nom, data.Id, versionId);
        return data;
    }

    /// <param name="categorie">
    /// Catégorie officielle LRB, de 1 (équipes les plus performantes) à 4 (les
    /// plus faibles). <c>0</c> = non renseignée. Validée ICI et pas seulement
    /// dans l'écran : toute valeur choisie par l'utilisateur doit être vérifiée
    /// côté serveur.
    /// </param>
    public async Task ModifierTeamTypeAsync(int id, string nom, int categorie, int coutRelance, string reglesSpeciales, string ligues)
    {
        if (categorie is < 0 or > 4)
            throw new InvalidOperationException(
                $"Catégorie invalide : {categorie}. Le livre de règles n'en définit que quatre (1 à 4), 0 signifiant « non renseignée ».");

        var t = await db.TeamTypes.FindAsync(id) ?? throw new InvalidOperationException("TeamType introuvable");
        t.Nom = nom;
        t.Categorie = categorie;
        t.CoutRelance = coutRelance;
        t.ReglesSpeciales = reglesSpeciales;
        t.LiguesTexteObsolete = ligues;
        await db.SaveChangesAsync();
    }

    public async Task SupprimerTeamTypeAsync(int id)
    {
        var nbEquipes = await db.Teams.CountAsync(e => e.TeamTypeId == id);
        if (nbEquipes > 0)
            throw new InvalidOperationException($"{nbEquipes} équipe(s) utilisent ce type. Supprimer les équipes d'abord.");

        var t = await db.TeamTypes.FindAsync(id) ?? throw new InvalidOperationException("TeamType introuvable");
        db.TeamTypes.Remove(t);
        await db.SaveChangesAsync();
        logger.LogInformation("TeamType supprimé : {Nom} (id={Id})", t.Nom, id);
    }

    // ═══════════════════ PlayerPosition ═══════════════════

    /// <summary>
    /// Accès de catégorie choisis dans l'UI : identifiants des catégories principales
    /// et secondaires. Une catégorie présente dans les deux est traitée comme principale.
    /// </summary>
    public readonly record struct AccesCategoriesInput(IEnumerable<int> Principales, IEnumerable<int> Secondaires)
    {
        public static AccesCategoriesInput Vide => new([], []);
    }

    public async Task<PlayerPosition> AjouterPosteAsync(
        int teamTypeId, PlayerPosition data, IEnumerable<int> skillsDepart, AccesCategoriesInput acces)
    {
        data.TeamTypeId = teamTypeId;
        db.PlayerPositions.Add(data);
        await db.SaveChangesAsync();
        foreach (var sid in skillsDepart)
            db.PlayerPositionSkills.Add(new PlayerPositionSkill { PlayerPositionId = data.Id, SkillId = sid });
        AppliquerAccesPoste(data.Id, acces);
        await db.SaveChangesAsync();
        return data;
    }

    public async Task ModifierPosteAsync(
        int id, PlayerPosition data, IEnumerable<int> skillsDepart, AccesCategoriesInput acces)
    {
        var p = await db.PlayerPositions
            .Include(x => x.CompetencesDepart)
            .Include(x => x.AccesCategories)
            .FirstOrDefaultAsync(x => x.Id == id)
            ?? throw new InvalidOperationException("Poste introuvable");

        p.Nom = data.Nom;
        p.QuantiteMax = data.QuantiteMax;
        p.Cout = data.Cout;
        p.Mouvement = data.Mouvement;
        p.Force = data.Force;
        p.Agilite = data.Agilite;
        p.CapacitePasse = data.CapacitePasse;
        p.Armure = data.Armure;
        p.MotsCles = data.MotsCles;

        // Resync skills de départ + accès de catégorie
        db.PlayerPositionSkills.RemoveRange(p.CompetencesDepart);
        db.PlayerPositionCategoryAccesses.RemoveRange(p.AccesCategories);
        await db.SaveChangesAsync();

        foreach (var sid in skillsDepart)
            db.PlayerPositionSkills.Add(new PlayerPositionSkill { PlayerPositionId = p.Id, SkillId = sid });
        AppliquerAccesPoste(p.Id, acces);
        await db.SaveChangesAsync();
    }

    /// <summary>Ajoute les lignes d'accès ; principal l'emporte sur secondaire en cas de doublon.</summary>
    private void AppliquerAccesPoste(int positionId, AccesCategoriesInput acces)
    {
        var principales = acces.Principales.Distinct().ToList();
        foreach (var catId in principales)
            db.PlayerPositionCategoryAccesses.Add(new PlayerPositionCategoryAccess
            {
                PlayerPositionId = positionId, SkillCategoryDefId = catId, EstPrincipale = true
            });

        foreach (var catId in acces.Secondaires.Distinct().Where(c => !principales.Contains(c)))
            db.PlayerPositionCategoryAccesses.Add(new PlayerPositionCategoryAccess
            {
                PlayerPositionId = positionId, SkillCategoryDefId = catId, EstPrincipale = false
            });
    }

    public async Task SupprimerPosteAsync(int id)
    {
        var nbJoueurs = await db.TeamPlayers.CountAsync(j => j.PlayerPositionId == id);
        if (nbJoueurs > 0)
            throw new InvalidOperationException($"{nbJoueurs} joueur(s) utilisent ce poste.");

        var p = await db.PlayerPositions.FindAsync(id) ?? throw new InvalidOperationException("Poste introuvable");
        db.PlayerPositions.Remove(p);
        await db.SaveChangesAsync();
    }

    // ═══════════════════ Réserve (PoolPosition) ═══════════════════

    public async Task<List<PoolPosition>> GetReserveAsync(int versionId) =>
        await db.PoolPositions
            .Include(p => p.CompetencesDepart).ThenInclude(pps => pps.Skill)
            .Include(p => p.AccesCategories).ThenInclude(a => a.SkillCategoryDef)
            .Where(p => p.RulesVersionId == versionId)
            .OrderBy(p => p.Nom)
            .ToListAsync();

    public async Task<PoolPosition> AjouterReserveAsync(
        int versionId, PoolPosition data, IEnumerable<int> skillsDepart, AccesCategoriesInput acces)
    {
        data.RulesVersionId = versionId;
        db.PoolPositions.Add(data);
        await db.SaveChangesAsync();
        foreach (var sid in skillsDepart)
            db.PoolPositionSkills.Add(new PoolPositionSkill { PoolPositionId = data.Id, SkillId = sid });
        AppliquerAccesReserve(data.Id, acces);
        await db.SaveChangesAsync();
        logger.LogInformation("Réserve : poste ajouté {Nom} (id={Id}) sur version {V}", data.Nom, data.Id, versionId);
        return data;
    }

    public async Task ModifierReserveAsync(
        int id, PoolPosition data, IEnumerable<int> skillsDepart, AccesCategoriesInput acces)
    {
        var p = await db.PoolPositions
            .Include(x => x.CompetencesDepart)
            .Include(x => x.AccesCategories)
            .FirstOrDefaultAsync(x => x.Id == id)
            ?? throw new InvalidOperationException("Poste de réserve introuvable");

        p.Nom = data.Nom; p.QuantiteMax = data.QuantiteMax; p.Cout = data.Cout;
        p.Mouvement = data.Mouvement; p.Force = data.Force; p.Agilite = data.Agilite;
        p.CapacitePasse = data.CapacitePasse; p.Armure = data.Armure;
        p.MotsCles = data.MotsCles;

        db.PoolPositionSkills.RemoveRange(p.CompetencesDepart);
        db.PoolPositionCategoryAccesses.RemoveRange(p.AccesCategories);
        await db.SaveChangesAsync();
        foreach (var sid in skillsDepart)
            db.PoolPositionSkills.Add(new PoolPositionSkill { PoolPositionId = p.Id, SkillId = sid });
        AppliquerAccesReserve(p.Id, acces);
        await db.SaveChangesAsync();
    }

    /// <summary>Jumeau de <see cref="AppliquerAccesPoste"/> pour la Réserve.</summary>
    private void AppliquerAccesReserve(int poolId, AccesCategoriesInput acces)
    {
        var principales = acces.Principales.Distinct().ToList();
        foreach (var catId in principales)
            db.PoolPositionCategoryAccesses.Add(new PoolPositionCategoryAccess
            {
                PoolPositionId = poolId, SkillCategoryDefId = catId, EstPrincipale = true
            });

        foreach (var catId in acces.Secondaires.Distinct().Where(c => !principales.Contains(c)))
            db.PoolPositionCategoryAccesses.Add(new PoolPositionCategoryAccess
            {
                PoolPositionId = poolId, SkillCategoryDefId = catId, EstPrincipale = false
            });
    }

    public async Task SupprimerReserveAsync(int id)
    {
        var p = await db.PoolPositions.FindAsync(id)
            ?? throw new InvalidOperationException("Poste de réserve introuvable");
        db.PoolPositions.Remove(p); // cascade sur PoolPositionSkill ; n'affecte PAS les postes déjà importés
        await db.SaveChangesAsync();
        logger.LogInformation("Réserve : poste supprimé {Nom} (id={Id})", p.Nom, id);
    }

    /// <summary>
    /// Copie les postes de réserve indiqués dans le TeamType cible (design catalogue :
    /// copie indépendante). Les compétences de départ sont recopiées (même version → mêmes SkillId).
    /// </summary>
    public async Task ImporterReserveVersTeamTypeAsync(int teamTypeId, IEnumerable<int> poolIds)
    {
        var tt = await db.TeamTypes.FindAsync(teamTypeId)
            ?? throw new InvalidOperationException("TeamType introuvable");

        var ids = poolIds.ToHashSet();
        var pools = await db.PoolPositions
            .Include(p => p.CompetencesDepart)
            .Include(p => p.AccesCategories)
            .Where(p => ids.Contains(p.Id))
            .ToListAsync();

        await using var tx = await db.Database.BeginTransactionAsync();
        foreach (var pool in pools)
        {
            if (pool.RulesVersionId != tt.RulesVersionId)
                throw new InvalidOperationException(
                    $"Le poste de réserve '{pool.Nom}' n'appartient pas à la version de ce type d'équipe.");

            var copie = new PlayerPosition
            {
                TeamTypeId = teamTypeId,
                Nom = pool.Nom, QuantiteMax = pool.QuantiteMax, Cout = pool.Cout,
                Mouvement = pool.Mouvement, Force = pool.Force, Agilite = pool.Agilite,
                CapacitePasse = pool.CapacitePasse, Armure = pool.Armure,
                MotsCles = pool.MotsCles
            };
            db.PlayerPositions.Add(copie);
            await db.SaveChangesAsync();

            foreach (var pps in pool.CompetencesDepart)
                db.PlayerPositionSkills.Add(new PlayerPositionSkill
                {
                    PlayerPositionId = copie.Id,
                    SkillId = pps.SkillId
                });

            foreach (var acc in pool.AccesCategories)
                db.PlayerPositionCategoryAccesses.Add(new PlayerPositionCategoryAccess
                {
                    PlayerPositionId = copie.Id,
                    SkillCategoryDefId = acc.SkillCategoryDefId,
                    EstPrincipale = acc.EstPrincipale
                });
            await db.SaveChangesAsync();
        }
        await tx.CommitAsync();
        logger.LogInformation("Réserve : {N} poste(s) importé(s) dans TeamType {Id}", pools.Count, teamTypeId);
    }

    /// <summary>
    /// Chemin inverse de <see cref="ImporterReserveVersTeamTypeAsync"/> : copie un poste d'un
    /// TeamType vers la Réserve de sa version de règles. Copie indépendante (le poste d'origine
    /// reste en place). Refuse si un poste de réserve porte déjà le même nom dans cette version.
    /// </summary>
    public async Task<PoolPosition> ExporterPosteVersReserveAsync(int playerPositionId)
    {
        var poste = await db.PlayerPositions
            .Include(p => p.CompetencesDepart)
            .Include(p => p.AccesCategories)
            .Include(p => p.TeamType)
            .FirstOrDefaultAsync(p => p.Id == playerPositionId)
            ?? throw new InvalidOperationException("Poste introuvable");

        var versionId = poste.TeamType.RulesVersionId;

        var nomsExistants = await db.PoolPositions
            .Where(p => p.RulesVersionId == versionId)
            .Select(p => p.Nom)
            .ToListAsync();

        if (nomsExistants.Any(n => string.Equals(n, poste.Nom, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(
                $"Un poste « {poste.Nom} » existe déjà dans la Réserve de cette version. Renommez-le avant de le renvoyer.");

        var copie = new PoolPosition
        {
            RulesVersionId = versionId,
            Nom = poste.Nom, QuantiteMax = poste.QuantiteMax, Cout = poste.Cout,
            Mouvement = poste.Mouvement, Force = poste.Force, Agilite = poste.Agilite,
            CapacitePasse = poste.CapacitePasse, Armure = poste.Armure,
            MotsCles = poste.MotsCles
        };

        await using var tx = await db.Database.BeginTransactionAsync();
        db.PoolPositions.Add(copie);
        await db.SaveChangesAsync();

        foreach (var pps in poste.CompetencesDepart)
            db.PoolPositionSkills.Add(new PoolPositionSkill
            {
                PoolPositionId = copie.Id,
                SkillId = pps.SkillId
            });

        foreach (var acc in poste.AccesCategories)
            db.PoolPositionCategoryAccesses.Add(new PoolPositionCategoryAccess
            {
                PoolPositionId = copie.Id,
                SkillCategoryDefId = acc.SkillCategoryDefId,
                EstPrincipale = acc.EstPrincipale
            });

        await db.SaveChangesAsync();
        await tx.CommitAsync();

        logger.LogInformation("Réserve : poste {Nom} (id={Id}) du TeamType {Tt} renvoyé en Réserve (id={PoolId})",
            poste.Nom, poste.Id, poste.TeamTypeId, copie.Id);
        return copie;
    }

    /// <summary>
    /// Modifie le barème d'XP de référence d'une version de règles (R6).
    /// Les ligues déjà créées conservent le barème qu'elles ont enregistré.
    /// </summary>
    public async Task ModifierBaremeXpAsync(int versionId, BaremePsp bareme)
    {
        var version = await db.RulesVersions.FirstOrDefaultAsync(v => v.Id == versionId)
            ?? throw new InvalidOperationException("Version de règles introuvable");

        bareme.AppliquerA(version);
        await db.SaveChangesAsync();

        logger.LogInformation(
            "Barème d'XP de la version '{Version}' : TD={Td}, passe={Passe}, int={Int}, élim={Elim}, MVP={Mvp}",
            version.Nom, bareme.ParTouchdown, bareme.ParPasse, bareme.ParInterception,
            bareme.ParElimination, bareme.BonusMvp);
    }

    /// <summary>
    /// Barème de points de CLASSEMENT de référence d'une version de règles.
    /// Les ligues déjà créées gardent le leur : elles en ont pris une copie.
    /// </summary>
    public async Task ModifierBaremePointsAsync(int versionId, BaremePoints bareme)
    {
        var version = await db.RulesVersions.FirstOrDefaultAsync(v => v.Id == versionId)
            ?? throw new InvalidOperationException("Version de règles introuvable");

        bareme.AppliquerA(version);
        await db.SaveChangesAsync();

        logger.LogInformation(
            "Barème de points de la version '{Version}' : V={V}, N={N}, D={D}",
            version.Nom, bareme.Victoire, bareme.Nul, bareme.Defaite);
    }

    /// <summary>
    /// Barème des AMÉLIORATIONS de joueur d'une version : les 8 hausses de valeur
    /// et les 6 paliers de coût en PSP. Les paliers sont remplacés en bloc — le
    /// LRB en définit exactement 6 (rangs 1 à 6), il n'y a pas de liste ouverte.
    ///
    /// ⚠️ Contrairement au barème d'XP, ce barème est lu EN DIRECT par le calcul
    /// de la valeur d'un joueur : une correction se propage donc aux ligues en
    /// cours, ce qui est précisément la demande.
    /// </summary>
    public async Task<BaremeAmelioration> GetBaremeAmeliorationAsync(int versionId)
    {
        var version = await db.RulesVersions
            .Include(v => v.PaliersAmelioration)
            .FirstOrDefaultAsync(v => v.Id == versionId)
            ?? throw new InvalidOperationException("Version de règles introuvable");

        return BaremeAmelioration.DeVersion(version);
    }

    public async Task ModifierBaremeAmeliorationAsync(int versionId, BaremeAmelioration bareme)
    {
        var version = await db.RulesVersions
            .Include(v => v.PaliersAmelioration)
            .FirstOrDefaultAsync(v => v.Id == versionId)
            ?? throw new InvalidOperationException("Version de règles introuvable");

        foreach (var p in bareme.Paliers)
        {
            if (p.Rang is < 1 or > 6)
                throw new InvalidOperationException($"Rang d'amélioration invalide : {p.Rang} (attendu 1 à 6).");
            if (p.CoutAleaPrincipale < 0 || p.CoutChoixPrincipale < 0
                || p.CoutSecondaire < 0 || p.CoutCaracteristique < 0)
                throw new InvalidOperationException("Un coût en PSP ne peut pas être négatif.");
        }

        bareme.AppliquerA(version);

        // Les paliers sont mis à jour en place quand ils existent : garder la même
        // ligne évite de faire enfler les identifiants à chaque enregistrement.
        foreach (var saisi in bareme.Paliers)
        {
            var existant = version.PaliersAmelioration.FirstOrDefault(x => x.Rang == saisi.Rang);
            if (existant is null)
            {
                db.PaliersAmelioration.Add(new PalierAmeliorationPsp
                {
                    RulesVersionId      = versionId,
                    Rang                = saisi.Rang,
                    CoutAleaPrincipale  = saisi.CoutAleaPrincipale,
                    CoutChoixPrincipale = saisi.CoutChoixPrincipale,
                    CoutSecondaire      = saisi.CoutSecondaire,
                    CoutCaracteristique = saisi.CoutCaracteristique
                });
            }
            else
            {
                existant.CoutAleaPrincipale  = saisi.CoutAleaPrincipale;
                existant.CoutChoixPrincipale = saisi.CoutChoixPrincipale;
                existant.CoutSecondaire      = saisi.CoutSecondaire;
                existant.CoutCaracteristique = saisi.CoutCaracteristique;
            }
        }

        await db.SaveChangesAsync();

        logger.LogInformation(
            "Barème d'amélioration de la version '{Version}' : principale={P}, secondaire={S}, élite=+{E}, {Nb} palier(s)",
            version.Nom, bareme.HaussePrincipale, bareme.HausseSecondaire,
            bareme.SurcoutElite, bareme.Paliers.Count);
    }

    // ═══════════════════ Catégories de compétence ═══════════════════

    /// <summary>Longueur maximale du code d'affichage d'une catégorie.</summary>
    public const int CodeCategorieMaxLength = 2;

    public async Task<List<SkillCategoryDef>> GetCategoriesAsync(int versionId) =>
        await db.SkillCategories
            .Where(c => c.RulesVersionId == versionId)
            .OrderBy(c => c.Nom)
            .ToListAsync();

    public async Task<SkillCategoryDef> CreerCategorieAsync(int versionId, string nom, string code)
    {
        var (nomNet, codeNet) = ValiderCategorie(nom, code);
        await VerifierUniciteCategorieAsync(versionId, nomNet, codeNet, categorieExclue: null);

        var cat = new SkillCategoryDef
        {
            RulesVersionId = versionId,
            Nom = nomNet,
            Code = codeNet
        };
        db.SkillCategories.Add(cat);
        await db.SaveChangesAsync();
        logger.LogInformation("Catégorie créée : {Nom} ({Code}) sur version {V}", nomNet, codeNet, versionId);
        return cat;
    }

    /// <summary>
    /// Renomme / recode une catégorie. Autorisé même si elle est utilisée : les compétences
    /// pointent vers son identifiant, pas vers son libellé.
    /// </summary>
    public async Task ModifierCategorieAsync(int id, string nom, string code)
    {
        var cat = await db.SkillCategories.FindAsync(id)
            ?? throw new InvalidOperationException("Catégorie introuvable");

        var (nomNet, codeNet) = ValiderCategorie(nom, code);
        await VerifierUniciteCategorieAsync(cat.RulesVersionId, nomNet, codeNet, categorieExclue: id);

        cat.Nom = nomNet;
        cat.Code = codeNet;
        await db.SaveChangesAsync();
        logger.LogInformation("Catégorie modifiée : {Nom} ({Code}) id={Id}", nomNet, codeNet, id);
    }

    /// <summary>Supprime une catégorie. Refusé si au moins une compétence l'utilise.</summary>
    public async Task SupprimerCategorieAsync(int id)
    {
        var cat = await db.SkillCategories.FindAsync(id)
            ?? throw new InvalidOperationException("Catégorie introuvable");

        var nbSkills = await db.Skills.CountAsync(s => s.SkillCategoryDefId == id);
        if (nbSkills > 0)
            throw new InvalidOperationException(
                $"{nbSkills} compétence(s) utilisent la catégorie « {cat.Nom} ». Réaffectez-les avant de la supprimer.");

        db.SkillCategories.Remove(cat);
        await db.SaveChangesAsync();
        logger.LogInformation("Catégorie supprimée : {Nom} (id={Id})", cat.Nom, id);
    }

    private static (string nom, string code) ValiderCategorie(string nom, string code)
    {
        var nomNet = (nom ?? "").Trim();
        var codeNet = (code ?? "").Trim().ToUpperInvariant();

        if (string.IsNullOrWhiteSpace(nomNet))
            throw new InvalidOperationException("Le nom de la catégorie est obligatoire.");
        if (string.IsNullOrWhiteSpace(codeNet))
            throw new InvalidOperationException("Le code de la catégorie est obligatoire.");
        if (codeNet.Length > CodeCategorieMaxLength)
            throw new InvalidOperationException($"Le code doit faire 1 ou {CodeCategorieMaxLength} caractère(s) (reçu : « {codeNet} »).");

        return (nomNet, codeNet);
    }

    private async Task VerifierUniciteCategorieAsync(int versionId, string nom, string code, int? categorieExclue)
    {
        var existantes = await db.SkillCategories
            .Where(c => c.RulesVersionId == versionId && (categorieExclue == null || c.Id != categorieExclue))
            .Select(c => new { c.Nom, c.Code })
            .ToListAsync();

        if (existantes.Any(c => string.Equals(c.Nom, nom, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Une catégorie « {nom} » existe déjà dans cette version.");
        if (existantes.Any(c => string.Equals(c.Code, code, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Le code « {code} » est déjà utilisé par une autre catégorie de cette version.");
    }

    // ═══════════════════ Skill ═══════════════════
    public async Task<List<Skill>> GetSkillsAsync(int versionId) =>
        await db.Skills
            .Include(s => s.SkillCategoryDef)
            .Where(s => s.RulesVersionId == versionId)
            .OrderBy(s => s.SkillCategoryDef.Nom).ThenBy(s => s.Nom)
            .ToListAsync();

    public async Task<Skill> CreerSkillAsync(int versionId, Skill data)
    {
        await VerifierCategorieDeLaVersionAsync(versionId, data.SkillCategoryDefId);
        data.RulesVersionId = versionId;
        db.Skills.Add(data);
        await db.SaveChangesAsync();
        return data;
    }

    public async Task ModifierSkillAsync(int id, string nom, int categorieId, string description, bool estElite, bool estTrait)
    {
        var s = await db.Skills.FindAsync(id) ?? throw new InvalidOperationException("Skill introuvable");
        await VerifierCategorieDeLaVersionAsync(s.RulesVersionId, categorieId);
        s.Nom = nom;
        s.SkillCategoryDefId = categorieId;
        s.Description = description;
        s.EstElite = estElite;
        s.EstTrait = estTrait;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Une compétence ne peut pointer que vers une catégorie de SA PROPRE version.
    /// Garde-fou contre la corruption silencieuse qui casse ensuite le clonage.
    /// </summary>
    private async Task VerifierCategorieDeLaVersionAsync(int versionId, int categorieId)
    {
        var ok = await db.SkillCategories
            .AnyAsync(c => c.Id == categorieId && c.RulesVersionId == versionId);
        if (!ok)
            throw new InvalidOperationException(
                "La catégorie choisie n'appartient pas à cette version de règles.");
    }

    public async Task SupprimerSkillAsync(int id)
    {
        var nbJoueurs = await db.TeamPlayerSkills.CountAsync(t => t.SkillId == id);
        if (nbJoueurs > 0)
            throw new InvalidOperationException($"{nbJoueurs} joueur(s) ont cette compétence.");
        var nbImp = await db.PlayerImprovements.CountAsync(p => p.SkillId == id);
        if (nbImp > 0)
            throw new InvalidOperationException($"{nbImp} amélioration(s) référencent cette compétence.");
        var nbPostes = await db.PlayerPositionSkills.CountAsync(p => p.SkillId == id);
        if (nbPostes > 0)
            throw new InvalidOperationException($"{nbPostes} poste(s) ont cette compétence de départ.");

        var s = await db.Skills.FindAsync(id) ?? throw new InvalidOperationException("Skill introuvable");
        db.Skills.Remove(s);
        await db.SaveChangesAsync();
    }

    // ═══════════════════ KeywordLimit ═══════════════════

    public async Task<TeamTypeKeywordLimit> AjouterLimiteAsync(int teamTypeId, string motCle, int max)
    {
        var l = new TeamTypeKeywordLimit { TeamTypeId = teamTypeId, MotCle = motCle, Max = max };
        db.TeamTypeKeywordLimits.Add(l);
        await db.SaveChangesAsync();
        return l;
    }

    public async Task SupprimerLimiteAsync(int id)
    {
        var l = await db.TeamTypeKeywordLimits.FindAsync(id) ?? throw new InvalidOperationException("Limite introuvable");
        db.TeamTypeKeywordLimits.Remove(l);
        await db.SaveChangesAsync();
    }
}
