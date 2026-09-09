using BolDeSangManager.Data.Enums;
using BolDeSangManager.Data.Models;
using BolDeSangManager.Services;

namespace BolDeSangManager.Tests;

/// <summary>
/// Barème des améliorations : hausse de valeur et coût en PSP.
///
/// Référence : LRB Saison 3, deux tableaux DISTINCTS. Le code d'origine les
/// confondait et facturait une compétence choisie plus cher en VALEUR qu'une
/// compétence aléatoire — le LRB ne fait cette différence que sur les PSP.
/// Six des sept valeurs de hausse étaient également fausses.
/// </summary>
public class BaremeAmeliorationTests
{
    private static readonly BaremeAmelioration Lrb = BaremeAmelioration.ParDefaut();

    // ── Hausse de valeur : les 7 valeurs du LRB ──────────────────────────────

    [Theory]
    [InlineData(ImprovementType.SelectionPrimaire,   20_000)]
    [InlineData(ImprovementType.SelectionSecondaire, 40_000)]
    public void Hausse_Competences_SuitLeLrb(ImprovementType type, int attendu) =>
        Assert.Equal(attendu, Lrb.Hausse(type));

    [Theory]
    [InlineData(AffectedStat.Armure,        10_000)]
    [InlineData(AffectedStat.Mouvement,     20_000)]
    [InlineData(AffectedStat.CapacitePasse, 20_000)]
    [InlineData(AffectedStat.Agilite,       30_000)]
    public void Hausse_Caracteristiques_SuitLeLrb(AffectedStat stat, int attendu) =>
        Assert.Equal(attendu, Lrb.Hausse(ImprovementType.AmeliorationCarac, stat));

    [Fact]
    public void Hausse_Force_Vaut60k() =>
        Assert.Equal(60_000,
            Lrb.Hausse(ImprovementType.AmeliorationForceArmure, AffectedStat.Force));

    [Fact]
    public void Hausse_Armure_ViaForceArmure_Vaut10k() =>
        Assert.Equal(10_000,
            Lrb.Hausse(ImprovementType.AmeliorationForceArmure, AffectedStat.Armure));

    /// <summary>
    /// LE point corrigé : le hasard et le choix donnent la MÊME hausse de valeur.
    /// L'ancien code facturait 10k au hasard et 20k au choix.
    /// </summary>
    [Fact]
    public void Hausse_AleaEtChoix_SontIdentiques()
    {
        Assert.Equal(Lrb.Hausse(ImprovementType.SelectionPrimaire),
                     Lrb.Hausse(ImprovementType.AleaPrimaire));

        Assert.Equal(Lrb.Hausse(ImprovementType.SelectionSecondaire),
                     Lrb.Hausse(ImprovementType.AleaSecondaire));
    }

    // ── Surcoût Élite ────────────────────────────────────────────────────────

    [Fact]
    public void Hausse_CompetenceElite_Ajoute10k()
    {
        Assert.Equal(30_000, Lrb.Hausse(ImprovementType.SelectionPrimaire,   estElite: true));
        Assert.Equal(50_000, Lrb.Hausse(ImprovementType.SelectionSecondaire, estElite: true));
    }

    /// <summary>
    /// Contre-épreuve : le surcoût Élite ne concerne QUE les compétences. Une
    /// amélioration de caractéristique n'a pas de drapeau Élite ; sans ce test,
    /// un code qui ajouterait 10k partout passerait le test ci-dessus.
    /// </summary>
    [Fact]
    public void Hausse_Caracteristique_IgnoreLeDrapeauElite() =>
        Assert.Equal(60_000, Lrb.Hausse(
            ImprovementType.AmeliorationForceArmure, AffectedStat.Force, estElite: true));

    // ── Coût en PSP, par rang ────────────────────────────────────────────────

