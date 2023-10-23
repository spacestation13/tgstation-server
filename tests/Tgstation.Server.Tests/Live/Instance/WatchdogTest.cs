﻿using Byond.TopicSender;

using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using Mono.Unix;
using Mono.Unix.Native;

using Moq;

using Newtonsoft.Json;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Tgstation.Server.Api.Models;
using Tgstation.Server.Api.Models.Request;
using Tgstation.Server.Api.Models.Response;
using Tgstation.Server.Client;
using Tgstation.Server.Client.Components;
using Tgstation.Server.Common.Extensions;
using Tgstation.Server.Host.Components;
using Tgstation.Server.Host.Components.Chat;
using Tgstation.Server.Host.Components.Interop;
using Tgstation.Server.Host.Components.Session;
using Tgstation.Server.Host.Components.Watchdog;
using Tgstation.Server.Host.Controllers;
using Tgstation.Server.Host.Extensions;
using Tgstation.Server.Host.IO;
using Tgstation.Server.Host.System;

namespace Tgstation.Server.Tests.Live.Instance
{
	sealed class WatchdogTest : JobsRequiredTest
	{
		static readonly ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
		{
			builder.AddConsole();
			builder.SetMinimumLevel(LogLevel.Trace);
		});

		public static readonly TopicClient StaticTopicClient = new(new SocketParameters
		{
			SendTimeout = TimeSpan.FromSeconds(30),
			ReceiveTimeout = TimeSpan.FromSeconds(30),
			ConnectTimeout = TimeSpan.FromSeconds(30),
			DisconnectTimeout = TimeSpan.FromSeconds(30)
		}, loggerFactory.CreateLogger($"WatchdogTest.TopicClient.Static"));

		readonly IInstanceClient instanceClient;
		readonly InstanceManager instanceManager;
		readonly ushort serverPort;
		readonly ushort ddPort;
		readonly bool highPrioDD;
		readonly TopicClient topicClient;
		readonly Version testVersion;
		readonly bool usingBasicWatchdog;

		bool ranTimeoutTest = false;

		public WatchdogTest(Version testVersion, IInstanceClient instanceClient, InstanceManager instanceManager, ushort serverPort, bool highPrioDD, ushort ddPort, bool usingBasicWatchdog)
			: base(instanceClient.Jobs)
		{
			this.instanceClient = instanceClient ?? throw new ArgumentNullException(nameof(instanceClient));
			this.instanceManager = instanceManager ?? throw new ArgumentNullException(nameof(instanceManager));
			this.serverPort = serverPort;
			this.highPrioDD = highPrioDD;
			this.ddPort = ddPort;
			this.testVersion = testVersion ?? throw new ArgumentNullException(nameof(testVersion));
			this.usingBasicWatchdog = usingBasicWatchdog;

			topicClient = new(new SocketParameters
			{
				SendTimeout = TimeSpan.FromSeconds(30),
				ReceiveTimeout = TimeSpan.FromSeconds(30),
				ConnectTimeout = TimeSpan.FromSeconds(30),
				DisconnectTimeout = TimeSpan.FromSeconds(30)
			}, loggerFactory.CreateLogger($"WatchdogTest.TopicClient.{instanceClient.Metadata.Name}"));
		}

		public async Task Run(CancellationToken cancellationToken)
		{
			System.Console.WriteLine($"TEST: START WATCHDOG TESTS {instanceClient.Metadata.Name}");

			async Task CheckByondVersions()
			{
				var listTask = instanceClient.Byond.InstalledVersions(null, cancellationToken);

				var list = await listTask;

				Assert.AreEqual(1, list.Count);
				var byondVersion = list[0];

				Assert.AreEqual(1, byondVersion.Version.Build);
				Assert.AreEqual(testVersion.Major, byondVersion.Version.Major);
				Assert.AreEqual(testVersion.Minor, byondVersion.Version.Minor);
			}

			await Task.WhenAll(
				// Increase startup timeout, disable heartbeats, enable map threads because we've tested without for years
				instanceClient.DreamDaemon.Update(new DreamDaemonRequest
				{
					StartupTimeout = 15,
					HealthCheckSeconds = 0,
					Port = ddPort,
					MapThreads = 2,
					LogOutput = false,
					AdditionalParameters = "expect_chat_channels=1&expect_static_files=1"
				}, cancellationToken).AsTask(),
				CheckByondVersions(),
				ApiAssert.ThrowsException<ApiConflictException, DreamDaemonResponse>(() => instanceClient.DreamDaemon.Update(new DreamDaemonRequest
				{
					SoftShutdown = true,
					SoftRestart = true
				}, cancellationToken), ErrorCode.DreamDaemonDoubleSoft).AsTask(),
				ApiAssert.ThrowsException<ApiConflictException, DreamDaemonResponse>(() => instanceClient.DreamDaemon.Update(new DreamDaemonRequest
				{
					Port = 0
				}, cancellationToken), ErrorCode.ModelValidationFailure).AsTask(),
				ApiAssert.ThrowsException<ConflictException, JobResponse>(() => instanceClient.DreamDaemon.CreateDump(cancellationToken), ErrorCode.WatchdogNotRunning).AsTask(),
				ApiAssert.ThrowsException<ConflictException, JobResponse>(() => instanceClient.DreamDaemon.Restart(cancellationToken), ErrorCode.WatchdogNotRunning).AsTask());

			await RunBasicTest(cancellationToken);

			await TestDMApiFreeDeploy(cancellationToken);

			// long running test likes consistency with the channels
			await DummyChatProvider.RandomDisconnections(false, cancellationToken);

			await RunLongRunningTestThenUpdate(cancellationToken);

			await RunLongRunningTestThenUpdateWithNewDme(cancellationToken);
			await RunLongRunningTestThenUpdateWithByondVersionSwitch(cancellationToken);

			await RunHealthCheckTest(true, cancellationToken);
			await RunHealthCheckTest(false, cancellationToken);

			await InteropTestsForLongRunningDme(cancellationToken);

			await instanceClient.DreamDaemon.Update(new DreamDaemonRequest
			{
				AdditionalParameters = String.Empty
			}, cancellationToken);

			// for the restart staging tests
			await DeployTestDme("LongRunning/long_running_test", DreamDaemonSecurity.Trusted, true, cancellationToken);

			System.Console.WriteLine($"TEST: END WATCHDOG TESTS {instanceClient.Metadata.Name}");
		}

