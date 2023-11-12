﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Byond.TopicSender;

using Microsoft.Extensions.Logging;

using Tgstation.Server.Api.Models;
using Tgstation.Server.Api.Models.Internal;
using Tgstation.Server.Common.Extensions;
using Tgstation.Server.Host.Components.Byond;
using Tgstation.Server.Host.Components.Chat;
using Tgstation.Server.Host.Components.Deployment;
using Tgstation.Server.Host.Components.Events;
using Tgstation.Server.Host.Components.Interop;
using Tgstation.Server.Host.Components.Interop.Bridge;
using Tgstation.Server.Host.Configuration;
using Tgstation.Server.Host.Core;
using Tgstation.Server.Host.Extensions;
using Tgstation.Server.Host.IO;
using Tgstation.Server.Host.Jobs;
using Tgstation.Server.Host.Security;
using Tgstation.Server.Host.System;
using Tgstation.Server.Host.Utils;

namespace Tgstation.Server.Host.Components.Session
{
	/// <inheritdoc />
	sealed class SessionControllerFactory : ISessionControllerFactory
	{
		/// <summary>
		/// Path in Diagnostics folder to DreamDaemon logs.
		/// </summary>
		const string DreamDaemonLogsPath = "DreamDaemonLogs";

		/// <summary>
		/// The <see cref="IProcessExecutor"/> for the <see cref="SessionControllerFactory"/>.
		/// </summary>
		readonly IProcessExecutor processExecutor;

		/// <summary>
		/// The <see cref="IByondManager"/> for the <see cref="SessionControllerFactory"/>.
		/// </summary>
		readonly IByondManager byond;

		/// <summary>
		/// The <see cref="ITopicClientFactory"/> for the <see cref="SessionControllerFactory"/>.
		/// </summary>
		readonly ITopicClientFactory topicClientFactory;

		/// <summary>
		/// The <see cref="ICryptographySuite"/> for the <see cref="SessionControllerFactory"/>.
		/// </summary>
		readonly ICryptographySuite cryptographySuite;

		/// <summary>
		/// The <see cref="IAssemblyInformationProvider"/> for the <see cref="SessionControllerFactory"/>.
		/// </summary>
		readonly IAssemblyInformationProvider assemblyInformationProvider;

		/// <summary>
		/// The <see cref="IIOManager"/> for the Game directory.
		/// </summary>
		readonly IIOManager gameIOManager;

		/// <summary>
		/// The <see cref="IIOManager"/> for the Diagnostics directory.
		/// </summary>
		readonly IIOManager diagnosticsIOManager;

		/// <summary>
		/// The <see cref="IChatManager"/> for the <see cref="SessionControllerFactory"/>.
		/// </summary>
		readonly IChatManager chat;

		/// <summary>
		/// The <see cref="INetworkPromptReaper"/> for the <see cref="SessionControllerFactory"/>.
		/// </summary>
		readonly INetworkPromptReaper networkPromptReaper;

		/// <summary>
		/// The <see cref="IPlatformIdentifier"/> for the <see cref="SessionControllerFactory"/>.
		/// </summary>
		readonly IPlatformIdentifier platformIdentifier;

		/// <summary>
		/// The <see cref="IBridgeRegistrar"/> for the <see cref="SessionControllerFactory"/>.
		/// </summary>
		readonly IBridgeRegistrar bridgeRegistrar;

		/// <summary>
		/// The <see cref="IServerPortProvider"/> for the <see cref="SessionControllerFactory"/>.
		/// </summary>
		readonly IServerPortProvider serverPortProvider;

		/// <summary>
		/// The <see cref="IEventConsumer"/> for the <see cref="SessionControllerFactory"/>.
		/// </summary>
		readonly IEventConsumer eventConsumer;

		/// <summary>
		/// The <see cref="IAsyncDelayer"/> for the <see cref="SessionControllerFactory"/>.
		/// </summary>
		readonly IAsyncDelayer asyncDelayer;

		/// <summary>
		/// The <see cref="ILoggerFactory"/> for the <see cref="SessionControllerFactory"/>.
		/// </summary>
		readonly ILoggerFactory loggerFactory;

		/// <summary>
		/// The <see cref="ILogger"/> for the <see cref="SessionControllerFactory"/>.
		/// </summary>
		readonly ILogger<SessionControllerFactory> logger;

