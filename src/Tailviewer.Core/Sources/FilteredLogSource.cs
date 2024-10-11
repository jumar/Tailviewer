using System;
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
	///     A <see cref="ILogSource" /> implementation which offers a filtered view onto a log file.
	/// </summary>
	/// <remarks>
	///    Plugin authors are deliberately prevented from instantiating this type directly because it's constructor signature may change
	///    over time. In order to create an instance of this type, simply call <see cref="ILogSourceFactory.CreateFilteredLogFile"/>
	///    who's signature is guaranteed to never change.
	/// </remarks>
	[DebuggerTypeProxy(typeof(LogSourceDebuggerVisualization))]
	public sealed class FilteredLogSource
		: AbstractLogSource
		, ILogSourceListener
	{
		private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

		private const int BatchSize = 10000;

		private readonly ConcurrentPropertiesList _properties;
		private readonly PropertiesBufferList _propertiesBuffer;
		private readonly ILogLineFilter _logLineFilter;
		private readonly ILogEntryFilter _logEntryFilter;
		private readonly List<int> _indices;
		private readonly Dictionary<int, int> _logEntryIndices;
		private readonly ConcurrentQueue<LogSourceModification> _pendingModifications;
		private readonly ILogSource _source;
		private readonly LogBufferArray _array;
		private readonly TimeSpan _maximumWaitTime;

		private LogSourceSection _fullSourceSection;
		private int _maxCharactersPerLine;
		private int _currentSourceIndex;
		private readonly LogBufferList _lastLogBuffer;
		private int _currentLogEntryIndex;

		/// <summary>
		///     Initializes this object.
		/// </summary>
		/// <param name="scheduler"></param>
		/// <param name="maximumWaitTime"></param>
		/// <param name="source"></param>
		/// <param name="logLineFilter"></param>
		/// <param name="logEntryFilter"></param>
		public FilteredLogSource(ITaskScheduler scheduler,
			TimeSpan maximumWaitTime,
			ILogSource source,
			ILogLineFilter logLineFilter,
			ILogEntryFilter logEntryFilter)
			: base(scheduler)
		{
			_source = source ?? throw new ArgumentNullException(nameof(source));

			_properties = new ConcurrentPropertiesList(source.Properties);
			_propertiesBuffer = new PropertiesBufferList(); //< Will be used as temporary storage to hold the properties from the source

			_logLineFilter = logLineFilter ?? new NoFilter();
			_logEntryFilter = logEntryFilter ?? new NoFilter();
			_pendingModifications = new ConcurrentQueue<LogSourceModification>();
			_indices = new List<int>();
			_logEntryIndices = new Dictionary<int, int>();
			_array = new LogBufferArray(BatchSize, Core.Columns.Minimum);
			_lastLogBuffer = new LogBufferList(Core.Columns.Minimum);
			_maximumWaitTime = maximumWaitTime;

			_source.AddListener(this, maximumWaitTime, BatchSize);
			StartTask();
		}

		/// <inheritdoc />
		protected override void DisposeAdditional()
		{
			_source.RemoveListener(this);

			// https://github.com/Kittyfisto/Tailviewer/issues/282
			lock (_indices)
			{
				_indices.Clear();
				_indices.Capacity = 0;
			}

			_properties.Clear();
			_propertiesBuffer.Clear();
		}

		/// <inheritdoc />
		public override IReadOnlyList<IColumnDescriptor> Columns => Core.Columns.CombineWithMinimum(_source.Columns);

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

			_pendingModifications.Enqueue(modification);
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

			if (Equals(column, Core.Columns.Index))
			{
				GetIndex(sourceIndices, (LogLineIndex[])(object)destination, destinationIndex, queryOptions);
			}
			else if (Equals(column, Core.Columns.LogEntryIndex))
			{
				GetLogEntryIndex(sourceIndices, (LogEntryIndex[])(object)destination, destinationIndex, queryOptions);
			}
			else if (Equals(column, Core.Columns.DeltaTime))
			{
				GetDeltaTime(sourceIndices, (TimeSpan?[])(object)destination, destinationIndex, queryOptions);
			}
			else if (Equals(column, Core.Columns.LineNumber))
			{
				GetLineNumber(sourceIndices, (int[])(object)destination, destinationIndex, queryOptions);
			}
			else if (Equals(column, Core.Columns.OriginalIndex))
			{
				GetOriginalIndices(sourceIndices, (LogLineIndex[]) (object) destination, destinationIndex);
			}
			else
			{
				var actualIndices = GetOriginalIndices(sourceIndices);
				_source.GetColumn(actualIndices, column, destination, destinationIndex, queryOptions);
			}
		}

		private LogLineIndex[] GetOriginalIndices(IReadOnlyList<LogLineIndex> indices)
		{
			var actualIndices = new LogLineIndex[indices.Count];
			GetOriginalIndices(indices, actualIndices, 0);
			return actualIndices;
		}

		private void GetOriginalIndices(IReadOnlyList<LogLineIndex> indices, LogLineIndex[] destination, int destinationIndex)
		{
			lock (_indices)
			{
				for (int i = 0; i < indices.Count; ++i)
				{
					destination[destinationIndex + i] = ToSourceIndex(indices[i].Value);
				}
			}
		}

		/// <inheritdoc />
		public override void GetEntries(IReadOnlyList<LogLineIndex> sourceIndices, ILogBuffer destination, int destinationIndex, LogSourceQueryOptions queryOptions)
		{
			// TODO: This can probably be optimized (why are we translating indices each time for every column?!
			foreach (var column in destination.Columns)
			{
				destination.CopyFrom(column, destinationIndex, this, sourceIndices, queryOptions);
			}
		}

		private void GetIndex(IReadOnlyList<LogLineIndex> sourceIndices, LogLineIndex[] destination, int destinationIndex, LogSourceQueryOptions queryOptions)
		{
			lock (_indices)
			{
				for (int i = 0; i < sourceIndices.Count; ++i)
				{
					var sourceIndex = sourceIndices[i].Value;
					if (sourceIndex >= 0 && sourceIndex < _indices.Count)
					{
						destination[destinationIndex + i] = sourceIndex;
					}
					else
					{
						destination[destinationIndex + i] = Core.Columns.Index.DefaultValue;
					}
				}
			}
		}

		private void GetLogEntryIndex(IReadOnlyList<LogLineIndex> sourceIndices, LogEntryIndex[] destination, int destinationIndex, LogSourceQueryOptions queryOptions)
		{
			lock (_indices)
			{
				for (int i = 0; i < sourceIndices.Count; ++i)
				{
					var sourceIndex = sourceIndices[i].Value;
					if (sourceIndex >= 0 && sourceIndex < _indices.Count)
					{
						var originalIndex = _indices[sourceIndex];
						var logEntryIndex = _logEntryIndices[originalIndex];
						destination[destinationIndex + i] = logEntryIndex;
					}
					else
					{
						destination[destinationIndex + i] = Core.Columns.LogEntryIndex.DefaultValue;
					}
				}
			}
		}

		private void GetLineNumber(IReadOnlyList<LogLineIndex> indices, int[] destination, int destinationIndex, LogSourceQueryOptions queryOptions)
		{
			lock (_indices)
			{
				for (int i = 0; i < indices.Count; ++i)
				{
					var index = indices[i];
					if (index >= 0 && index < _indices.Count)
					{
						var lineNumber = (int) (index + 1);
						destination[destinationIndex + i] = lineNumber;
					}
					else
					{
						destination[destinationIndex + i] = Core.Columns.LineNumber.DefaultValue;
					}
				}
			}
		}
		
		private void GetDeltaTime(IReadOnlyList<LogLineIndex> indices, TimeSpan?[] destination, int destinationIndex, LogSourceQueryOptions queryOptions)
		{
			// The easiest way to serve random access to this column is to simply retrieve
			// the timestamp for every requested index as well as for the preceding index.
			var actualIndices = new LogLineIndex[indices.Count * 2];
			lock (_indices)
			{
				for(int i = 0; i < indices.Count; ++i)
				{
					var index = indices[i];
					actualIndices[i * 2 + 0] = ToSourceIndex(index - 1);
					actualIndices[i * 2 + 1] = ToSourceIndex(index);
				}
			}

			var timestamps = _source.GetColumn(actualIndices, Core.Columns.Timestamp);
			for (int i = 0; i < indices.Count; ++i)
			{
				var previousTimestamp = timestamps[i * 2 + 0];
				var currentTimestamp = timestamps[i * 2 + 1];
				destination[destinationIndex + i] = currentTimestamp - previousTimestamp;
			}
		}

		private LogLineIndex ToSourceIndex(LogLineIndex index)
		{
			if (index >= 0 && index < _indices.Count)
				return _indices[(int) index];

			return LogLineIndex.Invalid;
		}

		/// <inheritdoc />
		public override LogLineIndex GetLogLineIndexOfOriginalLineIndex(LogLineIndex originalSourceIndex)
		{
			lock (_indices)
			{
				for (int i = 0; i < _indices.Count; ++i)
				{
					if (_indices[i] == originalSourceIndex.Value)
					{
						return i;
					}
				}
			}

			return LogLineIndex.Invalid;
		}

		/// <inheritdoc />
		public override string ToString()
		{
			return string.Format("{0} (Filtered)", _source);
		}

		/// <inheritdoc />
		protected override TimeSpan RunOnce(CancellationToken token)
		{
			var performedWork= ProcessModifications(token);
			ProcessNewLogEntries(token);

			if (performedWork)
				return TimeSpan.Zero;

			return _maximumWaitTime;
		}

		/// <summary>
		///     Processes as many pending modifications as are available, removes existing indices if necessary and
		///     establishes the boundaries of the source log file.
		/// </summary>
		/// <param name="token"></param>
		/// <returns></returns>
		private bool ProcessModifications(CancellationToken token)
		{
			bool performedWork = false;
			while (_pendingModifications.TryDequeue(out var modification) && !token.IsCancellationRequested)
			{
				if (modification.IsReset())
				{
					Clear();
					_lastLogBuffer.Clear();
					_currentSourceIndex = 0;
				}
				else if (modification.IsRemoved(out var removedSection))
				{
					LogLineIndex startIndex = removedSection.Index;
					_fullSourceSection = new LogSourceSection(0, (int) startIndex);

					if (_currentSourceIndex > _fullSourceSection.LastIndex)
						_currentSourceIndex = (int) removedSection.Index;

					RemoveFrom(_currentSourceIndex);
					RemoveLinesFrom(_lastLogBuffer, _currentSourceIndex);
				}
				else if(modification.IsAppended(out var appendedSection))
				{
					_fullSourceSection = LogSourceSection.MinimumBoundingLine(_fullSourceSection, appendedSection);
				}

				performedWork = true;
			}

			return performedWork;
		}

		/// <summary>
		///    Processes all newly arrived log entries.
		/// </summary>
		/// <param name="token"></param>
		private void ProcessNewLogEntries(CancellationToken token)
		{
			if (!_fullSourceSection.IsEndOfSection(_currentSourceIndex))
			{
				int remaining = _fullSourceSection.Index + _fullSourceSection.Count - _currentSourceIndex;
				int nextCount = Math.Min(remaining, BatchSize);
				var nextSection = new LogSourceSection(_currentSourceIndex, nextCount);
				_source.GetEntries(nextSection, _array);

				for (int i = 0; i < nextCount; ++i)
				{
					if (token.IsCancellationRequested)
						break;

					var logEntry = _array[i];
					if (Log.IsDebugEnabled)
						Log.DebugFormat("Processing: LineIndex={0}, OriginalLineIndex={1}, LogEntryIndex={2}, Message={3}",
						                logEntry.Index,
						                logEntry.OriginalIndex,
						                logEntry.LogEntryIndex,
						                logEntry.RawContent);

					if (_lastLogBuffer.Count == 0 || _lastLogBuffer[0].LogEntryIndex == logEntry.LogEntryIndex)
					{
						TryAddLogLine(logEntry);
					}
					else if (logEntry.LogEntryIndex != _lastLogBuffer[0].LogEntryIndex)
					{
						TryAddLogEntry(_lastLogBuffer);
						_lastLogBuffer.Clear();
						TryAddLogLine(logEntry);
					}
				}

				_currentSourceIndex += nextCount;
			}

			// Now that we've processes all newly added log entries, we can check if we're at the end just yet...
			if (_fullSourceSection.IsEndOfSection(_currentSourceIndex))
			{
				TryAddLogEntry(_lastLogBuffer);
				UpdateProperties(); //< we need to update our own properties after we've added the last entry, but before we notify listeners...
				Listeners.OnRead(_indices.Count);

				if (_properties.GetValue(Core.Properties.PercentageProcessed) == Percentage.HundredPercent)
				{
					Listeners.Flush();
				}
			}
			else
			{
				UpdateProperties();
			}
		}

		private void UpdateProperties()
		{
			// First we want to retrieve all properties from the source
			_source.GetAllProperties(_propertiesBuffer);

			// Then we'll add / overwrite properties 
			_propertiesBuffer.SetValue(Core.Properties.PercentageProcessed, ComputePercentageProcessed());
			_propertiesBuffer.SetValue(Core.Properties.LogEntryCount, _indices.Count);

			// And last but not least we'll update our own properties to the new values
			// It's important we do this in one go so clients can retrieve all those properties
			// in a consistent state
			_properties.CopyFrom(_propertiesBuffer);
		}

		/// <summary>
		/// Computes the total percentage of log entries which have been processed so far, taking into account
		/// both how many log entries were filtered already, as well as how many were processed by the source log file.
		/// </summary>
		/// <returns></returns>
		[Pure]
		private Percentage ComputePercentageProcessed()
		{
			if (!_propertiesBuffer.TryGetValue(Core.Properties.PercentageProcessed, out var sourcePercentage))
				return Percentage.Zero;

			if (_fullSourceSection.Count <= 0)
				return sourcePercentage;

			var ownPercentage = Percentage.Of(_currentSourceIndex, _fullSourceSection.Count);
			var totalPercentage = (ownPercentage * sourcePercentage).Clamped();
			return totalPercentage;
		}

		private static void RemoveLinesFrom(LogBufferList lastLogBuffer, int currentSourceIndex)
		{
			while (lastLogBuffer.Count > 0)
			{
				int i = lastLogBuffer.Count - 1;
				var logEntry = lastLogBuffer[i];
				if (logEntry.Index >= currentSourceIndex)
				{
					lastLogBuffer.RemoveAt(i);
				}
				else
				{
					break;
				}
			}
		}

		private void RemoveFrom(int currentSourceIndex)
		{
			int numRemoved = 0;
			lock (_indices)
			{
				while (_indices.Count > 0)
				{
					int i = _indices.Count - 1;
					int sourceIndex = _indices[i];
					if (sourceIndex >= currentSourceIndex)
					{
						if (_logEntryIndices.TryGetValue(sourceIndex, out var previousLogEntryIndex))
						{
							_currentLogEntryIndex = previousLogEntryIndex;
						}
						_logEntryIndices.Remove(sourceIndex);

						_indices.RemoveAt(i);
						++numRemoved;
					}
					else
					{
						break;
					}
				}
			}
			Listeners.Remove(_indices.Count, numRemoved);
		}

		private void Clear()
		{
			_fullSourceSection = new LogSourceSection();
			lock (_indices)
			{
				_indices.Clear();
				_logEntryIndices.Clear();
				_currentLogEntryIndex = 0;
			}
			Listeners.OnRead(-1);
		}

		private void TryAddLogLine(IReadOnlyLogEntry logEntry)
		{
			// We have a filter that operates on individual lines (regardless of log entry affiliation).
			// We therefore have to evaluate each line for itself before we can even begin to consider adding a log
			// entry.
			if (_logLineFilter.PassesFilter(logEntry))
			{
				_lastLogBuffer.Add(logEntry);
			}
		}

		private bool TryAddLogEntry(IReadOnlyList<IReadOnlyLogEntry> logEntry)
		{
			if (_indices.Count > 0 && logEntry.Count > 0 &&
			    _indices[_indices.Count - 1] == logEntry[logEntry.Count - 1].Index)
				return true;

			if (_logEntryFilter.PassesFilter(logEntry))
			{
				lock (_indices)
				{
					if (logEntry.Count > 0)
					{
						foreach (var line in logEntry)
						{
							_indices.Add((int) line.Index);
							_logEntryIndices[(int) line.Index] = _currentLogEntryIndex;
							_maxCharactersPerLine = Math.Max(_maxCharactersPerLine, line.RawContent?.Length ?? 0);
						}
						++_currentLogEntryIndex;
					}
				}
				Listeners.OnRead(_indices.Count);
				return true;
			}
			
			return false;
		}
	}
}