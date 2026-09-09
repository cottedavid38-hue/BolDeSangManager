using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BolDeSangManager.Migrations
{
    /// <summary>
    /// Backfill ONE-SHOT : réaligne <c>LeagueStaffTypes.CompteDansVea</c> sur la
    /// définition de règles dont la ligue a été copiée.
    ///
    /// Pourquoi c'était faux : la création de ligue a DEUX chemins de copie du
    /// staff. <c>StaffService.CopierVersLigueAsync</c> (staff par défaut)
    /// transportait bien le drapeau ; la paire <c>Ligues/Creer.razor</c> +
    /// <c>LeagueService.CreerLigueAsync</c> — celle qu'emprunte le formulaire dès
    /// que le commissaire touche au staff — l'avait OUBLIÉ. Le champ repartait
    /// donc au défaut C# (<c>true</c>) et les Fans dévoués, exclus de la VEA dans
    /// les règles, la RÉINTÉGRAIENT : deux ligues, deux calculs de VEA, sans rien
    /// à l'écran pour l'expliquer.
    ///
    /// Pourquoi l'écrasement est SÛR — et ce serait faux sans cet argument : le
    /// drapeau n'est réglable que dans l'Admin des règles. Aucun écran n'expose
    /// « compte dans la VEA » au niveau ligue (le formulaire de ligue ne montre
    /// que coût / bornes / actif), donc aucune divergence ne peut résulter d'un
    /// choix de commissaire : toute valeur différente de sa définition d'origine
    /// est le bug, jamais une décision. Le jour où ce réglage sera ouvert par
    /// ligue, ce raisonnement tombe — ne pas rejouer ce backfill tel quel.
    ///
    /// Portée volontairement étroite : seules les lignes encore RATTACHÉES à une
    /// définition (<c>StaffTypeId IS NOT NULL</c>) sont touchées. Un staff créé
    /// directement dans une ligue, ou dont la définition a été supprimée (FK
    /// SetNull), n'a plus de référence — l'écraser avec une valeur devinée serait
    /// pire que de le laisser tel quel.
    ///
    /// ONE-SHOT : la correction de code accompagne cette migration, donc aucune
    /// ligue créée ensuite ne peut diverger à nouveau. Ne jamais transformer ce
    /// backfill en re-dérivation jouée à chaque démarrage : un backfill qui se
    /// rejoue entre en guerre avec toute modification ultérieure.
    /// </summary>
    public partial class RealignerCompteDansVeaDesLigues : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
UPDATE LeagueStaffTypes
SET CompteDansVea = (
    SELECT st.CompteDansVea FROM StaffTypes st WHERE st.Id = LeagueStaffTypes.StaffTypeId
)
WHERE StaffTypeId IS NOT NULL
  AND CompteDansVea <> (
    SELECT st.CompteDansVea FROM StaffTypes st WHERE st.Id = LeagueStaffTypes.StaffTypeId
  );");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Aucun retour en arrière : les valeurs d'avant étaient précisément
            // celles qui étaient fausses. Les restaurer remettrait les Fans
            // dévoués dans la VEA des ligues concernées.
        }
    }
}
