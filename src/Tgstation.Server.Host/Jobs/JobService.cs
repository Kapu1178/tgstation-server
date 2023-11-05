﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Serilog.Context;

using Tgstation.Server.Api.Hubs;
using Tgstation.Server.Api.Models.Response;
using Tgstation.Server.Common.Extensions;
using Tgstation.Server.Host.Components;
using Tgstation.Server.Host.Database;
using Tgstation.Server.Host.Extensions;
using Tgstation.Server.Host.Models;
using Tgstation.Server.Host.Utils;
using Tgstation.Server.Host.Utils.SignalR;

namespace Tgstation.Server.Host.Jobs
{
	/// <inheritdoc cref="IJobService" />
	sealed class JobService : IJobService, IDisposable
	{
		/// <summary>
		/// The maximum rate at which hub clients can receive updates.
		/// </summary>
		const int MaxHubUpdatesPerSecond = 4;

		/// <summary>
		/// The <see cref="IHubContext"/> for the <see cref="JobsHub"/>.
		/// </summary>
		readonly IConnectionMappedHubContext<JobsHub, IJobsHub> hub;

		/// <summary>
		/// The <see cref="IServiceProvider"/> for the <see cref="JobService"/>.
		/// </summary>
		readonly IDatabaseContextFactory databaseContextFactory;

		/// <summary>
		/// The <see cref="ILoggerFactory"/> for the <see cref="JobService"/>.
		/// </summary>
		readonly ILoggerFactory loggerFactory;

		/// <summary>
		/// The <see cref="ILogger"/> for the <see cref="JobService"/>.
		/// </summary>
		readonly ILogger<JobService> logger;

		/// <summary>
		/// <see cref="Dictionary{TKey, TValue}"/> of <see cref="Job"/> <see cref="Api.Models.EntityId.Id"/>s to running <see cref="JobHandler"/>s.
		/// </summary>
		readonly Dictionary<long, JobHandler> jobs;

		/// <summary>
		/// <see cref="TaskCompletionSource{TResult}"/> to delay starting jobs until the server is ready.
		/// </summary>
		readonly TaskCompletionSource<IInstanceCoreProvider> activationTcs;

		/// <summary>
		/// <see langword="lock"/> <see cref="object"/> for various operations.
		/// </summary>
		readonly object synchronizationLock;

		/// <summary>
		/// Prevents a really REALLY rare race condition between add and cancel operations.
		/// </summary>
		readonly object addCancelLock;

		/// <summary>
		/// Prevents jobs that are registered after shutdown from activating.
		/// </summary>
		volatile bool noMoreJobsShouldStart;

		/// <summary>
		/// Initializes a new instance of the <see cref="JobService"/> class.
		/// </summary>
		/// <param name="hub">The value of <see cref="hub"/>.</param>
		/// <param name="databaseContextFactory">The value of <see cref="databaseContextFactory"/>.</param>
		/// <param name="loggerFactory">The value of <see cref="loggerFactory"/>.</param>
		/// <param name="logger">The value of <see cref="logger"/>.</param>
		public JobService(
			IConnectionMappedHubContext<JobsHub, IJobsHub> hub,
			IDatabaseContextFactory databaseContextFactory,
			ILoggerFactory loggerFactory,
			ILogger<JobService> logger)
		{
			this.hub = hub ?? throw new ArgumentNullException(nameof(hub));
			this.databaseContextFactory = databaseContextFactory ?? throw new ArgumentNullException(nameof(databaseContextFactory));
			this.loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
			this.logger = logger ?? throw new ArgumentNullException(nameof(logger));

			jobs = new Dictionary<long, JobHandler>();
			activationTcs = new TaskCompletionSource<IInstanceCoreProvider>();
			synchronizationLock = new object();
			addCancelLock = new object();
		}

		/// <inheritdoc />
		public void Dispose()
		{
			foreach (var job in jobs)
				job.Value.Dispose();
		}

