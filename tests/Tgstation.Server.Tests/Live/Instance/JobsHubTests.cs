﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using Tgstation.Server.Api.Hubs;
using Tgstation.Server.Api.Models;
using Tgstation.Server.Api.Models.Request;
using Tgstation.Server.Api.Models.Response;
using Tgstation.Server.Client;
using Tgstation.Server.Common.Extensions;

namespace Tgstation.Server.Tests.Live.Instance
{
	sealed class JobsHubTests : IJobsHub
	{
		readonly IServerClient permedUser;
		readonly IServerClient permlessUser;

		readonly TaskCompletionSource finishTcs;

		readonly ConcurrentDictionary<long, JobResponse> seenJobs;

		readonly HashSet<long> permlessSeenJobs;

		HubConnection permedConn, permlessConn;
		bool permlessIsPermed;

		long? permlessPsId;

		public JobsHubTests(IServerClient permedUser, IServerClient permlessUser)
		{
			this.permedUser = permedUser;
			this.permlessUser = permlessUser;

			Assert.AreNotSame(permedUser, permlessUser);

			finishTcs = new TaskCompletionSource();

			seenJobs = new ConcurrentDictionary<long, JobResponse>();
			permlessSeenJobs = new HashSet<long>();
		}

		public Task ReceiveJobUpdate(JobResponse job, CancellationToken cancellationToken)
		{
			try
			{
				Assert.IsTrue(job.InstanceId.HasValue);
				Assert.IsNotNull(job.StartedBy);
				Assert.IsTrue(job.StartedBy.Id.HasValue);
				Assert.IsNotNull(job.StartedBy.Name);
				Assert.IsTrue(job.StartedAt.HasValue);
				Assert.IsNotNull(job.Description);

				seenJobs.AddOrUpdate(job.Id.Value, job, (_, old) =>
				{
					Assert.IsFalse(old.StoppedAt.HasValue, $"Received update for job {job.Id} after it had completed!");

					return job;
				});
			}
			catch(Exception ex)
			{
				finishTcs.SetException(ex);
			}

			return Task.CompletedTask;
		}


		class ShouldNeverReceiveUpdates : IJobsHub
		{
			public Action<JobResponse> Callback { get; set; }

			public Task ReceiveJobUpdate(JobResponse job, CancellationToken cancellationToken)
			{
				Callback(job);
				return Task.CompletedTask;
			}
		}

