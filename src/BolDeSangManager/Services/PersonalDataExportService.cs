using System.Text.Json;
using System.Text.Json.Serialization;
using BolDeSangManager.Data;
using BolDeSangManager.Helpers;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace BolDeSangManager.Services;

/// <summary>
/// Export RGPD : le dossier complet d'un coach, à sa demande (droit d'accès,
/// article 15 du RGPD).
///
/// Remplace l'export fourni par le squelette ASP.NET Identity, qui ne sortait
/// que les propriétés portant l'attribut <c>[PersonalData]</c> — soit, sur ce
/// projet, l'e-mail et deux drapeaux techniques. Ni le pseudo de coach, ni les
/// équipes, ni les matchs n'en faisaient partie, alors que c'est justement ce
/// que l'association détient sur la personne.
///
/// Le principe retenu : tout ce qui est rattaché au compte et que l'intéressé
/// peut légitimement réclamer. En revanche, on ne sort PAS les données des
/// autres coaches — un match a deux camps, on se limite donc au point de vue du
/// demandeur (son équipe, son score, ses joueurs) sans détailler l'adversaire
/// au-delà de son nom d'équipe public.
/// </summary>
public class PersonalDataExportService(
    ApplicationDbContext db,
    UserManager<ApplicationUser> userManager)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Construit le dossier et le sérialise en JSON lisible (indenté, accents
    /// non échappés). Retourne null si le compte n'existe pas.
    /// </summary>
    public async Task<byte[]?> ExporterJsonAsync(string userId)
    {
        var dossier = await ConstruireDossierAsync(userId);
        if (dossier is null) return null;

        return JsonSerializer.SerializeToUtf8Bytes(dossier, JsonOptions);
    }

    /// <summary>Nom de fichier proposé au téléchargement, daté du jour.</summary>
    public static string NomFichier(string pseudo)
    {
        var sain = new string((pseudo ?? "coach")
            .Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray())
            .Trim('-');
        if (string.IsNullOrEmpty(sain)) sain = "coach";

        return $"mes-donnees-{sain}-{DateTime.UtcNow:yyyy-MM-dd}.json";
    }

    public async Task<DossierPersonnel?> ConstruireDossierAsync(string userId)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return null;

        var roles = await userManager.GetRolesAsync(user);

        // AsNoTracking + AsSplitQuery : un export charge plusieurs collections
        // imbriquées, la jointure unique produirait un produit croisé (cf. la
        // note SplitQuery dans Program.cs).
        var equipes = await db.Teams.AsNoTracking().AsSplitQuery()
            .Where(t => t.CoachId == userId)
            .Include(t => t.TeamType)
            // RulesVersion + améliorations : la valeur d'un joueur est CALCULÉE
            // (coût du poste + hausses du barème). Sans ces Include, le fichier
            // exporté annoncerait des valeurs fausses sans aucune erreur.
            .Include(t => t.League).ThenInclude(l => l.RulesVersion).ThenInclude(v => v.PaliersAmelioration)
            .Include(t => t.Joueurs).ThenInclude(j => j.PlayerPosition)
            .Include(t => t.Joueurs).ThenInclude(j => j.Improvements).ThenInclude(i => i.Skill)
            .OrderBy(t => t.CreeLe)
            .ToListAsync();

        var equipeIds = equipes.Select(e => e.Id).ToList();

        var matchs = await db.Matches.AsNoTracking().AsSplitQuery()
            .Where(m => equipeIds.Contains(m.EquipeDomicileId) || equipeIds.Contains(m.EquipeExterieurId))
            .Include(m => m.EquipeDomicile)
            .Include(m => m.EquipeExterieur)
            .OrderBy(m => m.Ronde)
            .ToListAsync();

        var liguesGerees = await db.Leagues.AsNoTracking()
            .Where(l => l.CommissaireId == userId)
            .Include(l => l.Game)
            .OrderBy(l => l.Nom)
            .ToListAsync();

        var feuillesSaisies = await db.MatchSheets.AsNoTracking()
            .Where(f => f.SaisiParId == userId)
            .Include(f => f.Match).ThenInclude(m => m.EquipeDomicile)
            .Include(f => f.Match).ThenInclude(m => m.EquipeExterieur)
            .OrderBy(f => f.SaisiLe)
            .ToListAsync();

        return new DossierPersonnel(
            ExporteLe: DateTime.UtcNow,
            Explication:
                "Ce fichier contient l'ensemble des informations que BolDeSang Manager " +
                "conserve à votre sujet. Les équipes adverses n'y figurent que par leur " +
                "nom, public au sein de la ligue : leurs données appartiennent à leurs coaches.",
            Compte: new CompteExport(
                Pseudo: user.PseudoCoach,
                Email: user.Email ?? "",
                EmailConfirme: user.EmailConfirmed,
                Telephone: user.PhoneNumber,
                InscritLe: user.CreeLe,
                Roles: [.. roles],
                CompteSupprime: user.EstSupprime,
                SupprimeLe: user.SupprimeLe,
                // On signale l'EXISTENCE de l'abonnement iCalendar sans recopier
                // le jeton : c'est un secret équivalent à un mot de passe, et un
                // fichier d'export circule (téléphone, pièce jointe, sauvegarde).
                // Le coach retrouve son URL complète sur /profil, et peut la
                // régénérer. Le droit d'accès porte ici sur le fait qu'un lien
                // existe, pas sur la valeur du secret.
                AbonnementCalendrierActif: !string.IsNullOrEmpty(user.JetonCalendrier)),
            Equipes: [.. equipes.Select(e => new EquipeExport(
                Nom: e.Nom,
                Type: e.TeamType?.Nom ?? "",
                Ligue: e.League?.Nom ?? "Hors ligue",
                CreeeLe: e.CreeLe,
                Tresorerie: e.Tresorerie,
                PointsLigue: e.PointsLigue,
                MatchsJoues: e.NombreMatchsJoues,
                Victoires: e.NombreVictoires,
                Nuls: e.NombreNuls,
                Defaites: e.NombreDefaites,
                TouchdownsMarques: e.TouchdownsMarques,
                TouchdownsConcedes: e.TouchdownsConcedes,
                Joueurs: [.. e.Joueurs.OrderBy(j => j.Numero).Select(j => new JoueurExport(
                    Numero: j.Numero,
                    Nom: j.Nom,
                    Poste: j.PlayerPosition?.Nom ?? "",
                    PointsStarPlayer: j.PointsStarPlayer,
                    // Champ CONSERVÉ dans le format du fichier, alimenté par le
                    // calcul plutôt que par la colonne.
                    ValeurActuelle: ValeurJoueurCalculator.Calculer(j, BaremeAmelioration.DeVersion(e.League?.RulesVersion)),
                    RecruteLe: j.RecruteLe,
                    EstMort: j.EstMort,
                    EstRetraite: j.EstRetraite))]))],
            Matchs: [.. matchs.Select(m =>
            {
                var chezMoi = equipeIds.Contains(m.EquipeDomicileId);
                return new MatchExport(
                    Ronde: m.Ronde,
                    Statut: m.Statut.ToString(),
                    DateJouee: m.DateJouee,
                    MonEquipe: chezMoi ? m.EquipeDomicile?.Nom ?? "" : m.EquipeExterieur?.Nom ?? "",
                    Adversaire: chezMoi ? m.EquipeExterieur?.Nom ?? "" : m.EquipeDomicile?.Nom ?? "",
                    ADomicile: chezMoi,
                    MonScore: chezMoi ? m.ScoreDomicile : m.ScoreExterieur,
                    ScoreAdverse: chezMoi ? m.ScoreExterieur : m.ScoreDomicile);
            })],
            LiguesGerees: [.. liguesGerees.Select(l => new LigueExport(
                Nom: l.Nom,
                Jeu: l.Game?.Nom ?? "",
                Statut: l.Statut.ToString()))],
            FeuillesDeMatchSaisies: [.. feuillesSaisies.Select(f => new FeuilleExport(
                SaisieLe: f.SaisiLe,
                Rencontre: $"{f.Match?.EquipeDomicile?.Nom} — {f.Match?.EquipeExterieur?.Nom}",
                TouchdownsDomicile: f.TouchdownsDomicile,
                TouchdownsExterieur: f.TouchdownsExterieur,
                ValideeParLeCommissaire: f.ValideParCommissaire))]);
    }

    // Types de l'export. Volontairement plats et nommés en français : le
    // destinataire est un coach, pas un développeur.

    public record DossierPersonnel(
        DateTime ExporteLe,
        string Explication,
        CompteExport Compte,
        List<EquipeExport> Equipes,
        List<MatchExport> Matchs,
        List<LigueExport> LiguesGerees,
        List<FeuilleExport> FeuillesDeMatchSaisies);

    public record CompteExport(
        string Pseudo,
        string Email,
        bool EmailConfirme,
        string? Telephone,
        DateTime InscritLe,
        List<string> Roles,
        bool CompteSupprime,
        DateTime? SupprimeLe,
        bool AbonnementCalendrierActif);

    public record EquipeExport(
        string Nom,
        string Type,
        string Ligue,
        DateTime CreeeLe,
        int Tresorerie,
        int PointsLigue,
        int MatchsJoues,
        int Victoires,
        int Nuls,
        int Defaites,
        int TouchdownsMarques,
        int TouchdownsConcedes,
        List<JoueurExport> Joueurs);

    public record JoueurExport(
        int Numero,
        string Nom,
        string Poste,
        int PointsStarPlayer,
        int ValeurActuelle,
        DateTime RecruteLe,
        bool EstMort,
        bool EstRetraite);

    public record MatchExport(
        int Ronde,
        string Statut,
        DateTime? DateJouee,
        string MonEquipe,
        string Adversaire,
        bool ADomicile,
        int? MonScore,
        int? ScoreAdverse);

    public record LigueExport(string Nom, string Jeu, string Statut);

    public record FeuilleExport(
        DateTime SaisieLe,
        string Rencontre,
        int TouchdownsDomicile,
        int TouchdownsExterieur,
        bool ValideeParLeCommissaire);
}