		/// <summary>
		/// The <see cref="SessionConfiguration"/> for the <see cref="SessionControllerFactory"/>.
		/// </summary>
		readonly SessionConfiguration sessionConfiguration;

		/// <summary>
		/// The <see cref="Api.Models.Instance"/> for the <see cref="SessionControllerFactory"/>.
		/// </summary>
		readonly Api.Models.Instance instance;

		/// <summary>
		/// Change a given <paramref name="securityLevel"/> into the appropriate DreamDaemon command line word.
		/// </summary>
		/// <param name="securityLevel">The <see cref="DreamDaemonSecurity"/> level to change.</param>
		/// <returns>A <see cref="string"/> representation of the command line parameter.</returns>
		static string SecurityWord(DreamDaemonSecurity securityLevel)
		{
			return securityLevel switch
			{
				DreamDaemonSecurity.Safe => "safe",
				DreamDaemonSecurity.Trusted => "trusted",
				DreamDaemonSecurity.Ultrasafe => "ultrasafe",
				_ => throw new ArgumentOutOfRangeException(nameof(securityLevel), securityLevel, String.Format(CultureInfo.InvariantCulture, "Bad DreamDaemon security level: {0}", securityLevel)),
			};
		}

		/// <summary>
		/// Change a given <paramref name="visibility"/> into the appropriate DreamDaemon command line word.
		/// </summary>
		/// <param name="visibility">The <see cref="DreamDaemonVisibility"/> level to change.</param>
		/// <returns>A <see cref="string"/> representation of the command line parameter.</returns>
		static string VisibilityWord(DreamDaemonVisibility visibility)
		{
			return visibility switch
			{
				DreamDaemonVisibility.Public => "public",
				DreamDaemonVisibility.Private => "private",
				DreamDaemonVisibility.Invisible => "invisible",
				_ => throw new ArgumentOutOfRangeException(nameof(visibility), visibility, String.Format(CultureInfo.InvariantCulture, "Bad DreamDaemon visibility level: {0}", visibility)),
			};
		}

		/// <summary>
		/// Check if a given <paramref name="port"/> can be bound to.
		/// </summary>
		/// <param name="port">The port number to test.</param>
		void PortBindTest(ushort port)
		{
			try
			{
				logger.LogTrace("Bind test: {port}", port);
				SocketExtensions.BindTest(port, false);
			}
			catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
			{
				throw new JobException(ErrorCode.DreamDaemonPortInUse, ex);
			}
		}