		public async Task Run(CancellationToken cancellationToken)
		{
			var neverReceiver = new ShouldNeverReceiveUpdates()
			{
				Callback = job =>
				{
					if (!permlessIsPermed)
						finishTcs.TrySetException(new Exception($"ShouldNeverReceiveUpdates received an update for job {job.Id}!"));
					else
						lock (permlessSeenJobs)
							permlessSeenJobs.Add(job.Id.Value);
				},
			};

			await using (permedConn = (HubConnection)await permedUser.SubscribeToJobUpdates(
				this,
				null,
				null,
				cancellationToken))
			await using (permlessConn = (HubConnection)await permlessUser.SubscribeToJobUpdates(
				neverReceiver,
				null,
				null,
				cancellationToken))
			{
				Console.WriteLine($"Initial conn1: {permedConn.ConnectionId}");
				Console.WriteLine($"Initial conn2: {permlessConn.ConnectionId}");

				permedConn.Reconnected += (newId) =>
				{
					Console.WriteLine($"conn1 reconnected: {newId}");
					return Task.CompletedTask;
				};
				permlessConn.Reconnected += (newId) =>
				{
					Console.WriteLine($"conn1 reconnected: {newId}");
					return Task.CompletedTask;
				};

				await finishTcs.Task;
			}

			var allInstances = await permedUser.Instances.List(null, cancellationToken);

			async ValueTask<List<JobResponse>> CheckInstance(InstanceResponse instance)
			{
				var wasOffline = !instance.Online.Value;
				if (wasOffline)
					await permedUser.Instances.Update(new InstanceUpdateRequest
					{
						Id = instance.Id,
						Online = true,
					}, cancellationToken);

				var jobs = await permedUser.Instances.CreateClient(instance).Jobs.List(null, cancellationToken);
				if (wasOffline)
					await permedUser.Instances.Update(new InstanceUpdateRequest
					{
						Id = instance.Id,
						Online = false,
					}, cancellationToken);

				return jobs;
			}

			var allJobsTask = allInstances
				.Select(CheckInstance);

			var allJobs = (await ValueTaskExtensions.WhenAll(allJobsTask, allInstances.Count)).SelectMany(x => x).ToList();
			var missableMissedJobs = 0;
			foreach (var job in allJobs)
			{
				var seenThisJob = seenJobs.TryGetValue(job.Id.Value, out var hubJob);
				if (seenThisJob)
				{
					if (hubJob.StoppedAt.HasValue)
					{
						Assert.AreEqual(job.StoppedAt, hubJob.StoppedAt);
						Assert.AreEqual(job.ExceptionDetails, hubJob.ExceptionDetails);
						Assert.AreEqual(job.Progress, hubJob.Progress);
						Assert.AreEqual(job.Stage, hubJob.Stage);
						Assert.AreEqual(job.ErrorCode, hubJob.ErrorCode);
						Assert.AreEqual(job.CancelledBy?.Id, hubJob.CancelledBy?.Id);
						Assert.AreEqual(job.Cancelled, hubJob.Cancelled);
					}

					static DateTimeOffset PerformDBTruncation(DateTimeOffset original)
						=> new DateTimeOffset(
							original.Ticks - (original.Ticks % TimeSpan.TicksPerSecond),
							original.Offset);

					Assert.AreEqual(job.InstanceId, hubJob.InstanceId);
					Assert.AreEqual(job.StartedBy?.Id, hubJob.StartedBy?.Id);
					Assert.AreEqual(job.CancelRight, hubJob.CancelRight);
					Assert.AreEqual(job.CancelRightsType, hubJob.CancelRightsType);
					Assert.AreEqual(job.Description, hubJob.Description);
					Assert.AreEqual(PerformDBTruncation(job.StartedAt.Value), PerformDBTruncation(hubJob.StartedAt.Value)); // RHS may NOT be DB truncated, both sides because not all DBs do this
					Assert.AreEqual(job.JobCode, hubJob.JobCode);
				}
				else
				{
					var wasMissableJob = job.JobCode == JobCode.ReconnectChatBot
						|| job.JobCode == JobCode.StartupWatchdogLaunch
						|| job.JobCode == JobCode.StartupWatchdogReattach;
					Assert.IsTrue(wasMissableJob);
					++missableMissedJobs;
				}
			}

			// some instances may be detached, but our cache remains
			var accountedJobs = allJobs.Count - missableMissedJobs;
			var accountedSeenJobs = seenJobs.Where(x => allInstances.Any(i => i.Id.Value == x.Value.InstanceId)).Count();
			Assert.AreEqual(accountedJobs, accountedSeenJobs);
			Assert.IsTrue(accountedJobs <= seenJobs.Count);
			Assert.AreNotEqual(0, permlessSeenJobs.Count);
			Assert.IsTrue(permlessSeenJobs.Count < seenJobs.Count);
			Assert.IsTrue(permlessSeenJobs.All(id => seenJobs.ContainsKey(id)));

			await using var conn3 = (HubConnection)await permedUser.SubscribeToJobUpdates(
				this,
				null,
				null,
				cancellationToken);

			Assert.AreEqual(HubConnectionState.Connected, conn3.State);
			await permlessUser.DisposeAsync();
			await permedUser.DisposeAsync();
		}

		public void ExpectShutdown()
		{
			Assert.AreEqual(HubConnectionState.Connected, permedConn.State);
			Assert.AreEqual(HubConnectionState.Connected, permlessConn.State);
		}

		public async ValueTask WaitForReconnect(CancellationToken cancellationToken)
		{
			await permlessConn.StopAsync(cancellationToken);
			await permedConn.StopAsync(cancellationToken);

			Assert.AreEqual(HubConnectionState.Disconnected, permedConn.State);
			Assert.AreEqual(HubConnectionState.Disconnected, permlessConn.State);

			// force token refreshs
			await Task.WhenAll(permedUser.Administration.Read(cancellationToken).AsTask(), permlessUser.Instances.List(null, cancellationToken).AsTask());

			if (!permlessPsId.HasValue)
			{
				var permlessUserId = long.Parse(permlessUser.Token.ParseJwt().Subject);
				permlessPsId = (await permedUser.Users.GetId(new Api.Models.EntityId
				{
					Id = permlessUserId
				}, cancellationToken)).PermissionSet.Id;
			}

			var instancesTask = permedUser.Instances.List(null, cancellationToken);

			permlessIsPermed = !permlessIsPermed;

			var instances = await instancesTask;
			await ValueTaskExtensions.WhenAll(
				instances
				.Where(instance => instance.Online.Value)
				.Select<InstanceResponse, ValueTask>(async instance =>
				{
					var ic = permedUser.Instances.CreateClient(instance);
					if (permlessIsPermed)
						await ic.PermissionSets.Create(new InstancePermissionSetRequest
						{
							PermissionSetId = permlessPsId.Value,
						}, cancellationToken);
					else
						await ic.PermissionSets.Delete(new InstancePermissionSetRequest
						{
							PermissionSetId = permlessPsId.Value
						}, cancellationToken);
				}));

			await permedConn.StartAsync(cancellationToken);
			await permlessConn.StartAsync(cancellationToken);

			Assert.AreEqual(HubConnectionState.Connected, permedConn.State);
			Assert.AreEqual(HubConnectionState.Connected, permlessConn.State);
			Console.WriteLine($"New conn1: {permedConn.ConnectionId}");
			Console.WriteLine($"New conn2: {permlessConn.ConnectionId}");
		}

		public void CompleteNow() => finishTcs.TrySetResult();
	}
}