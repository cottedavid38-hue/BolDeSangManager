using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BolDeSangManager.Migrations
{
    /// <inheritdoc />
    public partial class RenommerXpEnPsp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ⚠️ Le scaffolder EF avait écrit DropTable + CreateTable : il ne
            // reconnaît PAS un renommage d'ENTITÉ et aurait DÉTRUIT toutes les
            // corrections de PSP déjà enregistrées (table vide en dev, pas
            // forcément en production). Remplacé par un vrai renommage, qui
            // préserve les lignes, les index et les clés étrangères.
            migrationBuilder.RenameTable(
                name: "XpCorrections",
                newName: "PspCorrections");

            migrationBuilder.RenameColumn(
                name: "XpParTouchdown",
                table: "RulesVersions",
                newName: "PspParTouchdown");

            migrationBuilder.RenameColumn(
                name: "XpParPasse",
                table: "RulesVersions",
                newName: "PspParPasse");

            migrationBuilder.RenameColumn(
                name: "XpParInterception",
                table: "RulesVersions",
                newName: "PspParInterception");

            migrationBuilder.RenameColumn(
                name: "XpParElimination",
                table: "RulesVersions",
                newName: "PspParElimination");

            migrationBuilder.RenameColumn(
                name: "XpParDeviation",
                table: "RulesVersions",
                newName: "PspParDeviation");

            migrationBuilder.RenameColumn(
                name: "XpParAgression",
                table: "RulesVersions",
                newName: "PspParAgression");

            migrationBuilder.RenameColumn(
                name: "XpBonusMvp",
                table: "RulesVersions",
                newName: "PspBonusMvp");

            migrationBuilder.RenameColumn(
                name: "XpDepensee",
                table: "PlayerImprovements",
                newName: "PspDepensee");

            migrationBuilder.RenameColumn(
                name: "XpParTouchdown",
                table: "Leagues",
                newName: "PspParTouchdown");

            migrationBuilder.RenameColumn(
                name: "XpParPasse",
                table: "Leagues",
                newName: "PspParPasse");

            migrationBuilder.RenameColumn(
                name: "XpParInterception",
                table: "Leagues",
                newName: "PspParInterception");

            migrationBuilder.RenameColumn(
                name: "XpParElimination",
                table: "Leagues",
                newName: "PspParElimination");

            migrationBuilder.RenameColumn(
                name: "XpParDeviation",
                table: "Leagues",
                newName: "PspParDeviation");

            migrationBuilder.RenameColumn(
                name: "XpParAgression",
                table: "Leagues",
                newName: "PspParAgression");

            migrationBuilder.RenameColumn(
                name: "XpBonusMvp",
                table: "Leagues",
                newName: "PspBonusMvp");

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameTable(
                name: "PspCorrections",
                newName: "XpCorrections");

            migrationBuilder.RenameColumn(
                name: "PspParTouchdown",
                table: "RulesVersions",
                newName: "XpParTouchdown");

            migrationBuilder.RenameColumn(
                name: "PspParPasse",
                table: "RulesVersions",
                newName: "XpParPasse");

            migrationBuilder.RenameColumn(
                name: "PspParInterception",
                table: "RulesVersions",
                newName: "XpParInterception");

            migrationBuilder.RenameColumn(
                name: "PspParElimination",
                table: "RulesVersions",
                newName: "XpParElimination");

            migrationBuilder.RenameColumn(
                name: "PspParDeviation",
                table: "RulesVersions",
                newName: "XpParDeviation");

            migrationBuilder.RenameColumn(
                name: "PspParAgression",
                table: "RulesVersions",
                newName: "XpParAgression");

            migrationBuilder.RenameColumn(
                name: "PspBonusMvp",
                table: "RulesVersions",
                newName: "XpBonusMvp");

            migrationBuilder.RenameColumn(
                name: "PspDepensee",
                table: "PlayerImprovements",
                newName: "XpDepensee");

            migrationBuilder.RenameColumn(
                name: "PspParTouchdown",
                table: "Leagues",
                newName: "XpParTouchdown");

            migrationBuilder.RenameColumn(
                name: "PspParPasse",
                table: "Leagues",
                newName: "XpParPasse");

            migrationBuilder.RenameColumn(
                name: "PspParInterception",
                table: "Leagues",
                newName: "XpParInterception");

            migrationBuilder.RenameColumn(
                name: "PspParElimination",
                table: "Leagues",
                newName: "XpParElimination");

            migrationBuilder.RenameColumn(
                name: "PspParDeviation",
                table: "Leagues",
                newName: "XpParDeviation");

            migrationBuilder.RenameColumn(
                name: "PspParAgression",
                table: "Leagues",
                newName: "XpParAgression");

            migrationBuilder.RenameColumn(
                name: "PspBonusMvp",
                table: "Leagues",
                newName: "XpBonusMvp");


        }
    }
}
