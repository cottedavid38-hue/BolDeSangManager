using BolDeSangManager.Data;
using BolDeSangManager.Data.Models;
using BolDeSangManager.Services;
using BolDeSangManager.Tests.Helpers;
using Microsoft.EntityFrameworkCore;

namespace BolDeSangManager.Tests;

/// <summary>
/// Backfill de la migration <c>RealignerCompteDansVeaDesLigues</c>.
///
/// Les ligues créées depuis le formulaire avaient perdu le drapeau
/// <c>CompteDansVea</c> (copie champ par champ incomplète) : leurs Fans dévoués
/// comptaient dans la VEA alors que les règles les en excluent. Le SQL est
/// rejoué ici mot pour mot — s'il change dans la migration, il doit changer ici,
/// et c'est voulu : c'est ce qui rend le test capable de la contredire.
///
/// ⚠️ La sûreté de ce backfill repose sur un fait produit, pas technique :
/// aucun écran n'expose ce drapeau au niveau LIGUE, donc une divergence ne peut
/// pas être un choix de commissaire. Si ce réglage est un jour ouvert par ligue,
/// ces tests doivent être relus avant de rejouer la même opération.
/// </summary>
public class RealignementCompteDansVeaTests : IDisposable
{
    private readonly TestDbFactory _factory = new();
    public void Dispose() => _factory.Dispose();

    /// <summary>SQL identique à celui de la migration.</summary>
    private const string SqlBackfill = @"
UPDATE LeagueStaffTypes
SET CompteDansVea = (
    SELECT st.CompteDansVea FROM StaffTypes st WHERE st.Id = LeagueStaffTypes.StaffTypeId
)
WHERE StaffTypeId IS NOT NULL
  AND CompteDansVea <> (
    SELECT st.CompteDansVea FROM StaffTypes st WHERE st.Id = LeagueStaffTypes.StaffTypeId
  );";

    [Fact]
    public async Task Backfill_RemetLesFansHorsVea_SansToucherAuResteNiAuxRegles()
    {
        await using var db = _factory.CreateContext();
        var (ligueId, fansId, cheerId) = await SeedLigueDesalignéeAsync(db);

        await db.Database.ExecuteSqlRawAsync(SqlBackfill);

        await using var relecture = _factory.CreateContext();
        var staff = await relecture.LeagueStaffTypes
            .Where(l => l.LeagueId == ligueId)
            .ToDictionaryAsync(l => l.Nom, l => l.CompteDansVea);

        // Le cas à réparer.
        Assert.False(staff["Fans dévoués"]);

        // CONTRE-ÉPREUVE : sans elle, un backfill qui écrase tout à false
        // passerait l'assertion ci-dessus. Les Cheerleaders comptent dans la
        // VEA et doivent le rester.
        Assert.True(staff["Cheerleaders"]);

        // Les RÈGLES ne bougent pas : le backfill lit la définition, il ne
        // l'écrit jamais.
        var regles = await relecture.StaffTypes
            .Where(s => s.Id == fansId || s.Id == cheerId)
            .ToDictionaryAsync(s => s.Nom, s => s.CompteDansVea);
        Assert.False(regles["Fans dévoués"]);
        Assert.True(regles["Cheerleaders"]);
    }

    /// <summary>
    /// Un staff DÉTACHÉ (créé dans la ligue, ou dont la définition a été
    /// supprimée — FK SetNull) n'a plus de référence : le backfill doit le
    /// laisser intact plutôt que lui inventer une valeur.
    /// </summary>
    [Fact]
    public async Task Backfill_IgnoreLeStaffSansDefinitionDOrigine()
    {
        await using var db = _factory.CreateContext();
        var (ligueId, _, _) = await SeedLigueDesalignéeAsync(db);

        db.LeagueStaffTypes.Add(new LeagueStaffType
        {
            LeagueId = ligueId, StaffTypeId = null,
            Nom = "Mascotte maison", Cout = 5_000, CompteDansVea = true
        });
        await db.SaveChangesAsync();

        await db.Database.ExecuteSqlRawAsync(SqlBackfill);

        await using var relecture = _factory.CreateContext();
        var orpheline = await relecture.LeagueStaffTypes
            .FirstAsync(l => l.LeagueId == ligueId && l.Nom == "Mascotte maison");
        Assert.True(orpheline.CompteDansVea);
    }