		/// <summary>
		/// Initializes a new instance of the <see cref="SessionControllerFactory"/> class.
		/// </summary>
		/// <param name="processExecutor">The value of <see cref="processExecutor"/>.</param>
		/// <param name="byond">The value of <see cref="byond"/>.</param>
		/// <param name="topicClientFactory">The value of <see cref="topicClientFactory"/>.</param>
		/// <param name="cryptographySuite">The value of <see cref="cryptographySuite"/>.</param>
		/// <param name="assemblyInformationProvider">The value of <see cref="assemblyInformationProvider"/>.</param>
		/// <param name="instance">The value of <see cref="instance"/>.</param>
		/// <param name="gameIOManager">The value of <see cref="gameIOManager"/>.</param>
		/// <param name="diagnosticsIOManager">The value of <see cref="diagnosticsIOManager"/>.</param>
		/// <param name="chat">The value of <see cref="chat"/>.</param>
		/// <param name="networkPromptReaper">The value of <see cref="networkPromptReaper"/>.</param>
		/// <param name="platformIdentifier">The value of <see cref="platformIdentifier"/>.</param>
		/// <param name="bridgeRegistrar">The value of <see cref="bridgeRegistrar"/>.</param>
		/// <param name="serverPortProvider">The value of <see cref="serverPortProvider"/>.</param>
		/// <param name="eventConsumer">The value of <see cref="eventConsumer"/>.</param>
		/// <param name="asyncDelayer">The value of <see cref="asyncDelayer"/>.</param>
		/// <param name="loggerFactory">The value of <see cref="loggerFactory"/>.</param>
		/// <param name="logger">The value of <see cref="logger"/>.</param>
		/// <param name="sessionConfiguration">The value of <see cref="sessionConfiguration"/>.</param>
		public SessionControllerFactory(
			IProcessExecutor processExecutor,
			IByondManager byond,
			ITopicClientFactory topicClientFactory,
			ICryptographySuite cryptographySuite,
			IAssemblyInformationProvider assemblyInformationProvider,
			IIOManager gameIOManager,
			IIOManager diagnosticsIOManager,
			IChatManager chat,
			INetworkPromptReaper networkPromptReaper,
			IPlatformIdentifier platformIdentifier,
			IBridgeRegistrar bridgeRegistrar,
			IServerPortProvider serverPortProvider,
			IEventConsumer eventConsumer,
			IAsyncDelayer asyncDelayer,
			ILoggerFactory loggerFactory,
			ILogger<SessionControllerFactory> logger,
			SessionConfiguration sessionConfiguration,
			Api.Models.Instance instance)
		{
			this.processExecutor = processExecutor ?? throw new ArgumentNullException(nameof(processExecutor));
			this.byond = byond ?? throw new ArgumentNullException(nameof(byond));
			this.topicClientFactory = topicClientFactory ?? throw new ArgumentNullException(nameof(topicClientFactory));
			this.cryptographySuite = cryptographySuite ?? throw new ArgumentNullException(nameof(cryptographySuite));
			this.assemblyInformationProvider = assemblyInformationProvider ?? throw new ArgumentNullException(nameof(assemblyInformationProvider));
			this.gameIOManager = gameIOManager ?? throw new ArgumentNullException(nameof(gameIOManager));
			this.diagnosticsIOManager = diagnosticsIOManager ?? throw new ArgumentNullException(nameof(diagnosticsIOManager));
			this.chat = chat ?? throw new ArgumentNullException(nameof(chat));
			this.networkPromptReaper = networkPromptReaper ?? throw new ArgumentNullException(nameof(networkPromptReaper));
			this.platformIdentifier = platformIdentifier ?? throw new ArgumentNullException(nameof(platformIdentifier));
			this.bridgeRegistrar = bridgeRegistrar ?? throw new ArgumentNullException(nameof(bridgeRegistrar));
			this.serverPortProvider = serverPortProvider ?? throw new ArgumentNullException(nameof(serverPortProvider));
			this.eventConsumer = eventConsumer ?? throw new ArgumentNullException(nameof(eventConsumer));
			this.asyncDelayer = asyncDelayer ?? throw new ArgumentNullException(nameof(asyncDelayer));
			this.loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
			this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
			this.sessionConfiguration = sessionConfiguration ?? throw new ArgumentNullException(nameof(sessionConfiguration));
			this.instance = instance ?? throw new ArgumentNullException(nameof(instance));
		}