		/// <inheritdoc />
		public ValueTask RegisterOperation(Job job, JobEntrypoint operation, CancellationToken cancellationToken)
			=> databaseContextFactory.UseContext(
				async databaseContext =>
				{
					ArgumentNullException.ThrowIfNull(job);
					ArgumentNullException.ThrowIfNull(operation);

					job.StartedAt = DateTimeOffset.UtcNow;
					job.Cancelled = false;

					job.Instance = new Models.Instance
					{
						Id = job.Instance.Id ?? throw new InvalidOperationException("Instance associated with job does not have an Id!"),
					};
					databaseContext.Instances.Attach(job.Instance);

					if (job.StartedBy == null)
						job.StartedBy = await databaseContext
							.Users
							.GetTgsUser(cancellationToken);
					else
						job.StartedBy = new User
						{
							Id = job.StartedBy.Id ?? throw new InvalidOperationException("StartedBy User associated with job does not have an Id!"),
						};
					databaseContext.Users.Attach(job.StartedBy);

					databaseContext.Jobs.Add(job);

					await databaseContext.Save(cancellationToken);

					logger.LogDebug("Registering job {jobId}: {jobDesc}...", job.Id, job.Description);
					var jobHandler = new JobHandler(jobCancellationToken => RunJob(job, operation, jobCancellationToken));
					try
					{
						lock (addCancelLock)
						{
							bool jobShouldStart;
							lock (synchronizationLock)
							{
								jobs.Add(job.Id.Value, jobHandler);
								jobShouldStart = !noMoreJobsShouldStart;
							}

							if (jobShouldStart)
								jobHandler.Start();
						}
					}
					catch
					{
						jobHandler.Dispose();
						throw;
					}
				});

		/// <inheritdoc />
		public Task StartAsync(CancellationToken cancellationToken)
			=> databaseContextFactory.UseContext(async databaseContext =>
			{
				// mark all jobs as cancelled
				var badJobIds = await databaseContext
					.Jobs
					.AsQueryable()
					.Where(y => !y.StoppedAt.HasValue)
					.Select(y => y.Id)
					.ToListAsync(cancellationToken);
				if (badJobIds.Count > 0)
				{
					logger.LogTrace("Cleaning {unfinishedJobCount} unfinished jobs...", badJobIds.Count);
					foreach (var badJobId in badJobIds)
					{
						var job = new Job { Id = badJobId };
						databaseContext.Jobs.Attach(job);
						job.Cancelled = true;
						job.StoppedAt = DateTimeOffset.UtcNow;
					}

					await databaseContext.Save(cancellationToken);
				}

				noMoreJobsShouldStart = false;
			})
			.AsTask();

		/// <inheritdoc />
		public Task StopAsync(CancellationToken cancellationToken)
		{
			List<ValueTask<Job>> joinTasks;
			lock (addCancelLock)
				lock (synchronizationLock)
				{
					noMoreJobsShouldStart = true;
					joinTasks = jobs.Select(x => CancelJob(
						new Job
						{
							Id = x.Key,
						},
						null,
						true,
						cancellationToken))
						.ToList();
				}

			return ValueTaskExtensions.WhenAll(joinTasks).AsTask();
		}

		/// <inheritdoc />
		public async ValueTask<Job> CancelJob(Job job, User user, bool blocking, CancellationToken cancellationToken)
		{
			ArgumentNullException.ThrowIfNull(job);

			JobHandler handler;
			lock (addCancelLock)
			{
				lock (synchronizationLock)
					if (!jobs.TryGetValue(job.Id.Value, out handler))
						return null;

				logger.LogDebug("Cancelling job ID {jobId}...", job.Id.Value);
				handler.Cancel(); // this will ensure the db update is only done once
			}

			await databaseContextFactory.UseContext(async databaseContext =>
			{
				user ??= await databaseContext.Users.GetTgsUser(cancellationToken);

				var updatedJob = new Job { Id = job.Id };
				databaseContext.Jobs.Attach(updatedJob);
				var attachedUser = new User { Id = user.Id };
				databaseContext.Users.Attach(attachedUser);
				updatedJob.CancelledBy = attachedUser;

				// let either startup or cancellation set job.cancelled
				await databaseContext.Save(cancellationToken);
				job.CancelledBy = user;
			});

			if (blocking)
			{
				logger.LogTrace("Waiting on cancelled job #{jobId}...", job.Id);
				await handler.Wait(cancellationToken);
				logger.LogTrace("Done waiting on job #{jobId}...", job.Id);
			}

			return job;
		}

