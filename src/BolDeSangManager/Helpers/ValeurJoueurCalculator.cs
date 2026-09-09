using BolDeSangManager.Data.Models;
using BolDeSangManager.Services;

namespace BolDeSangManager.Helpers;

/// <summary>
/// Valeur d'un joueur — <b>calculée</b>, jamais lue depuis une colonne.
///
/// <code>Valeur = PlayerPosition.Cout + Σ Bareme.Hausse(amélioration)</code>
///
/// ⚠️ Pourquoi un helper statique et PAS une propriété <c>[NotMapped]</c> sur
/// <see cref="TeamPlayer"/> : le barème vit sur la <see cref="RulesVersion"/>,
/// atteignable seulement par <c>Team → League → RulesVersion</c>. Une propriété
/// d'entité masquerait cette dépendance et renverrait des valeurs fausses en
/// silence dès qu'un <c>Include</c> manque. Même patron que
/// <see cref="VeaCalculator"/> : les dépendances sont passées explicitement.
///
/// ⚠️ La hausse est <b>recalculée depuis le barème</b>, jamais reprise de
/// <c>PlayerImprovement.ValeurHausse</c> (colonne de trace, legacy) : c'est tout
/// l'intérêt du chantier — corriger le barème doit se propager aux améliorations
/// déjà acquises, et corriger le coût d'un poste doit se propager aux joueurs
/// déjà recrutés.
/// </summary>
public static class ValeurJoueurCalculator
{
    /// <summary>
    /// Valeur courante du joueur, en pièces d'or.
    ///
    /// Un poste non chargé (ou supprimé du catalogue) donne 0 : il n'existe plus
    /// aucune valeur de repli en base depuis que la colonne <c>ValeurActuelle</c>
    /// a disparu. C'est volontaire — un zéro visible à l'écran signale un
    /// <c>Include</c> manquant, là où une valeur figée l'aurait masqué.
    /// </summary>
    public static int Calculer(TeamPlayer joueur, BaremeAmelioration bareme)
    {
        var poste = joueur.PlayerPosition;
        if (poste is null) return 0;

        var hausses = joueur.Improvements.Sum(imp =>
            bareme.Hausse(imp.Type, imp.StatAmelioree, imp.Skill?.EstElite ?? false));

        return poste.Cout + hausses;
    }

    /// <summary>
    /// Barème applicable à une équipe : celui de la version de règles de sa
    /// ligue. Repli sur le LRB quand la chaîne n'est pas chargée ou que l'équipe
    /// n'a pas de ligue.
    ///
    /// ⚠️ Toute requête alimentant un écran qui affiche une valeur doit charger
    /// <c>League.RulesVersion</c>, <c>Joueurs.PlayerPosition</c> et
    /// <c>Joueurs.Improvements.Skill</c> — sinon la valeur est fausse SANS
    /// aucune erreur.
    /// </summary>
    public static BaremeAmelioration BaremeDe(Team? equipe) =>
        BaremeAmelioration.DeVersion(equipe?.League?.RulesVersion);
}
