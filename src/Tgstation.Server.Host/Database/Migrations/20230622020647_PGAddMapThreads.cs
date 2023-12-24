﻿using System;

using Microsoft.EntityFrameworkCore.Migrations;

namespace Tgstation.Server.Host.Database.Migrations
{
	/// <summary>
	/// Adds the MapThreads DreamDaemonSettings column for PostgresSQL.
	/// </summary>
	public partial class PGAddMapThreads : Migration
	{
		/// <inheritdoc />
		protected override void Up(MigrationBuilder migrationBuilder)
		{
			ArgumentNullException.ThrowIfNull(migrationBuilder);

			migrationBuilder.AddColumn<long>(
				name: "MapThreads",
				table: "DreamDaemonSettings",
				type: "bigint",
				nullable: false,
				defaultValue: 0L);
		}

		/// <inheritdoc />
		protected override void Down(MigrationBuilder migrationBuilder)
		{
			ArgumentNullException.ThrowIfNull(migrationBuilder);

			migrationBuilder.DropColumn(
				name: "MapThreads",
				table: "DreamDaemonSettings");
		}
	}
}
