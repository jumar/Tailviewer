using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;

namespace Tailviewer.Core.Plugins
{
	/// <summary>
	///     Describes a plugin that is located inside a .NET assembly.
	/// </summary>
	public sealed class PluginDescription
		: IPluginDescription
	{
		private static readonly ReadOnlyDictionary<Type, string> NoPlugins;

		static PluginDescription()
		{
			NoPlugins = new ReadOnlyDictionary<Type, string>(new Dictionary<Type, string>());
		}

		/// <summary>
		///     Initializes this plugin descriptions.
		/// </summary>
		public PluginDescription()
		{
			Plugins = NoPlugins;
		}

		/// <inheritdoc />
		public string FilePath { get; set; }

		/// <inheritdoc />
		public string Name
		{
			get
			{
				var path = FilePath;
				var name = Path.GetFileNameWithoutExtension(path);
				return name;
			}
		}

		/// <inheritdoc />
		public string Author { get; set; }

		/// <inheritdoc />
		public string Description { get; set; }

		/// <inheritdoc />
		public Uri Website { get; set; }

		/// <inheritdoc />
		public string Error { get; set; }

		/// <inheritdoc />
		public IReadOnlyDictionary<Type, string> Plugins { get; set; }
	}
}