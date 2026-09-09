using BolDeSangManager.Data.Enums;
using BolDeSangManager.Data.Models;

namespace BolDeSangManager.Services;

/// <summary>
/// Barème des améliorations de joueur : ce que chaque amélioration COÛTE en PSP
/// et ce qu'elle AJOUTE à la valeur du joueur.
///
/// Source : LRB Saison 3 — deux tableaux DISTINCTS que le code confondait
/// auparavant en un seul :
///
/// <list type="bullet">
///   <item><b>Tableau des Améliorations</b> → coût en PSP. Croise le type
///   d'amélioration ET le RANG (1re à 6e amélioration du joueur).</item>
///   <item><b>Tableau de Hausse de Valeur</b> → pièces d'or. Une seule ligne par
///   type, sans rang, et <b>sans distinguer</b> une compétence tirée au hasard
///   d'une compétence choisie.</item>
/// </list>
///
/// ⚠️ C'est ce dernier point qui était faux : l'ancien code faisait payer une
/// compétence choisie plus cher en VALEUR qu'une compétence aléatoire. Le LRB ne
/// fait cette différence que sur le COÛT EN PSP.
///
/// Isolé ici — plutôt qu'en constantes en dur — pour que le barème soit porté par
/// la <see cref="RulesVersion"/> et réglable en admin, comme <see cref="BaremePsp"/> :
/// une édition future se règle sans développement (principe #2).
/// </summary>
public class BaremeAmelioration
{
    // ── Hausse de valeur (pièces d'or) ───────────────────────────────────────

    /// <summary>Compétence de la catégorie principale, au hasard OU choisie.</summary>
    public int HaussePrincipale { get; init; } = 20_000;

    /// <summary>Compétence de la catégorie secondaire, au hasard OU choisie.</summary>
    public int HausseSecondaire { get; init; } = 40_000;

    public int HausseArmure { get; init; } = 10_000;
    public int HausseMouvement { get; init; } = 20_000;
    public int HausseCapacitePasse { get; init; } = 20_000;
    public int HausseAgilite { get; init; } = 30_000;
    public int HausseForce { get; init; } = 60_000;

    /// <summary>
    /// Supplément pour une <b>Compétence d'Élite</b> (LRB : « Si un joueur gagne
    /// une Compétence d'Élite, augmentez sa valeur de 10 000 pièces d'or
    /// supplémentaires »). S'ajoute à la hausse normale, et ne concerne QUE les
    /// compétences — jamais une amélioration de caractéristique.
    /// </summary>
    public int SurcoutElite { get; init; } = 10_000;

    /// <summary>
    /// Hausse de valeur d'une amélioration. Fonction PURE : c'est elle que
    /// partagent l'application au fil de l'eau et le recalcul complet, condition
    /// non négociable pour que les deux chemins ne divergent jamais.
    /// </summary>
    public int Hausse(ImprovementType type, AffectedStat? stat = null, bool estElite = false)
    {
        var basePrix = type switch
        {
            ImprovementType.AleaPrimaire or ImprovementType.SelectionPrimaire       => HaussePrincipale,
            ImprovementType.AleaSecondaire or ImprovementType.SelectionSecondaire   => HausseSecondaire,
            ImprovementType.AmeliorationCarac or ImprovementType.AmeliorationForceArmure
                => HausseStat(stat),
            _ => 0
        };

        // Le surcoût Élite ne s'applique qu'aux compétences : une amélioration de
        // caractéristique n'est pas une compétence et n'a pas de drapeau Élite.
        return basePrix + (estElite && EstCompetence(type) ? SurcoutElite : 0);
    }

    /// <summary>Hausse propre à chaque caractéristique améliorée.</summary>
    private int HausseStat(AffectedStat? stat) => stat switch
    {
        AffectedStat.Armure        => HausseArmure,
        AffectedStat.Mouvement     => HausseMouvement,
        AffectedStat.CapacitePasse => HausseCapacitePasse,
        AffectedStat.Agilite       => HausseAgilite,
        AffectedStat.Force         => HausseForce,
        _ => 0
    };

    public static bool EstCompetence(ImprovementType type) =>
        type is ImprovementType.AleaPrimaire or ImprovementType.SelectionPrimaire
             or ImprovementType.AleaSecondaire or ImprovementType.SelectionSecondaire;

    // ── Coût en PSP, par rang ────────────────────────────────────────────────

    /// <summary>
    /// Coût en PSP proposé pour la <paramref name="rang"/>-ième amélioration d'un
    /// joueur (1 = Expérimenté … 6 = Légende).
    ///
    /// ⚠️ C'est une <b>PROPOSITION</b>, jamais une valeur imposée : le coach
    /// corrige à la main, comme pour l'XP de la feuille de match. Le service ne
    /// refuse que ce qui dépasse la cagnotte du joueur.
    ///
    /// Les paliers viennent de la version de règles ; en leur absence (version
    /// non paramétrée), repli sur le tableau LRB par défaut.
    /// </summary>
    public IReadOnlyList<PalierAmeliorationPsp> Paliers { get; init; } = [];

