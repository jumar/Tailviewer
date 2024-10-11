﻿// ReSharper disable once CheckNamespace
namespace Tailviewer.Core
{
	/// <summary>
	/// Describes if a log entry was retrieved from a column or not.
	/// </summary>
	public enum RetrievalState : byte
	{
		/// <summary>
		/// The requested data wasn't retrieved because it isn't part of the source.
		/// </summary>
		NotInSource = 0,

		/// <summary>
		/// The requested data wasn't retrieved because it wasn't cached.
		/// </summary>
		NotCached = 1,

		/// <summary>
		/// THe requested data was retrieved.
		/// </summary>
		Retrieved = 2
	}
}