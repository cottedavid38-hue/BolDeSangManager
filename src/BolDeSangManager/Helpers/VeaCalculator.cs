using BolDeSangManager.Data.Models;
using BolDeSangManager.Services;

namespace BolDeSangManager.Helpers;

/// <summary>
/// Calcul de la Valeur d'Équipe Actuelle (VEA) — <b>source unique</b>.
///
/// ⚠️ Historique : la VEA était calculée à deux endroits. <c>PdfService</c>
/// refaisait la somme à partir des colonnes de staff HISTORIQUES de
/// <see cref="Team"/> (<c>FansDevoues</c>, <c>NombreRelances</c>…), que le
/// modèle documente pourtant comme « ne pas lire dans du code nouveau » depuis
/// que le staff est porté par la liste ouverte <see cref="Team.Staff"/>.
/// La feuille d'équipe imprimée affichait donc une VEA amputée de tout le
/// staff (80k au lieu de 300k sur l'équipe de référence des tests).
///
/// Toute nouvelle règle touchant la VEA — par exemple « Trois-quarts à Vil
/// Prix », qui traite le coût des Trois-quarts comme nul — s'écrit ICI et
/// nulle part ailleurs, sinon écran et PDF divergent à nouveau.
/// </summary>
public static class VeaCalculator
{
    /// <summary>
    /// Somme la valeur des joueurs actifs et du staff détenu.
    ///
    /// Les joueurs morts ou retraités sont exclus. Le staff compte sa quantité
    /// TOTALE, y compris les unités offertes à la création (<c>MinCreation</c>) :
    /// elles ne sont pas facturées au budget de départ, mais elles ont une
    /// valeur — voir <see cref="StaffService.UnitesFacturees"/>.
    ///
    /// En revanche, seuls les staff marqués <c>CompteDansVea</c> entrent dans
    /// la somme : les <b>Fans dévoués</b> en sont exclus par défaut (ils
    /// mesurent le public, pas la puissance de l'équipe). Le drapeau est réglé
    /// en Admin, jamais par un test sur le nom — une édition future ou un staff
    /// inventé par l'association se règle ainsi sans dev.
    /// </summary>
    /// <param name="bareme">
    /// Barème des améliorations, qui sert à CALCULER la valeur de chaque joueur
    /// (<see cref="ValeurJoueurCalculator"/>). Omis, il est résolu depuis la
    /// version de règles de la ligue de l'équipe — ce qui exige que la requête
    /// ait chargé <c>League.RulesVersion</c>.
    /// </param>
    public static int Calculer(Team equipe, BaremeAmelioration? bareme = null)
    {
        bareme ??= ValeurJoueurCalculator.BaremeDe(equipe);

        var totalJoueurs = equipe.Joueurs
            .Where(j => !j.EstMort && !j.EstRetraite)
            .Sum(j => ValeurComptee(j, equipe.TeamType, bareme));

        var totalStaff = equipe.Staff
            .Where(s => s.LeagueStaffType is not null && s.LeagueStaffType.CompteDansVea)
            .Sum(s => s.Quantite * StaffService.CoutUnitaire(s.LeagueStaffType, equipe.TeamType));

        return totalJoueurs + totalStaff;
    }

    /// <summary>
    /// Valeur d'un joueur dans la VEA, après application de « Trois-quarts à
    /// Vil Prix » si la race la porte.
    ///
    /// On soustrait le COÛT D'EMBAUCHE du poste, on ne met pas la valeur à
    /// zéro : le LRB précise que « toute augmentation de valeur de ces joueurs
    /// est incluse normalement ». Un Snotling embauché 15 000 et amélioré à
    /// 35 000 compte donc 20 000, pas 0.
    /// </summary>
    private static int ValeurComptee(TeamPlayer joueur, TeamType? teamType, BaremeAmelioration bareme)
    {
        var valeur = ValeurJoueurCalculator.Calculer(joueur, bareme);

        var poste = joueur.PlayerPosition;
        if (poste is null || teamType is null) return valeur;

        var motsClesExoneres = MotsClesExoneres(teamType);
        if (motsClesExoneres.Count == 0) return valeur;

        var motsClesDuPoste = SpecialRuleCodes.DecouperOptions(poste.MotsCles);
        var estExonere = motsClesDuPoste.Any(m =>
            motsClesExoneres.Contains(m, StringComparer.OrdinalIgnoreCase));

        if (!estExonere) return valeur;

        // Jamais de contribution négative : un joueur dont la valeur est
        // inférieure à son coût d'embauche compte 0, pas un montant négatif
        // qui viendrait amputer la valeur des coéquipiers.
        return Math.Max(0, valeur - poste.Cout);
    }

    /// <summary>
    /// Mots-clés dont le coût d'embauche est annulé pour cette race.
    ///
    /// Ils viennent de la fiche de race (<c>OptionsChoix</c> sur la liaison),
    /// jamais du code : une future édition visant un autre mot-clé se règle en
    /// admin. Une valeur vide n'exonère personne — sinon elle correspondrait à
    /// tous les postes et mettrait la VEA à zéro.
    /// </summary>
    private static List<string> MotsClesExoneres(TeamType teamType) =>
        teamType.ReglesSpecialesListe
            .Where(l => l.SpecialRule?.Code == SpecialRuleCodes.CoutNulParMotCle)
            .SelectMany(l => SpecialRuleCodes.DecouperOptions(l.OptionsChoix))
            .ToList();
}
