﻿using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Tgstation.Server.Host.Watchdog;

namespace Tgstation.Server.Host.Service.Tests
{
	/// <summary>
	/// Tests for <see cref="ServerService"/>
	/// </summary>
	[TestClass]
	public sealed class TestServerService
	{
		[TestMethod]
		public void TestConstructionAndDisposal()
		{
			Assert.ThrowsException<ArgumentNullException>(() => new ServerService(null, null, default));
			var mockWatchdogFactory = new Mock<IWatchdogFactory>();
			Assert.ThrowsException<ArgumentNullException>(() => new ServerService(mockWatchdogFactory.Object, null, default));
			new ServerService(mockWatchdogFactory.Object, Array.Empty<string>(), default).Dispose();
		}

		[TestMethod]
		public void TestRun()
		{
			var type = typeof(ServerService);
			var onStart = type.GetMethod("OnStart", BindingFlags.Instance | BindingFlags.NonPublic);
			var onStop = type.GetMethod("OnStop", BindingFlags.Instance | BindingFlags.NonPublic);

			var mockWatchdog = new Mock<IWatchdog>();
			var args = Array.Empty<string>();
			CancellationToken cancellationToken;
			mockWatchdog.Setup(x => x.RunAsync(false, args, It.IsAny<CancellationToken>())).Callback((bool x, string[] _, CancellationToken token) => cancellationToken = token).Returns(Task.FromResult(true)).Verifiable();
			var mockWatchdogFactory = new Mock<IWatchdogFactory>();
			mockWatchdogFactory.Setup(x => x.CreateWatchdog(It.IsNotNull<ISignalChecker>(), It.IsNotNull<ILoggerFactory>())).Returns(mockWatchdog.Object).Verifiable();

			using (var service = new ServerService(mockWatchdogFactory.Object, Array.Empty<string>(), default))
			{
				onStart.Invoke(service, new object[] { args });
				onStop.Invoke(service, Array.Empty<object>());
				mockWatchdog.VerifyAll();
			}

			mockWatchdogFactory.VerifyAll();
		}
	}
}