		/// <inheritdoc />
		public void SetJobProgress(JobResponse apiResponse)
		{
			ArgumentNullException.ThrowIfNull(apiResponse);
			lock (synchronizationLock)
			{
				if (!jobs.TryGetValue(apiResponse.Id.Value, out var handler))
					return;
				apiResponse.Progress = handler.Progress;
				apiResponse.Stage = handler.Stage;
			}
		}

		/// <inheritdoc />
		public async ValueTask<bool?> WaitForJobCompletion(Job job, User canceller, CancellationToken jobCancellationToken, CancellationToken cancellationToken)
		{
			ArgumentNullException.ThrowIfNull(job);

			if (!cancellationToken.CanBeCanceled)
				throw new ArgumentException("A cancellable CancellationToken should be provided!", nameof(cancellationToken));

			JobHandler handler;
			bool noMoreJobsShouldStart;
			lock (synchronizationLock)
			{
				if (!jobs.TryGetValue(job.Id.Value, out handler))
					return null;

				noMoreJobsShouldStart = this.noMoreJobsShouldStart;
			}

			if (noMoreJobsShouldStart && !handler.Started)
				await Extensions.TaskExtensions.InfiniteTask.WaitAsync(cancellationToken);

			var cancelTask = ValueTask.FromResult<Job>(null);
			bool result;
			using (jobCancellationToken.Register(() => cancelTask = CancelJob(job, canceller, true, cancellationToken)))
				result = await handler.Wait(cancellationToken);

			await cancelTask;

			return result;
		}

		/// <inheritdoc />
		public void Activate(IInstanceCoreProvider instanceCoreProvider)
		{
			ArgumentNullException.ThrowIfNull(instanceCoreProvider);

			logger.LogTrace("Activating job manager...");
			activationTcs.SetResult(instanceCoreProvider);
		}

