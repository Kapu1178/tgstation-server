﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Tgstation.Server.Api.Models;
using Tgstation.Server.Common.Extensions;
using Tgstation.Server.Host.Configuration;
using Tgstation.Server.Host.IO;
using Tgstation.Server.Host.Jobs;
using Tgstation.Server.Host.System;
using Tgstation.Server.Host.Utils;

namespace Tgstation.Server.Host.Components.Byond
{
	/// <summary>
	/// <see cref="IByondInstaller"/> for windows systems.
	/// </summary>
	sealed class WindowsByondInstaller : ByondInstallerBase, IDisposable
	{
		/// <summary>
		/// Directory to byond installation configuration.
		/// </summary>
		const string ByondConfigDirectory = "byond/cfg";

		/// <summary>
		/// BYOND's DreamDaemon config file.
		/// </summary>
		const string ByondDreamDaemonConfigFilename = "daemon.txt";

		/// <summary>
		/// Setting to add to <see cref="ByondDreamDaemonConfigFilename"/> to suppress an invisible user prompt for running a trusted mode .dmb.
		/// </summary>
		const string ByondNoPromptTrustedMode = "trusted-check 0";

		/// <summary>
		/// The directory that contains the BYOND directx redistributable.
		/// </summary>
		const string ByondDXDir = "byond/directx";

		/// <summary>
		/// The file TGS uses to determine if dd.exe has been firewalled.
		/// </summary>
		const string TgsFirewalledDDFile = "TGSFirewalledDD";

		/// <summary>
		/// The first version of BYOND to ship with dd.exe on the Windows build.
		/// </summary>
		public static Version DDExeVersion => new (515, 1598);

		/// <inheritdoc />
		public override string DreamMakerName => "dm.exe";

		/// <inheritdoc />
		public override string PathToUserByondFolder { get; }

		/// <inheritdoc />
		protected override string ByondRevisionsUrlTemplate => "https://www.byond.com/download/build/{0}/{0}.{1}_byond.zip";

		/// <summary>
		/// The <see cref="IProcessExecutor"/> for the <see cref="WindowsByondInstaller"/>.
		/// </summary>
		readonly IProcessExecutor processExecutor;

		/// <summary>
		/// The <see cref="GeneralConfiguration"/> for the <see cref="WindowsByondInstaller"/>.
		/// </summary>
		readonly GeneralConfiguration generalConfiguration;

		/// <summary>
		/// The <see cref="SemaphoreSlim"/> for the <see cref="WindowsByondInstaller"/>.
		/// </summary>
		readonly SemaphoreSlim semaphore;

		/// <summary>
		/// If DirectX was installed.
		/// </summary>
		bool installedDirectX;

		/// <summary>
		/// Initializes a new instance of the <see cref="WindowsByondInstaller"/> class.
		/// </summary>
		/// <param name="processExecutor">The value of <see cref="processExecutor"/>.</param>
		/// <param name="generalConfigurationOptions">The <see cref="IOptions{TOptions}"/> containing the value of <see cref="generalConfiguration"/>.</param>
		/// <param name="ioManager">The <see cref="IIOManager"/> for the <see cref="ByondInstallerBase"/>.</param>
		/// <param name="fileDownloader">The <see cref="IFileDownloader"/> for the <see cref="ByondInstallerBase"/>.</param>
		/// <param name="logger">The <see cref="ILogger"/> for the <see cref="ByondInstallerBase"/>.</param>
		public WindowsByondInstaller(
			IProcessExecutor processExecutor,
			IIOManager ioManager,
			IFileDownloader fileDownloader,
			IOptions<GeneralConfiguration> generalConfigurationOptions,
			ILogger<WindowsByondInstaller> logger)
			: base(ioManager, fileDownloader, logger)
		{
			this.processExecutor = processExecutor ?? throw new ArgumentNullException(nameof(processExecutor));
			generalConfiguration = generalConfigurationOptions?.Value ?? throw new ArgumentNullException(nameof(generalConfigurationOptions));

			var documentsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
			if (String.IsNullOrWhiteSpace(documentsDirectory))
				PathToUserByondFolder = null; // happens with the service account
			else
				PathToUserByondFolder = IOManager.ResolvePath(IOManager.ConcatPath(documentsDirectory, "BYOND"));

			semaphore = new SemaphoreSlim(1);
			installedDirectX = false;
		}

		/// <inheritdoc />
		public void Dispose() => semaphore.Dispose();

		/// <inheritdoc />
		public override string GetDreamDaemonName(Version version, out bool supportsCli, out bool supportsMapThreads)
		{
			ArgumentNullException.ThrowIfNull(version);

			supportsCli = version >= DDExeVersion;
			supportsMapThreads = version >= MapThreadsVersion;
			return supportsCli ? "dd.exe" : "dreamdaemon.exe";
		}

		/// <inheritdoc />
		public override ValueTask InstallByond(Version version, string path, CancellationToken cancellationToken)
		{
			var tasks = new List<ValueTask>(3)
			{
				SetNoPromptTrusted(path, cancellationToken),
				InstallDirectX(path, cancellationToken),
			};

			if (!generalConfiguration.SkipAddingByondFirewallException)
				tasks.Add(AddDreamDaemonToFirewall(version, path, cancellationToken));

			return ValueTaskExtensions.WhenAll(tasks);
		}

