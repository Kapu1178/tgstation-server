using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using Tgstation.Server.Api.Models;
using Tgstation.Server.Host.Configuration;
using Tgstation.Server.Host.Core;
using Tgstation.Server.Host.System;

namespace Tgstation.Server.Host.Security.OAuth
{
	/// <inheritdoc />
	sealed class OAuthProviders : IOAuthProviders
	{
		/// <summary>
		/// The <see cref="IReadOnlyCollection{T}"/> of <see cref="IOAuthValidator"/>s.
		/// </summary>
		readonly IReadOnlyCollection<IOAuthValidator> validators;

		/// <summary>
		/// Initializes a new instance of the <see cref="OAuthProviders"/> <see langword="class"/>.
		/// </summary>
		/// <param name="gitHubClientFactory">The <see cref="IGitHubClientFactory"/> to use.</param>
		/// <param name="httpClientFactory">The <see cref="IHttpClientFactory"/> to use.</param>
		/// <param name="assemblyInformationProvider">The <see cref="IAssemblyInformationProvider"/> to use.</param>
		/// <param name="loggerFactory">The <see cref="ILoggerFactory"/> to use.</param>
		/// <param name="securityConfigurationOptions">The <see cref="IOptions{TOptions}"/> containing the <see cref="SecurityConfiguration"/> to use.</param>
		public OAuthProviders(
			IGitHubClientFactory gitHubClientFactory,
			IHttpClientFactory httpClientFactory,
			IAssemblyInformationProvider assemblyInformationProvider,
			ILoggerFactory loggerFactory,
			IOptions<SecurityConfiguration> securityConfigurationOptions)
		{
			if (loggerFactory == null)
				throw new ArgumentNullException(nameof(loggerFactory));

			var securityConfiguration = securityConfigurationOptions?.Value ?? throw new ArgumentNullException(nameof(securityConfigurationOptions));

			var validatorsBuilder = new List<IOAuthValidator>();

			if (securityConfiguration.GitHubOAuth != null)
				validatorsBuilder.Add(
					new GitHubOAuthValidator(
						gitHubClientFactory,
						loggerFactory.CreateLogger<GitHubOAuthValidator>(),
						securityConfiguration.GitHubOAuth));

			if (securityConfiguration.DiscordOAuth != null)
				validatorsBuilder.Add(
					new DiscordOAuthValidator(
						httpClientFactory,
						assemblyInformationProvider,
						loggerFactory.CreateLogger<DiscordOAuthValidator>(),
						securityConfiguration.DiscordOAuth));

			validators = validatorsBuilder;
		}

		/// <inheritdoc />
		public IOAuthValidator GetValidator(OAuthProvider oAuthProvider) => validators.First(x => x.Provider == oAuthProvider);

		/// <inheritdoc />
		public Dictionary<OAuthProvider, string> ClientIds() => validators
			.ToDictionary(
				x => x.Provider,
				x => x.ClientId);
	}
}
