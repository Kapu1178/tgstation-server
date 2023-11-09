﻿using System;

using Tgstation.Server.Api.Models.Internal;

namespace Tgstation.Server.Api.Models.Response
{
	/// <inheritdoc />
	public sealed class CompileJobResponse : CompileJob
	{
		/// <summary>
		/// The <see cref="Job"/> relating to this job.
		/// </summary>
		public JobResponse? Job { get; set; }

		/// <summary>
		/// Git revision the compiler ran on.
		/// </summary>
		public RevisionInformation? RevisionInformation { get; set; }

		/// <summary>
		/// The <see cref="EngineVersion"/> the <see cref="CompileJobResponse"/> was made with.
		/// </summary>
		public EngineVersion? EngineVersion { get; set; }

		/// <summary>
		/// The origin <see cref="Uri"/> of the repository the compile job was built from.
		/// </summary>
		public Uri? RepositoryOrigin { get; set; }
	}
}
