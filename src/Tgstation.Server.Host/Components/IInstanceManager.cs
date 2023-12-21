﻿using System.Threading.Tasks;

using Tgstation.Server.Host.Components.Interop.Bridge;

namespace Tgstation.Server.Host.Components
{
	/// <summary>
	/// For managing <see cref="IInstance"/>s.
	/// </summary>
	public interface IInstanceManager : IInstanceOperations, IBridgeDispatcher
	{
		/// <summary>
		/// <see cref="Task"/> that completes when the <see cref="IInstanceManager"/> finishes initializing.
		/// </summary>
		Task Ready { get; }

		/// <summary>
		/// Get the <see cref="IInstanceReference"/> associated with given <paramref name="metadata"/>.
		/// </summary>
		/// <param name="metadata">The <see cref="Models.Instance"/> of the desired <see cref="IInstance"/>.</param>
		/// <returns>The <see cref="IInstance"/> associated with the given <paramref name="metadata"/> if it is online, <see langword="null"/> otherwise.</returns>
		IInstanceReference GetInstanceReference(Api.Models.Instance metadata);
	}
}
