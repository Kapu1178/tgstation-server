﻿using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using Tgstation.Server.Host.IO;

namespace Tgstation.Server.Host.System
{
	/// <inheritdoc />
	sealed class ProcessExecutor : IProcessExecutor
	{
		/// <summary>
		/// <see cref="ReaderWriterLockSlim"/> for <see cref="WithProcessLaunchExclusivity(Action)"/>.
		/// </summary>
		static readonly ReaderWriterLockSlim ExclusiveProcessLaunchLock = new();

		/// <summary>
		/// The <see cref="IProcessFeatures"/> for the <see cref="ProcessExecutor"/>.
		/// </summary>
		readonly IProcessFeatures processFeatures;

		/// <summary>
		/// The <see cref="IIOManager"/> for the <see cref="ProcessExecutor"/>.
		/// </summary>
		readonly IIOManager ioManager;

		/// <summary>
		/// The <see cref="ILogger"/> for the <see cref="ProcessExecutor"/>.
		/// </summary>
		readonly ILogger<ProcessExecutor> logger;

		/// <summary>
		/// The <see cref="ILoggerFactory"/> for the <see cref="ProcessExecutor"/>.
		/// </summary>
		readonly ILoggerFactory loggerFactory;

		/// <summary>
		/// Runs a given <paramref name="action"/> making sure to not launch any processes while its running.
		/// </summary>
		/// <param name="action">The <see cref="Action"/> to execute.</param>
		public static void WithProcessLaunchExclusivity(Action action)
		{
			ExclusiveProcessLaunchLock.EnterWriteLock();
			try
			{
				action();
			}
			finally
			{
				ExclusiveProcessLaunchLock.ExitWriteLock();
			}
		}

		/// <summary>
		/// Initializes a new instance of the <see cref="ProcessExecutor"/> class.
		/// </summary>
		/// <param name="processFeatures">The value of <see cref="processFeatures"/>.</param>
		/// <param name="ioManager">The value of <see cref="ioManager"/>.</param>
		/// <param name="logger">The value of <see cref="logger"/>.</param>
		/// <param name="loggerFactory">The value of <see cref="loggerFactory"/>.</param>
		public ProcessExecutor(
			IProcessFeatures processFeatures,
			IIOManager ioManager,
			ILogger<ProcessExecutor> logger,
			ILoggerFactory loggerFactory)
		{
			this.processFeatures = processFeatures ?? throw new ArgumentNullException(nameof(processFeatures));
			this.ioManager = ioManager ?? throw new ArgumentNullException(nameof(ioManager));
			this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
			this.loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
		}

		/// <inheritdoc />
		public IProcess GetProcess(int id)
		{
			logger.LogDebug("Attaching to process {pid}...", id);
			global::System.Diagnostics.Process handle;
			try
			{
				handle = global::System.Diagnostics.Process.GetProcessById(id);
			}
			catch (Exception e)
			{
				logger.LogDebug(e, "Unable to get process {pid}!", id);
				return null;
			}

			return CreateFromExistingHandle(handle);
		}

		/// <inheritdoc />
		public IProcess GetCurrentProcess()
		{
			logger.LogTrace("Getting current process...");
			var handle = global::System.Diagnostics.Process.GetCurrentProcess();
			return CreateFromExistingHandle(handle);
		}

		/// <inheritdoc />
		public IProcess LaunchProcess(
			string fileName,
			string workingDirectory,
			string arguments,
			string fileRedirect,
			bool readStandardHandles,
			bool noShellExecute)
		{
			ArgumentNullException.ThrowIfNull(fileName);
			ArgumentNullException.ThrowIfNull(workingDirectory);
			ArgumentNullException.ThrowIfNull(arguments);

			if (noShellExecute)
				logger.LogDebug(
				"Launching process in {workingDirectory}: {exe} {arguments}",
				workingDirectory,
				fileName,
				arguments);
			else
				logger.LogDebug(
				"Shell launching process in {workingDirectory}: {exe} {arguments}",
				workingDirectory,
				fileName,
				arguments);

			var handle = new global::System.Diagnostics.Process();
			try
			{
				handle.StartInfo.FileName = fileName;
				handle.StartInfo.Arguments = arguments;
				handle.StartInfo.WorkingDirectory = workingDirectory;

				handle.StartInfo.UseShellExecute = !noShellExecute;

				Task<string> readTask = null;
				CancellationTokenSource disposeCts = null;
				try
				{
					TaskCompletionSource<int> processStartTcs = null;
					if (readStandardHandles)
					{
						processStartTcs = new TaskCompletionSource<int>();
						handle.StartInfo.RedirectStandardOutput = true;
						handle.StartInfo.RedirectStandardError = true;

						disposeCts = new CancellationTokenSource();
						readTask = ConsumeReaders(handle, processStartTcs.Task, fileRedirect, disposeCts.Token);
					}

					int pid;
					try
					{
						ExclusiveProcessLaunchLock.EnterReadLock();
						try
						{
							handle.Start();
						}
						finally
						{
							ExclusiveProcessLaunchLock.ExitReadLock();
						}

						pid = handle.Id;
						processStartTcs?.SetResult(pid);
					}
					catch (Exception ex)
					{
						processStartTcs?.SetException(ex);
						throw;
					}

					var process = new Process(
						processFeatures,
						handle,
						disposeCts,
						readTask,
						loggerFactory.CreateLogger<Process>(),
						false);

					return process;
				}
				catch
				{
					disposeCts?.Dispose();
					throw;
				}
			}
			catch
			{
				handle.Dispose();
				throw;
			}
		}

