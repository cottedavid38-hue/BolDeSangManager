using BolDeSangManager.Data;
using BolDeSangManager.Data.Enums;
using BolDeSangManager.Data.Models;
using BolDeSangManager.Data.Seeding;
using BolDeSangManager.Helpers;
using Microsoft.EntityFrameworkCore;

namespace BolDeSangManager.Services;

public class TeamService(ApplicationDbContext db, ILogger<TeamService> logger)
{
    public async Task<List<TeamType>> GetTypesEquipesAsync(int gameId) =>
        await db.TeamTypes
            .Include(t => t.Postes)
            .Where(t => t.GameId == gameId)
            .OrderBy(t => t.Nom)
            .ToListAsync();

    public async Task<List<TeamType>> GetTypesEquipesParVersionAsync(int versionId) =>
        await db.TeamTypes
            .Include(t => t.Postes)
            // Règles spéciales : affichées dès le choix de la race, c'est un
            // critère de décision pour le coach.
            .Include(t => t.ReglesSpecialesListe).ThenInclude(l => l.SpecialRule)
            .Where(t => t.RulesVersionId == versionId)
            .OrderBy(t => t.Nom)
            .ToListAsync();

    public async Task<TeamType?> GetTeamTypeAvecPostesAsync(int teamTypeId) =>
        await db.TeamTypes
            .Include(t => t.Postes)
                .ThenInclude(p => p.CompetencesDepart)
                .ThenInclude(pps => pps.Skill)
            .Include(t => t.Postes)
                .ThenInclude(p => p.AccesCategories)
                .ThenInclude(a => a.SkillCategoryDef)
            .Include(t => t.LimitesMotsCles)
            .Include(t => t.ReglesSpecialesListe).ThenInclude(l => l.SpecialRule)
            .FirstOrDefaultAsync(t => t.Id == teamTypeId);

    // ── « Favori de… » : divinité de l'équipe (LRB p.93) ─────────────────────

