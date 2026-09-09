using BolDeSangManager.Data.Models;
using BolDeSangManager.Services;
using BolDeSangManager.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using UglyToad.PdfPig;

namespace BolDeSangManager.Tests;

/// <summary>
/// La VEA doit avoir UNE seule source de vérité.
///
/// Le staff est porté par la collection <see cref="Team.Staff"/> depuis le dev
/// staff ; les colonnes <c>FansDevoues</c>, <c>NombreRelances</c>… de
/// <see cref="Team"/> sont des vestiges que le modèle documente explicitement
/// comme « ne pas lire dans du code nouveau ». Une équipe réelle a donc son
/// staff dans <c>Staff</c> et des colonnes historiques à zéro.
///
/// Ces tests vérifient que la feuille d'équipe PDF affiche la MÊME VEA que
/// <see cref="TeamService.CalculerVEA"/>, qui est la valeur montrée à l'écran.
/// </summary>
public class VeaCoherenceTests : IDisposable
{
    private readonly TestDbFactory _factory = new();
    public void Dispose() => _factory.Dispose();

    /// <summary>Extrait tout le texte du PDF — seule preuve honnête de ce qui est imprimé.</summary>
    private static string LireTexte(byte[] pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        return string.Join("\n", doc.GetPages().Select(p => p.Text));
    }

    /// <summary>
    /// Équipe réaliste d'après le dev staff : tout le staff dans <c>Staff</c>,
    /// colonnes historiques laissées à zéro comme pour une équipe créée
    /// aujourd'hui.
    /// </summary>
    private static Team EquipeAvecStaffModerne()
    {
        var teamType = new TeamType { Nom = "Nains", CoutRelance = 70_000 };

        var equipe = new Team
        {
            Nom = "Les Testeurs",
            Tresorerie = 50_000,
            TeamType = teamType,
            // Colonnes HISTORIQUES volontairement à zéro : c'est l'état d'une
            // équipe moderne. Si le PDF lit ces champs, il affichera une VEA
            // amputée de tout le staff.
            NombreRelances = 0,
            FansDevoues = 0,
            NombreCoachsAssistants = 0,
            NombreCheerleaders = 0,
            Apothicaire = false
        };

        equipe.Joueurs.Add(new TeamPlayer
        {
            Numero = 1,
            Nom = "Grim",
            Blessures = [],
            // La valeur du joueur vient de son POSTE (colonne ValeurActuelle
            // supprimée) : 80k pour l'équipe de référence.
            PlayerPosition = new PlayerPosition { Nom = "Blitzer", Cout = 80_000 }
        });

        // 2 relances tarifées par la race : 2 × 70 000 = 140 000
        equipe.Staff.Add(new TeamStaff
        {
            Quantite = 2,
            LeagueStaffType = new LeagueStaffType { Nom = "Relances", CoutDepuisTypeEquipe = true }
        });
        // 3 fans dévoués : 3 × 10 000 = 30 000
        equipe.Staff.Add(new TeamStaff
        {
            Quantite = 3,
            LeagueStaffType = new LeagueStaffType { Nom = "Fans dévoués", Cout = 10_000 }
        });
        // 1 apothicaire : 50 000
        equipe.Staff.Add(new TeamStaff
        {
            Quantite = 1,
            LeagueStaffType = new LeagueStaffType { Nom = "Apothicaire", Cout = 50_000 }
        });

        return equipe;
    }

    /// <summary>
    /// Garde-fou du test lui-même : si cette valeur changeait, l'assertion
    /// principale ne prouverait plus rien.
    /// </summary>
    [Fact]
    public async Task CalculerVEA_SurEquipeDeReference_Vaut300k()
    {
        await using var db = _factory.CreateContext();
        var svc = new TeamService(db, NullLogger<TeamService>.Instance);

        // 80k joueur + 140k relances + 30k fans + 50k apothicaire
        Assert.Equal(300_000, svc.CalculerVEA(EquipeAvecStaffModerne()));
    }

    [Fact]
    public async Task FeuilleEquipePdf_AfficheLaMemeVeaQueLEcran()
    {
        await using var db = _factory.CreateContext();
        var svc = new TeamService(db, NullLogger<TeamService>.Instance);
        var equipe = EquipeAvecStaffModerne();

        var attendue = svc.CalculerVEA(equipe);          // 300 000 → « 300k po »
        var texte = LireTexte(new PdfService().GenererFeuilleEquipe(equipe, false));

        Assert.Contains($"{attendue / 1000}k po", texte);
    }

    /// <summary>
    /// Test discriminant : prouve que l'assertion ci-dessus échouerait bien
    /// pour la bonne raison. Le PDF ne doit PAS afficher la VEA calculée à
    /// partir des seules colonnes historiques (ici 80k, le joueur seul).
    /// </summary>
    [Fact]
    public async Task FeuilleEquipePdf_NAffichePasLaVeaDesColonnesHistoriques()
    {
        await using var db = _factory.CreateContext();
        var equipe = EquipeAvecStaffModerne();

        var texte = LireTexte(new PdfService().GenererFeuilleEquipe(equipe, false));

        // 80k = joueur seul, staff ignoré : la valeur fausse d'avant correction.
        Assert.DoesNotContain("80k po", texte);
        await Task.CompletedTask;
    }

    /// <summary>
    /// Le staff est une liste OUVERTE : l'association peut ajouter autant de
    /// types qu'elle veut. Les cases de statistiques doivent alors se répartir
    /// sur plusieurs rangées SANS couper les libellés — le défaut observé était
    /// « Cheerleaders » imprimé « Cheerlead / ers » et « Apothicaire » en
    /// « Apothicair / e » dès qu'on dépassait ~9 cases.
    ///
    /// PdfPig restitue le texte dans l'ordre de tracé : un libellé coupé
    /// apparaît donc scindé dans la chaîne extraite, ce que ce test détecte.
    /// </summary>
    [Fact]
    public void FeuilleEquipePdf_AvecBeaucoupDeStaff_NeCoupePasLesLibelles()
    {
        var equipe = EquipeAvecStaffModerne();

        // On pousse jusqu'à 12 cases au total, au-delà de ce qu'une seule
        // rangée peut tenir sur A4 portrait.
        foreach (var (nom, cout) in new[]
                 {
                     ("Cheerleaders", 10_000),
                     ("Coachs assistants", 10_000),
                     ("Sorcier de touche", 30_000),
                     ("Chef de bande vétéran", 20_000),
                     ("Masseur itinérant", 20_000),
                 })
        {
            equipe.Staff.Add(new TeamStaff
            {
                Quantite = 1,
                LeagueStaffType = new LeagueStaffType { Nom = nom, Cout = cout }
            });
        }

        var texte = LireTexte(new PdfService().GenererFeuilleEquipe(equipe, false));

        // Les libellés complets doivent apparaître d'un seul tenant.
        Assert.Contains("Cheerleaders", texte);
        Assert.Contains("Apothicaire", texte);
        Assert.Contains("Coachs assistants", texte);
        // Et la VEA ne doit pas être scindée entre le montant et son unité.
        Assert.Contains("k po", texte);
    }
}
