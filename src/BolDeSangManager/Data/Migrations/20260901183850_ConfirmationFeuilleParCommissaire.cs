using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BolDeSangManager.Migrations
{
    /// <inheritdoc />
    public partial class ConfirmationFeuilleParCommissaire : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ConfirmeeParCommissaire",
                table: "MatchSheets",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ConfirmeeParCommissaireLe",
                table: "MatchSheets",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConfirmeeParCommissaire",
                table: "MatchSheets");

            migrationBuilder.DropColumn(
                name: "ConfirmeeParCommissaireLe",
                table: "MatchSheets");
        }
    }
}
