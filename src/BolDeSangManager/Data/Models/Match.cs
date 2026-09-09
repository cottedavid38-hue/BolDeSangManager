using BolDeSangManager.Data.Enums;

namespace BolDeSangManager.Data.Models;

public class Match
{
    public int Id { get; set; }
    public int? DivisionId { get; set; }
    public Division? Division { get; set; }
    public int Ronde { get; set; }
    public bool EstPlayoff { get; set; } = false;

    public int EquipeDomicileId { get; set; }
    public Team EquipeDomicile { get; set; } = null!;
    public int EquipeExterieurId { get; set; }
    public Team EquipeExterieur { get; set; } = null!;

    public MatchStatus Statut { get; set; } = MatchStatus.Programme;
    /// <summary>
    /// Date et heure convenues pour le match (#1). Stockée en UTC, affichée en
    /// heure locale. Fixée librement par l'un ou l'autre coach, ou le commissaire.
    /// </summary>
    public DateTime? DateProgrammee { get; set; }

    /// <summary>Lieu convenu pour le match (#1) — texte libre.</summary>
    public string Lieu { get; set; } = string.Empty;
    public DateTime? DateJouee { get; set; }

    public int? ScoreDomicile { get; set; }
    public int? ScoreExterieur { get; set; }

    public MatchSheet? Feuille { get; set; }
    public ICollection<PlayerInjury> Blessures { get; set; } = [];
}

public class MatchSheet
{
    public int Id { get; set; }
    public int MatchId { get; set; }
    public Match Match { get; set; } = null!;
    public string SaisiParId { get; set; } = string.Empty;
    public ApplicationUser SaisiPar { get; set; } = null!;
    public DateTime SaisiLe { get; set; } = DateTime.UtcNow;

    // Résultats
    public int TouchdownsDomicile { get; set; }
    public int TouchdownsExterieur { get; set; }
    public int EliminationsDomicile { get; set; }
    public int EliminationsExterieur { get; set; }

    // Gains post-match (calculés selon règles)
    public int GainsDomicile { get; set; }
    public int GainsExterieur { get; set; }
    public int VariationFansDomicile { get; set; }
    public int VariationFansExterieur { get; set; }

    /// <summary>
    /// Variation de fans RÉELLEMENT appliquée, après écrêtage par le plancher (1)
    /// et le plafond de ligue. L'annulation d'un match doit soustraire cette
    /// valeur-là, pas la variation théorique saisie ci-dessus.
    ///
    /// Sans ça : plafond 12, équipe à 11 qui gagne +3 → écrêtée à 12 ; annuler le
    /// match ferait 12 − 3 = 9 alors qu'elle avait 11. Deux fans disparaissent en
    /// silence à chaque annulation.
    ///
    /// Nullable : les feuilles saisies avant cette colonne n'ont pas
    /// l'information, on retombe alors sur la variation théorique.
    /// </summary>
    public int? VariationFansDomicileAppliquee { get; set; }
    public int? VariationFansExterieurAppliquee { get; set; }

    /// <summary>
    /// Nombre de tours joués sur ce match (un seul chiffre : les deux équipes
    /// jouent le même nombre de tours). Sert aux PALIERS du barème de points de
    /// ligue : « victoire avant le 13e tour = 3000 pts ».
    ///
    /// Nullable, et c'est essentiel : les feuilles saisies avant l'ajout des
    /// paliers n'ont pas l'information. Le calcul retombe alors sur les points de
    /// base de la ligue plutôt que d'inventer une valeur. Le champ n'est proposé
    /// à la saisie que si la ligue a effectivement défini des paliers.
    /// </summary>
    public int? NombreDeTours { get; set; }

    /// <summary>
    /// Date de la dernière correction de cette feuille par un commissaire, en UTC.
    /// Null = jamais corrigée.
    ///
    /// Une correction ANNULE les choix d'après-match déjà faits par les deux
    /// coaches (améliorations, XP dépensée, blessures) et les oblige à
    /// recommencer. Sans cette trace, le coach reçoit un écran d'après-match
    /// vierge sans comprendre pourquoi son travail a disparu : c'est elle qui
    /// alimente le bandeau d'avertissement de l'écran d'après-match.
    /// </summary>
    public DateTime? CorrigeeLe { get; set; }

    /// <summary>Score AVANT la dernière correction, pour l'annoncer au coach.</summary>
    public int? ScoreAvantCorrectionDomicile { get; set; }
    public int? ScoreAvantCorrectionExterieur { get; set; }

    /// <summary>
    /// Pseudo du commissaire ayant confirmé la feuille À LA PLACE du coach
    /// adverse (débloquage d'un match qui traîne). Null = confirmation normale
    /// par l'adversaire, ou feuille pas encore confirmée.
    ///
    /// ⚠️ Volontairement un TEXTE et pas une FK vers ApplicationUser : une
    /// cinquième FK en Restrict vers un compte obligerait à l'ajouter au
    /// comptage de UserAccountService.EvaluerSuppressionAsync et à l'export
    /// RGPD (voir CLAUDE.md). Ici on ne veut qu'une trace d'affichage.
    /// </summary>
    public string? ConfirmeeParCommissaire { get; set; }

    /// <summary>Date UTC de la confirmation par un commissaire. Null = jamais.</summary>
    public DateTime? ConfirmeeParCommissaireLe { get; set; }

    // Inducements pré-match (JSON simple: {"entrainement": 2, "potDeVin": 1})
    public string InducementsDomicile { get; set; } = "{}";
    public string InducementsExterieur { get; set; } = "{}";

    public bool ValideParCommissaire { get; set; } = false;
    public string NotesCommissaire { get; set; } = string.Empty;

    public bool ApresMatchDomicileValide { get; set; } = false;
    public bool ApresMatchExterieurValide { get; set; } = false;

    public ICollection<MatchPlayerRecord> RecordsJoueurs { get; set; } = [];
}

public class MatchPlayerRecord
{
    public int Id { get; set; }
    public int MatchSheetId { get; set; }
    public MatchSheet MatchSheet { get; set; } = null!;
    public int TeamPlayerId { get; set; }
    public TeamPlayer TeamPlayer { get; set; } = null!;
    public bool EstCoteDomicile { get; set; }

    // Actions du match
    public int Touchdowns { get; set; } = 0;
    public int Passes { get; set; } = 0;
    public int Interceptions { get; set; } = 0;
    public int EliminationsInfligees { get; set; } = 0;

    /// <summary>
    /// Déviations (DEV) réalisées par le joueur. Ne rapporte aucune XP par défaut
    /// (XpParDeviation = 0), mais peut valoir des points de classement selon le
    /// barème de la ligue.
    /// </summary>
    public int Deviations { get; set; } = 0;

    /// <summary>
    /// Agressions (AGRO) : frapper un joueur au sol. Aucune XP par défaut, comme
    /// les déviations.
    /// </summary>
    public int Agressions { get; set; } = 0;

    public bool EstMVP { get; set; } = false;

    // PSP gagnés ce match
    public int PspGagnes { get; set; } = 0;

    // Blessure subie (si applicable)
    public InjuryType? Blessure { get; set; }
    public AffectedStat? StatAffectee { get; set; }
    public bool AManqueLeMatch { get; set; } = false; // Absent avant le match
}