		/// <inheritdoc />
		public IProcess GetProcessByName(string name)
		{
			logger.LogTrace("GetProcessByName: {processName}...", name ?? throw new ArgumentNullException(nameof(name)));
			var procs = global::System.Diagnostics.Process.GetProcessesByName(name);
			global::System.Diagnostics.Process handle = null;
			foreach (var proc in procs)
				if (handle == null)
					handle = proc;
				else
				{
					logger.LogTrace("Disposing extra found PID: {pid}...", proc.Id);
					proc.Dispose();
				}

			if (handle == null)
				return null;

			return CreateFromExistingHandle(handle);
		}

		/// <summary>
		/// Consume the stdout/stderr streams into a <see cref="Task"/>.
		/// </summary>
		/// <param name="handle">The <see cref="global::System.Diagnostics.Process"/>.</param>
		/// <param name="startupAndPid">The <see cref="Task{TResult}"/> resulting in the <see cref="global::System.Diagnostics.Process.Id"/> of the started process.</param>
		/// <param name="fileRedirect">The optional path to redirect the streams to.</param>
		/// <param name="disposeToken">The <see cref="CancellationToken"/> that triggers when the <see cref="Process"/> is disposed.</param>
		/// <returns>A <see cref="Task{TResult}"/> resulting in the program's output/error text if <paramref name="fileRedirect"/> is <see langword="null"/>, <see langword="null"/> otherwise.</returns>
		async Task<string> ConsumeReaders(global::System.Diagnostics.Process handle, Task<int> startupAndPid, string fileRedirect, CancellationToken disposeToken)
		{
			var pid = await startupAndPid;
			logger.LogTrace("Starting read for PID {pid}...", pid);

			var stdOutHandle = handle.StandardOutput;
			var stdErrHandle = handle.StandardError;
			bool outputOpen = true, errorOpen = true;
			var outputReadTask = stdOutHandle.ReadLineAsync(disposeToken).AsTask();
			var errorReadTask = stdErrHandle.ReadLineAsync(disposeToken).AsTask();

			async ValueTask<string> GetNextLine()
			{
				var nextLineTask = outputOpen
					? errorOpen
						? await Task.WhenAny(outputReadTask, errorReadTask)
						: outputReadTask
					: errorReadTask;

				var line = await nextLineTask;

				// Important to retrigger the reading block asap after pushing
				if (nextLineTask == outputReadTask)
				{
					if (line == null)
					{
						outputOpen = false;
						outputReadTask = null;
					}
					else
						outputReadTask = stdOutHandle.ReadLineAsync(disposeToken).AsTask();
				}
				else
				{
					global::System.Diagnostics.Debug.Assert(nextLineTask == errorReadTask, "How is this incorrect?");

					if (line == null)
					{
						errorOpen = false;
						errorReadTask = null;
					}
					else
						errorReadTask = stdErrHandle.ReadLineAsync(disposeToken).AsTask();
				}

				if (line == null && (errorOpen || outputOpen))
					return await GetNextLine();

				return line;
			}

			await using var fileStream = fileRedirect != null ? ioManager.CreateAsyncSequentialWriteStream(fileRedirect) : null;
			await using var writer = fileStream != null ? new StreamWriter(fileStream) : null;

			var stringBuilder = fileStream == null ? new StringBuilder() : null;
			ulong fileFlushToken = 0;
			async ValueTask QueueWrite(ValueTask previous, string text)
			{
				if (fileStream != null)
				{
					var startFlushToken = Interlocked.Increment(ref fileFlushToken);
					await previous;
					await writer.WriteLineAsync(text.AsMemory(), disposeToken);

					// only flush if there isn't anything rapidly coming in
					if (fileFlushToken == startFlushToken)
						await writer.FlushAsync(disposeToken);
				}
				else
					stringBuilder.AppendLine(text);
			}

			ValueTask writeQueue = ValueTask.CompletedTask;
			try
			{
				string text;
				while ((text = await GetNextLine()) != null)
					writeQueue = QueueWrite(writeQueue, text);

				await writeQueue;

				logger.LogTrace("Finished read for PID {pid}", pid);
			}
			catch (OperationCanceledException ex)
			{
				logger.LogWarning(ex, "PID {pid} stream reading interrupted!", pid);
				if (fileStream != null)
					await writer.WriteLineAsync("-- Process detached, log truncated. This is likely due a to TGS restart --");
			}

			return stringBuilder?.ToString();
		}

		/// <summary>
		/// Create a <see cref="IProcess"/> given an existing <paramref name="handle"/>.
		/// </summary>
		/// <param name="handle">The <see cref="global::System.Diagnostics.Process"/> to create a <see cref="IProcess"/> from.</param>
		/// <returns>The <see cref="IProcess"/> based on <paramref name="handle"/>.</returns>
		Process CreateFromExistingHandle(global::System.Diagnostics.Process handle)
		{
			try
			{
				var pid = handle.Id;
				return new Process(
					processFeatures,
					handle,
					null,
					null,
					loggerFactory.CreateLogger<Process>(),
					true);
			}
			catch
			{
				handle.Dispose();
				throw;
			}
		}
	}
}
