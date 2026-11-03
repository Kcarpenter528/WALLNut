using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;
using WallNut.Service.Configuration;
using WallNut.Service.Monitoring;
using WallNut.Shared.Actions;

namespace WallNut.Service.Actions;

/// <summary>Work that must finish before this computer shuts down (scripts marked "run before shutdown").</summary>
public interface IBeforeShutdown
{
    /// <summary>Runs everything and waits for it, each part up to its own time limit. Never throws.</summary>
    Task RunAsync(UpsEvent shutdown, CancellationToken ct);
}

/// <summary>
/// Runs a script for an event. Event details arrive as WALLNUT_* environment variables (and as
/// {placeholders} in the arguments). Output goes to the service log. A script that runs too long is
/// stopped, with everything it started.
/// </summary>
public sealed class ScriptRunner(
    IOptionsMonitor<WallNutOptions> options,
    UpsStateStore states,
    ILogger<ScriptRunner> log) : IActionSender, IBeforeShutdown
{
    private const int MaxOutput = 16 * 1024;

    public string Type => ActionTypes.Script;

    /// <summary>Tests turn this off to run scripts from folders other users can write to.</summary>
    public bool CheckSafety { get; init; } = true;

    public async Task<ActionResult> SendAsync(ActionOptions action, ActionMessage m, CancellationToken ct)
    {
        var s = action.Script ?? throw new InvalidOperationException("Script settings are missing.");
        // A shutdown rehearsal shouldn't stop real things (VMs, databases) unless asked to.
        if (m.Kind == "ShutdownInitiated" && m.TestMode && !s.RunInTestMode)
            return ActionResult.Success("skipped: this was a shutdown test (turn on \"Also run in test mode\" to run it)");
        return await RunAsync(action.Name ?? "script", s, m, ct);
    }

    public async Task RunAsync(UpsEvent shutdown, CancellationToken ct)
    {
        var o = options.CurrentValue;
        var message = ActionMessage.From(shutdown, states, o.Shutdown.DryRun);
        var runs = new List<Task>();
        foreach (var (id, a) in o.Actions)
        {
            if (!a.Enabled || a.Type != ActionTypes.Script || a.Script is not { RunBeforeShutdown: true } s) continue;
            var name = a.Name ?? id;
            if (ActionValidation.Validate(id, a) is { Count: > 0 } problems)
            {
                log.LogError("Not running '{Name}' before shutdown: {Problems}", name, string.Join(" ", problems));
                continue;
            }
            if (message.TestMode && !s.RunInTestMode)
            {
                log.LogWarning("Shutdown test: would run '{Name}' ({Path}) and wait up to {Timeout}s.", name, s.Path, s.TimeoutSeconds);
                continue;
            }
            log.LogWarning("Running '{Name}' before shutdown (waiting up to {Timeout}s)", name, s.TimeoutSeconds);
            runs.Add(Task.Run(async () =>
            {
                try
                {
                    var r = await RunAsync(name, s, message, ct);
                    if (r.Ok) log.LogWarning("'{Name}' finished before shutdown: {Result}", name, r.Message);
                    else log.LogError("'{Name}' before shutdown: {Result}. Shutting down anyway.", name, r.Message);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    log.LogError(e, "'{Name}' before shutdown failed. Shutting down anyway.", name);
                }
            }, CancellationToken.None));
        }
        try { await Task.WhenAll(runs); }
        catch (OperationCanceledException) { }
    }

    private async Task<ActionResult> RunAsync(string name, ScriptOptions s, ActionMessage m, CancellationToken ct)
    {
        if (CheckSafety && ScriptSafety.Check(s.Path) is { } unsafeReason)
            return ActionResult.Failed("Not run: " + unsafeReason, retry: false);

        ProcessStartInfo psi;
        try { psi = StartInfo(s, m); }
        catch (NotSupportedException e) { return ActionResult.Failed(e.Message, retry: false); }

        var output = new StringBuilder();
        void Collect(string? line, bool error)
        {
            if (line is null) return;
            lock (output) if (output.Length < MaxOutput) output.AppendLine(line);
            if (error) log.LogWarning("[{Name}] {Line}", name, line);
            else log.LogInformation("[{Name}] {Line}", name, line);
        }

        var sw = Stopwatch.StartNew();
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => Collect(e.Data, false);
        process.ErrorDataReceived += (_, e) => Collect(e.Data, true);
        try { process.Start(); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return ActionResult.Failed($"couldn't start {s.Path}: {e.Message}", retry: false);
        }
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(s.TimeoutSeconds));
        try { await process.WaitForExitAsync(limit.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (ct.IsCancellationRequested) throw;
            return ActionResult.Failed($"stopped after {s.TimeoutSeconds:0}s (its time limit)", retry: false);
        }
        await process.WaitForExitAsync(CancellationToken.None); // let the output readers finish

        var took = $"{sw.Elapsed.TotalSeconds:0.#}s";
        if (process.ExitCode == 0) return ActionResult.Success($"finished in {took}");
        string last;
        lock (output) last = output.ToString().TrimEnd().Split('\n').LastOrDefault()?.Trim() ?? "";
        return ActionResult.Failed($"exited with code {process.ExitCode} after {took}" + (last.Length > 0 ? $": {last}" : ""), retry: false);
    }

    /// <summary>How to start the script: the right interpreter for its type, arguments and WALLNUT_* variables.</summary>
    public static ProcessStartInfo StartInfo(ScriptOptions s, ActionMessage m)
    {
        var path = Path.GetFullPath(s.Path);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var isCmd = OperatingSystem.IsWindows() && ext is ".bat" or ".cmd";
        var args = SplitArguments(s.Arguments ?? "").Select(a => Fill(a, m.Fields, isCmd)).ToList();

        ProcessStartInfo psi;
        if (ext == ".ps1")
        {
            var shell = OperatingSystem.IsWindows()
                ? Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe")
                : "pwsh";
            psi = new ProcessStartInfo(shell);
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            if (OperatingSystem.IsWindows()) { psi.ArgumentList.Add("-ExecutionPolicy"); psi.ArgumentList.Add("Bypass"); }
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(path);
        }
        else if (isCmd)
        {
            // cmd.exe has its own quoting rules, so build its command line by hand.
            var cmd = Environment.GetEnvironmentVariable("ComSpec") ?? Path.Combine(Environment.SystemDirectory, "cmd.exe");
            var line = "\"" + path + "\"" + string.Concat(args.Select(a => " \"" + a + "\""));
            psi = new ProcessStartInfo(cmd) { Arguments = $"/d /c \"{line}\"" };
            args.Clear();
        }
        else if (ext == ".sh" && !OperatingSystem.IsWindows())
        {
            psi = new ProcessStartInfo("/bin/sh");
            psi.ArgumentList.Add(path);
        }
        else if (OperatingSystem.IsWindows() && ext is not (".exe" or ".com"))
        {
            throw new NotSupportedException($"WALLNut can run .ps1, .bat, .cmd and .exe files on Windows, not {ext} files.");
        }
        else psi = new ProcessStartInfo(path); // an executable, or a script with a #! line

        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.WorkingDirectory = Path.GetDirectoryName(path)!;
        foreach (var (key, value) in m.Fields) psi.Environment["WALLNUT_" + key.ToUpperInvariant()] = value;
        psi.Environment["WALLNUT_IS_TEST"] = m.IsTest ? "true" : "false";
        return psi;
    }

    /// <summary>Replaces {event}, {ups}, ... with the event's values.</summary>
    private static string Fill(string arg, IReadOnlyDictionary<string, string> fields, bool forCmd)
    {
        foreach (var (key, value) in fields)
        {
            var v = forCmd ? value.Replace("\"", "'").Replace("%", "") : value; // can't be escaped safely for cmd.exe
            arg = arg.Replace("{" + key + "}", v, StringComparison.OrdinalIgnoreCase);
        }
        return arg;
    }

    /// <summary>Splits an argument string like a shell would: spaces separate, double quotes group.</summary>
    public static List<string> SplitArguments(string text)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        bool quoted = false, any = false;
        foreach (var c in text)
        {
            if (c == '"') { quoted = !quoted; any = true; }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (any || current.Length > 0) result.Add(current.ToString());
                current.Clear();
                any = false;
            }
            else current.Append(c);
        }
        if (any || current.Length > 0) result.Add(current.ToString());
        return result;
    }
}
