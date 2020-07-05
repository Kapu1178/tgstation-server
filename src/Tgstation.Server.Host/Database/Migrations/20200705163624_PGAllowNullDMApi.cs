﻿using Microsoft.EntityFrameworkCore.Migrations;
using System;

namespace Tgstation.Server.Host.Database.Migrations
{
	/// <summary>
	/// Update models for making the DMAPI optional for PostgresSQL.
	/// </summary>
	public partial class PGAllowNullDMApi : Migration
	{
		/// <inheritdoc />
		protected override void Up(MigrationBuilder migrationBuilder)
		{
			if (migrationBuilder == null)
				throw new ArgumentNullException(nameof(migrationBuilder));

			migrationBuilder.AddColumn<bool>(
				name: "RequireDMApiValidation",
				table: "DreamMakerSettings",
				nullable: false,
				defaultValue: true);

			migrationBuilder.AlterColumn<int>(
				name: "MinimumSecurityLevel",
				table: "CompileJobs",
				nullable: true,
				oldClrType: typeof(int),
				oldType: "integer");
		}

		/// <inheritdoc />
		protected override void Down(MigrationBuilder migrationBuilder)
		{
			if (migrationBuilder == null)
				throw new ArgumentNullException(nameof(migrationBuilder));

			migrationBuilder.DropColumn(
				name: "RequireDMApiValidation",
				table: "DreamMakerSettings");

			migrationBuilder.AlterColumn<int>(
				name: "MinimumSecurityLevel",
				table: "CompileJobs",
				type: "integer",
				nullable: false,
				oldClrType: typeof(int),
				oldNullable: true);
		}
	}
}