		/// <summary>
		/// Runner for <see cref="JobHandler"/>s.
		/// </summary>
		/// <param name="job">The <see cref="Job"/> being run. Must be fully populated.</param>
		/// <param name="operation">The <see cref="JobEntrypoint"/> for the <paramref name="job"/>.</param>
		/// <param name="cancellationToken">The <see cref="CancellationToken"/> for the operation.</param>
		/// <returns>A <see cref="Task"/> representing the running operation.</returns>
#pragma warning disable CA1506 // TODO: Decomplexify
		async Task<bool> RunJob(Job job, JobEntrypoint operation, CancellationToken cancellationToken)
#pragma warning restore CA1506
		{
			using (LogContext.PushProperty(SerilogContextHelper.JobIdContextProperty, job.Id))
				try
				{
					void LogException(Exception ex) => logger.LogDebug(ex, "Job {jobId} exited with error!", job.Id);

					var hubUpdatesTask = Task.CompletedTask;
					var result = false;

					Stopwatch stopwatch = null;
					void QueueHubUpdate(JobResponse update)
					{
						var currentUpdatesTask = hubUpdatesTask;
						async Task ChainHubUpdate()
						{
							await currentUpdatesTask;

							// DCT: Cancellation token is for job, operation should always run
							await hub
								.Clients
								.Group(JobsHub.HubGroupName(job))
								.ReceiveJobUpdate(update, CancellationToken.None);
						}

						Stopwatch enteredLock = null;
						try
						{
							if (stopwatch != null)
							{
								Monitor.Enter(stopwatch);
								enteredLock = stopwatch;
								if (stopwatch.ElapsedMilliseconds * MaxHubUpdatesPerSecond < 1)
									return; // don't spam client
							}

							hubUpdatesTask = ChainHubUpdate();
							stopwatch = Stopwatch.StartNew();
						}
						finally
						{
							if (enteredLock != null)
								Monitor.Exit(enteredLock);
						}
					}

					try
					{
						void UpdateProgress(string stage, double? progress)
						{
							if (progress.HasValue
								&& (progress.Value < 0 || progress.Value > 1))
							{
								var exception = new ArgumentOutOfRangeException(nameof(progress), progress, "Progress must be a value from 0-1!");
								logger.LogError(exception, "Invalid progress value!");
								return;
							}

							int? newProgress = progress.HasValue ? (int)Math.Floor(progress.Value * 100) : null;
							lock (synchronizationLock)
								if (jobs.TryGetValue(job.Id.Value, out var handler))
								{
									handler.Stage = stage;
									handler.Progress = newProgress;

									var updatedJob = job.ToApi();
									updatedJob.Stage = stage;
									updatedJob.Progress = newProgress;
									QueueHubUpdate(updatedJob);
								}
						}

						var instanceCoreProvider = await activationTcs.Task.WaitAsync(cancellationToken);
						QueueHubUpdate(job.ToApi());

						logger.LogTrace("Starting job...");
						await operation(
							instanceCoreProvider.GetInstance(job.Instance),
							databaseContextFactory,
							job,
							new JobProgressReporter(
								loggerFactory.CreateLogger<JobProgressReporter>(),
								null,
								UpdateProgress),
							cancellationToken);

						logger.LogDebug("Job {jobId} completed!", job.Id);
						result = true;
					}
					catch (OperationCanceledException ex)
					{
						logger.LogDebug(ex, "Job {jobId} cancelled!", job.Id);
						job.Cancelled = true;
					}
					catch (JobException e)
					{
						job.ErrorCode = e.ErrorCode;
						job.ExceptionDetails = String.IsNullOrWhiteSpace(e.Message) ? e.InnerException?.Message : e.Message + $" (Inner exception: {e.InnerException?.Message})";
						LogException(e);
					}
					catch (Exception e)
					{
						job.ExceptionDetails = e.ToString();
						LogException(e);
					}

					await databaseContextFactory.UseContext(async databaseContext =>
					{
						var attachedJob = new Job
						{
							Id = job.Id,
						};

						databaseContext.Jobs.Attach(attachedJob);
						attachedJob.StoppedAt = DateTimeOffset.UtcNow;
						attachedJob.ExceptionDetails = job.ExceptionDetails;
						attachedJob.ErrorCode = job.ErrorCode;
						attachedJob.Cancelled = job.Cancelled;

						// DCT: Cancellation token is for job, operation should always run
						await databaseContext.Save(CancellationToken.None);
					});

					// Resetting the context here because I CBA to worry if the cache is being used
					await databaseContextFactory.UseContext(async databaseContext =>
					{
						// Cancellation might be set in another async context, forced to reload here for the final hub update
						// DCT: Cancellation token is for job, operation should always run
						var finalJob = await databaseContext
							.Jobs
							.AsQueryable()
							.Include(x => x.Instance)
							.Include(x => x.StartedBy)
							.Include(x => x.CancelledBy)
							.Where(dbJob => dbJob.Id == job.Id.Value)
							.FirstAsync(CancellationToken.None);
						QueueHubUpdate(finalJob.ToApi());
					});

					try
					{
						await hubUpdatesTask;
					}
					catch (Exception ex)
					{
						logger.LogError(ex, "Error in hub updates chain task!");
					}

					return result;
				}
				finally
				{
					lock (synchronizationLock)
					{
						var handler = jobs[job.Id.Value];
						jobs.Remove(job.Id.Value);
						handler.Dispose();
					}
				}
		}
	}
}