    /// <summary>
    /// Sur une base déjà saine, le backfill ne doit RIEN changer : c'est la
    /// garantie de non-régression au déploiement, et ce qui autorise à le jouer
    /// sur l'instance live sans redouter d'effet de bord.
    /// </summary>
    [Fact]
    public async Task Backfill_SurUneBaseSaine_NeChangeRien()
    {
        await using var db = _factory.CreateContext();
        var (ligueId, _, _) = await SeedLigueDesalignéeAsync(db);

        // On répare d'abord…
        await db.Database.ExecuteSqlRawAsync(SqlBackfill);
        var apres1 = await LireAsync(ligueId);

        // …puis on rejoue : aucun effet supplémentaire.
        await using var db2 = _factory.CreateContext();
        var lignesTouchees = await db2.Database.ExecuteSqlRawAsync(SqlBackfill);
        Assert.Equal(0, lignesTouchees);
        Assert.Equal(apres1, await LireAsync(ligueId));
    }

    private async Task<Dictionary<string, bool>> LireAsync(int ligueId)
    {
        await using var db = _factory.CreateContext();
        return await db.LeagueStaffTypes
            .Where(l => l.LeagueId == ligueId)
            .ToDictionaryAsync(l => l.Nom, l => l.CompteDansVea);
    }

    /// <summary>
    /// Reproduit l'état laissé par le bug : la ligue a été copiée depuis les
    /// règles, mais son drapeau est reparti au défaut C# (<c>true</c>) alors que
    /// la définition d'origine dit <c>false</c>.
    /// </summary>
    private static async Task<(int ligueId, int fansId, int cheerId)>
        SeedLigueDesalignéeAsync(ApplicationDbContext db)
    {
        var (game, rv) = await DataSeeder.SeedGameAsync(db);
        var commissaire = DataSeeder.CreateUser("com_backfill");
        db.Users.Add(commissaire);
        await db.SaveChangesAsync();

        var fans = new StaffDefinition
        {
            RulesVersionId = rv.Id, Nom = "Fans dévoués",
            Cout = 5_000, MinCreation = 1, MaxCreation = 3, CompteDansVea = false
        };
        var cheer = new StaffDefinition
        {
            RulesVersionId = rv.Id, Nom = "Cheerleaders",
            Cout = 10_000, MaxCreation = 6, CompteDansVea = true
        };
        db.StaffTypes.AddRange(fans, cheer);
        await db.SaveChangesAsync();

        var ligue = await DataSeeder.SeedLeagueAsync(db, game.Id, rv.Id, commissaire.Id);

        // ⚠️ SeedLeagueAsync matérialise DÉJÀ un staff de ligue, mais DÉTACHÉ
        // (StaffTypeId null). On rattache les deux lignes qui nous intéressent à
        // leur définition — c'est l'état réel d'une ligue copiée depuis les
        // règles — et on laisse les autres détachées : elles servent au test
        // « le backfill ignore le staff sans définition d'origine ».
        var lstFans = await db.LeagueStaffTypes
            .FirstAsync(l => l.LeagueId == ligue.Id && l.Nom == fans.Nom);
        lstFans.StaffTypeId   = fans.Id;
        lstFans.CompteDansVea = true;            // ← le bug

        var lstCheer = await db.LeagueStaffTypes
            .FirstAsync(l => l.LeagueId == ligue.Id && l.Nom == cheer.Nom);
        lstCheer.StaffTypeId   = cheer.Id;
        lstCheer.CompteDansVea = true;           // ← correct, doit le rester

        await db.SaveChangesAsync();

        return (ligue.Id, fans.Id, cheer.Id);
    }
}
