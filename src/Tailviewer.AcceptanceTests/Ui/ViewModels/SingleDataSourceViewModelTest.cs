﻿using System;
using System.Text;
using System.Threading;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using Tailviewer.AcceptanceTests.BusinessLogic.Sources.Text;
using Tailviewer.BusinessLogic.ActionCenter;
using Tailviewer.BusinessLogic.DataSources;
using Tailviewer.Core;
using Tailviewer.Core.Properties;
using Tailviewer.Core.Sources.Text;
using Tailviewer.Core.Sources.Text.Simple;
using Tailviewer.Settings;
using Tailviewer.Test;
using Tailviewer.Ui.ViewModels;

namespace Tailviewer.AcceptanceTests.Ui.ViewModels
{
	[TestFixture]
	public sealed class SingleDataSourceViewModelTest
	{
		private DefaultTaskScheduler _taskScheduler;

		[SetUp]
		public void SetUp()
		{
			_taskScheduler = new DefaultTaskScheduler();
		}

		[TearDown]
		public void TearDown()
		{
			_taskScheduler.Dispose();
		}

		private TextLogSource Create(string fileName)
		{
			var serviceContainer = new ServiceContainer();
			return new TextLogSource(_taskScheduler, fileName, LogFileFormats.GenericText, Encoding.Default);
		}

		[Test]
		[Ignore("I broke this one")]
		[LocalTest("AppVeyor doesn't like this test very much")]
		[Description("Verifies that the number of search results is properly forwarded to the view model upon Update()")]
		public void TestSearch1()
		{
			var settings = new DataSource(AbstractTextLogSourceAcceptanceTest.File2Mb) { Id = DataSourceId.CreateNew() };
			using (var logFile = Create(AbstractTextLogSourceAcceptanceTest.File2Mb))
			using (var dataSource = new SingleDataSource(_taskScheduler, settings, logFile, TimeSpan.Zero))
			{
				var model = new SingleDataSourceViewModel(dataSource, new Mock<IActionCenter>().Object);

				logFile.Property(x => x.GetProperty(GeneralProperties.PercentageProcessed)).ShouldEventually().Be(Percentage.HundredPercent);

				model.Property(x =>
				{
					x.Update();
					return x.TotalCount;
				}).ShouldEventually().Be(16114);

				//model.Update();
				//model.TotalCount.Should().Be(16114);

				model.SearchTerm = "RPC #12";
				var search = dataSource.Search;
				search.Property(x => x.Count).ShouldEventually().Be(334);

				model.Update();
				model.SearchResultCount.Should().Be(334);
				model.CurrentSearchResultIndex.Should().Be(0);
			}
		}

		[Test]
		[Issue("https://github.com/Kittyfisto/Tailviewer/issues/125")]
		[Description("This is a temporary requirement until #195 is implemented in which case this test must be removed once more")]
		public void TestCannotBeRemovedAsPartOfFolder()
		{
			var actionCenter = new Mock<IActionCenter>();
			var dataSource = new Mock<ISingleDataSource>();
			dataSource.Setup(x => x.Settings).Returns(new DataSource());
			var model = new SingleDataSourceViewModel(dataSource.Object, actionCenter.Object);
			using (var monitor = model.Monitor())
			{
				model.CanBeRemoved.Should().BeTrue();

				var folderDataSource = new Mock<IFolderDataSource>();
				folderDataSource.Setup(x => x.Settings).Returns(new DataSource());
				var folder = new FolderDataSourceViewModel(folderDataSource.Object, actionCenter.Object);
				model.Parent = folder;
				model.CanBeRemoved.Should().BeFalse();
				monitor.Should().RaisePropertyChangeFor(x => x.CanBeRemoved);

				monitor.Clear();
				model.Parent = null;
				model.CanBeRemoved.Should().BeTrue();
				monitor.Should().RaisePropertyChangeFor(x => x.CanBeRemoved);
			}
		}

		[Test]
		[Issue("https://github.com/Kittyfisto/Tailviewer/issues/125")]
		[Description("This is a temporary requirement until #195 is implemented in which case this test must be removed once more")]
		public void TestCanBeRemovedAsPartOfMergedDataSource()
		{
			var actionCenter = new Mock<IActionCenter>();
			var dataSource = new Mock<ISingleDataSource>();
			dataSource.Setup(x => x.Settings).Returns(new DataSource());
			var model = new SingleDataSourceViewModel(dataSource.Object, actionCenter.Object);
			using (var monitor = model.Monitor())
			{
				model.CanBeRemoved.Should().BeTrue();

				var mergedDataSource = new Mock<IMergedDataSource>();
				mergedDataSource.Setup(x => x.Settings).Returns(new DataSource());
				var merged = new MergedDataSourceViewModel(mergedDataSource.Object, actionCenter.Object);
				model.Parent = merged;
				model.CanBeRemoved.Should().BeTrue();
				monitor.Should().NotRaisePropertyChangeFor(x => x.CanBeRemoved);

				monitor.Clear();
				model.Parent = null;
				model.CanBeRemoved.Should().BeTrue();
				monitor.Should().NotRaisePropertyChangeFor(x => x.CanBeRemoved);
			}
		}

		[Test]
		[Issue("https://github.com/Kittyfisto/Tailviewer/issues/215")]
		public void TestClearAllShowAll()
		{
			var dataSource = new Mock<ISingleDataSource>();
			var model = new SingleDataSourceViewModel(dataSource.Object, new Mock<IActionCenter>().Object);

			model.ScreenCleared.Should().BeFalse();

			model.ClearScreenCommand.Should().NotBeNull();
			model.ClearScreenCommand.CanExecute(null).Should().BeTrue("because the screen can always be cleared");
			model.ShowAllCommand.Should().NotBeNull();
			model.ShowAllCommand.CanExecute(null).Should().BeFalse("because the screen hasn't been cleared so nothing needs to be shown again");
			model.ClearScreenCommand.Execute(null);
			dataSource.Verify(x => x.ClearScreen(), Times.Once);

			model.ShowAllCommand.Should().NotBeNull();
			model.ShowAllCommand.CanExecute(null).Should().BeTrue("because the screen has been cleared and thus everything may be shown again");
			model.ShowAllCommand.Execute(null);
			dataSource.Verify(x => x.ShowAll(), Times.Once);

			model.ShowAllCommand.CanExecute(null).Should().BeFalse("because everything has been shown again and thus nothing further can be shown");
		}
	}
}