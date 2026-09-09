using BolDeSangManager.Data.Enums;

namespace BolDeSangManager.Data.Models;

public class PlayerImprovement
{
    public int Id { get; set; }
    public int TeamPlayerId { get; set; }
    public TeamPlayer TeamPlayer { get; set; } = null!;

    public int Palier { get; set; }              // rang de l'amélioration (1, 2, 3… dans l'ordre d'acquisition)
    public ImprovementType Type { get; set; }

    /// <summary>
    /// XP retirée de la cagnotte du joueur pour cette amélioration (R4).
    /// Saisie par le coach à l'après-match. Restituée si la feuille est annulée.
    /// </summary>
    public int PspDepensee { get; set; }

    // Skill acquise (si Type = AleaPrimaire/SelectionPrimaire/AleaSecondaire/SelectionSecondaire)
    public int? SkillId { get; set; }
    public Skill? Skill { get; set; }

    // Caractéristique améliorée (si Type = AmeliorationCarac ou AmeliorationForceArmure)
    public AffectedStat? StatAmelioree { get; set; }

    public int ValeurHausse { get; set; }        // kpo ajoutés à TeamPlayer.ValeurActuelle
    public DateTime AppliqueLe { get; set; } = DateTime.UtcNow;
    public bool EnAttenteValidation { get; set; } = false;

    // Traçabilité : null si appliqué pendant la phase de repos
    public int? MatchSheetId { get; set; }
    public MatchSheet? MatchSheet { get; set; }
}
