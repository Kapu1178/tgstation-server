﻿using System;
using System.Globalization;

using Tgstation.Server.Api.Models;
using Tgstation.Server.Host.Components.Interop;
using Tgstation.Server.Host.Components.Session;

namespace Tgstation.Server.Host.Models
{
	/// <summary>
	/// Base class for <see cref="ReattachInformation"/>.
	/// </summary>
	public abstract class ReattachInformationBase : DMApiParameters
	{
		/// <summary>
		/// The system process ID.
		/// </summary>
		public int ProcessId { get; set; }

		/// <summary>
		/// The port DreamDaemon was last listening on.
		/// </summary>
		public ushort Port { get; set; }

		/// <summary>
		/// The current DreamDaemon reboot state.
		/// </summary>
		public RebootState RebootState { get; set; }

		/// <summary>
		/// The <see cref="DreamDaemonSecurity"/> level DreamDaemon was launched with.
		/// </summary>
		public DreamDaemonSecurity LaunchSecurityLevel { get; set; }

		/// <summary>
		/// The <see cref="DreamDaemonVisibility"/> DreamDaemon was launched with.
		/// </summary>
		public DreamDaemonVisibility LaunchVisibility { get; set; }

		/// <summary>
		/// Initializes a new instance of the <see cref="ReattachInformationBase"/> class.
		/// </summary>
		protected ReattachInformationBase()
		{
		}

		/// <summary>
		/// Initializes a new instance of the <see cref="ReattachInformationBase"/> class.
		/// </summary>
		/// <param name="copy">The <see cref="ReattachInformationBase"/> to copy values from.</param>
		protected ReattachInformationBase(ReattachInformationBase copy)
		{
			ArgumentNullException.ThrowIfNull(copy);
			AccessIdentifier = copy.AccessIdentifier;
			Port = copy.Port;
			ProcessId = copy.ProcessId;
			RebootState = copy.RebootState;
			LaunchSecurityLevel = copy.LaunchSecurityLevel;
			LaunchVisibility = copy.LaunchVisibility;
		}

		/// <inheritdoc />
		public override string ToString() => String.Format(CultureInfo.InvariantCulture, "Process ID: {0}, Access Identifier {1}, RebootState: {2}, Port: {3}", ProcessId, AccessIdentifier, RebootState, Port);
	}
}
