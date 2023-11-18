﻿using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Tgstation.Server.Api.Models;
using Tgstation.Server.Api.Models.Internal;
using Tgstation.Server.Host.Components.Deployment;
using Tgstation.Server.Host.IO;

namespace Tgstation.Server.Host.Components.Engine
{
	/// <summary>
	/// Implementation of <see cref="IEngineInstallation"/> for <see cref="EngineType.OpenDream"/>.
	/// </summary>
	sealed class OpenDreamInstallation : EngineInstallationBase
	{
		/// <inheritdoc />
		public override EngineVersion Version { get; }

		/// <inheritdoc />
		public override string ServerExePath { get; }

		/// <inheritdoc />
		public override string CompilerExePath { get; }

		/// <inheritdoc />
		public override bool PromptsForNetworkAccess => false;

		/// <inheritdoc />
		public override bool HasStandardOutput => true;

		/// <inheritdoc />
		public override bool PreferFileLogging => true;

		/// <inheritdoc />
		public override Task InstallationTask { get; }

		/// <summary>
		/// The <see cref="IIOManager"/> for the <see cref="OpenDreamInstallation"/>.
		/// </summary>
		readonly IIOManager ioManager;

		/// <summary>
		/// Initializes a new instance of the <see cref="OpenDreamInstallation"/> class.
		/// </summary>
		/// <param name="ioManager">The value of <see cref="ioManager"/>.</param>
		/// <param name="serverExePath">The value of <see cref="ServerExePath"/>.</param>
		/// <param name="compilerExePath">The value of <see cref="CompilerExePath"/>.</param>
		/// <param name="installationTask">The value of <see cref="InstallationTask"/>.</param>
		/// <param name="version">The value of <see cref="Version"/>.</param>
		public OpenDreamInstallation(
			IIOManager ioManager,
			string serverExePath,
			string compilerExePath,
			Task installationTask,
			EngineVersion version)
		{
			this.ioManager = ioManager ?? throw new ArgumentNullException(nameof(ioManager));
			ServerExePath = serverExePath ?? throw new ArgumentNullException(nameof(serverExePath));
			CompilerExePath = compilerExePath ?? throw new ArgumentNullException(nameof(compilerExePath));
			InstallationTask = installationTask ?? throw new ArgumentNullException(nameof(installationTask));
			Version = version ?? throw new ArgumentNullException(nameof(version));

			if (version.Engine.Value != EngineType.OpenDream)
				throw new ArgumentException($"Invalid EngineType: {version.Engine.Value}", nameof(version));
		}

		/// <inheritdoc />
		public override string FormatServerArguments(
			IDmbProvider dmbProvider,
			IReadOnlyDictionary<string, string> parameters,
			DreamDaemonLaunchParameters launchParameters,
			string logFilePath)
		{
			ArgumentNullException.ThrowIfNull(dmbProvider);
			ArgumentNullException.ThrowIfNull(parameters);
			ArgumentNullException.ThrowIfNull(launchParameters);

			var parametersString = EncodeParameters(parameters, launchParameters);

			var loggingEnabled = logFilePath != null;
			var arguments = $"--cvar {(loggingEnabled ? $"log.path=\"{ioManager.GetDirectoryName(logFilePath)}\" --cvar log.format=\"{ioManager.GetFileName(logFilePath)}\"" : "log.enabled=false")} --cvar log.runtimelog=false --cvar net.port={launchParameters.Port.Value} --cvar opendream.topic_port=0 --cvar opendream.world_params=\"{parametersString}\" --cvar opendream.json_path=\"./{dmbProvider.DmbName}\"";
			return arguments;
		}

		/// <inheritdoc />
		public override string FormatCompilerArguments(string dmePath)
			=> $"--suppress-unimplemented --notices-enabled \"{dmePath ?? throw new ArgumentNullException(nameof(dmePath))}\"";
	}
}