    /// <summary>
    /// Divinités que la RACE autorise, d'après la règle « Favori de… »
    /// rattachée à sa fiche. Liste vide = la race n'est pas concernée.
    ///
    /// Le cadre est défini une fois en admin (options cochées sur la fiche
    /// d'équipe) ; le commissaire choisit ensuite pour chaque équipe dans cette
    /// liste. Un cas particulier se règle en élargissant les options de la race,
    /// pas en rattachant une règle à une équipe isolée.
    /// </summary>
    public async Task<List<string>> GetOptionsDiviniteAsync(int teamTypeId)
    {
        var lien = await db.TeamTypeSpecialRules
            .Include(l => l.SpecialRule)
            .FirstOrDefaultAsync(l => l.TeamTypeId == teamTypeId
                                   && l.SpecialRule.Code == SpecialRuleCodes.FavoriDe);

        if (lien is null) return [];

        return lien.OptionsChoix
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    /// <summary>
    /// Enregistre la divinité d'une équipe. Action de COMMISSAIRE.
    /// </summary>
    /// <param name="divinite">
    /// Doit appartenir aux options de la race. Chaîne vide = effacer le choix.
    /// Validé ICI : une valeur postée depuis un écran n'est jamais digne de foi.
    /// </param>
    public async Task DefinirDiviniteAsync(int teamId, string divinite)
    {
        var equipe = await db.Teams.FirstOrDefaultAsync(t => t.Id == teamId)
            ?? throw new InvalidOperationException("Équipe introuvable.");

        if (string.IsNullOrWhiteSpace(divinite))
        {
            equipe.DiviniteChoisie = string.Empty;
            await db.SaveChangesAsync();
            return;
        }

        var options = await GetOptionsDiviniteAsync(equipe.TeamTypeId);
        if (options.Count == 0)
            throw new InvalidOperationException(
                "Cette équipe n'a pas la règle spéciale « Favori de… » : aucune divinité ne peut lui être attribuée.");

        var choisie = options.FirstOrDefault(o => o.Equals(divinite.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"« {divinite} » ne fait pas partie des divinités autorisées pour cette race ({string.Join(", ", options)}).");

        // On enregistre la forme canonique de la liste, pas la saisie brute.
        equipe.DiviniteChoisie = choisie;
        await db.SaveChangesAsync();
        logger.LogInformation("Divinité définie pour l'équipe id={TeamId} : {Divinite}", teamId, choisie);
    }

    public async Task<Team?> GetEquipeAsync(int teamId) =>
        await db.Teams
            .Include(t => t.Coach)
            .Include(t => t.TeamType).ThenInclude(tt => tt.Game)
            // Règles spéciales de la race : affichées sur la feuille d'équipe
            // et sur le PDF. Sans ce Include, elles disparaîtraient en silence.
            .Include(t => t.TeamType).ThenInclude(tt => tt.ReglesSpecialesListe).ThenInclude(l => l.SpecialRule)
            // RulesVersion : porte le barème des améliorations, dont dépend la
            // valeur CALCULÉE de chaque joueur (ValeurJoueurCalculator). Sans ce
            // Include, le barème retombe sur le LRB par défaut en silence.
            .Include(t => t.League).ThenInclude(l => l.RulesVersion).ThenInclude(v => v.PaliersAmelioration)
            .Include(t => t.Joueurs)
                .ThenInclude(j => j.PlayerPosition)
                    .ThenInclude(pp => pp.CompetencesDepart)
                    .ThenInclude(pps => pps.Skill)
                    .ThenInclude(s => s!.SkillCategoryDef)
            .Include(t => t.Joueurs)
                .ThenInclude(j => j.PlayerPosition)
                    .ThenInclude(pp => pp.AccesCategories)
                    .ThenInclude(a => a.SkillCategoryDef)
            .Include(t => t.Joueurs)
                .ThenInclude(j => j.Competences.Where(c => !c.EstCompetenceDepart))
                .ThenInclude(c => c.Skill)
                .ThenInclude(s => s!.SkillCategoryDef)
            .Include(t => t.Joueurs)
                .ThenInclude(j => j.Blessures)
            // Améliorations + leur compétence (drapeau Élite) : la valeur du
            // joueur est RECALCULÉE depuis elles. Un Include manquant donnerait
            // une valeur fausse sans la moindre erreur.
            .Include(t => t.Joueurs)
                .ThenInclude(j => j.Improvements)
                .ThenInclude(i => i.Skill)
            // Staff : indispensable au calcul de VEA, qui vaudrait sinon zéro.
            .Include(t => t.Staff).ThenInclude(s => s.LeagueStaffType)
            .FirstOrDefaultAsync(t => t.Id == teamId);

    public async Task<List<Team>> GetEquipesCoachAsync(string coachId) =>
        await db.Teams
            .Include(t => t.TeamType).ThenInclude(tt => tt.Game)
            .Include(t => t.League).ThenInclude(l => l.RulesVersion).ThenInclude(v => v.PaliersAmelioration)
            .Include(t => t.Joueurs.Where(j => !j.EstMort && !j.EstRetraite))
                .ThenInclude(j => j.PlayerPosition)
            .Include(t => t.Joueurs.Where(j => !j.EstMort && !j.EstRetraite))
                .ThenInclude(j => j.Improvements).ThenInclude(i => i.Skill)
            .Include(t => t.Staff).ThenInclude(s => s.LeagueStaffType)
            .Where(t => t.CoachId == coachId)
            .OrderByDescending(t => t.CreeLe)
            .ToListAsync();

    public async Task<List<Team>> GetEquipesLigueAsync(int ligueId) =>
        await db.Teams
            .Include(t => t.Coach)
            .Include(t => t.TeamType)
            .Include(t => t.Division)
            .Include(t => t.League).ThenInclude(l => l.RulesVersion).ThenInclude(v => v.PaliersAmelioration)
            .Include(t => t.Joueurs.Where(j => !j.EstMort && !j.EstRetraite))
                .ThenInclude(j => j.PlayerPosition)
            .Include(t => t.Joueurs.Where(j => !j.EstMort && !j.EstRetraite))
                .ThenInclude(j => j.Improvements).ThenInclude(i => i.Skill)
            .Include(t => t.Staff).ThenInclude(s => s.LeagueStaffType)
            .Where(t => t.LeagueId == ligueId)
            .OrderByDescending(t => t.PointsLigue)
            .ToListAsync();

    /// <param name="staff">
    /// Quantités de staff par identifiant de LeagueStaffType. Les bornes de
    /// création (min/max) sont vérifiées ici, pas seulement dans l'UI.
    /// </param>
    public async Task<Team> CreerEquipeAsync(
        Team equipe,
        List<(int positionId, string nom, int numero)> joueurs,
        IReadOnlyDictionary<int, int>? staff = null)
    {
        var ligue = await db.Leagues.FirstOrDefaultAsync(l => l.Id == equipe.LeagueId)
            ?? throw new InvalidOperationException("Ligue introuvable");
        if (!DisplayHelpers.InscriptionOuverte(ligue.Statut, ligue.Format))
            throw new InvalidOperationException("Création d'équipe possible uniquement en phase Inscription.");

        var teamType = await GetTeamTypeAvecPostesAsync(equipe.TeamTypeId)
            ?? throw new InvalidOperationException("Type d'équipe introuvable");

        ValiderRoster(teamType, joueurs);

        // « Favori de… » : quand la race n'autorise qu'une seule divinité, elle
        // est IMPOSÉE — on l'assigne sans rien demander (Pestiférés → Nurgle).
        // Plusieurs options = c'est au commissaire de trancher, le champ reste
        // vide jusque-là. Une valeur postée par l'écran n'est jamais reprise
        // telle quelle.
        var optionsDivinite = teamType.ReglesSpecialesListe
            .FirstOrDefault(l => l.SpecialRule?.Code == SpecialRuleCodes.FavoriDe)
            ?.OptionsChoix
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?? [];

        equipe.DiviniteChoisie = optionsDivinite.Length == 1 ? optionsDivinite[0] : string.Empty;

        equipe.CreeLe = DateTime.UtcNow;
        db.Teams.Add(equipe);
        await db.SaveChangesAsync();

        // Staff : validé contre les bornes de la ligue (min/max à la création).
        // Le coût retourné exclut les minimums, compris de base dans l'équipe.
        var coutStaff = await AppliquerStaffAsync(equipe, staff ?? new Dictionary<int, int>(), aLaCreation: true);

        // Trésorerie recalculée par le SERVEUR : l'écran la propose, mais la
        // valeur postée ne fait pas foi (elle est modifiable côté client).
        var coutJoueurs = joueurs.Sum(j => teamType.Postes.First(p => p.Id == j.positionId).Cout);
        equipe.Tresorerie = ligue.BudgetDepart - coutJoueurs - coutStaff;
        if (equipe.Tresorerie < 0)
            throw new InvalidOperationException("Budget dépassé : le coût de l'équipe excède le budget de départ.");

        foreach (var (positionId, nom, numero) in joueurs)
        {
            var position = teamType.Postes.First(p => p.Id == positionId);
            var joueur = new TeamPlayer
            {
                TeamId = equipe.Id,
                PlayerPositionId = positionId,
                Nom = string.IsNullOrWhiteSpace(nom) ? $"#{numero}" : nom,
                Numero = numero,
                RecruteLe = DateTime.UtcNow
            };
            db.TeamPlayers.Add(joueur);
            await db.SaveChangesAsync();

            AjouterCompetencesDepart(joueur.Id, position.CompetencesDepart);
        }

        await db.SaveChangesAsync();
        logger.LogInformation("Équipe créée : {NomEquipe} (id={Id}), {NbJoueurs} joueurs initiaux", equipe.Nom, equipe.Id, joueurs.Count);
        return equipe;
    }

    /// <summary>
    /// Le roster d'une équipe peut-il encore être refondu (modification complète
    /// ou suppression) ?
    ///
    /// En phase Inscription : oui, rien n'a commencé.
    /// En format Open la ligue reste ouverte indéfiniment, mais autoriser la
    /// refonte à vie effacerait des joueurs déjà présents dans des feuilles de
    /// match validées. On la limite donc aux équipes qui n'ont pas encore joué :
    /// une équipe fraîchement inscrite peut corriger son roster, une équipe
    /// engagée est figée comme dans les autres formats.
    /// </summary>
    private async Task<bool> RosterEncoreEditableAsync(Team equipe)
    {
        if (equipe.League!.Statut == LeagueStatus.Inscription) return true;
        if (!DisplayHelpers.SansCalendrier(equipe.League.Format)) return false;
        if (equipe.League.Statut == LeagueStatus.Termine) return false;

        var aJoue = await db.Matches.AnyAsync(m =>
            (m.EquipeDomicileId == equipe.Id || m.EquipeExterieurId == equipe.Id)
            && m.Statut != MatchStatus.Programme);
        return !aJoue;
    }

    /// <summary>
    /// Écrit les quantités de staff d'une équipe, en validant les bornes de la
    /// ligue. Un type absent du dictionnaire est remis à zéro : l'écran envoie
    /// toujours l'état complet.
    /// </summary>
    /// <returns>
    /// Coût FACTURÉ du staff (minimums inclus déduits) quand
    /// <paramref name="aLaCreation"/> est vrai, sinon 0. Voir
    /// <see cref="StaffService.UnitesFacturees"/>.
    /// </returns>
    private async Task<int> AppliquerStaffAsync(
        Team equipe, IReadOnlyDictionary<int, int> staff, bool aLaCreation)
    {
        var typesLigue = await db.LeagueStaffTypes
            .Where(l => l.LeagueId == equipe.LeagueId)
            .ToListAsync();

        var teamType = await db.TeamTypes.FirstOrDefaultAsync(t => t.Id == equipe.TeamTypeId);
        var coutFacture = 0;

        var lignes = await db.TeamStaffs
            .Where(t => t.TeamId == equipe.Id)
            .ToListAsync();

        foreach (var type in typesLigue)
        {
            var voulu = staff.TryGetValue(type.Id, out var q) ? q : 0;

            if (voulu < 0)
                throw new InvalidOperationException("Une quantité de staff ne peut pas être négative.");
            if (!type.EstActif && voulu > 0)
                throw new InvalidOperationException($"« {type.Nom} » n'est pas disponible dans cette ligue.");

            if (aLaCreation)
            {
                if (voulu < type.MinCreation)
                    throw new InvalidOperationException(
                        $"« {type.Nom} » : minimum {type.MinCreation} à la création de l'équipe.");
                if (voulu > type.MaxCreation)
                    throw new InvalidOperationException(
                        $"« {type.Nom} » : maximum {type.MaxCreation} à la création de l'équipe.");
            }

            if (type.MaxLigue is int plafond && voulu > plafond)
                throw new InvalidOperationException(
                    $"« {type.Nom} » : plafond de {plafond} atteint pour cette ligue.");

            // Le minimum imposé par les règles est COMPRIS DE BASE dans l'équipe :
            // il n'est pas décompté du budget de départ. Il compte en revanche
            // toujours dans la VEA, qui somme la quantité totale.
            if (aLaCreation)
                coutFacture += StaffService.CoutFactureCreation(type, teamType, voulu);

            var ligne = lignes.FirstOrDefault(l => l.LeagueStaffTypeId == type.Id);
            if (ligne is null)
            {
                if (voulu > 0)
                    db.TeamStaffs.Add(new TeamStaff
                    {
                        TeamId = equipe.Id, LeagueStaffTypeId = type.Id, Quantite = voulu
                    });
            }
            else
            {
                ligne.Quantite = voulu;
            }

            // Colonnes historiques tenues en miroir : les exports et écrans qui
            // les lisent encore restent cohérents.
            switch (type.Nom)
            {
                case StaffService.NomFans:         equipe.FansDevoues = voulu; break;
                case StaffService.NomRelances:     equipe.NombreRelances = voulu; break;
                case StaffService.NomCoachs:       equipe.NombreCoachsAssistants = voulu; break;
                case StaffService.NomCheerleaders: equipe.NombreCheerleaders = voulu; break;
                case StaffService.NomApothicaire:  equipe.Apothicaire = voulu > 0; break;
            }
        }

        await db.SaveChangesAsync();
        return coutFacture;
    }

    /// <param name="staff">
    /// Quantités par identifiant de LeagueStaffType. Remplace les anciens
    /// paramètres relances/fans/coachs/cheerleaders/apothicaire : le staff est
    /// désormais une liste ouverte définie dans les règles.
    /// </param>
    public async Task<Team> ModifierEquipeAsync(
        int teamId,
        string coachId,
        string nouveauNom,
        int tresorerie,
        IReadOnlyDictionary<int, int> staff,
        List<(int positionId, string nom, int numero)> joueurs)
    {
        var equipe = await db.Teams
            .Include(t => t.League)
            .Include(t => t.Joueurs).ThenInclude(j => j.Competences)
            .FirstOrDefaultAsync(t => t.Id == teamId)
            ?? throw new InvalidOperationException("Équipe introuvable");

        if (equipe.CoachId != coachId)
            throw new InvalidOperationException("Vous n'êtes pas le coach de cette équipe.");
        if (equipe.League is null || !await RosterEncoreEditableAsync(equipe))
            throw new InvalidOperationException("Modification possible uniquement en phase Inscription.");

        var teamType = await GetTeamTypeAvecPostesAsync(equipe.TeamTypeId)
            ?? throw new InvalidOperationException("Type d'équipe introuvable");

        ValiderRoster(teamType, joueurs);

        // Supprimer l'ancien roster (compétences puis joueurs)
        var anciennesCompetences = equipe.Joueurs.SelectMany(j => j.Competences).ToList();
        if (anciennesCompetences.Count > 0)
            db.TeamPlayerSkills.RemoveRange(anciennesCompetences);
        if (equipe.Joueurs.Count > 0)
            db.TeamPlayers.RemoveRange(equipe.Joueurs);

        equipe.Nom = nouveauNom;
        await db.SaveChangesAsync();

        var coutStaff = await AppliquerStaffAsync(equipe, staff, aLaCreation: true);

        // Même recalcul serveur qu'à la création (le paramètre tresorerie posté
        // par l'écran n'est qu'indicatif) : minimums de staff non facturés.
        var coutJoueurs = joueurs.Sum(j => teamType.Postes.First(p => p.Id == j.positionId).Cout);
        equipe.Tresorerie = equipe.League!.BudgetDepart - coutJoueurs - coutStaff;
        if (equipe.Tresorerie < 0)
            throw new InvalidOperationException("Budget dépassé : le coût de l'équipe excède le budget de départ.");


        // Recréer le roster
        foreach (var (positionId, nom, numero) in joueurs)
        {
            var position = teamType.Postes.First(p => p.Id == positionId);
            var joueur = new TeamPlayer
            {
                TeamId = equipe.Id,
                PlayerPositionId = positionId,
                Nom = string.IsNullOrWhiteSpace(nom) ? $"#{numero}" : nom,
                Numero = numero,
                RecruteLe = DateTime.UtcNow
            };
            db.TeamPlayers.Add(joueur);
            await db.SaveChangesAsync();

            AjouterCompetencesDepart(joueur.Id, position.CompetencesDepart);
        }

        await db.SaveChangesAsync();
        logger.LogInformation("Équipe modifiée : {NomEquipe} (id={Id}), {NbJoueurs} joueurs", equipe.Nom, equipe.Id, joueurs.Count);
        return equipe;
    }

    public async Task SupprimerEquipeAsync(int teamId, string coachId)
    {
        var equipe = await db.Teams
            .Include(t => t.League)
            .Include(t => t.Joueurs).ThenInclude(j => j.Competences)
            .FirstOrDefaultAsync(t => t.Id == teamId)
            ?? throw new InvalidOperationException("Équipe introuvable");

        if (equipe.CoachId != coachId)
            throw new InvalidOperationException("Vous n'êtes pas le coach de cette équipe.");
        if (equipe.League is null || !await RosterEncoreEditableAsync(equipe))
            throw new InvalidOperationException("Suppression possible uniquement en phase Inscription.");

        var competences = equipe.Joueurs.SelectMany(j => j.Competences).ToList();
        if (competences.Count > 0)
            db.TeamPlayerSkills.RemoveRange(competences);
        if (equipe.Joueurs.Count > 0)
            db.TeamPlayers.RemoveRange(equipe.Joueurs);
        db.Teams.Remove(equipe);
        await db.SaveChangesAsync();

        logger.LogInformation("Équipe supprimée : {NomEquipe} (id={Id})", equipe.Nom, equipe.Id);
    }

    private static void ValiderRoster(TeamType teamType, List<(int positionId, string nom, int numero)> joueurs)
    {
        // Limites par poste (quantité max)
        foreach (var (posId, _, _) in joueurs)
        {
            var pos = teamType.Postes.FirstOrDefault(p => p.Id == posId)
                ?? throw new InvalidOperationException($"Poste {posId} introuvable");
            var countPoste = joueurs.Count(j => j.positionId == posId);
            if (countPoste > pos.QuantiteMax)
                throw new InvalidOperationException($"Limite dépassée pour {pos.Nom} : maximum {pos.QuantiteMax} par équipe.");
        }

        // Limites par mot-clé (ex : max 3 Gros Bras pour Renégats du Chaos)
        if (teamType.LimitesMotsCles.Count > 0)
        {
            var keywordsParPosition = teamType.Postes.ToDictionary(
                p => p.Id,
                p => p.MotsCles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                .ToHashSet(StringComparer.OrdinalIgnoreCase));

            foreach (var limite in teamType.LimitesMotsCles)
            {
                var count = joueurs.Count(j =>
                    keywordsParPosition.TryGetValue(j.positionId, out var kws) && kws.Contains(limite.MotCle));
                if (count > limite.Max)
                    throw new InvalidOperationException(
                        $"Limite « {limite.MotCle} » dépassée : maximum {limite.Max} joueurs avec ce mot-clé.");
            }
        }
    }

    /// <summary>
    /// Désigne le capitaine de l'équipe (règle « Capitaine »), ou retire le
    /// titre si <paramref name="joueurId"/> vaut <c>null</c>.
    ///
    /// Un seul capitaine par équipe : l'ancien perd le titre automatiquement.
    /// Le joueur doit appartenir à l'équipe — l'écran propose, le service fait
    /// autorité.
    /// </summary>
    public async Task DefinirCapitaineAsync(int teamId, int? joueurId)
    {
        var joueurs = await db.TeamPlayers
            .Where(p => p.TeamId == teamId)
            .ToListAsync();

        if (joueurId is not null && joueurs.All(p => p.Id != joueurId))
            throw new InvalidOperationException("Ce joueur n'appartient pas à cette équipe.");

        foreach (var p in joueurs)
            p.EstCapitaine = p.Id == joueurId;

        await db.SaveChangesAsync();
        logger.LogInformation(
            "Capitaine de l'équipe id={TeamId} : {Joueur}", teamId,
            joueurId is null ? "aucun" : $"joueur id={joueurId}");
    }

    /// <summary>
    /// Renomme un joueur, à tout moment — y compris saison lancée (#16).
    ///
    /// ⚠️ Commande DÉDIÉE, et volontairement étroite : le seul autre chemin qui
    /// écrit le nom d'un joueur est <see cref="ModifierEquipeAsync"/>, qui
    /// SUPPRIME puis recrée tout le roster. C'est précisément pour ça qu'il est
    /// verrouillé en phase Inscription : l'ouvrir en cours de saison effacerait
    /// XP, compétences acquises, blessures et améliorations. Élargir ce verrou
    /// serait la correction évidente et la pire — passer par ici à la place.
    ///
    /// Un nom n'entre dans aucun calcul (ni VEA, ni budget, ni classement) :
    /// aucune raison de regarder le statut de la ligue. Les joueurs morts ou
    /// retraités restent renommables — c'est de l'historique, pas du jeu.
    ///
    /// Le coach ne renomme que SES joueurs ; l'écran propose, le service fait
    /// autorité (décision produit : le commissaire n'est pas sur ce chemin).
    /// </summary>
    public async Task RenommerJoueurAsync(int joueurId, string coachId, string nouveauNom)
    {
        var joueur = await db.TeamPlayers
            .Include(p => p.Team)
            .FirstOrDefaultAsync(p => p.Id == joueurId)
            ?? throw new InvalidOperationException("Joueur introuvable");

        if (joueur.Team.CoachId != coachId)
            throw new InvalidOperationException("Vous n'êtes pas le coach de cette équipe.");

        // Mêmes règles qu'à la création : un nom vide retombe sur le numéro de
        // maillot plutôt que de laisser une ligne anonyme, et la longueur est
        // bornée — une valeur postée par le navigateur n'est jamais de confiance.
        var propre = (nouveauNom ?? string.Empty).Trim();
        if (propre.Length > 100)
            throw new InvalidOperationException("Le nom d'un joueur ne peut pas dépasser 100 caractères.");

        joueur.Nom = string.IsNullOrWhiteSpace(propre) ? $"#{joueur.Numero}" : propre;
        await db.SaveChangesAsync();

        logger.LogInformation("Joueur id={JoueurId} renommé en « {Nom} » (équipe id={TeamId})",
            joueur.Id, joueur.Nom, joueur.TeamId);
    }

    /// <summary>
    /// Retire le titre à un capitaine mort ou retraité. Appelé après
    /// l'après-match : sans ça, la compétence resterait affichée sur un joueur
    /// absent de l'effectif.
    /// </summary>
    public async Task NettoyerCapitaineAsync(int teamId)
    {
        var perimes = await db.TeamPlayers
            .Where(p => p.TeamId == teamId && p.EstCapitaine && (p.EstMort || p.EstRetraite))
            .ToListAsync();

        if (perimes.Count == 0) return;

        foreach (var p in perimes)
            p.EstCapitaine = false;

        await db.SaveChangesAsync();
        logger.LogInformation(
            "Capitaine retiré (joueur mort ou retraité) pour l'équipe id={TeamId}", teamId);
    }

    public async Task<TeamPlayer> RecruterJoueurAsync(int teamId, int positionId, string nom, int numero) =>
        await RecruterAsync(teamId, positionId, nom, numero, gratuit: false);

    /// <summary>
    /// Recrutement gratuit au titre d'une règle spéciale (« Maîtres de la
    /// Non-Vie », LRB p.94) : le joueur est embauché sans débiter la trésorerie.
    ///
    /// Le poste doit porter le mot-clé visé par la règle sur la fiche de race.
    /// Vérifié ICI, côté serveur : l'écran propose, il ne fait pas foi.
    ///
    /// Le droit est PLAFONNÉ par phase d'après-match (1 dans le LRB, réglable
    /// par race). Sans ce plafond, un coach remplissait tout son effectif
    /// gratuitement d'un coup. Le compte se fait sur les joueurs déjà marqués
    /// comme offerts pour CE match : supprimer une recrue rend donc le droit,
    /// sans compteur à corriger.
    /// </summary>
    public async Task<TeamPlayer> RecruterJoueurGratuitAsync(
        int teamId, int positionId, string nom, int numero, int? matchId = null)
    {
        var eligibles = await GetPostesRecrutementGratuitAsync(teamId);
        if (eligibles.All(p => p.Id != positionId))
            throw new InvalidOperationException(
                "Ce poste n'est pas éligible au recrutement gratuit de cette équipe.");

        if (matchId is not null)
        {
            var limite = await LimiteRecruesGratuitesAsync(teamId);
            if (limite > 0)
            {
                var dejaOffertes = await db.TeamPlayers
                    .CountAsync(p => p.TeamId == teamId && p.RecrueGratuiteMatchId == matchId);

                if (dejaOffertes >= limite)
                    throw new InvalidOperationException(
                        limite == 1
                            ? "Cette équipe a déjà utilisé sa recrue offerte pour ce match."
                            : $"Cette équipe a déjà utilisé ses {limite} recrues offertes pour ce match.");
            }
        }

        return await RecruterAsync(teamId, positionId, nom, numero, gratuit: true, matchId: matchId);
    }

    /// <summary>
    /// Nombre de recrues offertes autorisées par phase d'après-match pour cette
    /// équipe. 0 = pas de plafond.
    /// </summary>
    public async Task<int> LimiteRecruesGratuitesAsync(int teamId)
    {
        var equipe = await db.Teams
            .Include(t => t.TeamType).ThenInclude(tt => tt.ReglesSpecialesListe).ThenInclude(l => l.SpecialRule)
            .FirstOrDefaultAsync(t => t.Id == teamId);

        var lien = equipe?.TeamType?.ReglesSpecialesListe
            .FirstOrDefault(l => l.SpecialRule?.Code == SpecialRuleCodes.RecrutementGratuitParMotCle);

        return lien?.LimiteParApresMatch ?? 0;
    }

    /// <summary>
    /// Recrues offertes déjà utilisées par cette équipe pour ce match : sert à
    /// l'écran pour masquer le bouton une fois le droit consommé.
    /// </summary>
    public async Task<int> RecruesGratuitesUtiliseesAsync(int teamId, int matchId) =>
        await db.TeamPlayers.CountAsync(p => p.TeamId == teamId && p.RecrueGratuiteMatchId == matchId);

    /// <summary>
    /// Postes que l'équipe peut embaucher gratuitement. Liste vide = la race
    /// ne porte pas la règle, ou aucun mot-clé n'est renseigné.
    ///
    /// Comme pour « Vil Prix », le mot-clé vient de la fiche de race
    /// (<c>OptionsChoix</c>) : viser un autre poste est un réglage admin, pas
    /// un développement. Un mot-clé vide ne propose RIEN — sinon il
    /// correspondrait à tous les postes.
    /// </summary>
    public async Task<List<PlayerPosition>> GetPostesRecrutementGratuitAsync(int teamId)
    {
        var equipe = await db.Teams
            .Include(t => t.TeamType).ThenInclude(tt => tt.ReglesSpecialesListe).ThenInclude(l => l.SpecialRule)
            .FirstOrDefaultAsync(t => t.Id == teamId);

        if (equipe?.TeamType is null) return [];

        var motsCles = equipe.TeamType.ReglesSpecialesListe
            .Where(l => l.SpecialRule?.Code == SpecialRuleCodes.RecrutementGratuitParMotCle)
            .SelectMany(l => SpecialRuleCodes.DecouperOptions(l.OptionsChoix))
            .ToList();

        if (motsCles.Count == 0) return [];

        // Le filtrage par mot-clé se fait en mémoire : les mots-clés sont un CSV
        // en base, et une comparaison SQL par sous-chaîne ferait correspondre
        // « Trois-quart » à « Trois-quartier ».
        var postes = await db.PlayerPositions
            .Where(p => p.TeamTypeId == equipe.TeamTypeId)
            .ToListAsync();

        return postes
            .Where(p => SpecialRuleCodes.DecouperOptions(p.MotsCles)
                .Any(m => motsCles.Contains(m, StringComparer.OrdinalIgnoreCase)))
            .OrderBy(p => p.Nom)
            .ToList();
    }

    /// <param name="gratuit">
    /// Quand vrai, ni contrôle de fonds ni débit — mais TOUTES les autres
    /// règles de roster (maximum par poste, limites de mots-clés) restent
    /// opposables : la gratuité ne dispense pas des plafonds.
    /// </param>
    private async Task<TeamPlayer> RecruterAsync(
        int teamId, int positionId, string nom, int numero, bool gratuit, int? matchId = null)
    {
        var equipe = await db.Teams.FindAsync(teamId)
            ?? throw new InvalidOperationException("Équipe introuvable");
        var position = await db.PlayerPositions
            .Include(p => p.CompetencesDepart).ThenInclude(pps => pps.Skill)
            .FirstOrDefaultAsync(p => p.Id == positionId)
            ?? throw new InvalidOperationException("Poste introuvable");

        if (!gratuit && equipe.Tresorerie < position.Cout)
            throw new InvalidOperationException("Fonds insuffisants.");

        var nbDejaPoste = await db.TeamPlayers
            .CountAsync(j => j.TeamId == teamId && j.PlayerPositionId == positionId
                          && !j.EstMort && !j.EstRetraite);
        if (nbDejaPoste >= position.QuantiteMax)
            throw new InvalidOperationException($"Limite atteinte : maximum {position.QuantiteMax} {position.Nom} par équipe.");

        // Limites par mot-clé
        var limites = await db.Set<TeamTypeKeywordLimit>()
            .Where(l => l.TeamTypeId == position.TeamTypeId)
            .ToListAsync();

        if (limites.Count > 0)
        {
            var posKeywords = position.MotsCles
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var limite in limites)
            {
                if (!posKeywords.Contains(limite.MotCle)) continue;

                var posIdsAvecMotCle = await db.PlayerPositions
                    .Where(p => p.TeamTypeId == position.TeamTypeId
                             && (p.MotsCles.Contains(limite.MotCle + ",") || p.MotsCles.EndsWith(limite.MotCle) || p.MotsCles == limite.MotCle))
                    .Select(p => p.Id)
                    .ToListAsync();

                var nbDejaMotCle = await db.TeamPlayers
                    .CountAsync(j => j.TeamId == teamId && posIdsAvecMotCle.Contains(j.PlayerPositionId)
                                  && !j.EstMort && !j.EstRetraite);

                if (nbDejaMotCle >= limite.Max)
                    throw new InvalidOperationException(
                        $"Limite « {limite.MotCle} » atteinte : maximum {limite.Max} joueurs avec ce mot-clé.");
            }
        }

        var joueur = new TeamPlayer
        {
            TeamId = teamId,
            PlayerPositionId = positionId,
            Nom = string.IsNullOrWhiteSpace(nom) ? $"#{numero}" : nom,
            Numero = numero,
            // Trace la recrue offerte : c'est elle qui plafonne le droit à une
            // par phase d'après-match, et la supprimer rend ce droit.
            RecrueGratuiteMatchId = gratuit ? matchId : null,
            RecruteLe = DateTime.UtcNow
        };
        db.TeamPlayers.Add(joueur);
        await db.SaveChangesAsync();

        AjouterCompetencesDepart(joueur.Id, position.CompetencesDepart);

        // Gratuit : aucun débit. Le joueur garde en revanche sa valeur (déjà
        // posée ci-dessus) — le LRB précise qu'« il ajoute quand même sa valeur
        // à la Valeur d'Équipe ».
        if (!gratuit)
            equipe.Tresorerie -= position.Cout;

        await db.SaveChangesAsync();
        logger.LogInformation(
            "Joueur recruté : {NomJoueur} (poste={Poste}, coût={Cout}{Gratuit}) pour l'équipe id={TeamId}",
            nom, position.Nom, position.Cout, gratuit ? ", GRATUIT" : "", teamId);
        return joueur;
    }

    // Valeur d'équipe actuelle (VEA)
    //
    // Le calcul lui-même vit dans Helpers/VeaCalculator : c'est la source
    // UNIQUE, partagée avec la feuille d'équipe PDF. Ne pas réimplémenter la
    // somme ailleurs — écran et PDF ont déjà divergé une fois.
    public int CalculerVEA(Team equipe) => VeaCalculator.Calculer(equipe);

    /// <param name="rulesVersionId">
    /// Version de règles (R7) : sans ce filtre la liste mélange les versions, et
    /// les catégories d'une autre version ne correspondent à aucun accès de poste.
    /// </param>
    public async Task<List<Skill>> GetCompetencesAsync(string? categorie = null, int? rulesVersionId = null)
    {
        // R7 : la catégorie est nécessaire pour filtrer selon les accès du poste
        var query = db.Skills.Include(s => s.SkillCategoryDef).AsQueryable();
        if (rulesVersionId is int vid)
            query = query.Where(s => s.RulesVersionId == vid);
        if (!string.IsNullOrEmpty(categorie) && Enum.TryParse<SkillCategory>(categorie, out var cat))
            query = query.Where(s => s.Categorie == cat);
        return await query.OrderBy(s => s.Nom).ToListAsync();
    }

    /// <summary>
    /// Applique une amélioration à un joueur en débitant sa cagnotte d'XP (R4).
    ///
    /// Depuis R4 les paliers LRB (6/16/31…) ne commandent plus les améliorations :
    /// le coach saisit lui-même l'XP qu'il consomme, et peut prendre autant
    /// d'améliorations que sa cagnotte le permet.
    /// </summary>
    /// <param name="pspDepensee">XP retirée de la cagnotte. Doit être &gt; 0 et ≤ XP disponible.</param>
    public async Task AppliquerAmeliorationAsync(
        int joueurId,
        ImprovementType type,
        int? skillId = null,
        AffectedStat? statAmelioree = null,
        int? matchSheetId = null,
        int pspDepensee = 0)
    {
        var joueur = await db.TeamPlayers
            .Include(j => j.Improvements)
            .FirstOrDefaultAsync(j => j.Id == joueurId)
            ?? throw new InvalidOperationException("Joueur introuvable");

        if (pspDepensee <= 0)
            throw new InvalidOperationException("L'XP dépensée doit être supérieure à zéro.");

        if (pspDepensee > joueur.PointsStarPlayer)
            throw new InvalidOperationException(
                $"XP insuffisante : {joueur.Nom} dispose de {joueur.PointsStarPlayer} XP, {pspDepensee} demandés.");

        // Validation du type vs paramètres fournis
        bool requiertSkill = type is ImprovementType.AleaPrimaire or ImprovementType.SelectionPrimaire
                               or ImprovementType.AleaSecondaire or ImprovementType.SelectionSecondaire;
        bool requiertStat = type is ImprovementType.AmeliorationCarac or ImprovementType.AmeliorationForceArmure;

        if (requiertSkill && skillId is null)
            throw new InvalidOperationException("Un skillId est requis pour ce type d'amélioration.");
        if (requiertStat && statAmelioree is null)
            throw new InvalidOperationException("Une stat ciblée est requise pour ce type d'amélioration.");

        var prochainPalier = joueur.Improvements.Count + 1;
        var hausse = ImprovementThresholds.HausseValeur(type, statAmelioree);

        // Débit de la cagnotte
        joueur.PointsStarPlayer -= pspDepensee;

        var improvement = new PlayerImprovement
        {
            TeamPlayerId = joueurId,
            Palier = prochainPalier,
            Type = type,
            SkillId = skillId,
            StatAmelioree = statAmelioree,
            ValeurHausse = hausse,
            PspDepensee = pspDepensee,
            MatchSheetId = matchSheetId
        };
        db.PlayerImprovements.Add(improvement);

        // Si skill : ajouter à la liste des compétences acquises (non de départ)
        if (skillId.HasValue)
        {
            db.TeamPlayerSkills.Add(new TeamPlayerSkill
            {
                TeamPlayerId = joueurId,
                SkillId = skillId.Value,
                EstCompetenceDepart = false,
                EnAttenteValidation = false
            });
        }

        // Si stat : appliquer le modificateur
        if (statAmelioree.HasValue)
        {
            switch (statAmelioree.Value)
            {
                case AffectedStat.Mouvement: joueur.ModMouvement++; break;
                case AffectedStat.Force: joueur.ModForce++; break;
                case AffectedStat.Agilite: joueur.ModAgilite++; break;
                case AffectedStat.CapacitePasse: joueur.ModCapacitePasse++; break;
                case AffectedStat.Armure: joueur.ModArmure++; break;
            }
        }

        // La valeur n'est plus stockée : l'ajout de l'amélioration ci-dessus
        // suffit, ValeurJoueurCalculator la recalcule depuis le barème courant.
        await db.SaveChangesAsync();

        logger.LogInformation(
            "Joueur id={JoueurId} : amélioration palier {Palier} (type={Type}, skill={SkillId}, stat={Stat}, hausse={Hausse}, xp dépensée={Xp})",
            joueurId, prochainPalier, type, skillId, statAmelioree, hausse, pspDepensee);
    }

    /// <summary>
    /// Corrige manuellement l'XP d'un joueur (R4) — réservé aux commissaires.
    /// La correction est journalisée dans <see cref="PspCorrection"/> pour rester
    /// auditable auprès des coaches.
    /// </summary>
    public async Task CorrigerXpAsync(int joueurId, int nouvelleValeur, string motif, string commissaireId)
    {
        var joueur = await db.TeamPlayers.FirstOrDefaultAsync(j => j.Id == joueurId)
            ?? throw new InvalidOperationException("Joueur introuvable");

        if (nouvelleValeur < 0)
            throw new InvalidOperationException("L'XP ne peut pas être négative.");

        if (string.IsNullOrWhiteSpace(motif))
            throw new InvalidOperationException("Un motif est requis pour corriger l'XP d'un joueur.");

        var ancienne = joueur.PointsStarPlayer;
        if (ancienne == nouvelleValeur) return;

        joueur.PointsStarPlayer = nouvelleValeur;
        db.PspCorrections.Add(new PspCorrection
        {
            TeamPlayerId   = joueurId,
            AncienneValeur = ancienne,
            NouvelleValeur = nouvelleValeur,
            Motif          = motif.Trim(),
            CorrigeParId   = commissaireId
        });
        await db.SaveChangesAsync();

        logger.LogInformation(
            "XP corrigée par commissaire {Commissaire} : joueur id={JoueurId} {Ancienne} → {Nouvelle} ({Motif})",
            commissaireId, joueurId, ancienne, nouvelleValeur, motif);
    }

    /// <summary>Historique des corrections d'XP d'un joueur, plus récente d'abord.</summary>
    public async Task<List<PspCorrection>> GetCorrectionsXpAsync(int joueurId) =>
        await db.PspCorrections
            .Include(c => c.CorrigePar)
            .Where(c => c.TeamPlayerId == joueurId)
            .OrderByDescending(c => c.CorrigeLe)
            .ToListAsync();

    public async Task<List<PlayerPosition>> GetPostesDisponiblesAsync(int teamTypeId) =>
        await db.PlayerPositions
            .Include(p => p.CompetencesDepart).ThenInclude(pps => pps.Skill)
            .Where(p => p.TeamTypeId == teamTypeId)
            .OrderBy(p => p.Cout)
            .ToListAsync();

    private void AjouterCompetencesDepart(int joueurId, IEnumerable<PlayerPositionSkill> competences)
    {
        foreach (var comp in competences)
            db.TeamPlayerSkills.Add(new TeamPlayerSkill
            {
                TeamPlayerId = joueurId,
                SkillId = comp.SkillId,
                EstCompetenceDepart = true
            });
    }
}