		async ValueTask RegressionTest1686(CancellationToken cancellationToken)
		{
			async ValueTask RunTest(bool useTrusted)
			{
				System.Console.WriteLine($"TEST: RegressionTest1686 {useTrusted}...");
				var ddUpdateTask = instanceClient.DreamDaemon.Update(new DreamDaemonRequest
				{
					SecurityLevel = useTrusted ? DreamDaemonSecurity.Trusted : DreamDaemonSecurity.Safe,
					AdditionalParameters = "expect_chat_channels=1&expect_static_files=1",
				}, cancellationToken);
				var currentStatus = await DeployTestDme("long_running_test_rooted", DreamDaemonSecurity.Trusted, true, cancellationToken);
				await ddUpdateTask;

				Assert.AreEqual(WatchdogStatus.Offline, currentStatus.Status);

				var startJob = await StartDD(cancellationToken);

				await WaitForJob(startJob, 40, false, null, cancellationToken);

				currentStatus = await instanceClient.DreamDaemon.Update(new DreamDaemonRequest
				{
					SoftShutdown = true,
				}, cancellationToken);

				Assert.AreEqual(WatchdogStatus.Online, currentStatus.Status);

				// reimplement TellWorldToReboot because it expects a new deployment and we don't care
				System.Console.WriteLine("TEST: Hack world reboot topic...");
				var result = await topicClient.SendTopic(IPAddress.Loopback, "tgs_integration_test_special_tactics=1", ddPort, cancellationToken);
				Assert.AreEqual("ack", result.StringData);

				using var tempCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
				var tempToken = tempCts.Token;
				using (tempToken.Register(() => System.Console.WriteLine("TEST ERROR: Timeout in RegressionTest1686!")))
				{
					tempCts.CancelAfter(TimeSpan.FromMinutes(2));

					do
					{
						await Task.Delay(TimeSpan.FromSeconds(1), tempToken);
						currentStatus = await instanceClient.DreamDaemon.Read(tempToken);
					}
					while (currentStatus.Status != WatchdogStatus.Offline);
				}

				await CheckDMApiFail(currentStatus.ActiveCompileJob, cancellationToken);
			}

			await RunTest(true);

			if (new PlatformIdentifier().IsWindows || !usingBasicWatchdog)
				await RunTest(false);
		}

		async Task InteropTestsForLongRunningDme(CancellationToken cancellationToken)
		{
			await RegressionTest1686(cancellationToken);

			await StartAndLeaveRunning(cancellationToken);

			await RegressionTest1550(cancellationToken);

			var deleteJobTask = TestDeleteByondInstallErrorCasesAndQueing(cancellationToken);

			SessionController.LogTopicRequests = false;
			await WhiteBoxChatCommandTest(cancellationToken);
			await SendChatOverloadCommand(cancellationToken);
			await ValidateTopicLimits(cancellationToken);
			SessionController.LogTopicRequests = true;

			// This one fucks with the access_identifer, run it in isolation
			await WhiteBoxValidateBridgeRequestLimitAndTestChunking(cancellationToken);

			var ddInfo = await instanceClient.DreamDaemon.Read(cancellationToken);
			await CheckDMApiFail(ddInfo.ActiveCompileJob, cancellationToken);

			var deleteJob = await deleteJobTask;

			// And this freezes DD
			await DumpTests(cancellationToken);

			// Restart to unlock previous BYOND version
			var restartJob = await instanceClient.DreamDaemon.Restart(cancellationToken);
			await WaitForJob(deleteJob, 15, false, null, cancellationToken);
			await WaitForJob(restartJob, 15, false, null, cancellationToken);
		}

		async ValueTask RegressionTest1550(CancellationToken cancellationToken)
		{
			// we need to cycle deployments twice because TGS holds the initial deployment
			var currentStatus = await DeployTestDme("LongRunning/long_running_test", DreamDaemonSecurity.Trusted, true, cancellationToken);

			Assert.AreEqual(WatchdogStatus.Online, currentStatus.Status);
			Assert.IsNotNull(currentStatus.StagedCompileJob);
			var expectedStaged = currentStatus.StagedCompileJob;
			Assert.AreNotEqual(expectedStaged.Id, currentStatus.ActiveCompileJob.Id);

			await TellWorldToReboot(cancellationToken);

			currentStatus = await instanceClient.DreamDaemon.Read(cancellationToken);
			Assert.AreEqual(expectedStaged.Id, currentStatus.ActiveCompileJob.Id);

			await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);

			var topicRequestResult = await topicClient.SendTopic(
				IPAddress.Loopback,
				$"shadow_wizard_money_gang=1",
				ddPort,
				cancellationToken);

			Assert.IsNotNull(topicRequestResult);
			Assert.AreEqual("we love casting spells", topicRequestResult.StringData);

			await DeployTestDme("LongRunning/long_running_test", DreamDaemonSecurity.Trusted, true, cancellationToken);

			currentStatus = await instanceClient.DreamDaemon.Read(cancellationToken);

			Assert.AreEqual(WatchdogStatus.Online, currentStatus.Status);
			Assert.IsNotNull(currentStatus.StagedCompileJob);
			Assert.AreEqual(expectedStaged.Id, currentStatus.ActiveCompileJob.Id);
			expectedStaged = currentStatus.StagedCompileJob;
			Assert.AreNotEqual(expectedStaged.Id, currentStatus.ActiveCompileJob.Id);

			await TellWorldToReboot(cancellationToken);

			currentStatus = await instanceClient.DreamDaemon.Read(cancellationToken);
			Assert.AreEqual(WatchdogStatus.Online, currentStatus.Status);
			Assert.IsNull(currentStatus.StagedCompileJob);
			Assert.AreEqual(expectedStaged.Id, currentStatus.ActiveCompileJob.Id);