    [Theory]
    [InlineData(1, ImprovementType.AleaPrimaire,       3)]
    [InlineData(1, ImprovementType.SelectionPrimaire,  6)]
    [InlineData(1, ImprovementType.SelectionSecondaire, 10)]
    [InlineData(1, ImprovementType.AmeliorationCarac,  14)]
    [InlineData(6, ImprovementType.AleaPrimaire,      15)]
    [InlineData(6, ImprovementType.SelectionPrimaire, 30)]
    [InlineData(6, ImprovementType.SelectionSecondaire, 34)]
    [InlineData(6, ImprovementType.AmeliorationCarac, 38)]
    public void CoutPsp_SuitLeTableauDesAmeliorations(int rang, ImprovementType type, int attendu) =>
        Assert.Equal(attendu, Lrb.CoutPsp(type, rang));

    /// <summary>
    /// Le coût AUGMENTE avec le rang : c'est toute la logique du tableau, et un
    /// barème plat passerait les cas isolés ci-dessus rang par rang.
    /// </summary>
    [Fact]
    public void CoutPsp_CroitAvecLeRang()
    {
        var couts = Enumerable.Range(1, 6)
            .Select(r => Lrb.CoutPsp(ImprovementType.SelectionPrimaire, r))
            .ToList();

        Assert.Equal(couts.OrderBy(c => c), couts);
        Assert.True(couts.Last() > couts.First());
    }

    /// <summary>
    /// Une compétence secondaire tirée au hasard coûte le même prix qu'une
    /// secondaire choisie : le LRB n'a qu'une colonne pour la secondaire.
    /// </summary>
    [Fact]
    public void CoutPsp_SecondaireAleaEtChoix_SontIdentiques() =>
        Assert.Equal(Lrb.CoutPsp(ImprovementType.SelectionSecondaire, 3),
                     Lrb.CoutPsp(ImprovementType.AleaSecondaire, 3));

    /// <summary>Un rang hors tableau ne doit pas jeter, juste ne rien proposer.</summary>
    [Fact]
    public void CoutPsp_RangHorsTableau_RenvoieZero() =>
        Assert.Equal(0, Lrb.CoutPsp(ImprovementType.SelectionPrimaire, 99));

    // ── Résolution depuis la version de règles ───────────────────────────────

    [Fact]
    public void DeVersion_Null_RetombeSurLeLrb()
    {
        var bareme = BaremeAmelioration.DeVersion(null);
        Assert.Equal(60_000, bareme.Hausse(ImprovementType.AmeliorationForceArmure, AffectedStat.Force));
        Assert.Equal(3, bareme.CoutPsp(ImprovementType.AleaPrimaire, 1));
    }

    [Fact]
    public void DeVersion_UtiliseLesValeursDeLaVersion()
    {
        var version = new RulesVersion
        {
            Nom = "Maison", HaussePrincipale = 25_000, HausseForce = 70_000, SurcoutElite = 5_000
        };
        version.PaliersAmelioration.Add(new PalierAmeliorationPsp
        {
            Rang = 1, CoutAleaPrincipale = 2, CoutChoixPrincipale = 4,
            CoutSecondaire = 8, CoutCaracteristique = 12
        });

        var bareme = BaremeAmelioration.DeVersion(version);

        Assert.Equal(25_000, bareme.Hausse(ImprovementType.SelectionPrimaire));
        Assert.Equal(30_000, bareme.Hausse(ImprovementType.SelectionPrimaire, estElite: true));
        Assert.Equal(70_000, bareme.Hausse(ImprovementType.AmeliorationForceArmure, AffectedStat.Force));
        Assert.Equal(4, bareme.CoutPsp(ImprovementType.SelectionPrimaire, 1));
    }

    /// <summary>
    /// Une version jamais paramétrée n'a aucun palier : proposer 0 PSP partout
    /// serait pire que rien. Repli sur le LRB.
    /// </summary>
    [Fact]
    public void DeVersion_SansPalier_RetombeSurLesPaliersLrb()
    {
        var bareme = BaremeAmelioration.DeVersion(new RulesVersion { Nom = "Vierge" });
        Assert.Equal(3, bareme.CoutPsp(ImprovementType.AleaPrimaire, 1));
    }
}
