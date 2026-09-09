namespace BolDeSangManager.Data.Models;

/// <summary>
/// Trace d'une correction manuelle de l'XP d'un joueur par un commissaire (R4).
///
/// Le commissaire peut ajuster l'XP a posteriori, y compris après validation du
/// match. Chaque correction est journalisée pour rester auditable auprès des
/// coaches : qui, quand, de combien à combien, et pourquoi.
/// </summary>
public class PspCorrection
{
    public int Id { get; set; }

    public int TeamPlayerId { get; set; }
    public TeamPlayer TeamPlayer { get; set; } = null!;

    /// <summary>XP du joueur avant la correction.</summary>
    public int AncienneValeur { get; set; }

    /// <summary>XP du joueur après la correction.</summary>
    public int NouvelleValeur { get; set; }

    /// <summary>Motif saisi par le commissaire (obligatoire côté UI).</summary>
    public string Motif { get; set; } = string.Empty;

    /// <summary>
    /// Commissaire auteur de la correction. Nullable : la trace doit survivre
    /// à la suppression du compte (auditabilité).
    /// </summary>
    public string? CorrigeParId { get; set; }
    public ApplicationUser? CorrigePar { get; set; }

    public DateTime CorrigeLe { get; set; } = DateTime.UtcNow;

    /// <summary>Écart appliqué (peut être négatif).</summary>
    public int Ecart => NouvelleValeur - AncienneValeur;
}
