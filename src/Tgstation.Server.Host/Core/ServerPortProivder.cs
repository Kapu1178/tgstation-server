﻿using System;
using System.Linq;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Tgstation.Server.Host.Configuration;

namespace Tgstation.Server.Host.Core
{
	/// <inheritdoc />
	sealed class ServerPortProivder : IServerPortProvider
	{
		/// <inheritdoc />
		public ushort HttpApiPort => generalConfiguration.ApiPort;

		/// <summary>
		/// The <see cref="GeneralConfiguration"/> for the <see cref="ServerPortProivder"/>.
		/// </summary>
		readonly GeneralConfiguration generalConfiguration;

		/// <summary>
		/// Initializes a new instance of the <see cref="ServerPortProivder"/> class.
		/// </summary>
		/// <param name="generalConfigurationOptions">The <see cref="IOptions{TOptions}"/> containing the value of <see cref="generalConfiguration"/>.</param>
		/// <param name="configuration">The <see cref="IConfiguration"/> to use.</param>
		/// <param name="logger">The <see cref="ILogger"/> to use.</param>
		public ServerPortProivder(
			IOptions<GeneralConfiguration> generalConfigurationOptions,
			IConfiguration configuration,
			ILogger<ServerPortProivder> logger)
		{
			generalConfiguration = generalConfigurationOptions?.Value ?? throw new ArgumentNullException(nameof(generalConfigurationOptions));
			ArgumentNullException.ThrowIfNull(configuration);

			var usingDefaultPort = generalConfiguration.ApiPort == default;
			if (!usingDefaultPort)
				return;

			var httpEndpoint = configuration
				.GetSection("Kestrel")
				.GetSection("EndPoints")
				.GetSection("Http")
				.GetSection("Url")
				.Value
				?? throw new InvalidOperationException("Missing required configuration option General:ApiPort!");

			logger.LogWarning("The \"Kestrel\" configuration section is deprecated! Please set your API port using the \"General:ApiPort\" configuration option!");

			var splits = httpEndpoint.Split(":", StringSplitOptions.RemoveEmptyEntries);
			var portString = splits.Last();
			portString = portString.TrimEnd('/');

			if (!UInt16.TryParse(portString, out var result))
				throw new InvalidOperationException($"Failed to parse HTTP EndPoint port: {httpEndpoint}");

			generalConfiguration.ApiPort = result;
		}
	}
}