		/// <inheritdoc />
		public override async ValueTask UpgradeInstallation(Version version, string path, CancellationToken cancellationToken)
		{
			ArgumentNullException.ThrowIfNull(version);
			ArgumentNullException.ThrowIfNull(path);

			if (generalConfiguration.SkipAddingByondFirewallException)
				return;

			if (version < DDExeVersion)
				return;

			if (await IOManager.FileExists(IOManager.ConcatPath(path, TgsFirewalledDDFile), cancellationToken))
				return;

			Logger.LogInformation("BYOND Version {version} needs dd.exe added to firewall", version);
			await AddDreamDaemonToFirewall(version, path, cancellationToken);
		}

		/// <summary>
		/// Creates the BYOND cfg file that prevents the trusted mode dialog from appearing when launching DreamDaemon.
		/// </summary>
		/// <param name="path">The path to the BYOND installation.</param>
		/// <param name="cancellationToken">The <see cref="CancellationToken"/> for the operation.</param>
		/// <returns>A <see cref="Task"/> representing the running operation.</returns>
		async ValueTask SetNoPromptTrusted(string path, CancellationToken cancellationToken)
		{
			var configPath = IOManager.ConcatPath(path, ByondConfigDirectory);
			await IOManager.CreateDirectory(configPath, cancellationToken);

			var configFilePath = IOManager.ConcatPath(configPath, ByondDreamDaemonConfigFilename);
			Logger.LogTrace("Disabling trusted prompts in {configFilePath}...", configFilePath);
			await IOManager.WriteAllBytes(
				configFilePath,
				Encoding.UTF8.GetBytes(ByondNoPromptTrustedMode),
				cancellationToken);
		}

		/// <summary>
		/// Attempt to install the DirectX redistributable included with BYOND.
		/// </summary>
		/// <param name="path">The path to the BYOND installation.</param>
		/// <param name="cancellationToken">The <see cref="CancellationToken"/> for the operation.</param>
		/// <returns>A <see cref="ValueTask"/> representing the running operation.</returns>
		async ValueTask InstallDirectX(string path, CancellationToken cancellationToken)
		{
			using var lockContext = await SemaphoreSlimContext.Lock(semaphore, cancellationToken);
			if (installedDirectX)
			{
				Logger.LogTrace("DirectX already installed.");
				return;
			}

			Logger.LogTrace("Installing DirectX redistributable...");

			// always install it, it's pretty fast and will do better redundancy checking than us
			var rbdx = IOManager.ConcatPath(path, ByondDXDir);

			try
			{
				// noShellExecute because we aren't doing runas shennanigans
				await using var directXInstaller = processExecutor.LaunchProcess(
					IOManager.ConcatPath(rbdx, "DXSETUP.exe"),
					rbdx,
					"/silent",
					noShellExecute: true);

				int exitCode;
				using (cancellationToken.Register(() => directXInstaller.Terminate()))
					exitCode = (await directXInstaller.Lifetime).Value;
				cancellationToken.ThrowIfCancellationRequested();

				if (exitCode != 0)
					throw new JobException(ErrorCode.ByondDirectXInstallFail, new JobException($"Invalid exit code: {exitCode}"));
				installedDirectX = true;
			}
			catch (Exception e)
			{
				throw new JobException(ErrorCode.ByondDirectXInstallFail, e);
			}
		}

		/// <summary>
		/// Attempt to add the DreamDaemon executable as an exception to the Windows firewall.
		/// </summary>
		/// <param name="version">The BYOND <see cref="Version"/>.</param>
		/// <param name="path">The path to the BYOND installation.</param>
		/// <param name="cancellationToken">The <see cref="CancellationToken"/> for the operation.</param>
		/// <returns>A <see cref="ValueTask"/> representing the running operation.</returns>
		async ValueTask AddDreamDaemonToFirewall(Version version, string path, CancellationToken cancellationToken)
		{
			var dreamDaemonName = GetDreamDaemonName(version, out var usesDDExe, out var _);

			var dreamDaemonPath = IOManager.ResolvePath(
				IOManager.ConcatPath(
					path,
					ByondManager.BinPath,
					dreamDaemonName));

			Logger.LogInformation("Adding Windows Firewall exception for {path}...", dreamDaemonPath);
			try
			{
				// I really wish we could add the instance name here but
				// 1. It'd make IByondInstaller need to be transient per-instance and WindowsByondInstaller relys on being a singleton for its DX installer call
				// 2. The instance could be renamed, so it'd have to be an unfriendly ID anyway.
				var arguments = $"advfirewall firewall add rule name=\"TGS DreamDaemon {version}\" program=\"{dreamDaemonPath}\" protocol=tcp dir=in enable=yes action=allow";
				await using var netshProcess = processExecutor.LaunchProcess(
					"netsh.exe",
					IOManager.ResolvePath(),
					arguments,
					readStandardHandles: true,
					noShellExecute: true);

				int exitCode;
				using (cancellationToken.Register(() => netshProcess.Terminate()))
					exitCode = (await netshProcess.Lifetime).Value;
				cancellationToken.ThrowIfCancellationRequested();

				Logger.LogDebug(
					"netsh.exe output:{newLine}{output}",
					Environment.NewLine,
					await netshProcess.GetCombinedOutput(cancellationToken));

				if (exitCode != 0)
					throw new JobException(ErrorCode.ByondDreamDaemonFirewallFail, new JobException($"Invalid exit code: {exitCode}"));

				if (usesDDExe)
					await IOManager.WriteAllBytes(
						IOManager.ConcatPath(path, TgsFirewalledDDFile),
						Array.Empty<byte>(),
						cancellationToken);
			}
			catch (Exception ex)
			{
				throw new JobException(ErrorCode.ByondDreamDaemonFirewallFail, ex);
			}
		}
	}
}