    public int CoutPsp(ImprovementType type, int rang)
    {
        var palier = Paliers.FirstOrDefault(p => p.Rang == rang)
                     ?? PaliersLrbParDefaut().FirstOrDefault(p => p.Rang == rang);

        if (palier is null) return 0;

        return type switch
        {
            ImprovementType.AleaPrimaire        => palier.CoutAleaPrincipale,
            ImprovementType.SelectionPrimaire   => palier.CoutChoixPrincipale,
            // Le LRB ne donne qu'une colonne « Choisir une Compétence
            // Secondaire » : une secondaire tirée au hasard coûte le même prix.
            ImprovementType.AleaSecondaire
                or ImprovementType.SelectionSecondaire => palier.CoutSecondaire,
            ImprovementType.AmeliorationCarac
                or ImprovementType.AmeliorationForceArmure => palier.CoutCaracteristique,
            _ => 0
        };
    }

    /// <summary>Tableau des Améliorations du LRB Saison 3.</summary>
    public static List<PalierAmeliorationPsp> PaliersLrbParDefaut() =>
    [
        new() { Rang = 1, CoutAleaPrincipale =  3, CoutChoixPrincipale =  6, CoutSecondaire = 10, CoutCaracteristique = 14 },
        new() { Rang = 2, CoutAleaPrincipale =  4, CoutChoixPrincipale =  8, CoutSecondaire = 12, CoutCaracteristique = 16 },
        new() { Rang = 3, CoutAleaPrincipale =  6, CoutChoixPrincipale = 12, CoutSecondaire = 16, CoutCaracteristique = 20 },
        new() { Rang = 4, CoutAleaPrincipale =  8, CoutChoixPrincipale = 16, CoutSecondaire = 20, CoutCaracteristique = 24 },
        new() { Rang = 5, CoutAleaPrincipale = 10, CoutChoixPrincipale = 20, CoutSecondaire = 24, CoutCaracteristique = 28 },
        new() { Rang = 6, CoutAleaPrincipale = 15, CoutChoixPrincipale = 30, CoutSecondaire = 34, CoutCaracteristique = 38 }
    ];

    // ── Résolution depuis la version de règles ───────────────────────────────

    /// <summary>Barème LRB, utilisé quand aucune version n'est résolue.</summary>
    public static BaremeAmelioration ParDefaut() => new()
    {
        Paliers = PaliersLrbParDefaut()
    };

    /// <summary>
    /// Écrit les 8 hausses de valeur sur une version de règles. Ne touche PAS aux
    /// paliers PSP : ils forment une collection, remplacée séparément par le
    /// service (même découpage que <see cref="BaremePoints.AppliquerA(RulesVersion)"/>).
    ///
    /// ⚠️ Exhaustif par construction : tout champ oublié ici reprendrait son
    /// défaut C# et écraserait silencieusement la valeur en base.
    /// </summary>
    public void AppliquerA(RulesVersion version)
    {
        version.HaussePrincipale    = HaussePrincipale;
        version.HausseSecondaire    = HausseSecondaire;
        version.HausseArmure        = HausseArmure;
        version.HausseMouvement     = HausseMouvement;
        version.HausseCapacitePasse = HausseCapacitePasse;
        version.HausseAgilite       = HausseAgilite;
        version.HausseForce         = HausseForce;
        version.SurcoutElite        = SurcoutElite;
    }

    /// <summary>
    /// Barème porté par la version de règles — la référence éditable en admin.
    /// Repli sur le LRB si la version est inconnue.
    /// </summary>
    public static BaremeAmelioration DeVersion(RulesVersion? version) =>
        version is null
            ? ParDefaut()
            : new BaremeAmelioration
            {
                HaussePrincipale    = version.HaussePrincipale,
                HausseSecondaire    = version.HausseSecondaire,
                HausseArmure        = version.HausseArmure,
                HausseMouvement     = version.HausseMouvement,
                HausseCapacitePasse = version.HausseCapacitePasse,
                HausseAgilite       = version.HausseAgilite,
                HausseForce         = version.HausseForce,
                SurcoutElite        = version.SurcoutElite,
                // Une version jamais paramétrée n'a aucun palier : on retombe sur
                // le LRB plutôt que de proposer 0 PSP partout.
                Paliers = version.PaliersAmelioration.Count > 0
                    ? version.PaliersAmelioration.OrderBy(p => p.Rang).ToList()
                    : PaliersLrbParDefaut()
            };
}
