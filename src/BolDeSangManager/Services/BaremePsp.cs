using BolDeSangManager.Data.Enums;
using BolDeSangManager.Data.Models;

namespace BolDeSangManager.Services;

/// <summary>
/// Barème de calcul de l'XP (PSP) gagnée sur un match.
///
/// Isolé ici — plutôt qu'en constantes en dur dans <see cref="MatchService"/> — pour
/// servir de point d'extension : la carte « Barème d'XP configurable à la création
/// d'une ligue/tournoi » n'aura qu'à fournir une autre source de valeurs
/// (par ligue) sans toucher au reste du calcul.
///
/// Les valeurs par défaut sont celles du LRB Saison 3 / Dungeon Bowl Edition 2022.
/// </summary>
public class BaremePsp
{
    /// <summary>XP par touchdown. 5 en Dungeon Bowl, 3 sinon.</summary>
    public int ParTouchdown { get; init; } = 3;

    /// <summary>XP par passe complétée.</summary>
    public int ParPasse { get; init; } = 1;

    /// <summary>XP par interception.</summary>
    public int ParInterception { get; init; } = 2;

    /// <summary>XP par élimination infligée.</summary>
    public int ParElimination { get; init; } = 2;

    /// <summary>XP bonus pour le joueur désigné MVP.</summary>
    public int BonusMvp { get; init; } = 4;

    /// <summary>
    /// XP par déviation (DEV). <b>Zéro par défaut</b> : l'action est saisie pour
    /// le classement, pas pour la progression du joueur. Une ligue qui voudrait
    /// la valoriser peut le faire, mais rien ne change pour les autres.
    /// </summary>
    public int ParDeviation { get; init; } = 0;

    /// <summary>XP par agression (AGRO). Zéro par défaut, comme la déviation.</summary>
    public int ParAgression { get; init; } = 0;

    /// <summary>Barème par défaut pour un type de jeu donné.</summary>
    public static BaremePsp ParDefaut(GameType gameType) => new()
    {
        ParTouchdown = gameType == GameType.DungeonBowl ? 5 : 3
    };

    /// <summary>
    /// Barème défini par la version de règles (R6) — la référence dont héritent
    /// les nouvelles ligues. Repli sur les valeurs du jeu si la version est inconnue.
    /// </summary>
    public static BaremePsp DeVersion(RulesVersion? version, GameType gameType) =>
        version is null
            ? ParDefaut(gameType)
            : new BaremePsp
            {
                ParTouchdown    = version.PspParTouchdown,
                ParPasse        = version.PspParPasse,
                ParInterception = version.PspParInterception,
                ParElimination  = version.PspParElimination,
                BonusMvp        = version.PspBonusMvp,
                ParDeviation    = version.PspParDeviation,
                ParAgression    = version.PspParAgression
            };

    /// <summary>Applique ce barème à une version de règles.</summary>
    public void AppliquerA(RulesVersion version)
    {
        version.PspParTouchdown    = ParTouchdown;
        version.PspParPasse        = ParPasse;
        version.PspParInterception = ParInterception;
        version.PspParElimination  = ParElimination;
        version.PspBonusMvp        = BonusMvp;
        version.PspParDeviation    = ParDeviation;
        version.PspParAgression    = ParAgression;
    }

    /// <summary>
    /// Barème configuré sur la ligue (R6). Si la ligue est inconnue, on retombe
    /// sur les valeurs par défaut du jeu.
    /// </summary>
    public static BaremePsp DeLigue(League? ligue, GameType gameType) =>
        ligue is null
            ? ParDefaut(gameType)
            : new BaremePsp
            {
                ParTouchdown    = ligue.PspParTouchdown,
                ParPasse        = ligue.PspParPasse,
                ParInterception = ligue.PspParInterception,
                ParElimination  = ligue.PspParElimination,
                BonusMvp        = ligue.PspBonusMvp,
                ParDeviation    = ligue.PspParDeviation,
                ParAgression    = ligue.PspParAgression
            };

    /// <summary>Applique ce barème aux champs d'une ligue (création / édition).</summary>
    public void AppliquerA(League ligue)
    {
        ligue.PspParTouchdown    = ParTouchdown;
        ligue.PspParPasse        = ParPasse;
        ligue.PspParInterception = ParInterception;
        ligue.PspParElimination  = ParElimination;
        ligue.PspBonusMvp        = BonusMvp;
        ligue.PspParDeviation    = ParDeviation;
        ligue.PspParAgression    = ParAgression;
    }

    /// <summary>
    /// XP proposée pour la performance d'un joueur sur un match.
    /// C'est une <b>valeur par défaut</b> : depuis R4, le coach peut la modifier
    /// sur la feuille de match et c'est sa saisie qui est persistée.
    /// </summary>
    public int Calculer(MatchPlayerRecord record)
    {
        int xp = 0;
        xp += record.Touchdowns * ParTouchdown;
        xp += record.Passes * ParPasse;
        xp += record.Interceptions * ParInterception;
        xp += record.EliminationsInfligees * ParElimination;
        xp += record.Deviations * ParDeviation;
        xp += record.Agressions * ParAgression;
        if (record.EstMVP) xp += BonusMvp;
        return xp;
    }

    /// <summary>Variante pour les écrans de saisie, qui manipulent des stats détachées.</summary>
    public int Calculer(int touchdowns, int passes, int interceptions, int eliminations, bool estMvp,
                        int deviations = 0, int agressions = 0)
    {
        int xp = 0;
        xp += touchdowns * ParTouchdown;
        xp += passes * ParPasse;
        xp += interceptions * ParInterception;
        xp += eliminations * ParElimination;
        xp += deviations * ParDeviation;
        xp += agressions * ParAgression;
        if (estMvp) xp += BonusMvp;
        return xp;
    }
}
