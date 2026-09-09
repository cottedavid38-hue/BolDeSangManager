using BolDeSangManager.Data.Enums;

namespace BolDeSangManager.Data.Models;

public class League
{
    public int Id { get; set; }
    public string Nom { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string CommissaireId { get; set; } = string.Empty;
    public ApplicationUser Commissaire { get; set; } = null!;
    public int GameId { get; set; }
    public Game Game { get; set; } = null!;
    public int RulesVersionId { get; set; }
    public RulesVersion RulesVersion { get; set; } = null!;
    public LeagueFormat Format { get; set; } = LeagueFormat.RoundRobinAvecPlayoffs;
    public LeagueStatus Statut { get; set; } = LeagueStatus.Creation;
    public int BudgetDepart { get; set; } = 1_000_000;
    public int NombreEquipesPlayoff { get; set; } = 4;

    /// <summary>
    /// Mode brouillard (#2) : masque le calendrier à venir aux coaches.
    /// Un coach ne voit alors que son prochain match programmé et l'ensemble des
    /// matchs déjà joués (les siens comme ceux des autres). But : éviter qu'on
    /// joue un match en anticipant les rencontres suivantes.
    /// Les commissaires ne sont jamais concernés.
    /// </summary>
    public bool ModeBrouillard { get; set; } = false;

    /// <summary>
    /// Règlement de la ligue en markdown brut (R5). Rédigé par les commissaires,
    /// consultable par tous les participants et exportable en PDF.
    /// Le HTML brut est désactivé au rendu : voir MarkdownService.
    /// </summary>
    public string Reglement { get; set; } = string.Empty;

    // ── Barème d'XP de la ligue (R6) ──────────────────────────────────────────
    // Valeurs par défaut = LRB Saison 3. Le touchdown vaut 5 en Dungeon Bowl :
    // à la création, le formulaire pré-remplit selon le jeu choisi.
    // Les matchs déjà saisis conservent leur XP : rien n'est recalculé.

    /// <summary>XP par touchdown.</summary>
    public int PspParTouchdown { get; set; } = 3;

    /// <summary>XP par passe complétée.</summary>
    public int PspParPasse { get; set; } = 1;

    /// <summary>XP par interception.</summary>
    public int PspParInterception { get; set; } = 2;

    /// <summary>XP par élimination infligée.</summary>
    public int PspParElimination { get; set; } = 2;

    /// <summary>XP bonus pour le joueur désigné MVP.</summary>
    public int PspBonusMvp { get; set; } = 4;

    /// <summary>XP par déviation (DEV). Zéro par défaut.</summary>
    public int PspParDeviation { get; set; } = 0;

    /// <summary>XP par agression (AGRO). Zéro par défaut.</summary>
    public int PspParAgression { get; set; } = 0;

    // ── Barème de points de classement de la ligue ────────────────────────────
    // Copie prise sur la version de règles à la création, puis éditable par les
    // commissaires MÊME SAISON LANCÉE : chaque enregistrement déclenche un
    // recalcul complet du classement depuis les feuilles de match
    // (LeagueService.RecalculerClassementAsync). C'est ce recalcul qui rend
    // l'édition en cours de route sûre — contrairement à Team.Tresorerie, aucune
    // valeur dérivée n'est ici figée sans moyen de la reconstruire.

    /// <summary>Points de classement pour une victoire (au-delà du dernier palier).</summary>
    public int PointsVictoire { get; set; } = 3;

    /// <summary>Points de classement pour un match nul.</summary>
    public int PointsNul { get; set; } = 1;

    /// <summary>Points de classement pour une défaite.</summary>
    public int PointsDefaite { get; set; } = 0;

    /// <summary>Points bonus par touchdown marqué.</summary>
    public int PointsParTouchdown { get; set; } = 0;

    /// <summary>Points bonus par élimination infligée.</summary>
    public int PointsParElimination { get; set; } = 0;

    /// <summary>Points bonus par interception.</summary>
    public int PointsParInterception { get; set; } = 0;

    /// <summary>Points bonus par passe réussie.</summary>
    public int PointsParPasse { get; set; } = 0;

    /// <summary>Points bonus par déviation.</summary>
    public int PointsParDeviation { get; set; } = 0;

    /// <summary>Points bonus par agression.</summary>
    public int PointsParAgression { get; set; } = 0;

    public DateTime CreeLe { get; set; } = DateTime.UtcNow;

    public ICollection<Division> Divisions { get; set; } = [];
    public ICollection<Team> Equipes { get; set; } = [];

    /// <summary>
    /// Paliers de points selon le nombre de tours joués. Vide = barème simple
    /// (victoire / nul / défaite), et le champ « nombre de tours » n'est alors
    /// même pas proposé sur la feuille de match.
    /// </summary>
    public ICollection<PalierPointsLigue> PaliersPoints { get; set; } = [];
    public ICollection<PhaseDeReposValidation> ValidationsRepos { get; set; } = [];
    public ICollection<LeagueAward> Awards { get; set; } = [];
    public ICollection<LeagueCommissioner> CommissairesDeLigue { get; set; } = [];

    /// <summary>
    /// Copie du staff des règles, ajustable par le commissaire pour cette ligue.
    /// </summary>
    public ICollection<LeagueStaffType> StaffTypes { get; set; } = [];
}

public class Division
{
    public int Id { get; set; }
    public int LeagueId { get; set; }
    public League League { get; set; } = null!;
    public string Nom { get; set; } = string.Empty;
    public int Ordre { get; set; }

    public ICollection<Team> Equipes { get; set; } = [];
    public ICollection<Match> Matchs { get; set; } = [];
}

/// <summary>
/// Date indicative de fin d'une ronde : la date à laquelle les matchs de cette
/// ronde devraient être joués. Purement informative — rien n'est bloqué ni
/// clôturé automatiquement à son échéance.
///
/// Table dédiée plutôt qu'une colonne sur <see cref="Match"/> : une ronde n'est
/// pas une entité en base (juste un numéro porté par ses matchs), et dupliquer
/// la date sur chaque match la rendrait incohérente dès la première édition.
/// </summary>
public class EcheanceRonde
{
    public int Id { get; set; }
    public int LeagueId { get; set; }
    public League League { get; set; } = null!;

    /// <summary>Numéro de ronde concerné (même numérotation que Match.Ronde).</summary>
    public int Ronde { get; set; }

    /// <summary>Date limite conseillée, stockée en UTC.</summary>
    public DateTime DateLimite { get; set; }
}
