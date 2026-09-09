using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BolDeSangManager.Migrations
{
    /// <inheritdoc />
    public partial class AddBaremeAmelioration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "HausseAgilite",
                table: "RulesVersions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 30000);

            migrationBuilder.AddColumn<int>(
                name: "HausseArmure",
                table: "RulesVersions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 10000);

            migrationBuilder.AddColumn<int>(
                name: "HausseCapacitePasse",
                table: "RulesVersions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 20000);

            migrationBuilder.AddColumn<int>(
                name: "HausseForce",
                table: "RulesVersions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 60000);

            migrationBuilder.AddColumn<int>(
                name: "HausseMouvement",
                table: "RulesVersions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 20000);

            migrationBuilder.AddColumn<int>(
                name: "HaussePrincipale",
                table: "RulesVersions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 20000);

            migrationBuilder.AddColumn<int>(
                name: "HausseSecondaire",
                table: "RulesVersions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 40000);

            migrationBuilder.AddColumn<int>(
                name: "SurcoutElite",
                table: "RulesVersions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 10000);

            migrationBuilder.CreateTable(
                name: "PaliersAmelioration",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RulesVersionId = table.Column<int>(type: "INTEGER", nullable: false),
                    Rang = table.Column<int>(type: "INTEGER", nullable: false),
                    CoutAleaPrincipale = table.Column<int>(type: "INTEGER", nullable: false),
                    CoutChoixPrincipale = table.Column<int>(type: "INTEGER", nullable: false),
                    CoutSecondaire = table.Column<int>(type: "INTEGER", nullable: false),
                    CoutCaracteristique = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaliersAmelioration", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaliersAmelioration_RulesVersions_RulesVersionId",
                        column: x => x.RulesVersionId,
                        principalTable: "RulesVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PaliersAmelioration_RulesVersionId_Rang",
                table: "PaliersAmelioration",
                columns: new[] { "RulesVersionId", "Rang" },
                unique: true);

            // Backfill ONE-SHOT : le Tableau des Améliorations du LRB pour CHAQUE
            // version existante. Sans lui, les versions déjà en base n'ont aucun
            // palier — le barème retomberait sur le défaut codé, ce qui marche,
            // mais rien ne serait éditable en admin pour ces versions-là.
            //
            // Idempotent (NOT EXISTS) : une version qui aurait déjà ses paliers
            // n'est pas doublonnée, et l'index unique (version, rang) le garantit.
            migrationBuilder.Sql(@"
INSERT INTO PaliersAmelioration
    (RulesVersionId, Rang, CoutAleaPrincipale, CoutChoixPrincipale, CoutSecondaire, CoutCaracteristique)
SELECT v.Id, p.Rang, p.Alea, p.Choix, p.Sec, p.Carac
FROM RulesVersions v
CROSS JOIN (
    SELECT 1 AS Rang,  3 AS Alea,  6 AS Choix, 10 AS Sec, 14 AS Carac
    UNION ALL SELECT 2,  4,  8, 12, 16
    UNION ALL SELECT 3,  6, 12, 16, 20
    UNION ALL SELECT 4,  8, 16, 20, 24
    UNION ALL SELECT 5, 10, 20, 24, 28
    UNION ALL SELECT 6, 15, 30, 34, 38
) p
WHERE NOT EXISTS (
    SELECT 1 FROM PaliersAmelioration x
    WHERE x.RulesVersionId = v.Id AND x.Rang = p.Rang
);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PaliersAmelioration");

            migrationBuilder.DropColumn(
                name: "HausseAgilite",
                table: "RulesVersions");

            migrationBuilder.DropColumn(
                name: "HausseArmure",
                table: "RulesVersions");

            migrationBuilder.DropColumn(
                name: "HausseCapacitePasse",
                table: "RulesVersions");

            migrationBuilder.DropColumn(
                name: "HausseForce",
                table: "RulesVersions");

            migrationBuilder.DropColumn(
                name: "HausseMouvement",
                table: "RulesVersions");

            migrationBuilder.DropColumn(
                name: "HaussePrincipale",
                table: "RulesVersions");

            migrationBuilder.DropColumn(
                name: "HausseSecondaire",
                table: "RulesVersions");

            migrationBuilder.DropColumn(
                name: "SurcoutElite",
                table: "RulesVersions");
        }
    }
}
