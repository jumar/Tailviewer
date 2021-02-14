﻿using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Tailviewer.BusinessLogic.LogFiles;

namespace Tailviewer.Core.LogFiles
{
	/// <summary>
	///     Provides a view onto another <see cref="ILogFileProperties" /> with a reduced set of properties.
	/// </summary>
	/// <remarks>
	///     This class implements a similar concept to what System.Span{T} implements: The data isn't copied over, instead
	///     it offers a view onto a sub-region (in this case limited by the set of properties given during construction) of
	///     properties and values which still reside in another <see cref="ILogFileProperties"/> object: When properties change
	///     in the source, then so do they change when viewed through this object.
	/// </remarks>
	[DebuggerTypeProxy(typeof(LogFilePropertiesDebuggerView))]
	public sealed class LogFilePropertiesView
		: ILogFileProperties
	{
		private readonly IReadOnlyList<ILogFilePropertyDescriptor> _properties;
		private readonly ILogFileProperties _source;

		/// <summary>
		/// </summary>
		/// <param name="source"></param>
		/// <param name="properties"></param>
		public LogFilePropertiesView(ILogFileProperties source, IReadOnlyList<ILogFilePropertyDescriptor> properties)
		{
			_source = source;
			_properties = properties;
		}

		#region Implementation of ILogFileProperties

		/// <inheritdoc />
		public IReadOnlyList<ILogFilePropertyDescriptor> Properties
		{
			get { return _properties; }
		}

		/// <inheritdoc />
		public void CopyFrom(ILogFileProperties properties)
		{
			properties.CopyAllValuesTo(this);
		}

		/// <inheritdoc />
		public void SetValue(ILogFilePropertyDescriptor property, object value)
		{
			if (!_properties.Contains(property))
				return;

			_source.SetValue(property, value);
		}

		/// <inheritdoc />
		public void SetValue<T>(ILogFilePropertyDescriptor<T> property, T value)
		{
			if (!_properties.Contains(property))
				return;

			_source.SetValue(property, value);
		}

		/// <inheritdoc />
		public bool TryGetValue(ILogFilePropertyDescriptor property, out object value)
		{
			if (!_properties.Contains(property))
			{
				value = property.DefaultValue;
				return false;
			}

			return _source.TryGetValue(property, out value);
		}

		/// <inheritdoc />
		public bool TryGetValue<T>(ILogFilePropertyDescriptor<T> property, out T value)
		{
			if (!_properties.Contains(property))
			{
				value = property.DefaultValue;
				return false;
			}

			return _source.TryGetValue(property, out value);
		}

		/// <inheritdoc />
		public object GetValue(ILogFilePropertyDescriptor property)
		{
			if (!_properties.Contains(property))
				return property.DefaultValue;

			return _source.GetValue(property);
		}

		/// <inheritdoc />
		public T GetValue<T>(ILogFilePropertyDescriptor<T> property)
		{
			if (!_properties.Contains(property))
				return property.DefaultValue;

			return _source.GetValue(property);
		}

		/// <inheritdoc />
		public void CopyAllValuesTo(ILogFileProperties destination)
		{
			foreach (var property in _properties) destination.SetValue(property, GetValue(property));
		}

		#endregion
	}
}