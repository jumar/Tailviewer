﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.Reflection;
using System.Threading;
using log4net;
using Tailviewer.Api;

// ReSharper disable once CheckNamespace
namespace Tailviewer.Core
{
	/// <summary>
	///     Responsible for merging consecutive lines into multi-line log entries,
	///     if they belong together.
	/// </summary>
	/// <remarks>
	///     Two lines are defined to belong together if the first line contains a log
	///     level and the next one does not.
	/// </remarks>
	/// <remarks>
	///    Plugin authors are deliberately prevented from instantiating this type directly because it's constructor signature may change
	///    over time. In order to create an instance of this type, simply call <see cref="ILogSourceFactory.CreateMultiLineLogFile"/>
	///    who's signature is guaranteed to never change.
	/// </remarks>
	[DebuggerTypeProxy(typeof(LogSourceDebuggerVisualization))]
	public sealed class MultiLineLogSource
		: AbstractLogSource
		, ILogSourceListener
	{
		private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

		private const int MaximumBatchSize = 10000;

		private readonly object _syncRoot;
		private readonly HashSet<IColumnDescriptor> _specialColumns;
		private readonly List<LogEntryInfo> _indices;
		private readonly TimeSpan _maximumWaitTime;
		private readonly ConcurrentQueue<LogSourceModification> _pendingModifications;
		private readonly ILogSource _source;
		private readonly ConcurrentPropertiesList _properties;
		private readonly PropertiesBufferList _propertiesBuffer;
		private LogEntryInfo _currentLogEntry;
		private LogLineIndex _currentSourceIndex;

		private LogSourceSection _fullSourceSection;

		/// <summary>
		///     Initializes this object.
		/// </summary>
		/// <remarks>
		///    Plugin authors are deliberately prevented from calling this constructor directly because it's signature may change
		///    over time. In order to create an instance of this type, simply call <see cref="ILogSourceFactory.CreateMultiLineLogFile"/>.
		/// </remarks>
		/// <param name="taskScheduler"></param>
		/// <param name="source"></param>
		/// <param name="maximumWaitTime"></param>
		public MultiLineLogSource(ITaskScheduler taskScheduler, ILogSource source, TimeSpan maximumWaitTime)
			: base(taskScheduler)
		{
			if (source == null)
				throw new ArgumentNullException(nameof(source));

			_maximumWaitTime = maximumWaitTime;
			_pendingModifications = new ConcurrentQueue<LogSourceModification>();
			_syncRoot = new object();
			_specialColumns = new HashSet<IColumnDescriptor>{Core.Columns.LogEntryIndex, Core.Columns.Timestamp, Core.Columns.LogLevel};
			_indices = new List<LogEntryInfo>();

			// The log file we were given might offer even more properties than the minimum set and we
			// want to expose those as well.
			_propertiesBuffer = new PropertiesBufferList(Core.Properties.CombineWithMinimum(source.Properties));
			_propertiesBuffer.SetValue(Core.Properties.EmptyReason, null);

			_properties = new ConcurrentPropertiesList(Core.Properties.CombineWithMinimum(source.Properties));
			_properties.CopyFrom(_propertiesBuffer);

			_currentLogEntry = new LogEntryInfo(-1, 0);

			_source = source;
			_source.AddListener(this, maximumWaitTime, MaximumBatchSize);
			StartTask();
		}

		/// <inheritdoc />
		public override IReadOnlyList<IColumnDescriptor> Columns => _source.Columns;

		/// <inheritdoc />
		public override IReadOnlyList<IReadOnlyPropertyDescriptor> Properties => _properties.Properties;

		/// <inheritdoc />
		public override object GetProperty(IReadOnlyPropertyDescriptor property)
		{
			_properties.TryGetValue(property, out var value);
			return value;
		}

		/// <inheritdoc />
		public override T GetProperty<T>(IReadOnlyPropertyDescriptor<T> property)
		{
			_properties.TryGetValue(property, out var value);
			return value;
		}

		public override void SetProperty(IPropertyDescriptor property, object value)
		{
			_source.SetProperty(property, value);
		}

		public override void SetProperty<T>(IPropertyDescriptor<T> property, T value)
		{
			_source.SetProperty(property, value);
		}

		/// <inheritdoc />
		public override void GetAllProperties(IPropertiesBuffer destination)
		{
			_properties.CopyAllValuesTo(destination);
		}

		/// <inheritdoc />
		public void OnLogFileModified(ILogSource logSource, LogSourceModification modification)
		{
			Log.DebugFormat("OnLogFileModified({0})", modification);

			_pendingModifications.EnqueueMany(modification.Split(MaximumBatchSize));
		}

		/// <inheritdoc />
		protected override void DisposeAdditional()
		{
			_source.RemoveListener(this);
			_pendingModifications.Clear();

			// https://github.com/Kittyfisto/Tailviewer/issues/282
			lock (_syncRoot)
			{
				_indices.Clear();
				_indices.Capacity = 0;
				_currentSourceIndex = 0;
			}

			_properties.Clear();
		}

		/// <inheritdoc />
		public override void GetColumn<T>(IReadOnlyList<LogLineIndex> sourceIndices, IColumnDescriptor<T> column, T[] destination, int destinationIndex, LogSourceQueryOptions queryOptions)
		{
			if (sourceIndices == null)
				throw new ArgumentNullException(nameof(sourceIndices));
			if (column == null)
				throw new ArgumentNullException(nameof(column));
			if (destination == null)
				throw new ArgumentNullException(nameof(destination));
			if (destinationIndex < 0)
				throw new ArgumentOutOfRangeException(nameof(destinationIndex));
			if (destinationIndex + sourceIndices.Count > destination.Length)
				throw new ArgumentException("The given buffer must have an equal or greater length than destinationIndex+length");

			if (!TryGetSpecialColumn(sourceIndices, column, destination, destinationIndex, queryOptions))
			{
				_source.GetColumn(sourceIndices, column, destination, destinationIndex, queryOptions);
			}
		}

		/// <inheritdoc />
		public override void GetEntries(IReadOnlyList<LogLineIndex> sourceIndices, ILogBuffer destination, int destinationIndex, LogSourceQueryOptions queryOptions)
		{
			if (IsDisposed)
			{
				destination.FillDefault(destinationIndex, sourceIndices.Count);
				return;
			}

			var remainingColumns = new List<IColumnDescriptor>();
			bool partiallyRetrieved = false;
			foreach (var column in destination.Columns)
			{
				if (_specialColumns.Contains(column))
				{
					destination.CopyFrom(column, destinationIndex, this, sourceIndices, queryOptions);
					partiallyRetrieved = true;
				}
				else
				{
					remainingColumns.Add(column);
				}
			}

			if (remainingColumns.Count > 0)
			{
				if (partiallyRetrieved)
				{
					var view = new LogBufferView(destination, remainingColumns);
					_source.GetEntries(sourceIndices, view, destinationIndex, queryOptions);
				}
				else
				{
					_source.GetEntries(sourceIndices, destination, destinationIndex, queryOptions);
				}
			}
		}

		/// <inheritdoc />
		public override string ToString()
		{
			return $"MultiLineLogFile({_source})";
		}

		/// <inheritdoc />
		protected override TimeSpan RunOnce(CancellationToken token)
		{
			bool performedWork = false;

			// Every Process() invocation locks the sync root until
			// the changes have been processed. The goal is to minimize
			// total process time and to prevent locking for too long.
			// The following number has been empirically determined
			// via testing and it felt alright :P
			const int maxLineCount = 10000;
			if (_pendingModifications.TryDequeueUpTo(maxLineCount, out var modifications))
			{
				foreach (var modification in modifications)
				{
					if (modification.IsReset())
					{
						Clear();
					}
					else if (modification.IsRemoved(out var removedSection))
					{
						Remove(removedSection);
					}
					else if (modification.IsAppended(out var appendedSection))
					{
						Append(appendedSection);
					}

					performedWork = true;
				}
			}

			UpdateProperties();

			if (_indices.Count != _currentSourceIndex)
			{
				Log.ErrorFormat("Inconsistency detected: We have {0} indices for {1} lines", _indices.Count,
				                _currentSourceIndex);
			}

			Listeners.OnRead((int)_currentSourceIndex);

			if (_properties.GetValue(Core.Properties.PercentageProcessed) == Percentage.HundredPercent)
			{
				Listeners.Flush();
			}

			if (performedWork)
				return TimeSpan.Zero;

			return _maximumWaitTime;
		}

		private void UpdateProperties()
		{
			// Now we can perform a block-copy of all properties and then update our own as desired..
			_source.GetAllProperties(_propertiesBuffer);

			var sourceProcessed = _propertiesBuffer.GetValue(Core.Properties.PercentageProcessed);
			var sourceCount = _propertiesBuffer.GetValue(Core.Properties.LogEntryCount);
			var ownProgress = sourceCount > 0
				? Percentage.Of(_indices.Count, sourceCount).Clamped()
				: Percentage.HundredPercent;
			var totalProgress = (sourceProcessed * ownProgress).Clamped();

			_propertiesBuffer.SetValue(Core.Properties.PercentageProcessed, totalProgress);
			_propertiesBuffer.SetValue(Core.Properties.LogEntryCount, (int)_currentSourceIndex);

			// We want to update all properties at once, hence we modify _sourceProperties where necessary and then
			// move them to our properties in a single call
			_properties.CopyFrom(_propertiesBuffer);
		}

		private void Append(LogSourceSection section)
		{
			var buffer = new LogBufferArray(section.Count, Core.Columns.Index, Core.Columns.Timestamp, Core.Columns.LogLevel);
			_source.GetEntries(section, buffer);

			lock (_syncRoot)
			{
				for (var i = 0; i < section.Count; ++i)
				{
					var line = buffer[i];

					if (_currentLogEntry.EntryIndex.IsInvalid ||
					    !AppendToCurrentLogEntry(line))
					{
						_currentLogEntry = _currentLogEntry.NextEntry(line.Index);
					}

					_indices.Add(_currentLogEntry);
				}
			}

			_currentSourceIndex += section.Count;
			_fullSourceSection = new LogSourceSection(0, _currentSourceIndex.Value);
		}

		private void Remove(LogSourceSection sectionToRemove)
		{
			var firstRemovedIndex = LogLineIndex.Min(_fullSourceSection.LastIndex, sectionToRemove.Index);
			var lastRemovedIndex = LogLineIndex.Min(_fullSourceSection.LastIndex, sectionToRemove.LastIndex);
			var removedCount = lastRemovedIndex - firstRemovedIndex + 1;
			var previousSourceIndex = _currentSourceIndex;

			_fullSourceSection = new LogSourceSection(0, (int)firstRemovedIndex);
			if (_fullSourceSection.Count > 0)
			{
				// It's possible (likely) that we've received an invalidation for a region of the source
				// that we've already processed (i.e. created indices for). If that's the case, then we need
				// to rewind the index. Otherwise nothing needs to be done...
				var newIndex = _fullSourceSection.LastIndex + 1;
				if (newIndex < _currentSourceIndex)
				{
					_currentSourceIndex = newIndex;
				}
			}
			else
			{
				_currentSourceIndex = 0;
			}

			lock (_syncRoot)
			{
				var toRemove = _indices.Count - lastRemovedIndex;
				if (toRemove > 0)
				{
					_indices.RemoveRange((int)firstRemovedIndex, toRemove);
					_currentLogEntry = new LogEntryInfo(firstRemovedIndex - 1, 0);
				}
				if (previousSourceIndex != _currentSourceIndex)
				{
					_indices.RemoveRange((int) _currentSourceIndex, _indices.Count - _currentSourceIndex);
				}
			}

			if (_indices.Count != _currentSourceIndex)
			{
				Log.ErrorFormat("Inconsistency detected: We have {0} indices for {1} lines", _indices.Count,
					_currentSourceIndex);
			}

			Listeners.Remove((int)firstRemovedIndex, removedCount);

			if (_fullSourceSection.Count > firstRemovedIndex)
			{
				_fullSourceSection = new LogSourceSection(0, firstRemovedIndex.Value);
			}
		}

		private void Clear()
		{
			_fullSourceSection = new LogSourceSection(0, 0);
			_currentSourceIndex = 0;
			_currentLogEntry = new LogEntryInfo(-1, 0);
			lock (_syncRoot)
			{
				_indices.Clear();
			}
			Listeners.OnRead(-1);
		}

		private bool TryGetSpecialColumn<T>(IReadOnlyList<LogLineIndex> indices, IColumnDescriptor<T> column, T[] buffer, int destinationIndex, LogSourceQueryOptions queryOptions)
		{
			if (Equals(column, Core.Columns.Timestamp) ||
			    Equals(column, Core.Columns.LogLevel))
			{
				var firstLineIndices = GetFirstLineIndices(indices);
				_source.GetColumn(firstLineIndices, column, buffer, destinationIndex, queryOptions);
				return true;
			}

			if (Equals(column, Core.Columns.LogEntryIndex))
			{
				GetLogEntryIndex(indices, (LogEntryIndex[])(object)buffer, destinationIndex);
				return true;
			}

			return false;
		}

		private bool AppendToCurrentLogEntry(IReadOnlyLogEntry logLine)
		{
			if (logLine.Timestamp != null)
				return false; //< A line with a timestamp is never added to a previous log entry
			if (logLine.LogLevel != LevelFlags.None && logLine.LogLevel != LevelFlags.Other)
				return false; //< A line with a log level is never added to a previous log entry

			return true;
		}

		private void GetLogEntryIndex(IReadOnlyList<LogLineIndex> indices, LogEntryIndex[] buffer, int destinationIndex)
		{
			lock (_syncRoot)
			{
				for(int i = 0; i < indices.Count; ++i)
				{
					var index = indices[i];
					var entryInfo = TryGetLogEntryInfo(index);
					buffer[destinationIndex + i] = entryInfo?.EntryIndex ?? LogEntryIndex.Invalid;
				}
			}
		}

		private IReadOnlyList<LogLineIndex> GetFirstLineIndices(IReadOnlyList<LogLineIndex> indices)
		{
			lock (_syncRoot)
			{
				var firstLineIndices = new List<LogLineIndex>(indices.Count);
				foreach (var index in indices)
				{
					var entryInfo = TryGetLogEntryInfo(index);
					if (entryInfo != null)
						firstLineIndices.Add(entryInfo.Value.FirstLineIndex);
					else
						firstLineIndices.Add(LogLineIndex.Invalid);
				}
				return firstLineIndices;
			}
		}

		private LogEntryInfo? TryGetLogEntryInfo(LogLineIndex logLineIndex)
		{
			if (logLineIndex >= 0 && logLineIndex < _indices.Count)
			{
				return _indices[(int) logLineIndex];
			}
			return null;
		}

		private struct LogEntryInfo
		{
			public readonly LogEntryIndex EntryIndex;
			public readonly LogLineIndex FirstLineIndex;

			public LogEntryInfo(LogEntryIndex entryIndex, LogLineIndex firstLineIndex)
			{
				EntryIndex = entryIndex;
				FirstLineIndex = firstLineIndex;
			}

			[Pure]
			public LogEntryInfo NextEntry(LogLineIndex lineLineIndex)
			{
				return new LogEntryInfo(EntryIndex + 1, lineLineIndex);
			}

			public override string ToString()
			{
				return string.Format("Log entry {0} starting at line {1}", EntryIndex, FirstLineIndex);
			}
		}
	}
}