			await CheckDMApiFail(currentStatus.ActiveCompileJob, cancellationToken, false);
			await CheckDMApiFail(expectedStaged, cancellationToken, false);
		}

		async Task<JobResponse> TestDeleteByondInstallErrorCasesAndQueing(CancellationToken cancellationToken)
		{
			var testCustomVersion = new Version(testVersion.Major, testVersion.Minor, 1);
			var currentByond = await instanceClient.Byond.ActiveVersion(cancellationToken);
			Assert.IsNotNull(currentByond);
			Assert.AreEqual(testVersion.Semver(), currentByond.Version);

			// Change the active version and check we get delayed while deleting the old one because the watchdog is using it
			var setActiveResponse = await instanceClient.Byond.SetActiveVersion(
				new ByondVersionRequest
				{
					Version = testCustomVersion,
				},
				null,
				cancellationToken);

			Assert.IsNotNull(setActiveResponse);
			Assert.IsNull(setActiveResponse.InstallJob);

			var deleteJob = await instanceClient.Byond.DeleteVersion(
				new ByondVersionDeleteRequest
				{
					Version = testVersion,
				},
				cancellationToken);

			Assert.IsNotNull(deleteJob);

			deleteJob = await WaitForJobProgress(deleteJob, 15, cancellationToken);
			Assert.IsNotNull(deleteJob);
			Assert.IsNotNull(deleteJob.Stage);
			Assert.IsTrue(deleteJob.Stage.Contains("Waiting"));

			// then change it back and check it fails the job because it's active again
			setActiveResponse = await instanceClient.Byond.SetActiveVersion(
				new ByondVersionRequest
				{
					Version = testVersion,
				},
				null,
				cancellationToken);

			Assert.IsNotNull(setActiveResponse);
			Assert.IsNull(setActiveResponse.InstallJob);

			await WaitForJob(deleteJob, 5, true, ErrorCode.ByondCannotDeleteActiveVersion, cancellationToken);

			// finally, queue the last delete job which should complete when the watchdog restarts with a newly deployed .dmb
			// queue the byond change followed by the deployment for that first
			setActiveResponse = await instanceClient.Byond.SetActiveVersion(
				new ByondVersionRequest
				{
					Version = testCustomVersion,
				},
				null,
				cancellationToken);

			Assert.IsNotNull(setActiveResponse);
			Assert.IsNull(setActiveResponse.InstallJob);

			deleteJob = await instanceClient.Byond.DeleteVersion(
				new ByondVersionDeleteRequest
				{
					Version = testVersion,
				},
				cancellationToken);

			Assert.IsNotNull(deleteJob);

			await DeployTestDme("LongRunning/long_running_test", DreamDaemonSecurity.Safe, true, cancellationToken);
			return deleteJob;
		}

		async Task SendChatOverloadCommand(CancellationToken cancellationToken)
		{
			// for the code coverage really...
			var topicRequestResult = await topicClient.SendTopic(
				IPAddress.Loopback,
				$"tgs_integration_test_tactics5=1",
				ddPort,
				cancellationToken);

			Assert.IsNotNull(topicRequestResult);
			Assert.AreEqual("sent", topicRequestResult.StringData);
		}

		async Task DumpTests(CancellationToken cancellationToken)
		{
			System.Console.WriteLine("TEST: WATCHDOG DUMP TESTS");
			var dumpJob = await instanceClient.DreamDaemon.CreateDump(cancellationToken);
			await WaitForJob(dumpJob, 30, false, null, cancellationToken);

			var dumpFiles = Directory.GetFiles(Path.Combine(
				instanceClient.Metadata.Path, "Diagnostics", "ProcessDumps"), "*.dmp");
			Assert.AreEqual(1, dumpFiles.Length);
			File.Delete(dumpFiles.Single());

			JobResponse job;
			while (true)
			{
				KillDD(true);
				var jobTcs = new TaskCompletionSource();
				var killTaskStarted = new TaskCompletionSource();
				var killTask = Task.Run(() =>
				{
					killTaskStarted.SetResult();
					while (!jobTcs.Task.IsCompleted)
						KillDD(false);
				}, cancellationToken);

				try
				{
					await killTaskStarted.Task;
					var dumpTask = instanceClient.DreamDaemon.CreateDump(cancellationToken);
					job = await WaitForJob(await dumpTask, 20, true, null, cancellationToken);
				}
				finally
				{
					jobTcs.SetResult();
					await killTask;
				}

				// these can also happen

				if (!(new PlatformIdentifier().IsWindows
					&& (job.ExceptionDetails.Contains("BetterWin32Errors.Win32Exception: E_ACCESSDENIED: Access is denied.")
					|| job.ExceptionDetails.Contains("BetterWin32Errors.Win32Exception: E_HANDLE: The handle is invalid.")
					|| job.ExceptionDetails.Contains("BetterWin32Errors.Win32Exception: 3489660936: Unknown error (0xd0000008)")
					|| job.ExceptionDetails.Contains("System.InvalidOperationException: No process is associated with this object.")
					|| job.ExceptionDetails.Contains("BetterWin32Errors.Win32Exception: 2147942424: The program issued a command but the command length is incorrect.")
					|| job.ExceptionDetails.Contains("BetterWin32Errors.Win32Exception: 2147942699: Only part of a ReadProcessMemory or WriteProcessMemory request was completed.")
					|| job.ExceptionDetails.Contains("BetterWin32Errors.Win32Exception: 3489660964: Unknown error (0xd0000024)"))))
					break;

				var restartJob = await instanceClient.DreamDaemon.Restart(cancellationToken);
				await WaitForJob(restartJob, 20, false, null, cancellationToken);
			}

			Assert.IsTrue(job.ErrorCode == ErrorCode.DreamDaemonOffline || job.ErrorCode == ErrorCode.GCoreFailure, $"{job.ErrorCode}: {job.ExceptionDetails}");

			await Task.Delay(TimeSpan.FromSeconds(20), cancellationToken);

			var ddStatus = await instanceClient.DreamDaemon.Read(cancellationToken);
			Assert.AreEqual(WatchdogStatus.Online, ddStatus.Status.Value);
		}

		async Task TestDMApiFreeDeploy(CancellationToken cancellationToken)
		{
			System.Console.WriteLine("TEST: WATCHDOG API FREE TEST");
			var daemonStatus = await DeployTestDme("ApiFree/api_free", DreamDaemonSecurity.Safe, false, cancellationToken);

			Assert.AreEqual(WatchdogStatus.Offline, daemonStatus.Status.Value);
			Assert.IsNotNull(daemonStatus.ActiveCompileJob);
			Assert.IsNull(daemonStatus.StagedCompileJob);
			Assert.IsNull(daemonStatus.ActiveCompileJob.DMApiVersion);
			Assert.AreEqual(DreamDaemonSecurity.Ultrasafe, daemonStatus.ActiveCompileJob.MinimumSecurityLevel);

			var startJob = await StartDD(cancellationToken);

			await WaitForJob(startJob, 40, false, null, cancellationToken);

			daemonStatus = await instanceClient.DreamDaemon.Read(cancellationToken);

			Assert.AreEqual(WatchdogStatus.Online, daemonStatus.Status.Value);
			CheckDDPriority();
			Assert.AreEqual(false, daemonStatus.SoftRestart);
			Assert.AreEqual(false, daemonStatus.SoftShutdown);
			Assert.AreEqual(string.Empty, daemonStatus.AdditionalParameters);
			var initialCompileJob = daemonStatus.ActiveCompileJob;
			await CheckDMApiFail(daemonStatus.ActiveCompileJob, cancellationToken);

			daemonStatus = await DeployTestDme("BasicOperation/basic_operation_test", DreamDaemonSecurity.Trusted, true, cancellationToken);

			Assert.AreEqual(WatchdogStatus.Online, daemonStatus.Status.Value);

			Assert.AreEqual(initialCompileJob.Id, daemonStatus.ActiveCompileJob.Id);
			var newerCompileJob = daemonStatus.StagedCompileJob;

			Assert.IsNotNull(newerCompileJob);
			Assert.AreNotEqual(initialCompileJob.Id, newerCompileJob.Id);
			Assert.AreEqual(DreamDaemonSecurity.Trusted, newerCompileJob.MinimumSecurityLevel);
			Assert.AreEqual(DMApiConstants.InteropVersion, daemonStatus.StagedCompileJob.DMApiVersion);
			await instanceClient.DreamDaemon.Shutdown(cancellationToken);
		}

		async Task RunBasicTest(CancellationToken cancellationToken)
		{
			System.Console.WriteLine("TEST: WATCHDOG BASIC TEST");

			var daemonStatus = await instanceClient.DreamDaemon.Update(new DreamDaemonRequest
			{
				AdditionalParameters = "test=bababooey"
			}, cancellationToken);
			Assert.AreEqual("test=bababooey", daemonStatus.AdditionalParameters);
			daemonStatus = await DeployTestDme("BasicOperation/basic_operation_test", DreamDaemonSecurity.Trusted, true, cancellationToken);

			Assert.AreEqual(WatchdogStatus.Offline, daemonStatus.Status.Value);
			Assert.IsNotNull(daemonStatus.ActiveCompileJob);
			Assert.IsNull(daemonStatus.StagedCompileJob);
			Assert.AreEqual(DMApiConstants.InteropVersion, daemonStatus.ActiveCompileJob.DMApiVersion);
			Assert.AreEqual(DreamDaemonSecurity.Trusted, daemonStatus.ActiveCompileJob.MinimumSecurityLevel);

			JobResponse startJob;
			if (new PlatformIdentifier().IsWindows) // Can't get address reuse to trigger on linux for some reason
				using (var blockSocket = new Socket(SocketType.Stream, ProtocolType.Tcp))
				{
					blockSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ExclusiveAddressUse, true);
					blockSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
					blockSocket.Bind(new IPEndPoint(IPAddress.Any, ddPort));

					// Don't use StartDD here
					startJob = await instanceClient.DreamDaemon.Start(cancellationToken);

					await WaitForJob(startJob, 40, true, ErrorCode.DreamDaemonPortInUse, cancellationToken);
				}

			startJob = await StartDD(cancellationToken);

			await WaitForJob(startJob, 40, false, null, cancellationToken);

			daemonStatus = await instanceClient.DreamDaemon.Read(cancellationToken);

			Assert.AreEqual(WatchdogStatus.Online, daemonStatus.Status.Value);
			CheckDDPriority();
			Assert.AreEqual(false, daemonStatus.SoftRestart);
			Assert.AreEqual(false, daemonStatus.SoftShutdown);

			await GracefulWatchdogShutdown(60, cancellationToken);

			daemonStatus = await instanceClient.DreamDaemon.Read(cancellationToken);
			Assert.AreEqual(WatchdogStatus.Offline, daemonStatus.Status.Value);

			await CheckDMApiFail(daemonStatus.ActiveCompileJob, cancellationToken, false);

			daemonStatus = await instanceClient.DreamDaemon.Update(new DreamDaemonRequest
			{
				AdditionalParameters = string.Empty,
				LogOutput = true,
			}, cancellationToken);
			Assert.AreEqual(string.Empty, daemonStatus.AdditionalParameters);
		}

		void TestLinuxIsntBeingFuckingCheekyAboutFilePaths(DreamDaemonResponse currentStatus, CompileJobResponse previousStatus)
		{
			if (new PlatformIdentifier().IsWindows || usingBasicWatchdog)
				return;

			Assert.IsNotNull(currentStatus.ActiveCompileJob);
			Assert.IsTrue(currentStatus.ActiveCompileJob.DmeName.Contains("long_running_test"));
			Assert.AreEqual(WatchdogStatus.Online, currentStatus.Status);

			var procs = TestLiveServer.GetDDProcessesOnPort(currentStatus.Port.Value);
			Assert.AreEqual(1, procs.Count);
			var failingLinks = new List<string>();
			using var proc = procs[0];
			var pid = proc.Id;
			var foundLivePath = false;
			var allPaths = new List<string>();
			foreach (var fd in Directory.EnumerateFiles($"/proc/{pid}/fd"))
			{
				var sb = new StringBuilder(UInt16.MaxValue);
				if (Syscall.readlink(fd, sb) == -1)
					throw new UnixIOException(Stdlib.GetLastError());

				var path = sb.ToString();

				allPaths.Add($"Path: {path}");
				if (path.Contains($"Game/{previousStatus.DirectoryName}"))
					failingLinks.Add($"Found fd {fd} resolving to previous absolute path game dir path: {path}");

				if (path.Contains($"Game/{currentStatus.ActiveCompileJob.DirectoryName}"))
					failingLinks.Add($"Found fd {fd} resolving to current absolute path game dir path: {path}");

				if (path.Contains($"Game/Live"))
					foundLivePath = true;
			}

			if (!foundLivePath)
				failingLinks.Add($"Failed to find a path containing the 'Live' directory!");

			Assert.IsTrue(failingLinks.Count == 0, String.Join(Environment.NewLine, failingLinks.Concat(allPaths)));
		}

		async Task RunHealthCheckTest(bool checkDump, CancellationToken cancellationToken)
		{
			System.Console.WriteLine("TEST: WATCHDOG HEALTH CHECK TEST");
#pragma warning disable CS0618 // Type or member is obsolete
			// Check reverse mapping
			var status = await instanceClient.DreamDaemon.Update(new DreamDaemonRequest
			{
				DumpOnHealthCheckRestart = !checkDump,
			}, cancellationToken);

			Assert.AreEqual(!checkDump, status.DumpOnHeartbeatRestart);

			// enable health checks
			status = await instanceClient.DreamDaemon.Update(new DreamDaemonRequest
			{
				HealthCheckSeconds = 1,
				DumpOnHeartbeatRestart = checkDump,
			}, cancellationToken);

			Assert.AreEqual(checkDump, status.DumpOnHeartbeatRestart);
#pragma warning restore CS0618 // Type or member is obsolete
			Assert.AreEqual(checkDump, status.DumpOnHealthCheckRestart);

			var startJob = await StartDD(cancellationToken);

			await WaitForJob(startJob, 40, false, null, cancellationToken);

			CheckDDPriority();

			// lock on to DD and pause it so it can't health check
			var ddProcs = TestLiveServer.GetDDProcessesOnPort(ddPort).Where(x => !x.HasExited).ToList();
			if (ddProcs.Count != 1)
				Assert.Fail($"Incorrect number of DD processes: {ddProcs.Count}");

			using var ddProc = ddProcs.Single();
			IProcessExecutor executor = null;
			executor = new ProcessExecutor(
				RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
					? new WindowsProcessFeatures(Mock.Of<ILogger<WindowsProcessFeatures>>())
					: new PosixProcessFeatures(new Lazy<IProcessExecutor>(() => executor), Mock.Of<IIOManager>(), Mock.Of<ILogger<PosixProcessFeatures>>()),
				Mock.Of<IIOManager>(),
				Mock.Of<ILogger<ProcessExecutor>>(),
				LoggerFactory.Create(x => { }));
			await using var ourProcessHandler = executor
				.GetProcess(ddProc.Id);

			// Ensure it's responding to health checks
			await Task.WhenAny(Task.Delay(20000, cancellationToken), ourProcessHandler.Lifetime);
			Assert.IsFalse(ddProc.HasExited);

			// check DD agrees
			var topicRequestResult = await topicClient.SendTopic(
				IPAddress.Loopback,
				$"tgs_integration_test_tactics8=1",
				ddPort,
				cancellationToken);

			Assert.IsNotNull(topicRequestResult);
			Assert.AreEqual(TopicResponseType.StringResponse, topicRequestResult.ResponseType);
			Assert.IsNotNull(topicRequestResult.StringData);
			Assert.AreEqual(topicRequestResult.StringData, "received health check");

			await instanceClient.DreamDaemon.Update(new DreamDaemonRequest
			{
				SoftShutdown = true
			}, cancellationToken);

			ourProcessHandler.Suspend();

			await Task.WhenAny(ourProcessHandler.Lifetime, Task.Delay(TimeSpan.FromMinutes(1), cancellationToken));

			var timeout = 60;
			DreamDaemonResponse ddStatus;
			do
			{
				await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
				ddStatus = await instanceClient.DreamDaemon.Read(cancellationToken);
				Assert.AreEqual(1U, ddStatus.HealthCheckSeconds.Value);
#pragma warning disable CS0618 // Type or member is obsolete
				Assert.AreEqual(1U, ddStatus.HeartbeatSeconds.Value);
				if (ddStatus.Status.Value == WatchdogStatus.Offline)
				{
					await CheckDMApiFail(ddStatus.ActiveCompileJob, cancellationToken);
					break;
				}

				if (--timeout == 0)
					Assert.Fail("DreamDaemon didn't shutdown within the timeout!");
			}
			while (timeout > 0);

			// disable health checks
			ddStatus = await instanceClient.DreamDaemon.Update(new DreamDaemonRequest
			{
				HeartbeatSeconds = 0,
			}, cancellationToken);
			Assert.AreEqual(0U, ddStatus.HealthCheckSeconds.Value);
			Assert.AreEqual(0U, ddStatus.HeartbeatSeconds.Value);
#pragma warning restore CS0618 // Type or member is obsolete

			if (checkDump)
			{
				// check the dump happened
				var dumpFiles = Directory.GetFiles(Path.Combine(
					instanceClient.Metadata.Path, "Diagnostics", "ProcessDumps"), "*.dmp");
				Assert.AreEqual(1, dumpFiles.Length);
				File.Delete(dumpFiles.Single());
			}
		}

		async Task<JobResponse> StartDD(CancellationToken cancellationToken)
		{
			// integration tests may take a while to release the port
			using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			cts.CancelAfter(TimeSpan.FromMinutes(1));
			while (true)
			{
				try
				{
					SocketExtensions.BindTest(ddPort, false);
					break;
				}
				catch
				{
					try
					{
						await Task.Delay(TimeSpan.FromSeconds(1), cts.Token);
						continue;
					}
					catch (OperationCanceledException)
					{
					}

					throw;
				}
			}
			await Task.Delay(TimeSpan.FromSeconds(3), cts.Token);

			return await instanceClient.DreamDaemon.Start(cancellationToken);
		}

		class TestData
		{
			public string Size { get; set; }
			public string Payload { get; set; }
		}

		// - Uses instance manager concrete
		// - Injects a custom bridge handler into the bridge registrar and makes the test hack into the DMAPI and change its access_identifier
		async Task WhiteBoxValidateBridgeRequestLimitAndTestChunking(CancellationToken cancellationToken)
		{
			// first check the bridge limits
			var bridgeTestsTcs = new TaskCompletionSource();
			BridgeController.LogContent = false;
			using (var loggerFactory = LoggerFactory.Create(builder =>
			{
				builder.AddConsole();
				builder.SetMinimumLevel(LogLevel.Trace);
			}))
			{
				var accessIdentifier = $"tgs_integration_test_for_instance_{instanceClient.Metadata.Name}";
				var bridgeProcessor = new TestBridgeHandler(bridgeTestsTcs, loggerFactory.CreateLogger<TestBridgeHandler>(), accessIdentifier, serverPort);
				using var bridgeRegistration = instanceManager.RegisterHandler(bridgeProcessor);

				System.Console.WriteLine("TEST: Sending Bridge tests topic...");

				var bridgeTestTopicResult = await topicClient.SendTopic(IPAddress.Loopback, $"tgs_integration_test_tactics2={accessIdentifier}", ddPort, cancellationToken);
				Assert.AreEqual("ack2", bridgeTestTopicResult.StringData);

				await bridgeTestsTcs.Task.WaitAsync(cancellationToken);
			}

			BridgeController.LogContent = true;

			// Time for DD to revert the bridge access identifier change
			await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
		}

		async Task ValidateTopicLimits(CancellationToken cancellationToken)
		{
			// Time for topic tests
			// Request

			System.Console.WriteLine("TEST: Sending Topic tests topics...");

			var nextPow = 0;
			var lastSize = 0;

			var baseTopic = new TestData
			{
				Size = 0.ToString().PadLeft(6, '0'),
				Payload = "",
			};

			var json = JsonConvert.SerializeObject(baseTopic, DMApiConstants.SerializerSettings);

			var baseSize = (int)(DMApiConstants.MaximumTopicRequestLength - 1);

			var topicString = $"tgs_integration_test_tactics3={topicClient.SanitizeString(json)}";
			var wrappingSize = topicString.Length;

			while (!cancellationToken.IsCancellationRequested)
			{
				var currentSize = baseSize + (int)Math.Pow(2, nextPow);
				var payloadSize = currentSize - wrappingSize;

				var topic = new TestData
				{
					Size = payloadSize.ToString().PadLeft(6, '0'),
					Payload = new string('a', payloadSize),
				};

				TopicResponse topicRequestResult = null;
				try
				{
					System.Console.WriteLine($"Topic send limit test S:{currentSize}...");
					topicRequestResult = await topicClient.SendTopic(
						IPAddress.Loopback,
						$"tgs_integration_test_tactics3={topicClient.SanitizeString(JsonConvert.SerializeObject(topic, DMApiConstants.SerializerSettings))}",
						ddPort,
						cancellationToken);
				}
				catch (ArgumentOutOfRangeException)
				{
				}

				if (topicRequestResult == null
					|| topicRequestResult.ResponseType != TopicResponseType.StringResponse
					|| topicRequestResult.StringData != "pass")
				{
					if (topicRequestResult != null)
						Assert.AreEqual("fail", topicRequestResult.StringData);
					if (currentSize == lastSize + 1)
						break;
					baseSize = lastSize;
					nextPow = 0;
					continue;
				}

				lastSize = currentSize;
				++nextPow;
			}

			cancellationToken.ThrowIfCancellationRequested();

			Assert.AreEqual(DMApiConstants.MaximumTopicRequestLength, (uint)lastSize);

			System.Console.WriteLine("TEST: Receiving Topic tests topics...");

			// Receive
			baseSize = (int)(DMApiConstants.MaximumTopicResponseLength - 1);
			nextPow = 0;
			lastSize = 0;
			while (!cancellationToken.IsCancellationRequested)
			{
				var currentSize = baseSize + (int)Math.Pow(2, nextPow);
				System.Console.WriteLine($"Topic recieve limit test S:{currentSize}...");
				var topicRequestResult = await topicClient.SendTopic(
					IPAddress.Loopback,
					$"tgs_integration_test_tactics4={topicClient.SanitizeString(currentSize.ToString())}",
					ddPort,
					cancellationToken);

				if (topicRequestResult.ResponseType != TopicResponseType.StringResponse
					|| new string('a', currentSize) != topicRequestResult.StringData)
				{
					if (currentSize == lastSize + 1)
						break;
					baseSize = lastSize;
					nextPow = 0;
					continue;
				}

				lastSize = currentSize;
				++nextPow;
			}

			cancellationToken.ThrowIfCancellationRequested();
			Assert.AreEqual(DMApiConstants.MaximumTopicResponseLength, (uint)lastSize);
		}

		// - Uses instance manager concrete
		// - Injects a custom bridge handler into the bridge registrar and makes the test hack into the DMAPI and change its access_identifier
		async Task WhiteBoxChatCommandTest(CancellationToken cancellationToken)
		{
			MessageContent embedsResponse, overloadResponse, overloadResponse2, embedsResponse2;
			var startTime = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(5);
			using (var instanceReference = instanceManager.GetInstanceReference(instanceClient.Metadata))
			{
				var mockChatUser = new ChatUser
				{
					Channel = new ChannelRepresentation
					{
						IsAdminChannel = true,
						ConnectionName = "test_connection",
						EmbedsSupported = true,
						FriendlyName = "Test Connection",
						Id = "test_channel_id",
						IsPrivateChannel = false,
					},
					FriendlyName = "Test Sender",
					Id = "test_user_id",
					Mention = "test_user_mention",
					RealId = 1234,
				};

				var embedsResponseTask = ((WatchdogBase)instanceReference.Watchdog).HandleChatCommand(
					"embeds_test",
					string.Empty,
					mockChatUser,
					cancellationToken);

				var embedsResponseTask2 = ((WatchdogBase)instanceReference.Watchdog).HandleChatCommand(
					"embeds_test",
					new string('a', (int)DMApiConstants.MaximumTopicRequestLength * 3),
					mockChatUser,
					cancellationToken);

				var overloadResponseTask2 = ((WatchdogBase)instanceReference.Watchdog).HandleChatCommand(
					"response_overload_test",
					string.Empty,
					mockChatUser,
					cancellationToken);

				overloadResponse = await ((WatchdogBase)instanceReference.Watchdog).HandleChatCommand(
					"response_overload_test",
					string.Empty,
					mockChatUser,
					cancellationToken);

				overloadResponse2 = await overloadResponseTask2;
				embedsResponse = await embedsResponseTask;
				embedsResponse2 = await embedsResponseTask2;
			}

			var endTime = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);

			var ddInfo = await instanceClient.DreamDaemon.Read(cancellationToken);
			await CheckDMApiFail(ddInfo.ActiveCompileJob, cancellationToken);

			CheckEmbedsTest(embedsResponse, startTime, endTime);
			CheckEmbedsTest(embedsResponse2, startTime, endTime);

			var expectedString = new string('a', (int)DMApiConstants.MaximumTopicResponseLength * 3);
			Assert.IsNotNull(overloadResponse);
			Assert.AreEqual(expectedString, overloadResponse.Text);
			Assert.IsNotNull(overloadResponse2);
			Assert.AreEqual(expectedString, overloadResponse2.Text);
		}

		static void CheckEmbedsTest(MessageContent embedsResponse, DateTimeOffset startTime, DateTimeOffset endTime)
		{
			Assert.IsNotNull(embedsResponse);
			Assert.AreEqual("Embed support test2", embedsResponse.Text);
			Assert.AreEqual("desc", embedsResponse.Embed.Description);
			Assert.AreEqual("title", embedsResponse.Embed.Title);
			Assert.AreEqual("#0000FF", embedsResponse.Embed.Colour);
			Assert.AreEqual("Dominion", embedsResponse.Embed.Author?.Name);
			Assert.AreEqual("https://github.com/Cyberboss", embedsResponse.Embed.Author.Url);
			Assert.IsTrue(DateTimeOffset.TryParse(embedsResponse.Embed.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var timestamp));
			Assert.IsTrue(startTime < timestamp && endTime > timestamp);
			Assert.AreEqual("https://github.com/tgstation/tgstation-server", embedsResponse.Embed.Url);
			Assert.AreEqual(3, embedsResponse.Embed.Fields?.Count);
			Assert.AreEqual("field1", embedsResponse.Embed.Fields.ElementAt(0).Name);
			Assert.AreEqual("value1", embedsResponse.Embed.Fields.ElementAt(0).Value);
			Assert.IsNull(embedsResponse.Embed.Fields.ElementAt(0).IsInline);
			Assert.AreEqual("field2", embedsResponse.Embed.Fields.ElementAt(1).Name);
			Assert.AreEqual("value2", embedsResponse.Embed.Fields.ElementAt(1).Value);
			Assert.IsTrue(embedsResponse.Embed.Fields.ElementAt(1).IsInline);
			Assert.AreEqual("field3", embedsResponse.Embed.Fields.ElementAt(2).Name);
			Assert.AreEqual("value3", embedsResponse.Embed.Fields.ElementAt(2).Value);
			Assert.IsTrue(embedsResponse.Embed.Fields.ElementAt(2).IsInline);
			Assert.AreEqual("Footer text", embedsResponse.Embed.Footer?.Text);
		}

		void CheckDDPriority()
		{
			var allProcesses = TestLiveServer.GetDDProcessesOnPort(ddPort).Where(x => !x.HasExited).ToList();
			if (allProcesses.Count == 0)
				Assert.Fail("Expected DreamDaemon to be running here");

			if (allProcesses.Count > 1)
				Assert.Fail("Multiple DreamDaemon-like processes running!");

			using var process = allProcesses[0];

			Assert.AreEqual(
				highPrioDD
					? System.Diagnostics.ProcessPriorityClass.AboveNormal
					: System.Diagnostics.ProcessPriorityClass.Normal,
				process.PriorityClass);
		}

		async Task RunLongRunningTestThenUpdate(CancellationToken cancellationToken)
		{
			System.Console.WriteLine("TEST: WATCHDOG LONG RUNNING WITH UPDATE TEST");
			const string DmeName = "LongRunning/long_running_test";

			var daemonStatus = await DeployTestDme(DmeName, DreamDaemonSecurity.Trusted, true, cancellationToken);

			var initialCompileJob = daemonStatus.ActiveCompileJob;
			Assert.IsNotNull(initialCompileJob);
			Assert.AreEqual(WatchdogStatus.Offline, daemonStatus.Status.Value);
			Assert.IsNotNull(daemonStatus.ActiveCompileJob);
			Assert.IsNull(daemonStatus.StagedCompileJob);
			Assert.AreEqual(DMApiConstants.InteropVersion, daemonStatus.ActiveCompileJob.DMApiVersion);
			Assert.AreEqual(DreamDaemonSecurity.Safe, daemonStatus.ActiveCompileJob.MinimumSecurityLevel);

			var startJob = await StartDD(cancellationToken);

			await WaitForJob(startJob, 40, false, null, cancellationToken);

			daemonStatus = await DeployTestDme(DmeName, DreamDaemonSecurity.Safe, true, cancellationToken);

			Assert.AreEqual(WatchdogStatus.Online, daemonStatus.Status.Value);
			CheckDDPriority();

			Assert.AreEqual(initialCompileJob.Id, daemonStatus.ActiveCompileJob.Id);
			var newerCompileJob = daemonStatus.StagedCompileJob;

			Assert.IsNotNull(newerCompileJob);
			Assert.AreNotEqual(initialCompileJob.Id, newerCompileJob.Id);
			Assert.AreEqual(DreamDaemonSecurity.Safe, newerCompileJob.MinimumSecurityLevel);

			await CheckDMApiFail(daemonStatus.ActiveCompileJob, cancellationToken);
			daemonStatus = await TellWorldToReboot(cancellationToken);

			Assert.AreNotEqual(initialCompileJob.Id, daemonStatus.ActiveCompileJob.Id);
			Assert.IsNull(daemonStatus.StagedCompileJob);

			TestLinuxIsntBeingFuckingCheekyAboutFilePaths(daemonStatus, initialCompileJob);

			await instanceClient.DreamDaemon.Shutdown(cancellationToken);

			daemonStatus = await instanceClient.DreamDaemon.Read(cancellationToken);
			Assert.AreEqual(WatchdogStatus.Offline, daemonStatus.Status.Value);
			await CheckDMApiFail(daemonStatus.ActiveCompileJob, cancellationToken);
		}

		async Task RunLongRunningTestThenUpdateWithNewDme(CancellationToken cancellationToken)
		{
			System.Console.WriteLine("TEST: WATCHDOG LONG RUNNING WITH NEW DME TEST");

			var daemonStatus = await DeployTestDme("LongRunning/long_running_test", DreamDaemonSecurity.Trusted, true, cancellationToken);

			var initialCompileJob = daemonStatus.ActiveCompileJob;
			Assert.AreEqual(WatchdogStatus.Offline, daemonStatus.Status.Value);
			Assert.IsNotNull(daemonStatus.ActiveCompileJob);
			Assert.IsNull(daemonStatus.StagedCompileJob);
			Assert.AreEqual(DMApiConstants.InteropVersion, daemonStatus.ActiveCompileJob.DMApiVersion);
			Assert.AreEqual(DreamDaemonSecurity.Safe, daemonStatus.ActiveCompileJob.MinimumSecurityLevel);

			var startJob = await StartDD(cancellationToken);

			await WaitForJob(startJob, 40, false, null, cancellationToken);

			daemonStatus = await DeployTestDme("LongRunning/long_running_test_copy", DreamDaemonSecurity.Safe, true, cancellationToken);


			Assert.AreEqual(WatchdogStatus.Online, daemonStatus.Status.Value);
			CheckDDPriority();

			Assert.AreEqual(initialCompileJob.Id, daemonStatus.ActiveCompileJob.Id);
			var newerCompileJob = daemonStatus.StagedCompileJob;

			Assert.IsNotNull(newerCompileJob);
			Assert.AreNotEqual(initialCompileJob.Id, newerCompileJob.Id);
			Assert.AreEqual(DreamDaemonSecurity.Safe, newerCompileJob.MinimumSecurityLevel);

			await CheckDMApiFail(daemonStatus.ActiveCompileJob, cancellationToken);
			daemonStatus = await TellWorldToReboot(cancellationToken);

			Assert.AreNotEqual(initialCompileJob.Id, daemonStatus.ActiveCompileJob.Id);
			Assert.IsNull(daemonStatus.StagedCompileJob);

			await instanceClient.DreamDaemon.Shutdown(cancellationToken);

			daemonStatus = await instanceClient.DreamDaemon.Read(cancellationToken);
			Assert.AreEqual(WatchdogStatus.Offline, daemonStatus.Status.Value);
			await CheckDMApiFail(daemonStatus.ActiveCompileJob, cancellationToken);
		}

		async Task RunLongRunningTestThenUpdateWithByondVersionSwitch(CancellationToken cancellationToken)
		{
			System.Console.WriteLine("TEST: WATCHDOG BYOND VERSION UPDATE TEST");
			var versionToInstall = testVersion;

			versionToInstall = versionToInstall.Semver();
			var currentByondVersion = await instanceClient.Byond.ActiveVersion(cancellationToken);
			Assert.AreNotEqual(versionToInstall, currentByondVersion.Version);

			var initialStatus = await instanceClient.DreamDaemon.Read(cancellationToken);

			var startJob = await StartDD(cancellationToken);

			await WaitForJob(startJob, 70, false, null, cancellationToken);

			CheckDDPriority();

			var byondInstallJobTask = instanceClient.Byond.SetActiveVersion(
				new ByondVersionRequest
				{
					Version = versionToInstall
				},
				null,
				cancellationToken);
			var byondInstallJob = await byondInstallJobTask;

			// This used to be the case but it gets deleted now that we have and test that
			// Assert.IsNull(byondInstallJob.InstallJob);
			await WaitForJob(byondInstallJob.InstallJob, 60, false, null, cancellationToken);

			const string DmeName = "LongRunning/long_running_test";

			await DeployTestDme(DmeName, DreamDaemonSecurity.Safe, true, cancellationToken);

			var daemonStatus = await instanceClient.DreamDaemon.Read(cancellationToken);
			Assert.AreEqual(WatchdogStatus.Online, daemonStatus.Status.Value);
			Assert.IsNotNull(daemonStatus.ActiveCompileJob);


			Assert.AreEqual(initialStatus.ActiveCompileJob.Id, daemonStatus.ActiveCompileJob.Id);
			var newerCompileJob = daemonStatus.StagedCompileJob;
			Assert.AreNotEqual(daemonStatus.ActiveCompileJob.ByondVersion, newerCompileJob.ByondVersion);
			Assert.AreEqual(versionToInstall, newerCompileJob.ByondVersion);

			Assert.AreEqual(true, daemonStatus.SoftRestart);

			await CheckDMApiFail(daemonStatus.ActiveCompileJob, cancellationToken);
			daemonStatus = await TellWorldToReboot(cancellationToken);

			Assert.AreEqual(versionToInstall, daemonStatus.ActiveCompileJob.ByondVersion);
			Assert.IsNull(daemonStatus.StagedCompileJob);

			await instanceClient.DreamDaemon.Shutdown(cancellationToken);
			await CheckDMApiFail(daemonStatus.ActiveCompileJob, cancellationToken);

			daemonStatus = await instanceClient.DreamDaemon.Read(cancellationToken);
			Assert.AreEqual(WatchdogStatus.Offline, daemonStatus.Status.Value);
		}

		public async Task StartAndLeaveRunning(CancellationToken cancellationToken)
		{
			System.Console.WriteLine("TEST: WATCHDOG STARTING ENDLESS");
			var dd = await instanceClient.DreamDaemon.Read(cancellationToken);
			if (dd.ActiveCompileJob == null)
				await DeployTestDme("LongRunning/long_running_test", DreamDaemonSecurity.Trusted, true, cancellationToken);

			var startJob = await StartDD(cancellationToken);

			await WaitForJob(startJob, 40, false, null, cancellationToken);

			var daemonStatus = await instanceClient.DreamDaemon.Read(cancellationToken);

			Assert.AreEqual(WatchdogStatus.Online, daemonStatus.Status.Value);
			CheckDDPriority();
			Assert.AreEqual(ddPort, daemonStatus.CurrentPort);

			// Try killing the DD process to ensure it gets set to the restoring state
			do
			{
				KillDD(true);
				await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
				daemonStatus = await instanceClient.DreamDaemon.Read(cancellationToken);
			}
			while (daemonStatus.Status == WatchdogStatus.Online);
			Assert.AreEqual(WatchdogStatus.Restoring, daemonStatus.Status.Value);

			// Kill it again
			do
			{
				KillDD(false);
				daemonStatus = await instanceClient.DreamDaemon.Read(cancellationToken);
			}
			while (daemonStatus.Status == WatchdogStatus.Online || daemonStatus.Status == WatchdogStatus.Restoring);
			Assert.AreEqual(WatchdogStatus.DelayedRestart, daemonStatus.Status.Value);

			await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);

			daemonStatus = await instanceClient.DreamDaemon.Read(cancellationToken);
			Assert.AreEqual(WatchdogStatus.Online, daemonStatus.Status.Value);
			await CheckDMApiFail(daemonStatus.ActiveCompileJob, cancellationToken);
		}

		bool KillDD(bool require)
		{
			var ddProcs = TestLiveServer.GetDDProcessesOnPort(ddPort).Where(x => !x.HasExited).ToList();
			if (require && ddProcs.Count == 0 || ddProcs.Count > 1)
				Assert.Fail($"Incorrect number of DD processes: {ddProcs.Count}");

			using var ddProc = ddProcs.SingleOrDefault();
			ddProc?.Kill();
			ddProc?.WaitForExit();

			return ddProc != null;
		}

		public Task<DreamDaemonResponse> TellWorldToReboot(CancellationToken cancellationToken) => TellWorldToReboot2(instanceClient, topicClient, ddPort, cancellationToken);
		public static async Task<DreamDaemonResponse> TellWorldToReboot2(IInstanceClient instanceClient, ITopicClient topicClient, ushort ddPort, CancellationToken cancellationToken)
		{
			var daemonStatus = await instanceClient.DreamDaemon.Read(cancellationToken);
			Assert.IsNotNull(daemonStatus.StagedCompileJob);
			var initialCompileJob = daemonStatus.ActiveCompileJob;

			System.Console.WriteLine("TEST: Sending world reboot topic...");
			var result = await topicClient.SendTopic(IPAddress.Loopback, "tgs_integration_test_special_tactics=1", ddPort, cancellationToken);
			Assert.AreEqual("ack", result.StringData);

			using var tempCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			var tempToken = tempCts.Token;
			using (tempToken.Register(() => System.Console.WriteLine("TEST ERROR: Timeout in TellWorldToReboot!")))
			{
				tempCts.CancelAfter(TimeSpan.FromMinutes(2));

				do
				{
					await Task.Delay(TimeSpan.FromSeconds(1), tempToken);
					daemonStatus = await instanceClient.DreamDaemon.Read(tempToken);
				}
				while (initialCompileJob.Id == daemonStatus.ActiveCompileJob.Id);
			}

			await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);

			return daemonStatus;
		}

		async Task<DreamDaemonResponse> DeployTestDme(string dmeName, DreamDaemonSecurity deploymentSecurity, bool requireApi, CancellationToken cancellationToken)
		{
			var refreshed = await instanceClient.DreamMaker.Update(new DreamMakerRequest
			{
				ApiValidationSecurityLevel = deploymentSecurity,
				ProjectName = dmeName.Contains("rooted") ? dmeName : $"tests/DMAPI/{dmeName}",
				RequireDMApiValidation = requireApi,
				Timeout = TimeSpan.FromMilliseconds(1),
			}, cancellationToken);

			JobResponse compileJobJob;
			if (!ranTimeoutTest)
			{
				Assert.AreEqual(deploymentSecurity, refreshed.ApiValidationSecurityLevel);
				Assert.AreEqual(requireApi, refreshed.RequireDMApiValidation);
				Assert.AreEqual(TimeSpan.FromMilliseconds(1), refreshed.Timeout);

				compileJobJob = await instanceClient.DreamMaker.Compile(cancellationToken);

				await WaitForJob(compileJobJob, 90, true, ErrorCode.DeploymentTimeout, cancellationToken);
				ranTimeoutTest = true;
			}

			await instanceClient.DreamMaker.Update(new DreamMakerRequest
			{
				Timeout = TimeSpan.FromMinutes(5),
			}, cancellationToken);

			compileJobJob = await instanceClient.DreamMaker.Compile(cancellationToken);
			await WaitForJob(compileJobJob, 90, false, null, cancellationToken);

			var ddInfo = await instanceClient.DreamDaemon.Read(cancellationToken);
			if (requireApi)
				Assert.IsNotNull((ddInfo.StagedCompileJob ?? ddInfo.ActiveCompileJob).DMApiVersion);
			return ddInfo;
		}

		async Task GracefulWatchdogShutdown(uint timeout, CancellationToken cancellationToken)
		{
			await instanceClient.DreamDaemon.Update(new DreamDaemonRequest
			{
				SoftShutdown = true
			}, cancellationToken);

			var newStatus = await instanceClient.DreamDaemon.Read(cancellationToken);
			Assert.IsTrue(newStatus.SoftShutdown.Value || newStatus.Status.Value == WatchdogStatus.Offline);

			do
			{
				await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
				var ddStatus = await instanceClient.DreamDaemon.Read(cancellationToken);
				if (ddStatus.Status.Value == WatchdogStatus.Offline)
					break;

				if (--timeout == 0)
					Assert.Fail("DreamDaemon didn't shutdown within the timeout!");
			}
			while (timeout > 0);
		}

		async Task CheckDMApiFail(CompileJobResponse compileJob, CancellationToken cancellationToken, bool checkLogs = true)
		{
			var gameDir = Path.Combine(instanceClient.Metadata.Path, "Game", compileJob.DirectoryName.Value.ToString(), Path.GetDirectoryName(compileJob.DmeName));
			var failFile = Path.Combine(gameDir, "test_fail_reason.txt");
			if (!File.Exists(failFile))
			{
				var successFile = Path.Combine(gameDir, "test_success.txt");
				Assert.IsTrue(File.Exists(successFile));
			}
			else
			{
				var text = await File.ReadAllTextAsync(failFile, cancellationToken);
				Assert.Fail(text);
			}

			if (!checkLogs)
				return;

			var daemonStatus = await instanceClient.DreamDaemon.Read(cancellationToken);
			if (daemonStatus.Status != WatchdogStatus.Offline || !daemonStatus.LogOutput.Value)
				return;

			var outerLogsDir = Path.Combine(instanceClient.Metadata.Path, "Diagnostics", "DreamDaemonLogs");
			var logsDir = new DirectoryInfo(outerLogsDir).GetDirectories().OrderByDescending(x => x.CreationTime).FirstOrDefault();
			Assert.IsNotNull(logsDir);

			var logfile = logsDir.GetFiles().OrderByDescending(x => x.CreationTime).FirstOrDefault();
			Assert.IsNotNull(logfile);

			var logtext = await File.ReadAllTextAsync(logfile.FullName, cancellationToken);
			Assert.IsFalse(String.IsNullOrWhiteSpace(logtext));
		}
	}
}
