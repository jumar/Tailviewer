﻿using System;
using FluentAssertions;
using NUnit.Framework;
using Tailviewer.BusinessLogic.Scheduling;

namespace Tailviewer.Test.BusinessLogic.Scheduling
{
	[TestFixture]
	public sealed class PeriodicTaskTest
	{
		[Test]
		public void TestCtor()
		{
			var task = new PeriodicTask(() => { }, TimeSpan.FromSeconds(1));
			task.IsRemoved.Should().BeFalse();
			task.LastInvocation.Should().Be(DateTime.MinValue);
			task.MinimumWaitTime.Should().Be(TimeSpan.FromSeconds(1));
		}
	}
}