		/// <inheritdoc />
		#pragma warning disable CA1506 // TODO: Decomplexify
		public async ValueTask<ISessionController> LaunchNew(
			IDmbProvider dmbProvider,
			IByondExecutableLock currentByondLock,
			DreamDaemonLaunchParameters launchParameters,
			bool apiValidate,
			CancellationToken cancellationToken)
		{
			logger.LogTrace("Begin session launch...");
			if (!launchParameters.Port.HasValue)
				throw new InvalidOperationException("Given port is null!");
			switch (dmbProvider.CompileJob.MinimumSecurityLevel)
			{
				case DreamDaemonSecurity.Ultrasafe:
					break;
				case DreamDaemonSecurity.Safe:
					if (launchParameters.SecurityLevel == DreamDaemonSecurity.Ultrasafe)
					{
						logger.LogTrace("Boosting security level to minimum of Safe");
						launchParameters.SecurityLevel = DreamDaemonSecurity.Safe;
					}

					break;
				case DreamDaemonSecurity.Trusted:
					if (launchParameters.SecurityLevel != DreamDaemonSecurity.Trusted)
						logger.LogTrace("Boosting security level to minimum of Trusted");

					launchParameters.SecurityLevel = DreamDaemonSecurity.Trusted;
					break;
				default:
					throw new InvalidOperationException(String.Format(CultureInfo.InvariantCulture, "Invalid DreamDaemonSecurity value: {0}", dmbProvider.CompileJob.MinimumSecurityLevel));
			}

			// get the byond lock
			var byondLock = currentByondLock ?? await byond.UseExecutables(
				Version.Parse(dmbProvider.CompileJob.ByondVersion),
				gameIOManager.ConcatPath(dmbProvider.Directory, dmbProvider.DmbName),
				cancellationToken);
			try
			{
				logger.LogDebug(
					"Launching session with CompileJob {compileJobId}...",
					dmbProvider.CompileJob.Id);

				PortBindTest(launchParameters.Port.Value);
				await CheckPagerIsNotRunning();

				string outputFilePath = null;
				var preserveLogFile = true;

				var cliSupported = byondLock.SupportsCli;
				if (launchParameters.LogOutput.Value)
				{
					var now = DateTimeOffset.UtcNow;
					var dateDirectory = diagnosticsIOManager.ConcatPath(DreamDaemonLogsPath, now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
					await diagnosticsIOManager.CreateDirectory(dateDirectory, cancellationToken);
					outputFilePath = diagnosticsIOManager.ResolvePath(
						diagnosticsIOManager.ConcatPath(
							dateDirectory,
							$"dd-utc-{now.ToString("yyyy-MM-dd-HH-mm-ss", CultureInfo.InvariantCulture)}{(apiValidate ? "-dmapi" : String.Empty)}.log"));

					logger.LogInformation("Logging DreamDaemon output to {path}...", outputFilePath);
				}
				else if (!cliSupported)
				{
					outputFilePath = gameIOManager.ConcatPath(dmbProvider.Directory, $"{Guid.NewGuid()}.dd.log");
					preserveLogFile = false;
				}

				var accessIdentifier = cryptographySuite.GetSecureString();

				var byondTopicSender = topicClientFactory.CreateTopicClient(
					TimeSpan.FromMilliseconds(
						launchParameters.TopicRequestTimeout.Value));

				if (!apiValidate && dmbProvider.CompileJob.DMApiVersion == null)
					logger.LogDebug("Session will have no DMAPI support!");

				// launch dd
				var process = await CreateDreamDaemonProcess(
					dmbProvider,
					byondTopicSender,
					byondLock,
					launchParameters,
					accessIdentifier,
					outputFilePath,
					apiValidate,
					cancellationToken);

				try
				{
					var chatTrackingContext = chat.CreateTrackingContext();

					try
					{
						var runtimeInformation = CreateRuntimeInformation(
							dmbProvider,
							chatTrackingContext,
							launchParameters.SecurityLevel.Value,
							launchParameters.Visibility.Value,
							apiValidate);

						var reattachInformation = new ReattachInformation(
							dmbProvider,
							process,
							runtimeInformation,
							accessIdentifier,
							launchParameters.Port.Value);

						var sessionController = new SessionController(
							reattachInformation,
							instance,
							process,
							byondLock,
							byondTopicSender,
							chatTrackingContext,
							bridgeRegistrar,
							chat,
							assemblyInformationProvider,
							asyncDelayer,
							loggerFactory.CreateLogger<SessionController>(),
							() => LogDDOutput(
								process,
								outputFilePath,
								cliSupported,
								preserveLogFile,
								CancellationToken.None), // DCT: None available
							launchParameters.StartupTimeout,
							false,
							apiValidate);

						return sessionController;
					}
					catch
					{
						chatTrackingContext.Dispose();
						throw;
					}
				}
				catch
				{
					await using (process)
					{
						process.Terminate();
						await process.Lifetime;
						throw;
					}
				}
			}
			catch
			{
				if (currentByondLock == null)
					byondLock.Dispose();
				throw;
			}
		}
#pragma warning restore CA1506

		/// <inheritdoc />
		public async ValueTask<ISessionController> Reattach(
			ReattachInformation reattachInformation,
			CancellationToken cancellationToken)
		{
			ArgumentNullException.ThrowIfNull(reattachInformation);

			logger.LogTrace("Begin session reattach...");
			var byondTopicSender = topicClientFactory.CreateTopicClient(reattachInformation.TopicRequestTimeout);
			var byondLock = await byond.UseExecutables(
				Version.Parse(reattachInformation.Dmb.CompileJob.ByondVersion),
				null, // Doesn't matter if it's trusted or not on reattach
				cancellationToken);

			try
			{
				logger.LogDebug(
					"Attaching to session PID: {pid}, CompileJob: {compileJobId}...",
					reattachInformation.ProcessId,
					reattachInformation.Dmb.CompileJob.Id);

				var process = processExecutor.GetProcess(reattachInformation.ProcessId);
				if (process == null)
					return null;

				try
				{
					if (!byondLock.SupportsCli)
						networkPromptReaper.RegisterProcess(process);

					var chatTrackingContext = chat.CreateTrackingContext();
					try
					{
						var runtimeInformation = CreateRuntimeInformation(
							reattachInformation.Dmb,
							chatTrackingContext,
							reattachInformation.LaunchSecurityLevel,
							reattachInformation.LaunchVisibility,
							false);
						reattachInformation.SetRuntimeInformation(runtimeInformation);

						var controller = new SessionController(
							reattachInformation,
							instance,
							process,
							byondLock,
							byondTopicSender,
							chatTrackingContext,
							bridgeRegistrar,
							chat,
							assemblyInformationProvider,
							asyncDelayer,
							loggerFactory.CreateLogger<SessionController>(),
							() => ValueTask.CompletedTask,
							null,
							true,
							false);

						process = null;
						byondLock = null;
						chatTrackingContext = null;

						return controller;
					}
					catch
					{
						chatTrackingContext.Dispose();
						throw;
					}
				}
				catch
				{
					await process.DisposeAsync();
					throw;
				}
			}
			catch
			{
				byondLock.Dispose();
				throw;
			}
		}

		/// <summary>
		/// Creates the DreamDaemon <see cref="IProcess"/>.
		/// </summary>
		/// <param name="dmbProvider">The <see cref="IDmbProvider"/>.</param>
		/// <param name="byondTopicSender">The <see cref="ITopicClient"/> to use for sanitization.</param>
		/// <param name="byondLock">The <see cref="IByondExecutableLock"/>.</param>
		/// <param name="launchParameters">The <see cref="DreamDaemonLaunchParameters"/>.</param>
		/// <param name="accessIdentifier">The secure string to use for the session.</param>
		/// <param name="logFilePath">The path to log DreamDaemon output to.</param>
		/// <param name="apiValidate">If we are only validating the DMAPI then exiting.</param>
		/// <param name="cancellationToken">The <see cref="CancellationToken"/> for the operation.</param>
		/// <returns>A <see cref="ValueTask{TResult}"/> resulting in the DreamDaemon <see cref="IProcess"/>.</returns>
		async ValueTask<IProcess> CreateDreamDaemonProcess(
			IDmbProvider dmbProvider,
			ITopicClient byondTopicSender,
			IByondExecutableLock byondLock,
			DreamDaemonLaunchParameters launchParameters,
			string accessIdentifier,
			string logFilePath,
			bool apiValidate,
			CancellationToken cancellationToken)
		{
			// set command line options
			// more sanitization here cause it uses the same scheme
			var parameters = $"{DMApiConstants.ParamApiVersion}={byondTopicSender.SanitizeString(DMApiConstants.InteropVersion.Semver().ToString())}&{byondTopicSender.SanitizeString(DMApiConstants.ParamServerPort)}={serverPortProvider.HttpApiPort}&{byondTopicSender.SanitizeString(DMApiConstants.ParamAccessIdentifier)}={byondTopicSender.SanitizeString(accessIdentifier)}";

			if (!String.IsNullOrEmpty(launchParameters.AdditionalParameters))
				parameters = $"{parameters}&{launchParameters.AdditionalParameters}";

			// important to run on all ports to allow port changing
			var arguments = String.Format(
				CultureInfo.InvariantCulture,
				"{0} -port {1} -ports 1-65535 {2}-close -verbose -{3} -{4}{5}{6}{7} -params \"{8}\"",
				dmbProvider.DmbName,
				launchParameters.Port.Value,
				launchParameters.AllowWebClient.Value ? "-webclient " : String.Empty,
				SecurityWord(launchParameters.SecurityLevel.Value),
				VisibilityWord(launchParameters.Visibility.Value),
				!byondLock.SupportsCli
					? $" -logself -log {logFilePath}"
					: String.Empty, // DD doesn't output anything if -logself is set???
				launchParameters.StartProfiler.Value
					? " -profile"
					: String.Empty,
				byondLock.SupportsMapThreads && launchParameters.MapThreads.Value != 0
					? $" -map-threads {launchParameters.MapThreads.Value}"
					: String.Empty,
				parameters);

			var process = processExecutor.LaunchProcess(
				byondLock.DreamDaemonPath,
				dmbProvider.Directory,
				arguments,
				logFilePath,
				byondLock.SupportsCli,
				true);

			try
			{
				if (!apiValidate)
				{
					if (sessionConfiguration.HighPriorityLiveDreamDaemon)
						process.AdjustPriority(true);
				}
				else if (sessionConfiguration.LowPriorityDeploymentProcesses)
					process.AdjustPriority(false);

				if (!byondLock.SupportsCli)
					networkPromptReaper.RegisterProcess(process);

				// If this isnt a staging DD (From a Deployment), fire off an event
				if (!apiValidate)
					await eventConsumer.HandleEvent(
						EventType.DreamDaemonLaunch,
						new List<string>
						{
							process.Id.ToString(CultureInfo.InvariantCulture),
						},
						false,
						cancellationToken);

				return process;
			}
			catch
			{
				await using (process)
				{
					process.Terminate();
					await process.Lifetime;
					throw;
				}
			}
		}

		/// <summary>
		/// Attempts to log DreamDaemon output.
		/// </summary>
		/// <param name="process">The DreamDaemon <see cref="IProcess"/>.</param>
		/// <param name="outputFilePath">The path to the DreamDaemon log file. Will be deleted if <paramref name="preserveFile"/> is <see langword="false"/>.</param>
		/// <param name="cliSupported">If DreamDaemon was launched with CLI capabilities.</param>
		/// <param name="preserveFile">If <see langword="false"/>, <paramref name="outputFilePath"/> will be deleted.</param>
		/// <param name="cancellationToken">The <see cref="CancellationToken"/> for the operation.</param>
		/// <returns>A <see cref="ValueTask"/> representing the running operation.</returns>
		async ValueTask LogDDOutput(IProcess process, string outputFilePath, bool cliSupported, bool preserveFile, CancellationToken cancellationToken)
		{
			try
			{
				string ddOutput = null;
				if (cliSupported)
					ddOutput = await process.GetCombinedOutput(cancellationToken);

				if (ddOutput == null)
					try
					{
						var dreamDaemonLogBytes = await gameIOManager.ReadAllBytes(
							outputFilePath,
							cancellationToken);

						ddOutput = Encoding.UTF8.GetString(dreamDaemonLogBytes);
					}
					finally
					{
						if (!preserveFile)
							try
							{
								logger.LogTrace("Deleting temporary log file {path}...", outputFilePath);
								await gameIOManager.DeleteFile(outputFilePath, cancellationToken);
							}
							catch (Exception ex)
							{
								logger.LogWarning(ex, "Failed to delete DreamDaemon log file {outputFilePath}!", outputFilePath);
							}
					}

				logger.LogTrace(
					"DreamDaemon Output:{newLine}{output}",
					Environment.NewLine,
					ddOutput);
			}
			catch (Exception ex)
			{
				logger.LogWarning(ex, "Error reading DreamDaemon output!");
			}
		}

		/// <summary>
		/// Create <see cref="RuntimeInformation"/>.
		/// </summary>
		/// <param name="dmbProvider">The <see cref="IDmbProvider"/>.</param>
		/// <param name="chatTrackingContext">The <see cref="IChatTrackingContext"/>.</param>
		/// <param name="securityLevel">The <see cref="DreamDaemonSecurity"/> the server was launched with.</param>
		/// <param name="visibility">The <see cref="DreamDaemonVisibility"/> the server was launched with.</param>
		/// <param name="apiValidateOnly">The value of <see cref="RuntimeInformation.ApiValidateOnly"/>.</param>
		/// <returns>A new <see cref="RuntimeInformation"/> class.</returns>
		RuntimeInformation CreateRuntimeInformation(
			IDmbProvider dmbProvider,
			IChatTrackingContext chatTrackingContext,
			DreamDaemonSecurity securityLevel,
			DreamDaemonVisibility visibility,
			bool apiValidateOnly)
			=> new (
				chatTrackingContext,
				dmbProvider,
				assemblyInformationProvider.Version,
				instance.Name,
				securityLevel,
				visibility,
				serverPortProvider.HttpApiPort,
				apiValidateOnly);

		/// <summary>
		/// Make sure the BYOND pager is not running.
		/// </summary>
		/// <returns>A <see cref="ValueTask"/> representing the running operation.</returns>
		async ValueTask CheckPagerIsNotRunning()
		{
			if (!platformIdentifier.IsWindows)
				return;

			await using var otherProcess = processExecutor.GetProcessByName("byond");
			if (otherProcess == null)
				return;

			var otherUsername = otherProcess.GetExecutingUsername();

			await using var ourProcess = processExecutor.GetCurrentProcess();
			var ourUsername = ourProcess.GetExecutingUsername();

			if (otherUsername.Equals(ourUsername, StringComparison.Ordinal))
				throw new JobException(ErrorCode.DeploymentPagerRunning);
		}
	}
}
