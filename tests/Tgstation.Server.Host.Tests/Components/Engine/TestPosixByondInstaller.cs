﻿using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

using Tgstation.Server.Api.Models;
using Tgstation.Server.Api.Models.Internal;
using Tgstation.Server.Host.IO;

namespace Tgstation.Server.Host.Components.Engine.Tests
{
	[TestClass]
	public sealed class TestPosixByondInstaller
	{
		[TestMethod]
		public void TestConstruction()
		{
			Assert.ThrowsException<ArgumentNullException>(() => new PosixByondInstaller(null, null, null, null));
			var mockPostWriteHandler = new Mock<IPostWriteHandler>();
			Assert.ThrowsException<ArgumentNullException>(() => new PosixByondInstaller(mockPostWriteHandler.Object, null, null, null));
			var mockIOManager = new Mock<IIOManager>();
			Assert.ThrowsException<ArgumentNullException>(() => new PosixByondInstaller(mockPostWriteHandler.Object, mockIOManager.Object, null, null));
			var mockFileDownloader = Mock.Of<IFileDownloader>();
			Assert.ThrowsException<ArgumentNullException>(() => new PosixByondInstaller(mockPostWriteHandler.Object, mockIOManager.Object, mockFileDownloader, null));

			var mockLogger = new Mock<ILogger<PosixByondInstaller>>();
			_ = new PosixByondInstaller(mockPostWriteHandler.Object, mockIOManager.Object, mockFileDownloader, mockLogger.Object);
		}

		[TestMethod]
		public async Task TestCacheClean()
		{
			var mockPostWriteHandler = new Mock<IPostWriteHandler>();
			var mockIOManager = new Mock<IIOManager>();
			var mockLogger = new Mock<ILogger<PosixByondInstaller>>();
			var mockFileDownloader = Mock.Of<IFileDownloader>();
			var installer = new PosixByondInstaller(mockPostWriteHandler.Object, mockIOManager.Object, mockFileDownloader, mockLogger.Object);
			await installer.CleanCache(default);
		}


		[TestMethod]
		public async Task TestDownload()
		{
			var mockIOManager = new Mock<IIOManager>();
			var mockPostWriteHandler = new Mock<IPostWriteHandler>();
			var mockLogger = new Mock<ILogger<PosixByondInstaller>>();
			var mockFileDownloader = new Mock<IFileDownloader>();
			var installer = new PosixByondInstaller(mockPostWriteHandler.Object, mockIOManager.Object, mockFileDownloader.Object, mockLogger.Object);

			await Assert.ThrowsExceptionAsync<ArgumentNullException>(() => installer.DownloadVersion(null, null, default).AsTask());

			var ourArray = Array.Empty<byte>();
			mockFileDownloader
				.Setup(
					x => x.DownloadFile(
						It.Is<Uri>(uri => uri == new Uri("https://www.byond.com/download/build/511/511.1385_byond_linux.zip")),
						null))
				.Returns(
					new BufferedFileStreamProvider(
						new MemoryStream(ourArray)))
				.Verifiable();

			var result = ExtractMemoryStreamFromInstallationData(await installer.DownloadVersion(new EngineVersion
			{
				Engine = EngineType.Byond,
				Version = new Version(511, 1385),
			}, null, default));

			Assert.IsTrue(ourArray.SequenceEqual(result.ToArray()));
			mockIOManager.Verify();
		}
		static MemoryStream ExtractMemoryStreamFromInstallationData(IEngineInstallationData engineInstallationData)
		{
			var zipStreamData = (ZipStreamEngineInstallationData)engineInstallationData;
			return (MemoryStream)zipStreamData.GetType().GetField("zipStream", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(zipStreamData);
		}

		[TestMethod]
		public async Task TestInstallByond()
		{
			var mockIOManager = new Mock<IIOManager>();
			var mockPostWriteHandler = new Mock<IPostWriteHandler>();
			var mockLogger = new Mock<ILogger<PosixByondInstaller>>();
			var mockFileDownloader = Mock.Of<IFileDownloader>();
			var installer = new PosixByondInstaller(mockPostWriteHandler.Object, mockIOManager.Object, mockFileDownloader, mockLogger.Object);

			const string FakePath = "fake";
			await Assert.ThrowsExceptionAsync<ArgumentNullException>(() => installer.Install(null, null, default).AsTask());

			var byondVersion = new EngineVersion
			{
				Engine = EngineType.Byond,
				Version = new Version(123, 252345),
			};

			await Assert.ThrowsExceptionAsync<ArgumentNullException>(() => installer.Install(byondVersion, null, default).AsTask());

			byondVersion.Version = new Version(511, 1385);
			await installer.Install(byondVersion, FakePath, default);

			mockPostWriteHandler.Verify(x => x.HandleWrite(It.IsAny<string>()), Times.Exactly(4));
		}
	}
}
