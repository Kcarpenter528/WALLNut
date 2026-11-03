using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging.Abstractions;
using WallNut.Service.Actions;
using WallNut.Service.Configuration;
using WallNut.Service.Monitoring;
using WallNut.Service.Shutdown;
using WallNut.Shared.Actions;

namespace WallNut.Service.Tests;

/// <summary>Running scripts for events, and before shutdown.</summary>
public sealed class ScriptActionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"wallnut-scripts-{Guid.NewGuid():N}");

    public ScriptActionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static bool Win => OperatingSystem.IsWindows();

    /// <summary>Writes a .cmd (Windows) or .sh script and returns its path.</summary>
    private string Script(string name, string windows, string unix, string? dir = null)
    {
        var path = Path.Combine(dir ?? _dir, name + (Win ? ".cmd" : ".sh"));
        File.WriteAllText(path, Win ? "@echo off\r\n" + windows.Replace("\n", "\r\n") : "#!/bin/sh\n" + unix);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static ScriptRunner Runner(WallNutOptions? o = null, bool checkSafety = true) =>
        new(TestOptions.Fixed(o ?? new WallNutOptions()), new UpsStateStore(), NullLogger<ScriptRunner>.Instance) { CheckSafety = checkSafety };

    private static ActionOptions Action(string path, string? args = null, double timeout = 30, bool beforeShutdown = false, bool inTestMode = false) => new()
    {
        Name = "test script", Type = ActionTypes.Script,
        Script = new ScriptOptions { Path = path, Arguments = args, TimeoutSeconds = timeout, RunBeforeShutdown = beforeShutdown, RunInTestMode = inTestMode },
    };

    [Fact]
    public async Task Script_gets_event_details_as_variables_and_arguments()
    {
        var output = Path.Combine(_dir, "out.txt");
        var path = Script("details",
            $"echo event=%WALLNUT_EVENT% ups=%WALLNUT_UPS% charge=%WALLNUT_CHARGE% test=%WALLNUT_IS_TEST% a1=%~1 a2=%~2> \"{output}\"",
            $"echo \"event=$WALLNUT_EVENT ups=$WALLNUT_UPS charge=$WALLNUT_CHARGE test=$WALLNUT_IS_TEST a1=$1 a2=$2\" > \"{output}\"\n");

        var result = await Runner().SendAsync(Action(path, "\"{ups} now\" {status}"), ActionMessage.Test(), default);

        Assert.True(result.Ok, result.Message);
        Assert.Equal("event=Test ups=Test UPS charge=100 test=true a1=Test UPS now a2=OL", File.ReadAllText(output).Trim());
    }

    [Fact]
    public async Task Failure_reports_the_exit_code_and_last_output()
    {
        var path = Script("fails", "echo disk not mounted 1>&2\nexit /b 3", "echo 'disk not mounted' >&2\nexit 3\n");
        var result = await Runner().SendAsync(Action(path), ActionMessage.Test(), default);
        Assert.False(result.Ok);
        Assert.False(result.Retry); // scripts aren't retried: they may not be safe to run twice
        Assert.Contains("code 3", result.Message);
        Assert.Contains("disk not mounted", result.Message);
    }

    [Fact]
    public async Task Script_that_runs_too_long_is_stopped()
    {
        var path = Script("slow", "ping -n 30 127.0.0.1 > nul", "sleep 30\n");
        var sw = Stopwatch.StartNew();
        var result = await Runner().SendAsync(Action(path, timeout: 1), ActionMessage.Test(), default);
        Assert.False(result.Ok);
        Assert.Contains("time limit", result.Message);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"took {sw.Elapsed}");
    }

    [Fact]
    public async Task Shutdown_rehearsal_skips_the_script_unless_asked()
    {
        var marker = Path.Combine(_dir, "ran.txt");
        var path = Script("marker", $"echo ran> \"{marker}\"", $"echo ran > \"{marker}\"\n");
        var rehearsal = ActionMessage.Test() with { Kind = "ShutdownInitiated", TestMode = true };

        var skipped = await Runner().SendAsync(Action(path), rehearsal, default);
        Assert.True(skipped.Ok);
        Assert.Contains("skipped", skipped.Message);
        Assert.False(File.Exists(marker));

        var ran = await Runner().SendAsync(Action(path, inTestMode: true), rehearsal, default);
        Assert.True(ran.Ok, ran.Message);
        Assert.True(File.Exists(marker));
    }

    [Fact]
    public void Script_in_a_folder_others_can_change_is_refused()
    {
        var shared = Directory.CreateDirectory(Path.Combine(_dir, "shared")).FullName;
        var path = Script("open", "echo hi", "echo hi\n", shared);
        Assert.Null(ScriptSafety.Check(path));

        if (OperatingSystem.IsWindows())
        {
            var acl = new DirectoryInfo(shared).GetAccessControl();
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.Modify, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(shared).SetAccessControl(acl);
        }
        else File.SetUnixFileMode(shared, File.GetUnixFileMode(shared) | UnixFileMode.OtherWrite);

        var problem = ScriptSafety.Check(path);
        Assert.NotNull(problem);
        Assert.Contains("only administrators", problem);
    }

    [Fact]
    public async Task Unsafe_script_is_not_run()
    {
        var marker = Path.Combine(_dir, "ran.txt");
        var shared = Directory.CreateDirectory(Path.Combine(_dir, "shared")).FullName;
        var path = Script("open", $"echo ran> \"{marker}\"", $"echo ran > \"{marker}\"\n", shared);
        if (OperatingSystem.IsWindows())
        {
            var file = new FileInfo(path);
            var acl = file.GetAccessControl();
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                FileSystemRights.Write, AccessControlType.Allow));
            file.SetAccessControl(acl);
        }
        else File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.OtherWrite);

        var result = await Runner().SendAsync(Action(path), ActionMessage.Test(), default);
        Assert.False(result.Ok);
        Assert.StartsWith("Not run", result.Message);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task Before_shutdown_waits_for_scripts_and_respects_test_mode()
    {
        var marker = Path.Combine(_dir, "stopped-vms.txt");
        var path = Script("stop-vms", $"ping -n 2 127.0.0.1 > nul\necho done> \"{marker}\"", $"sleep 1\necho done > \"{marker}\"\n");
        WallNutOptions Options(bool dryRun, bool inTestMode) => new()
        {
            Shutdown = new ShutdownOptions { DryRun = dryRun },
            Actions = { ["vms"] = Action(path, beforeShutdown: true, inTestMode: inTestMode) },
        };
        var shutdown = new UpsEvent(UpsEvent.ThisMachine, "PC", UpsEventKind.ShutdownInitiated, DateTimeOffset.UtcNow, null, "Shutting down");

        await Runner(Options(dryRun: true, inTestMode: false)).RunAsync(shutdown, default);
        Assert.False(File.Exists(marker)); // a rehearsal doesn't stop the VMs

        await Runner(Options(dryRun: false, inTestMode: false)).RunAsync(shutdown, default);
        Assert.True(File.Exists(marker)); // finished before RunAsync returned
    }

    private sealed class SlowHook : IBeforeShutdown
    {
        public DateTimeOffset? Finished;
        public async Task RunAsync(UpsEvent shutdown, CancellationToken ct)
        {
            await Task.Delay(500, ct);
            Finished = DateTimeOffset.UtcNow;
        }
    }

    [Fact]
    public async Task Shutdown_waits_for_before_shutdown_work()
    {
        var store = new UpsStateStore();
        store.Set(ShutdownPolicyTests.Ups("nas", "OB LB"));
        var exec = new RecordingExecutor();
        var hook = new SlowHook();
        var c = new ShutdownCoordinator(
            TestOptions.Fixed(new WallNutOptions { Shutdown = new ShutdownOptions { DryRun = false, FinalDelaySeconds = 0 } }),
            store, [new RecordingSink()], exec, NullLogger<ShutdownCoordinator>.Instance, TimeProvider.System, hook);

        await c.CheckAsync(default);

        Assert.NotNull(hook.Finished);
        Assert.Single(exec.Calls);
    }

    [Fact]
    public void Arguments_split_like_a_shell()
    {
        Assert.Equal(["a", "b c", "", "d"], ScriptRunner.SplitArguments("a \"b c\" \"\"  d"));
        Assert.Empty(ScriptRunner.SplitArguments("   "));
    }
}
