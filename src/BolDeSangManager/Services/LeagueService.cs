using BolDeSangManager.Data;
using BolDeSangManager.Data.Enums;
using BolDeSangManager.Data.Models;
using BolDeSangManager.Helpers;
using Microsoft.EntityFrameworkCore;

namespace BolDeSangManager.Services;

/// <param name="notifications">
/// Diffusion temps réel des changements de ligue aux écrans ouverts.
/// Optionnel : les tests unitaires instancient le service sans lui, et une
/// notification manquante ne doit jamais faire échouer une opération métier.
/// </param>
public class LeagueService(
    ApplicationDbContext db,
    ILogger<LeagueService> logger,
    IAuthorizationService authService,
    StaffService staffService,
    LeagueNotificationService? notifications = null)
{
    /// <summary>
    /// Prévient les écrans ouverts qu'une ligue vient de changer. Appelé APRÈS
    /// le SaveChanges : on ne diffuse que des faits déjà en base.
    /// </summary>
    private Task NotifierChangementAsync(int ligueId) =>
        notifications?.NotifierAsync(ligueId) ?? Task.CompletedTask;

    public async Task<List<League>> GetAllLiguesAsync() =>
        await db.Leagues
            .Include(l => l.Game)
            .Include(l => l.RulesVersion)
            .Include(l => l.Commissaire)
            .Include(l => l.Equipes)
            .OrderByDescending(l => l.CreeLe)
            .ToListAsync();

    /// <param name="ignorerCache">
    /// Vide d'abord le cache de suivi du DbContext. Indispensable au
    /// rafraîchissement TEMPS RÉEL : le contexte est lié au circuit Blazor, donc
    /// à l'onglet, et il vit aussi longtemps que la page. Sans ce vidage, EF
    /// renvoie l'instance chargée à l'ouverture — un coach prévenu du lancement
    /// de la saison relisait « Inscription » et l'écran ne changeait pas, alors
    /// que la base était bien à jour.
    /// </param>
    public async Task<League?> GetLigueAsync(int id, bool ignorerCache = false)
    {
        if (ignorerCache) db.ChangeTracker.Clear();

        return await db.Leagues
            .Include(l => l.Game)
            .Include(l => l.RulesVersion)
            .Include(l => l.Commissaire)
            // Équipes de la ligue chargées directement : en format Libre elles
            // n'ont pas encore de division au moment de composer le calendrier,
            // et passer uniquement par Divisions les rendrait invisibles.
            .Include(l => l.Equipes).ThenInclude(e => e.Coach)
            .Include(l => l.Equipes).ThenInclude(e => e.TeamType)
            .Include(l => l.Divisions).ThenInclude(d => d.Equipes).ThenInclude(e => e.Coach)
            .Include(l => l.Divisions).ThenInclude(d => d.Equipes).ThenInclude(e => e.TeamType)
            .Include(l => l.Divisions).ThenInclude(d => d.Matchs)
                .ThenInclude(m => m.EquipeDomicile)
            .Include(l => l.Divisions).ThenInclude(d => d.Matchs)
                .ThenInclude(m => m.EquipeExterieur)
            // La feuille est nécessaire à l'affichage des cartes de match :
            // « Corriger la saisie » (commissaire) et « Confirmer / En attente
            // adversaire » testent Match.Feuille. Sans ce Include elle est
            // toujours nulle et ces boutons ne s'affichent JAMAIS sur la fiche
            // de ligue — panne silencieuse, aucun test de service ne la voit.
            .Include(l => l.Divisions).ThenInclude(d => d.Matchs)
                .ThenInclude(m => m.Feuille)
            // Paliers du barème de points : la fiche de ligue affiche le barème
            // et la feuille de match conditionne l'affichage du « nombre de
            // tours » à leur présence.
            .Include(l => l.PaliersPoints)
            .FirstOrDefaultAsync(l => l.Id == id);
    }

    /// <param name="staffPersonnalise">
    /// Staff ajusté par le commissaire à la création. Quand il est fourni, il
    /// remplace la copie brute des règles : c'est ce qui permet de régler les
    /// bornes de fans, de désactiver un staff, etc. pour CETTE ligue seulement.
    /// </param>
    /// <param name="baremePoints">
    /// Barème de classement choisi à la création. <c>null</c> = on reprend celui
    /// de la version de règles. Les valeurs sont VALIDÉES côté service : elles
    /// viennent du navigateur.
    /// </param>
    /// <param name="paliersPoints">
    /// Paliers par nombre de tours, propres à cette ligue (jamais hérités des
    /// règles : c'est un choix de format).
    /// </param>
    public async Task<League> CreerLigueAsync(
        League ligue,
        string commissaireId,
        IEnumerable<LeagueStaffType>? staffPersonnalise = null,
        BaremePoints? baremePoints = null,
        IEnumerable<PalierPointsLigue>? paliersPoints = null)
    {
        ligue.CommissaireId = commissaireId;
        ligue.Statut = LeagueStatus.Creation;
        ligue.CreeLe = DateTime.UtcNow;

        // Barème de points de CLASSEMENT : copié depuis la version de règles,
        // côté serveur. C'est le même principe que le staff juste en dessous —
        // la ligue prend une copie figée qu'elle pourra ajuster ensuite via sa
        // propre commande, sans qu'une évolution des règles rétro-modifie une
        // ligue en cours. L'écran de création ne poste donc AUCUNE de ces
        // valeurs : elles ne sont pas falsifiables.
        var listePaliers = paliersPoints?.ToList() ?? [];

        if (baremePoints is not null)
        {
            ValiderBaremePoints(baremePoints, listePaliers);
            baremePoints.AppliquerA(ligue);
        }
        else
        {
            var versionBareme = await db.RulesVersions
                .AsNoTracking()
                .FirstOrDefaultAsync(v => v.Id == ligue.RulesVersionId);
            BaremePoints.DeVersion(versionBareme).AppliquerA(ligue);
        }

        db.Leagues.Add(ligue);
        await db.SaveChangesAsync();

        foreach (var p in listePaliers)
        {
            db.PaliersPointsLigue.Add(new PalierPointsLigue
            {
                LeagueId       = ligue.Id,
                APartirDuTour    = p.APartirDuTour,
                PointsVictoire = p.PointsVictoire,
                PointsNul      = p.PointsNul,
                PointsDefaite  = p.PointsDefaite
            });
        }
        if (listePaliers.Count > 0) await db.SaveChangesAsync();

        // Le staff des règles est COPIÉ dans la ligue : le commissaire pourra
        // l'ajuster pour son format sans toucher aux règles, et une évolution
        // ultérieure des règles ne rétro-modifiera pas cette ligue.
        var perso = staffPersonnalise?.ToList();
        if (perso is { Count: > 0 })
        {
            foreach (var s in perso)
            {
                db.LeagueStaffTypes.Add(new LeagueStaffType
                {
                    LeagueId             = ligue.Id,
                    StaffTypeId          = s.StaffTypeId,
                    Nom                  = s.Nom,
                    Description          = s.Description,
                    Ordre                = s.Ordre,
                    EstActif             = s.EstActif,
                    Cout                 = s.CoutDepuisTypeEquipe ? 0 : s.Cout,
                    CoutDepuisTypeEquipe = s.CoutDepuisTypeEquipe,
                    MinCreation          = s.MinCreation,
                    MaxCreation          = s.MaxCreation,
                    MaxLigue             = s.MaxLigue,
                    // Idem CopierVersLigueAsync : le drapeau VEA fait partie de
                    // la copie. Omis, il repart à true et remet les fans dans la
                    // VEA de la ligue nouvellement créée.
                    CompteDansVea        = s.CompteDansVea
                });
            }
            await db.SaveChangesAsync();
        }
        else
        {
            await staffService.CopierVersLigueAsync(ligue.Id, ligue.RulesVersionId);
        }

        logger.LogInformation("Ligue créée : {NomLigue} (id={Id}) par commissaire {CommissaireId}", ligue.Nom, ligue.Id, commissaireId);
        return ligue;
    }

    /// <summary>
    /// Modifie les paramètres d'une ligue tant que la saison n'est pas lancée.
    ///
    /// Ne touche NI au règlement NI au mode brouillard : ils ont leur propre
    /// commande sur la fiche de ligue et restent modifiables en cours de saison.
    ///
    /// Tout est revalidé ici : le grisage de l'écran d'édition n'est qu'un
    /// confort d'affichage, jamais une sécurité.
    /// </summary>
    /// <param name="staff">
    /// Staff ajusté pour cette ligue. <c>null</c> = staff inchangé. Refusé dès
    /// qu'une équipe est inscrite : leur trésorerie a été figée à leur création
    /// à partir de ces coûts.
    /// </param>
    public async Task ModifierLigueAsync(
        int ligueId,
        League modifiee,
        string userId,
        IEnumerable<LeagueStaffType>? staff = null)
    {
        if (!await authService.PeutGererLigueAsync(userId, ligueId))
            throw new InvalidOperationException("Vous ne gérez pas cette ligue.");

        var ligue = await db.Leagues
            .Include(l => l.Equipes)
            .FirstOrDefaultAsync(l => l.Id == ligueId)
            ?? throw new InvalidOperationException("Ligue introuvable");

        if (!DisplayHelpers.ParametresLigueEditables(ligue.Statut))
            throw new InvalidOperationException(
                "La saison est lancée : les paramètres de la ligue ne sont plus modifiables.");

        var structurantsOuverts =
            DisplayHelpers.ParametresStructurantsEditables(ligue.Statut, ligue.Equipes.Count);

        if (!structurantsOuverts)
        {
            if (modifiee.BudgetDepart != ligue.BudgetDepart)
                throw new InvalidOperationException(
                    "Des équipes sont déjà inscrites : le budget de départ ne peut plus changer. "
                    + "Supprimez les équipes, ou créez une nouvelle ligue.");
            if (modifiee.RulesVersionId != ligue.RulesVersionId || modifiee.GameId != ligue.GameId)
                throw new InvalidOperationException(
                    "Des équipes sont déjà inscrites : la version des règles ne peut plus changer. "
                    + "Supprimez les équipes, ou créez une nouvelle ligue.");
            if (staff is not null)
                throw new InvalidOperationException(
                    "Des équipes sont déjà inscrites : le staff de la ligue ne peut plus changer. "
                    + "Supprimez les équipes, ou créez une nouvelle ligue.");
        }

        if (string.IsNullOrWhiteSpace(modifiee.Nom))
            throw new InvalidOperationException("Le nom de la ligue est obligatoire.");
        if (modifiee.BudgetDepart < 0 || modifiee.BudgetDepart > 10_000_000)
            throw new InvalidOperationException($"Budget de départ invalide : {modifiee.BudgetDepart}.");

        if (structurantsOuverts
            && (modifiee.GameId != ligue.GameId || modifiee.RulesVersionId != ligue.RulesVersionId))
        {
            // La version choisie doit appartenir au jeu choisi : la valeur vient
            // de l'écran, donc elle est falsifiable.
            var versionValide = await db.RulesVersions.AnyAsync(
                v => v.Id == modifiee.RulesVersionId && v.GameId == modifiee.GameId);
            if (!versionValide)
                throw new InvalidOperationException(
                    "La version des règles choisie n'appartient pas au jeu sélectionné.");
        }

        ligue.Nom                  = modifiee.Nom.Trim();
        ligue.Description          = modifiee.Description?.Trim() ?? string.Empty;
        ligue.Format               = modifiee.Format;
        ligue.NombreEquipesPlayoff = modifiee.NombreEquipesPlayoff;
        ligue.PspParTouchdown       = modifiee.PspParTouchdown;
        ligue.PspParPasse           = modifiee.PspParPasse;
        ligue.PspParInterception    = modifiee.PspParInterception;
        ligue.PspParElimination     = modifiee.PspParElimination;
        ligue.PspBonusMvp           = modifiee.PspBonusMvp;

        if (structurantsOuverts)
        {
            ligue.GameId         = modifiee.GameId;
            ligue.RulesVersionId = modifiee.RulesVersionId;
            ligue.BudgetDepart   = modifiee.BudgetDepart;
        }

        await db.SaveChangesAsync();

        if (structurantsOuverts && staff is not null)
        {
            // Mise à jour EN PLACE, jamais supprimer/recréer : TeamStaff pointe
            // vers LeagueStaffType en cascade. Aucune équipe n'existe sur ce
            // chemin, mais la règle doit tenir si la garde évolue un jour.
            // Chaque ligne est revérifiée comme appartenant à CETTE ligue :
            // l'écran pourrait poster l'id du staff d'une autre ligue.
            var idsDeLaLigue = await db.LeagueStaffTypes
                .Where(s => s.LeagueId == ligueId)
                .Select(s => s.Id)
                .ToListAsync();

            foreach (var s in staff)
            {
                if (!idsDeLaLigue.Contains(s.Id))
                    throw new InvalidOperationException(
                        "Un staff modifié n'appartient pas à cette ligue.");
                await staffService.ModifierStaffLigueAsync(s);
            }
        }

        logger.LogInformation("Ligue modifiée : {NomLigue} (id={Id}) par {UserId}",
            ligue.Nom, ligue.Id, userId);

        await NotifierChangementAsync(ligueId);
    }

    public async Task DemarrerInscriptionsAsync(int ligueId)
    {
        var ligue = await db.Leagues.FindAsync(ligueId)
            ?? throw new InvalidOperationException("Ligue introuvable");
        ligue.Statut = LeagueStatus.Inscription;
        await db.SaveChangesAsync();
        await NotifierChangementAsync(ligueId);
    }

    /// <summary>
    /// Lance la saison : crée la division par défaut puis génère le pool de
    /// matchs (sauf en format Libre, où le commissaire compose lui-même).
    /// </summary>
    /// <returns>
    /// Les numéros de ronde dont l'échéance a été retirée parce qu'ils
    /// dépassent le calendrier réellement généré. Le commissaire peut dater ses
    /// rondes avant le lancement, alors que le nombre de rondes n'est pas encore
    /// connu (il dépend du nombre d'équipes inscrites) : les dates en trop sont
    /// nettoyées ici pour ne pas afficher d'échéances fantômes.
    /// Liste vide en format Libre — aucune ronde n'existe encore au lancement,
    /// et tout nettoyer effacerait le planning préparé en amont.
    /// </returns>
    public async Task<IReadOnlyList<int>> LancerSaisonAsync(int ligueId)
    {
        var ligue = await db.Leagues
            .Include(l => l.Equipes)
            .Include(l => l.Divisions)
            .FirstOrDefaultAsync(l => l.Id == ligueId)
            ?? throw new InvalidOperationException("Ligue introuvable");

        if (ligue.Equipes.Count < 2)
            throw new InvalidOperationException("Il faut au moins 2 équipes pour lancer la saison.");

        // Créer une division par défaut si aucune
        if (!ligue.Divisions.Any())
        {
            var div = new Division { LeagueId = ligueId, Nom = "Division Unique", Ordre = 1 };
            db.Divisions.Add(div);
            await db.SaveChangesAsync();

            foreach (var equipe in ligue.Equipes)
                equipe.DivisionId = div.Id;

            await db.SaveChangesAsync();
        }

        // Format Libre : le commissaire compose lui-même les rondes après le
        // lancement. Format Open : aucune ronde du tout, les rencontres sont
        // créées à la volée. Dans les deux cas on crée quand même la division
        // ci-dessus — en Open elle est indispensable, car SupprimerLigueAsync
        // retrouve les matchs VIA les divisions : un match sans division
        // resterait orphelin en base pour toujours.
        var calendrierGenere = !DisplayHelpers.EstFormatLibre(ligue.Format)
                            && !DisplayHelpers.SansCalendrier(ligue.Format);
        if (calendrierGenere)
            await GenererPoolMatchsAsync(ligue);

        ligue.Statut = LeagueStatus.EnCours;
        await db.SaveChangesAsync();

        var orphelines = calendrierGenere
            ? await NettoyerEcheancesOrphelinesAsync(ligueId)
            : [];

        logger.LogInformation("Saison lancée pour la ligue {NomLigue} (id={Id}) avec {NbEquipes} équipes (format={Format})", ligue.Nom, ligue.Id, ligue.Equipes.Count, ligue.Format);
        await NotifierChangementAsync(ligueId);
        return orphelines;
    }

    /// <summary>
    /// Retire les échéances dont la ronde n'existe pas dans le calendrier généré.
    /// Appelé uniquement quand le calendrier est produit automatiquement : en
    /// format Libre les rondes sont créées après coup, il n'y a rien à comparer.
    /// </summary>
    private async Task<IReadOnlyList<int>> NettoyerEcheancesOrphelinesAsync(int ligueId)
    {
        var rondesReelles = await db.Matches
            .Where(m => m.Division!.LeagueId == ligueId)
            .Select(m => m.Ronde)
            .Distinct()
            .ToListAsync();

        var orphelines = await db.EcheancesRondes
            .Where(e => e.LeagueId == ligueId && !rondesReelles.Contains(e.Ronde))
            .ToListAsync();

        if (orphelines.Count == 0) return [];

        db.EcheancesRondes.RemoveRange(orphelines);
        await db.SaveChangesAsync();

        var numeros = orphelines.Select(e => e.Ronde).OrderBy(r => r).ToList();
        logger.LogInformation(
            "Ligue {Id} : {Nb} échéance(s) retirée(s), rondes {Rondes} absentes du calendrier généré",
            ligueId, numeros.Count, string.Join(", ", numeros));
        return numeros;
    }

    /// <summary>
    /// Format Libre : définit (ou remplace) les rencontres d'une ronde.
    /// Une équipe non citée est simplement au repos ce tour-ci.
    /// Une ronde dont un match est déjà joué ne peut plus être modifiée.
    /// </summary>
    public async Task DefinirRondeAsync(int ligueId, int ronde, IReadOnlyList<(int domicileId, int exterieurId)> paires)
    {
        var ligue = await db.Leagues
            .Include(l => l.Equipes)
            .Include(l => l.Divisions)
            .FirstOrDefaultAsync(l => l.Id == ligueId)
            ?? throw new InvalidOperationException("Ligue introuvable");

        if (!DisplayHelpers.EstFormatLibre(ligue.Format))
            throw new InvalidOperationException(
                "Seules les ligues au format Libre permettent de composer les rondes à la main.");

        if (ronde < 1)
            throw new InvalidOperationException("Le numéro de ronde doit être supérieur ou égal à 1.");

        var equipesDeLaLigue = ligue.Equipes.ToDictionary(e => e.Id, e => e.Nom);

        // Validation des paires avant toute écriture.
        var vues = new Dictionary<int, string>();
        foreach (var (domicileId, exterieurId) in paires)
        {
            if (domicileId == exterieurId)
                throw new InvalidOperationException("Une équipe ne peut pas se rencontrer elle-même.");

            foreach (var id in new[] { domicileId, exterieurId })
            {
                if (!equipesDeLaLigue.TryGetValue(id, out var nom))
                    throw new InvalidOperationException("Une des équipes sélectionnées n'appartient pas à cette ligue.");

                if (!vues.TryAdd(id, nom))
                    throw new InvalidOperationException(
                        $"« {nom} » apparaît deux fois dans la ronde {ronde} : une équipe ne peut jouer qu'un match par ronde.");
            }
        }

        var divisionId = ligue.Divisions.OrderBy(d => d.Ordre).FirstOrDefault()?.Id
            ?? throw new InvalidOperationException("La ligue n'a pas encore de division. Lancez la saison d'abord.");

        var existants = await db.Matches
            .Where(m => m.Division!.LeagueId == ligueId && m.Ronde == ronde && !m.EstPlayoff)
            .ToListAsync();

        if (existants.Any(BrouillardHelpers.EstJoue))
            throw new InvalidOperationException(
                $"La ronde {ronde} a déjà commencé : elle ne peut plus être modifiée.");

        await using var tx = await db.Database.BeginTransactionAsync();
        try
        {
            db.Matches.RemoveRange(existants);
            await db.SaveChangesAsync();

            db.Matches.AddRange(paires.Select(p => new Match
            {
                DivisionId        = divisionId,
                Ronde             = ronde,
                EquipeDomicileId  = p.domicileId,
                EquipeExterieurId = p.exterieurId,
                Statut            = MatchStatus.Programme,
                EstPlayoff        = false
            }));
            await db.SaveChangesAsync();

            await tx.CommitAsync();
            logger.LogInformation("Ronde {Ronde} définie pour la ligue {Id} : {NbMatchs} rencontre(s)", ronde, ligueId, paires.Count);
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    /// <summary>
    /// Format Open : crée une rencontre à la volée entre deux équipes de la
    /// ligue, sans passer par un calendrier. Proposable par n'importe quel
    /// participant — l'adversaire confirme ensuite via le flux de validation de
    /// feuille de match habituel.
    ///
    /// Le match est rattaché à la division technique de la ligue (sinon il
    /// serait invisible de <see cref="SupprimerLigueAsync"/>) et porte
    /// <c>Ronde = 0</c>, la convention « hors ronde » — le format Open n'a pas
    /// de rondes, et rendre la colonne nullable imposerait un AlterColumn sur
    /// une table live.
    /// </summary>
    /// <returns>L'identifiant du match créé.</returns>
    public async Task<int> ProposerRencontreAsync(
        int ligueId, int domicileId, int exterieurId,
        DateTime? dateProgrammee = null, string lieu = "")
    {
        var ligue = await db.Leagues
            .Include(l => l.Equipes)
            .Include(l => l.Divisions)
            .FirstOrDefaultAsync(l => l.Id == ligueId)
            ?? throw new InvalidOperationException("Ligue introuvable");

        if (!DisplayHelpers.SansCalendrier(ligue.Format))
            throw new InvalidOperationException(
                "Seules les ligues au format Open permettent de proposer une rencontre librement.");

        if (ligue.Statut == LeagueStatus.Termine)
            throw new InvalidOperationException("Cette ligue est clôturée : plus aucune rencontre ne peut y être créée.");

        if (domicileId == exterieurId)
            throw new InvalidOperationException("Une équipe ne peut pas se rencontrer elle-même.");

        foreach (var id in new[] { domicileId, exterieurId })
        {
            if (ligue.Equipes.All(e => e.Id != id))
                throw new InvalidOperationException("Une des équipes sélectionnées n'appartient pas à cette ligue.");
        }

        var division = ligue.Divisions.OrderBy(d => d.Ordre).FirstOrDefault()
            ?? throw new InvalidOperationException(
                "La ligue n'a pas encore de division : lancez-la avant de proposer une rencontre.");

        var match = new Match
        {
            DivisionId        = division.Id,
            Ronde             = 0,               // hors ronde : le format Open n'en a pas
            EquipeDomicileId  = domicileId,
            EquipeExterieurId = exterieurId,
            Statut            = MatchStatus.Programme,
            DateProgrammee    = dateProgrammee,
            Lieu              = lieu
        };
        db.Matches.Add(match);
        await db.SaveChangesAsync();

        logger.LogInformation(
            "Rencontre Open créée dans la ligue {Id} : équipe {Dom} contre équipe {Ext} (match {MatchId})",
            ligueId, domicileId, exterieurId, match.Id);
        await NotifierChangementAsync(ligueId);
        return match.Id;
    }

    /// <summary>
    /// Date indicative de fin de ronde : celle à laquelle les matchs devraient
    /// être joués. Purement informative — passer `null` retire l'échéance.
    /// </summary>
    public async Task DefinirEcheanceRondeAsync(int ligueId, int ronde, DateTime? dateLimite)
    {
        var ligue = await db.Leagues.FindAsync(ligueId)
            ?? throw new InvalidOperationException("Ligue introuvable");

        if (ronde < 1)
            throw new InvalidOperationException("Le numéro de ronde doit être supérieur ou égal à 1.");

        var existante = await db.EcheancesRondes
            .FirstOrDefaultAsync(e => e.LeagueId == ligueId && e.Ronde == ronde);

        // Une échéance est une DATE, pas un instant. On la stocke à midi UTC :
        // minuit basculerait d'un jour à l'autre selon le fuseau et l'heure
        // d'été, et la date affichée ne serait plus celle qui a été saisie.
        DateTime? valeur = dateLimite is null
            ? null
            : new DateTime(dateLimite.Value.Year, dateLimite.Value.Month, dateLimite.Value.Day,
                           12, 0, 0, DateTimeKind.Utc);

        if (valeur is null)
        {
            if (existante is not null) db.EcheancesRondes.Remove(existante);
        }
        else if (existante is null)
        {
            db.EcheancesRondes.Add(new EcheanceRonde
            {
                LeagueId   = ligueId,
                Ronde      = ronde,
                DateLimite = valeur.Value
            });
        }
        else
        {
            existante.DateLimite = valeur.Value;
        }

        await db.SaveChangesAsync();
        logger.LogInformation("Échéance de la ronde {Ronde} (ligue {Id}) : {Date}", ronde, ligueId, dateLimite?.ToString("yyyy-MM-dd") ?? "retirée");
    }

    /// <summary>Échéances indicatives d'une ligue, indexées par numéro de ronde.</summary>
    public async Task<Dictionary<int, DateTime>> GetEcheancesRondesAsync(int ligueId) =>
        await db.EcheancesRondes
            .Where(e => e.LeagueId == ligueId)
            .ToDictionaryAsync(e => e.Ronde, e => e.DateLimite);

    /// <summary>
    /// Format Libre : propose un appariement des équipes encore libres d'une
    /// ronde, en évitant autant que possible les affrontements déjà programmés
    /// dans les autres rondes de la ligue.
    ///
    /// Un simple appariement dans l'ordre rejouerait sans cesse les mêmes
    /// rencontres. On retient donc, parmi les paires possibles, celle dont les
    /// deux équipes se sont le moins souvent affrontées (puis, à égalité, celle
    /// qui a le moins joué), en inversant domicile/extérieur par rapport à la
    /// dernière confrontation.
    /// </summary>
    public async Task<List<(int domicileId, int exterieurId)>> ProposerAppariementsAsync(
        int ligueId, int ronde, IReadOnlyList<int> equipesLibres,
        IReadOnlyList<(int domicileId, int exterieurId)>? dejaComposees = null)
    {
        var libres = equipesLibres.ToList();
        var propositions = new List<(int, int)>();
        if (libres.Count < 2) return propositions;

        // Historique des confrontations, toutes rondes confondues sauf celle-ci.
        var historique = (await db.Matches
            .Where(m => m.Division!.LeagueId == ligueId && m.Ronde != ronde)
            .Select(m => new { m.EquipeDomicileId, m.EquipeExterieurId })
            .ToListAsync())
            .Select(m => (m.EquipeDomicileId, m.EquipeExterieurId))
            .ToList();

        // Rondes composées à l'écran mais pas encore enregistrées : sans elles,
        // deux rondes créées d'affilée proposeraient les mêmes rencontres.
        if (dejaComposees is not null)
            historique.AddRange(dejaComposees);

        static string Cle(int a, int b) => a < b ? $"{a}-{b}" : $"{b}-{a}";

        var nbRencontres = new Dictionary<string, int>();
        var nbMatchs     = new Dictionary<int, int>();
        var dernierDomicile = new Dictionary<string, int>();

        foreach (var (dom, ext) in historique)
        {
            var cle = Cle(dom, ext);
            nbRencontres[cle] = nbRencontres.GetValueOrDefault(cle) + 1;
            dernierDomicile[cle] = dom;
            nbMatchs[dom] = nbMatchs.GetValueOrDefault(dom) + 1;
            nbMatchs[ext] = nbMatchs.GetValueOrDefault(ext) + 1;
        }

        while (libres.Count >= 2)
        {
            // L'équipe la moins servie ouvre l'appariement.
            var a = libres.OrderBy(id => nbMatchs.GetValueOrDefault(id)).ThenBy(id => id).First();
            libres.Remove(a);

            // Son adversaire : celui qu'elle a le moins rencontré.
            var b = libres
                .OrderBy(id => nbRencontres.GetValueOrDefault(Cle(a, id)))
                .ThenBy(id => nbMatchs.GetValueOrDefault(id))
                .ThenBy(id => id)
                .First();
            libres.Remove(b);

            // Alternance : si a recevait la dernière fois, il se déplace.
            var cle = Cle(a, b);
            var aRecuDernierement = dernierDomicile.TryGetValue(cle, out var d) && d == a;
            propositions.Add(aRecuDernierement ? (b, a) : (a, b));

            nbRencontres[cle] = nbRencontres.GetValueOrDefault(cle) + 1;
            dernierDomicile[cle] = aRecuDernierement ? b : a;
            nbMatchs[a] = nbMatchs.GetValueOrDefault(a) + 1;
            nbMatchs[b] = nbMatchs.GetValueOrDefault(b) + 1;
        }

        return propositions;
    }

    /// <summary>
    /// Renumérote les rondes d'une ligue en 1, 2, 3… sans trou, après une
    /// suppression. Une ronde déjà commencée ne peut pas changer de numéro
    /// (des matchs joués y font référence) : dans ce cas on ne touche à rien
    /// et on renvoie false, à charge de l'appelant de laisser la numérotation.
    ///
    /// ⚠️ Les rondes existent sous DEUX formes : des matchs (après lancement) et
    /// des échéances de date (avant lancement, quand on prépare le calendrier).
    /// Ne regarder que les matchs faisait sortir la méthode immédiatement avant
    /// le lancement, et la numérotation gardait son trou (« Ronde 1, 2, 4 »).
    /// On renumérote donc sur l'UNION des deux.
    /// </summary>
    public async Task<bool> RenumeroterRondesAsync(int ligueId)
    {
        var matchs = await db.Matches
            .Where(m => m.Division!.LeagueId == ligueId && !m.EstPlayoff)
            .ToListAsync();

        var echeances = await db.EcheancesRondes.Where(e => e.LeagueId == ligueId).ToListAsync();

        if (matchs.Count == 0 && echeances.Count == 0) return true;

        var ordre = matchs.Select(m => m.Ronde)
                          .Concat(echeances.Select(e => e.Ronde))
                          .Distinct().OrderBy(r => r).ToList();

        var cible = ordre.Select((ancien, i) => (ancien, nouveau: i + 1))
                         .Where(x => x.ancien != x.nouveau)
                         .ToList();

        if (cible.Count == 0) return true;   // déjà compact

        // Une ronde commencée ne doit pas être renumérotée.
        var rondesCommencees = matchs.Where(BrouillardHelpers.EstJoue)
                                     .Select(m => m.Ronde).ToHashSet();
        if (cible.Any(x => rondesCommencees.Contains(x.ancien)))
        {
            logger.LogInformation("Renumérotation ignorée pour la ligue {Id} : une ronde a déjà commencé", ligueId);
            return false;
        }

        var map = cible.ToDictionary(x => x.ancien, x => x.nouveau);
        foreach (var m in matchs.Where(m => map.ContainsKey(m.Ronde)))
            m.Ronde = map[m.Ronde];

        foreach (var e in echeances.Where(e => map.ContainsKey(e.Ronde)))
            e.Ronde = map[e.Ronde];

        await db.SaveChangesAsync();
        logger.LogInformation("Rondes renumérotées pour la ligue {Id} : {Nb} décalage(s)", ligueId, map.Count);
        return true;
    }

    /// <summary>
    /// Format Libre : supprime une ronde entière. Les rondes suivantes ne sont
    /// pas renumérotées — le commissaire recompose s'il le souhaite.
    /// </summary>
    public async Task SupprimerRondeAsync(int ligueId, int ronde)
    {
        var ligue = await db.Leagues.FindAsync(ligueId)
            ?? throw new InvalidOperationException("Ligue introuvable");

        if (!DisplayHelpers.EstFormatLibre(ligue.Format))
            throw new InvalidOperationException(
                "Seules les ligues au format Libre permettent de supprimer une ronde.");

        var matchs = await db.Matches
            .Where(m => m.Division!.LeagueId == ligueId && m.Ronde == ronde && !m.EstPlayoff)
            .ToListAsync();

        if (matchs.Any(BrouillardHelpers.EstJoue))
            throw new InvalidOperationException(
                $"La ronde {ronde} a déjà commencé : elle ne peut plus être supprimée.");

        db.Matches.RemoveRange(matchs);

        // L'échéance de la ronde n'a plus d'objet.
        var echeance = await db.EcheancesRondes
            .FirstOrDefaultAsync(e => e.LeagueId == ligueId && e.Ronde == ronde);
        if (echeance is not null) db.EcheancesRondes.Remove(echeance);

        await db.SaveChangesAsync();
        logger.LogInformation("Ronde {Ronde} supprimée pour la ligue {Id} ({NbMatchs} rencontre(s))", ronde, ligueId, matchs.Count);
    }

    private async Task GenererPoolMatchsAsync(League ligue)
    {
        var divisions = await db.Divisions
            .Include(d => d.Equipes)
            .Where(d => d.LeagueId == ligue.Id)
            .ToListAsync();

        foreach (var division in divisions)
        {
            var equipes = division.Equipes.ToList();
            var matchs = GenererRoundRobin(equipes, division.Id);
            db.Matches.AddRange(matchs);
        }
        await db.SaveChangesAsync();
    }

    private static List<Match> GenererRoundRobin(List<Team> equipes, int divisionId)
    {
        // Copie locale ; si nombre impair, ajouter null comme équipe fantôme (bye)
        // Cela rend n pair et garantit que chaque équipe rencontre toutes les autres.
        var liste = new List<Team?>(equipes.Cast<Team?>());
        if (liste.Count % 2 != 0) liste.Add(null);
        int n = liste.Count;

        var matchs = new List<Match>();

        for (int ronde = 1; ronde < n; ronde++)
        {
            for (int i = 0; i < n / 2; i++)
            {
                var domicile = liste[i];
                var exterieur = liste[n - 1 - i];

                // Ignorer les matches impliquant l'équipe fantôme (= bye)
                if (domicile is null || exterieur is null) continue;

                matchs.Add(new Match
                {
                    DivisionId = divisionId,
                    Ronde = ronde,
                    EquipeDomicileId = domicile.Id,
                    EquipeExterieurId = exterieur.Id,
                    Statut = MatchStatus.Programme,
                    EstPlayoff = false
                });
            }

            // Rotation : fixer la première équipe, faire tourner les autres
            var derniere = liste[n - 1];
            liste.RemoveAt(n - 1);
            liste.Insert(1, derniere);
        }

        return matchs;
    }

    public async Task LancerPhaseDeReposAsync(int ligueId)
    {
        var ligue = await db.Leagues.FindAsync(ligueId)
            ?? throw new InvalidOperationException("Ligue introuvable");

        if (ligue.Statut != LeagueStatus.EnCours)
            throw new InvalidOperationException("La phase de repos ne peut être lancée que depuis l'état EnCours.");

        var teamIds = await db.Teams.Where(t => t.LeagueId == ligueId).Select(t => t.Id).ToListAsync();
        var joueurs = await db.TeamPlayers
            .Where(j => teamIds.Contains(j.TeamId) && j.ManqueSuivantMatch)
            .ToListAsync();

        foreach (var j in joueurs)
            j.ManqueSuivantMatch = false;

        ligue.Statut = LeagueStatus.PhaseDeRepos;
        await db.SaveChangesAsync();

        logger.LogInformation(
            "Phase de repos lancée pour la ligue {NomLigue} (id={Id}) : {NbResetRPM} RPM reset sur {NbEquipes} équipes",
            ligue.Nom, ligue.Id, joueurs.Count, teamIds.Count);

        await NotifierChangementAsync(ligueId);
    }

    public async Task GenererPlayoffsAsync(int ligueId)
    {
        var ligue = await db.Leagues
            .Include(l => l.Divisions).ThenInclude(d => d.Equipes)
            .FirstOrDefaultAsync(l => l.Id == ligueId)
            ?? throw new InvalidOperationException("Ligue introuvable");

        if (ligue.Statut != LeagueStatus.PhaseDeRepos && ligue.Statut != LeagueStatus.EnCours)
            throw new InvalidOperationException("Les playoffs ne peuvent être générés qu'après la saison régulière ou la phase de repos.");

        // Trier les équipes par points de ligue
        var equipesQualifiees = ligue.Divisions
            .SelectMany(d => d.Equipes)
            .OrderByDescending(e => e.PointsLigue)
            .ThenByDescending(e => e.TouchdownsMarques - e.TouchdownsConcedes)
            .ThenByDescending(e => e.EliminationsInfligees)
            .Take(ligue.NombreEquipesPlayoff)
            .ToList();

        if (equipesQualifiees.Count < 2)
            throw new InvalidOperationException("Pas assez d'équipes qualifiées pour les playoffs.");

        // Générer les quarts (ou demi si 4 équipes)
        var matchsPlayoff = new List<Match>();
        int ronde = 100; // Ronde 100+ = playoffs
        for (int i = 0; i < equipesQualifiees.Count / 2; i++)
        {
            matchsPlayoff.Add(new Match
            {
                DivisionId = ligue.Divisions.First().Id,
                Ronde = ronde,
                EquipeDomicileId = equipesQualifiees[i].Id,
                EquipeExterieurId = equipesQualifiees[equipesQualifiees.Count - 1 - i].Id,
                Statut = MatchStatus.Programme,
                EstPlayoff = true
            });
        }

        db.Matches.AddRange(matchsPlayoff);
        ligue.Statut = LeagueStatus.PlayOffs;
        await db.SaveChangesAsync();
        logger.LogInformation("Playoffs générés pour la ligue {NomLigue} (id={Id}) : {NbMatchs} matchs, {NbEquipes} équipes qualifiées", ligue.Nom, ligue.Id, matchsPlayoff.Count, equipesQualifiees.Count);
        await NotifierChangementAsync(ligueId);
    }

    /// <summary>
    /// Validation post-match de repos par un coach : applique améliorations, recrutements et achat de relances
    /// sans qu'un Match ne soit nécessaire. Trace via PhaseDeReposValidation.
    /// </summary>
    public async Task ValiderApresMatchReposAsync(
        int ligueId,
        int teamId,
        List<(int joueurId, int skillId, bool estPrincipale, int pspDepensee)> competences,
        List<(int positionId, string nom, int numero)> nouveauxJoueurs,
        int nouvellesRelances,
        TeamService teamService)
    {
        var ligue = await db.Leagues.FindAsync(ligueId)
            ?? throw new InvalidOperationException("Ligue introuvable");
        if (ligue.Statut != LeagueStatus.PhaseDeRepos)
            throw new InvalidOperationException("La validation de repos n'est possible que pendant la phase de repos.");

        var dejaValide = await db.PhaseDeReposValidations
            .AnyAsync(v => v.LeagueId == ligueId && v.TeamId == teamId);
        if (dejaValide)
            throw new InvalidOperationException("Cette équipe a déjà validé sa phase de repos.");

        foreach (var (joueurId, skillId, estPrincipale, pspDepensee) in competences)
        {
            var type = estPrincipale ? ImprovementType.SelectionPrimaire : ImprovementType.SelectionSecondaire;
            await teamService.AppliquerAmeliorationAsync(joueurId, type, skillId: skillId,
                matchSheetId: null, pspDepensee: pspDepensee);
        }

        foreach (var (positionId, nom, numero) in nouveauxJoueurs)
            await teamService.RecruterJoueurAsync(teamId, positionId, nom, numero);

        if (nouvellesRelances > 0)
        {
            var equipe = await db.Teams.Include(t => t.TeamType).FirstAsync(t => t.Id == teamId);
            var coutRelance = (equipe.TeamType?.CoutRelance ?? 50_000) * 2;
            var total = nouvellesRelances * coutRelance;
            if (equipe.Tresorerie < total)
                throw new InvalidOperationException("Fonds insuffisants pour acheter les relances.");
            const int maxRelances = 8;
            if (equipe.NombreRelances + nouvellesRelances > maxRelances)
                throw new InvalidOperationException($"Maximum {maxRelances} relances par équipe.");
            equipe.Tresorerie -= total;
            equipe.NombreRelances += nouvellesRelances;
        }

        db.PhaseDeReposValidations.Add(new PhaseDeReposValidation
        {
            LeagueId = ligueId,
            TeamId = teamId
        });
        await db.SaveChangesAsync();

        logger.LogInformation(
            "Phase de repos validée pour équipe id={TeamId} dans ligue id={LigueId} : {NbComp} comp., {NbNouv} recrues, {NbRel} relances",
            teamId, ligueId, competences.Count, nouveauxJoueurs.Count, nouvellesRelances);
    }

    /// <summary>
    /// Cette équipe a-t-elle déjà validé sa phase de repos ? Sert à l'écran de
    /// validation, qui ne doit pas laisser un coach progresser deux fois.
    /// </summary>
    public async Task<bool> ADejaValideReposAsync(int ligueId, int teamId) =>
        await db.PhaseDeReposValidations
            .AnyAsync(v => v.LeagueId == ligueId && v.TeamId == teamId);

    public async Task TerminerLigueAsync(int ligueId)
    {
        var ligue = await db.Leagues.FindAsync(ligueId)
            ?? throw new InvalidOperationException("Ligue introuvable");
        ligue.Statut = LeagueStatus.Termine;
        await db.SaveChangesAsync();
        await NotifierChangementAsync(ligueId);
    }

    public async Task<List<Match>> GetMatchsDivisionAsync(int divisionId) =>
        await db.Matches
            .Include(m => m.EquipeDomicile).ThenInclude(e => e.Coach)
            .Include(m => m.EquipeExterieur).ThenInclude(e => e.Coach)
            .Include(m => m.Feuille)
            .Where(m => m.DivisionId == divisionId)
            .OrderBy(m => m.Ronde)
            .ToListAsync();

    public async Task<List<Game>> GetGamesAsync() =>
        await db.Games.Include(g => g.Versions).ToListAsync();

    public async Task<List<RulesVersion>> GetVersionsAsync(int gameId) =>
        await db.RulesVersions
            .Where(v => v.GameId == gameId)
            .OrderBy(v => v.Ordre)
            .ToListAsync();

    public async Task<bool> EstCommissaireAsync(int ligueId, string userId)
        => await authService.PeutGererLigueAsync(userId, ligueId);

    /// <summary>
    /// Enregistre le règlement (markdown) d'une ligue (R5). Réservé aux commissaires :
    /// l'habilitation est revérifiée ici, pas seulement masquée dans l'UI.
    /// </summary>
    public async Task DefinirReglementAsync(int ligueId, string markdown, string userId)
    {
        if (!await authService.PeutGererLigueAsync(userId, ligueId))
            throw new UnauthorizedAccessException("Seul un commissaire peut modifier le règlement.");

        var ligue = await db.Leagues.FirstOrDefaultAsync(l => l.Id == ligueId)
            ?? throw new InvalidOperationException("Ligue introuvable");

        ligue.Reglement = markdown ?? string.Empty;
        await db.SaveChangesAsync();

        logger.LogInformation("Ligue id={LigueId} : règlement mis à jour par {UserId} ({Taille} caractères)",
            ligueId, userId, ligue.Reglement.Length);
    }

    /// <summary>
    /// Active ou désactive le mode brouillard d'une ligue (#2). Réservé aux
    /// commissaires : l'habilitation est revérifiée ici, pas seulement dans l'UI.
    /// </summary>
    public async Task DefinirModeBrouillardAsync(int ligueId, bool actif, string userId)
    {
        if (!await authService.PeutGererLigueAsync(userId, ligueId))
            throw new UnauthorizedAccessException("Seul un commissaire peut modifier ce réglage.");

        var ligue = await db.Leagues.FirstOrDefaultAsync(l => l.Id == ligueId)
            ?? throw new InvalidOperationException("Ligue introuvable");

        ligue.ModeBrouillard = actif;
        await db.SaveChangesAsync();

        logger.LogInformation("Ligue id={LigueId} : mode brouillard {Etat} par {UserId}",
            ligueId, actif ? "activé" : "désactivé", userId);
    }

    public async Task<List<TeamPlayer>> GetTopJoueursParPspAsync(int ligueId, int limit = 10) =>
        await db.TeamPlayers
            .Include(j => j.Team)
            .Include(j => j.PlayerPosition)
            .Where(j => j.Team.LeagueId == ligueId && !j.EstMort && !j.EstRetraite)
            .OrderByDescending(j => j.PointsStarPlayer)
            .Take(limit)
            .ToListAsync();

    public async Task<List<TeamPlayer>> GetTopMarqueursAsync(int ligueId, int limit = 10) =>
        await db.TeamPlayers
            .Include(j => j.Team)
            .Include(j => j.PlayerPosition)
            .Include(j => j.RecordsMatchs)
            .Where(j => j.Team.LeagueId == ligueId)
            .OrderByDescending(j => j.RecordsMatchs.Sum(r => r.Touchdowns))
            .Take(limit)
            .ToListAsync();

    public async Task<List<TeamPlayer>> GetTopElimineursAsync(int ligueId, int limit = 10) =>
        await db.TeamPlayers
            .Include(j => j.Team)
            .Include(j => j.PlayerPosition)
            .Include(j => j.RecordsMatchs)
            .Where(j => j.Team.LeagueId == ligueId)
            .OrderByDescending(j => j.RecordsMatchs.Sum(r => r.EliminationsInfligees))
            .Take(limit)
            .ToListAsync();

    public async Task<List<TeamPlayer>> GetTopPasseursAsync(int ligueId, int limit = 10) =>
        await db.TeamPlayers
            .Include(j => j.Team)
            .Include(j => j.PlayerPosition)
            .Include(j => j.RecordsMatchs)
            .Where(j => j.Team.LeagueId == ligueId)
            .OrderByDescending(j => j.RecordsMatchs.Sum(r => r.Passes + r.Interceptions))
            .Take(limit)
            .ToListAsync();

    // ── Barème de points de classement ────────────────────────────────────────

    /// <summary>
    /// Modifie le barème de points d'une ligue et <b>recalcule aussitôt le
    /// classement</b> depuis les feuilles de match déjà saisies.
    ///
    /// ⚠️ Volontairement HORS de <see cref="ModifierLigueAsync"/> : celui-ci
    /// refuse toute modification dès que la saison est lancée, alors que le
    /// barème doit justement rester éditable en cours de route (c'est le besoin :
    /// paramétrer des ligues déjà en cours au moment du déploiement). C'est sans
    /// danger précisément parce que rien n'est figé — contrairement à
    /// Team.Tresorerie, PointsLigue est intégralement reconstructible.
    ///
    /// Ne touche à AUCUN autre paramètre de la ligue (un test le verrouille).
    /// </summary>
    /// <returns>Nombre de matchs rejoués par le recalcul.</returns>
    public async Task<int> ModifierBaremePointsAsync(
        int ligueId, BaremePoints bareme, IEnumerable<PalierPointsLigue> paliers, string userId)
    {
        if (!await authService.PeutGererLigueAsync(userId, ligueId))
            throw new InvalidOperationException("Vous ne gérez pas cette ligue.");

        var ligue = await db.Leagues
            .Include(l => l.PaliersPoints)
            .FirstOrDefaultAsync(l => l.Id == ligueId)
            ?? throw new InvalidOperationException("Ligue introuvable");

        var listePaliers = paliers.ToList();
        ValiderBaremePoints(bareme, listePaliers);

        bareme.AppliquerA(ligue);

        // Les paliers sont remplacés en bloc : plus simple et plus sûr qu'un
        // diff, la table n'a aucune donnée propre à préserver.
        db.PaliersPointsLigue.RemoveRange(ligue.PaliersPoints);
        foreach (var p in listePaliers)
        {
            db.PaliersPointsLigue.Add(new PalierPointsLigue
            {
                LeagueId       = ligueId,
                APartirDuTour    = p.APartirDuTour,
                PointsVictoire = p.PointsVictoire,
                PointsNul      = p.PointsNul,
                PointsDefaite  = p.PointsDefaite
            });
        }

        await db.SaveChangesAsync();

        var matchsRejoues = await RecalculerClassementAsync(ligueId);

        logger.LogInformation(
            "Barème de points modifié pour la ligue id={LigueId} ({NbPaliers} palier(s)) — {Nb} match(s) rejoué(s)",
            ligueId, listePaliers.Count, matchsRejoues);

        await NotifierChangementAsync(ligueId);
        return matchsRejoues;
    }

    /// <summary>
    /// Validation d'un barème posté par un écran. Centralisée parce que DEUX
    /// chemins y mènent — création de ligue et édition en cours de saison — et
    /// qu'un garde-fou présent d'un seul côté ne protège rien.
    /// </summary>
    private static void ValiderBaremePoints(BaremePoints bareme, List<PalierPointsLigue> paliers)
    {
        foreach (var valeur in new[]
                 {
                     bareme.Victoire, bareme.Nul, bareme.Defaite,
                     bareme.ParTouchdown, bareme.ParElimination, bareme.ParInterception,
                     bareme.ParPasse, bareme.ParDeviation, bareme.ParAgression
                 })
        {
            if (valeur < 0 || valeur > 1_000_000)
                throw new InvalidOperationException($"Valeur de barème invalide : {valeur}.");
        }

        foreach (var p in paliers)
        {
            if (p.APartirDuTour < 1 || p.APartirDuTour > 50)
                throw new InvalidOperationException(
                    $"Palier invalide : le nombre de tours doit être compris entre 1 et 50 (reçu {p.APartirDuTour}).");
            if (p.PointsVictoire < 0 || p.PointsNul < 0 || p.PointsDefaite < 0)
                throw new InvalidOperationException("Les points d'un palier ne peuvent pas être négatifs.");
        }

        if (paliers.Select(p => p.APartirDuTour).Distinct().Count() != paliers.Count)
            throw new InvalidOperationException("Deux paliers ne peuvent pas viser le même nombre de tours.");
    }

    /// <summary>
    /// Reconstruit intégralement les compteurs sportifs des équipes d'une ligue
    /// (points de classement, V/N/D, touchdowns, éliminations) en rejouant toutes
    /// les feuilles de match terminées.
    ///
    /// C'est la SOURCE DE VÉRITÉ du classement : la mise à jour au fil de l'eau
    /// (MatchService) et ce recalcul appellent la même fonction pure
    /// <see cref="BaremePoints.PointsEquipe"/>, et un test prouve qu'ils
    /// convergent. Sans cette propriété, éditer un barème en cours de saison
    /// laisserait des totaux faux.
    /// </summary>
    /// <returns>Nombre de matchs rejoués.</returns>
    public async Task<int> RecalculerClassementAsync(int ligueId)
    {
        var ligue = await db.Leagues
            .Include(l => l.PaliersPoints)
            .FirstOrDefaultAsync(l => l.Id == ligueId)
            ?? throw new InvalidOperationException("Ligue introuvable");

        var equipes = await db.Teams.Where(t => t.LeagueId == ligueId).ToListAsync();

        foreach (var e in equipes)
        {
            e.PointsLigue           = 0;
            e.NombreVictoires       = 0;
            e.NombreNuls            = 0;
            e.NombreDefaites        = 0;
            e.NombreMatchsJoues     = 0;
            e.TouchdownsMarques     = 0;
            e.TouchdownsConcedes    = 0;
            e.EliminationsInfligees = 0;
        }

        var parId = equipes.ToDictionary(e => e.Id);
        var bareme = BaremePoints.DeLigue(ligue);

        // Un match compte dès que sa feuille existe et qu'il n'est plus en cours
        // de saisie : même critère que la mise à jour au fil de l'eau, qui
        // s'applique à l'enregistrement de la feuille.
        var matchs = await db.Matches
            .Include(m => m.Feuille).ThenInclude(f => f!.RecordsJoueurs)
            .Where(m => m.Division!.LeagueId == ligueId && m.Feuille != null)
            .ToListAsync();

        int rejoues = 0;

        foreach (var match in matchs)
        {
            var feuille = match.Feuille!;
            if (!parId.TryGetValue(match.EquipeDomicileId, out var dom)) continue;
            if (!parId.TryGetValue(match.EquipeExterieurId, out var ext)) continue;

            var records = feuille.RecordsJoueurs.ToList();

            dom.NombreMatchsJoues++;
            ext.NombreMatchsJoues++;
            dom.TouchdownsMarques     += feuille.TouchdownsDomicile;
            dom.TouchdownsConcedes    += feuille.TouchdownsExterieur;
            ext.TouchdownsMarques     += feuille.TouchdownsExterieur;
            ext.TouchdownsConcedes    += feuille.TouchdownsDomicile;
            dom.EliminationsInfligees += feuille.EliminationsDomicile;
            ext.EliminationsInfligees += feuille.EliminationsExterieur;

            dom.PointsLigue += bareme.PointsEquipe(
                feuille.TouchdownsDomicile, feuille.TouchdownsExterieur, feuille.NombreDeTours,
                BaremePoints.ActionsDe(records, coteDomicile: true));
            ext.PointsLigue += bareme.PointsEquipe(
                feuille.TouchdownsExterieur, feuille.TouchdownsDomicile, feuille.NombreDeTours,
                BaremePoints.ActionsDe(records, coteDomicile: false));

            if (feuille.TouchdownsDomicile > feuille.TouchdownsExterieur)
            { dom.NombreVictoires++; ext.NombreDefaites++; }
            else if (feuille.TouchdownsDomicile < feuille.TouchdownsExterieur)
            { ext.NombreVictoires++; dom.NombreDefaites++; }
            else
            { dom.NombreNuls++; ext.NombreNuls++; }

            rejoues++;
        }

        await db.SaveChangesAsync();
        logger.LogInformation(
            "Classement recalculé pour la ligue id={LigueId} : {Nb} match(s) rejoué(s)", ligueId, rejoues);

        return rejoues;
    }

    /// <summary>
    /// Nombre de matchs joués de la ligue dont le nombre de tours n'est pas
    /// renseigné, alors que la ligue utilise des paliers. Ces matchs sont comptés
    /// avec les points de BASE ; l'écran de ligue le signale pour qu'un
    /// commissaire aille compléter les feuilles concernées.
    /// Renvoie 0 si la ligue n'a aucun palier (l'information ne sert alors à rien).
    /// </summary>
    public async Task<int> CompterMatchsSansNombreDeToursAsync(int ligueId)
    {
        var aDesPaliers = await db.PaliersPointsLigue.AnyAsync(p => p.LeagueId == ligueId);
        if (!aDesPaliers) return 0;

        return await db.Matches.CountAsync(m =>
            m.Division!.LeagueId == ligueId
            && m.Feuille != null
            && m.Feuille.NombreDeTours == null);
    }

    public record CoachClassement(ApplicationUser Coach, int PointsLigue, int Victoires, int Nuls, int Defaites);

    public async Task<List<CoachClassement>> GetTopCoachsAsync(int ligueId)
    {
        var equipes = await db.Teams
            .Include(t => t.Coach)
            .Where(t => t.LeagueId == ligueId)
            .ToListAsync();

        return [.. equipes
            .GroupBy(t => t.Coach)
            .Select(g => new CoachClassement(
                g.Key,
                g.Sum(t => t.PointsLigue),
                g.Sum(t => t.NombreVictoires),
                g.Sum(t => t.NombreNuls),
                g.Sum(t => t.NombreDefaites)))
            .OrderByDescending(c => c.PointsLigue)
            .ThenByDescending(c => c.Victoires)];
    }

    public async Task AttribuerAwardAsync(
        int ligueId, AwardType type,
        int? teamPlayerId = null, int? teamId = null, string? coachId = null)
    {
        var ligue = await db.Leagues.FindAsync(ligueId)
            ?? throw new InvalidOperationException("Ligue introuvable");

        var award = new LeagueAward
        {
            LeagueId = ligueId,
            Type = type,
            TeamPlayerId = teamPlayerId,
            TeamId = teamId,
            CoachId = coachId
        };
        db.LeagueAwards.Add(award);
        await db.SaveChangesAsync();
        logger.LogInformation("Award {AwardType} attribué dans la ligue id={LigueId}", type, ligueId);
    }

    public async Task<List<LeagueAward>> GetAwardsAsync(int ligueId) =>
        await db.LeagueAwards
            .Include(a => a.TeamPlayer).ThenInclude(j => j!.Team)
            .Include(a => a.Team)
            .Include(a => a.Coach)
            .Where(a => a.LeagueId == ligueId)
            .OrderBy(a => a.Type)
            .ToListAsync();

    /// <summary>
    /// Supprime une ligue et TOUTES ses données rattachées.
    ///
    /// ⚠️ L'ordre compte, et la liste doit être exhaustive. Deux raisons :
    ///  - certaines FK sont en <c>Restrict</c> (PhaseDeReposValidation → Team) :
    ///    supprimer les équipes avant leurs validations lève
    ///    « FOREIGN KEY constraint failed » et la ligue reste en place ;
    ///  - <c>ExecuteDeleteAsync</c> émet du SQL direct et ne déclenche PAS les
    ///    cascades gérées par EF. Quand le pragma foreign_keys de SQLite est
    ///    inactif, les enfants non listés ici ne sont ni supprimés ni signalés :
    ///    ils restent **orphelins et silencieux** en base (constaté en
    ///    production — 12 LeagueStaffTypes rattachés à des ligues disparues).
    ///
    /// Toute nouvelle entité portant un <c>LeagueId</c> (ou rattachée à Team /
    /// LeagueStaffType) doit être ajoutée ici, sous peine de reproduire le bug.
    /// Vérification : <c>grep -rn "public int LeagueId" Data/Models/*.cs</c>.
    /// </summary>
    public async Task SupprimerLigueAsync(int ligueId)
    {
        var divIds    = await db.Divisions.Where(d => d.LeagueId == ligueId).Select(d => d.Id).ToListAsync();
        var matchIds  = await db.Matches.Where(m => m.DivisionId.HasValue && divIds.Contains(m.DivisionId.Value)).Select(m => m.Id).ToListAsync();
        var sheetIds  = await db.MatchSheets.Where(s => matchIds.Contains(s.MatchId)).Select(s => s.Id).ToListAsync();
        var teamIds   = await db.Teams.Where(t => t.LeagueId == ligueId).Select(t => t.Id).ToListAsync();
        var playerIds = await db.TeamPlayers.Where(j => teamIds.Contains(j.TeamId)).Select(j => j.Id).ToListAsync();
        var staffIds  = await db.LeagueStaffTypes.Where(s => s.LeagueId == ligueId).Select(s => s.Id).ToListAsync();

        await using var tx = await db.Database.BeginTransactionAsync();

        // 1. Feuilles de match et leurs enregistrements de joueurs.
        await db.MatchPlayerRecords.Where(r => sheetIds.Contains(r.MatchSheetId)).ExecuteDeleteAsync();
        await db.MatchSheets.Where(s => matchIds.Contains(s.MatchId)).ExecuteDeleteAsync();

        // 2. Ce qui pend aux JOUEURS.
        await db.PlayerInjuries.Where(b => playerIds.Contains(b.TeamPlayerId)).ExecuteDeleteAsync();
        await db.TeamPlayerSkills.Where(s => playerIds.Contains(s.TeamPlayerId)).ExecuteDeleteAsync();

        // 3. Récompenses : elles pointent vers joueur, équipe OU coach.
        await db.LeagueAwards.Where(a => a.LeagueId == ligueId).ExecuteDeleteAsync();

        await db.Matches.Where(m => matchIds.Contains(m.Id)).ExecuteDeleteAsync();
        await db.TeamPlayers.Where(j => playerIds.Contains(j.Id)).ExecuteDeleteAsync();

        // 4. Ce qui pend aux ÉQUIPES — AVANT les équipes elles-mêmes.
        //    PhaseDeReposValidation → Team est en Restrict : l'oublier fait
        //    échouer toute la suppression.
        await db.PhaseDeReposValidations.Where(p => p.LeagueId == ligueId).ExecuteDeleteAsync();
        await db.TeamStaffs.Where(ts => teamIds.Contains(ts.TeamId)
                                     || staffIds.Contains(ts.LeagueStaffTypeId)).ExecuteDeleteAsync();

        await db.Teams.Where(t => teamIds.Contains(t.Id)).ExecuteDeleteAsync();
        await db.Divisions.Where(d => divIds.Contains(d.Id)).ExecuteDeleteAsync();

        // 5. Ce qui pend à la LIGUE.
        await db.LeagueStaffTypes.Where(s => s.LeagueId == ligueId).ExecuteDeleteAsync();
        await db.EcheancesRondes.Where(e => e.LeagueId == ligueId).ExecuteDeleteAsync();
        await db.PaliersPointsLigue.Where(p => p.LeagueId == ligueId).ExecuteDeleteAsync();
        await db.LeagueCommissioners.Where(c => c.LeagueId == ligueId).ExecuteDeleteAsync();

        await db.Leagues.Where(l => l.Id == ligueId).ExecuteDeleteAsync();
        await tx.CommitAsync();

        logger.LogInformation("Ligue id={Id} supprimée avec toutes ses données", ligueId);
    }

    public async Task<List<LeagueCommissioner>> GetCommissairesDeLigueAsync(int ligueId)
        => await db.LeagueCommissioners
            .Include(lc => lc.User)
            .Where(lc => lc.LeagueId == ligueId)
            .OrderBy(lc => lc.AssigneLe)
            .ToListAsync();

    /// <summary>
    /// Coaches de la ligue pouvant être promus commissaires de cette ligue.
    /// Part de <c>Teams.LeagueId</c> et NON de <c>Divisions.Equipes</c> : une équipe
    /// sans division (ligue en Inscription, ou format Libre avant composition du
    /// calendrier) est le cas NORMAL, et passer par les divisions rendait alors la
    /// liste systématiquement vide — aucun coach n'était promouvable.
    /// Sont exclus : les déjà-commissaires de ligue, le commissaire créateur de la
    /// ligue (il la gère déjà) et les comptes supprimés/anonymisés.
    /// </summary>
    public async Task<List<ApplicationUser>> GetCoachesPromouvablesAsync(int ligueId)
    {
        var ligue = await db.Leagues.AsNoTracking().FirstOrDefaultAsync(l => l.Id == ligueId);
        if (ligue is null) return [];

        var dejaPromus = await db.LeagueCommissioners
            .Where(lc => lc.LeagueId == ligueId)
            .Select(lc => lc.UserId)
            .ToListAsync();

        return await db.Teams
            .Where(t => t.LeagueId == ligueId)
            .Select(t => t.Coach)
            .Where(c => !c.EstSupprime
                        && c.Id != ligue.CommissaireId
                        && !dejaPromus.Contains(c.Id))
            .Distinct()
            .OrderBy(c => c.PseudoCoach)
            .ToListAsync();
    }

    public async Task PromouvoirCommissaireDeLigueAsync(int ligueId, string userId, string assignePar)
    {
        var existe = await db.LeagueCommissioners.AnyAsync(lc => lc.LeagueId == ligueId && lc.UserId == userId);
        if (existe) return;
        db.LeagueCommissioners.Add(new LeagueCommissioner
        {
            LeagueId = ligueId,
            UserId = userId,
            AssignePar = assignePar
        });
        await db.SaveChangesAsync();
        logger.LogInformation("Coach id={UserId} promu commissaire de la ligue {LigueId} par {AssignePar}", userId, ligueId, assignePar);
    }

    public async Task RetirerCommissaireDeLigueAsync(int ligueId, string userId)
    {
        var entry = await db.LeagueCommissioners
            .FirstOrDefaultAsync(lc => lc.LeagueId == ligueId && lc.UserId == userId);
        if (entry is null) return;
        db.LeagueCommissioners.Remove(entry);
        await db.SaveChangesAsync();
        logger.LogInformation("Coach id={UserId} retiré comme commissaire de la ligue {LigueId}", userId, ligueId);
    }
}
