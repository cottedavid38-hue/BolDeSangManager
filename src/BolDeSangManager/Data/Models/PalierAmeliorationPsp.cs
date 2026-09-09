namespace BolDeSangManager.Data.Models;

/// <summary>
/// Coût en PSP d'une amélioration, pour un RANG donné (1 = 1re amélioration du
/// joueur … 6 = 6e). Porté par la <see cref="RulesVersion"/> et éditable en
/// admin, comme le barème d'XP.
///
/// Table dédiée plutôt que 24 colonnes sur <c>RulesVersion</c> : le LRB croise
/// 4 types d'amélioration × 6 rangs, ce qui serait illisible à plat. Même
/// principe que <see cref="PalierPointsLigue"/>.
///
/// ⚠️ Ces coûts sont des <b>propositions</b> : le coach corrige la valeur à la
/// main dans son après-match (décision produit, cohérente avec l'XP de feuille
/// de match). Rien n'est imposé ; seule la cagnotte du joueur fait limite.
/// </summary>
public class PalierAmeliorationPsp
{
    public int Id { get; set; }

    public int RulesVersionId { get; set; }
    public RulesVersion RulesVersion { get; set; } = null!;

    /// <summary>
    /// Rang de l'amélioration : 1 Expérimenté, 2 Vétéran, 3 Future Star,
    /// 4 Star, 5 Superstar, 6 Légende.
    /// </summary>
    public int Rang { get; set; }

    /// <summary>Compétence principale déterminée au hasard.</summary>
    public int CoutAleaPrincipale { get; set; }

    /// <summary>Compétence principale choisie (plus chère que le hasard).</summary>
    public int CoutChoixPrincipale { get; set; }

    /// <summary>
    /// Compétence secondaire. Le LRB n'a qu'une colonne pour la secondaire :
    /// hasard et choix coûtent le même prix.
    /// </summary>
    public int CoutSecondaire { get; set; }

    /// <summary>Amélioration de caractéristique (jet de D8).</summary>
    public int CoutCaracteristique { get; set; }
}
