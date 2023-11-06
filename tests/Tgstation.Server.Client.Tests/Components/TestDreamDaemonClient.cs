﻿using System;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Moq;

using Tgstation.Server.Api;
using Tgstation.Server.Api.Models.Response;

namespace Tgstation.Server.Client.Components.Tests
{
	[TestClass]
	public sealed class TestDreamDaemonClient
	{
		[TestMethod]
		public async Task TestStart()
		{
			var example = new JobResponse
			{
				Id = 347,
				StartedAt = DateTimeOffset.UtcNow
			};

			var inst = new InstanceResponse
			{
				Id = 4958
			};

			var mockApiClient = new Mock<IApiClient>();
			var result2 = ValueTask.FromResult(example);
			mockApiClient.Setup(x => x.Create<JobResponse>(Routes.DreamDaemon, inst.Id.Value, It.IsAny<CancellationToken>())).Returns(result2);

			var client = new DreamDaemonClient(mockApiClient.Object, inst);

			var result = await client.Start(default);
			Assert.AreSame(example, result);
		}
	}
}
