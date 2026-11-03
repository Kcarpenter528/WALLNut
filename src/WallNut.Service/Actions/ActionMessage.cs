using System.Globalization;
using WallNut.Service.Monitoring;
using WallNut.Shared;

namespace WallNut.Service.Actions;

/// <summary>Everything an action needs to say about an event, gathered once.</summary>
public sealed record ActionMessage
{
    public required string Kind { get; init; }
    /// <summary>"Power failure: Synology NAS".</summary>
    public required string Title { get; init; }
    public required string Message { get; init; }
    public required NotificationUrgency Urgency { get; init; }
    public required bool IsProblem { get; init; }
    public required DateTimeOffset Time { get; init; }
    public required string Machine { get; init; }

    public string? UpsId { get; init; }
    public string? UpsName { get; init; }
    public string? Host { get; init; }
    public string? Status { get; init; }
    public double? Charge { get; init; }
    public double? RuntimeSeconds { get; init; }
    public double? Load { get; init; }

    /// <summary>The shutdown is a rehearsal (test mode).</summary>
    public bool TestMode { get; init; }
    public bool IsTest => Kind == "Test";

    /// <summary>Values for webhook templates and script environment variables.</summary>
    public IReadOnlyDictionary<string, string> Fields => new Dictionary<string, string>
    {
        ["event"] = Kind,
        ["title"] = Title,
        ["message"] = Message,
        ["ups"] = UpsName ?? "",
        ["ups_id"] = UpsId ?? "",
        ["host"] = Host ?? "",
        ["status"] = Status ?? "",
        ["charge"] = F(Charge),
        ["runtime"] = F(RuntimeSeconds),
        ["load"] = F(Load),
        ["machine"] = Machine,
        ["time"] = Time.ToString("o", CultureInfo.InvariantCulture),
        ["problem"] = IsProblem ? "true" : "false",
        ["urgency"] = Urgency.ToString().ToLowerInvariant(),
        ["test_mode"] = TestMode ? "true" : "false",
    };

    private static string F(double? v) => v?.ToString("0.##", CultureInfo.InvariantCulture) ?? "";

    public static ActionMessage From(UpsEvent e, UpsStateStore states, bool shutdownTestMode)
    {
        var kind = NotificationCatalog.Find(e.Kind.ToString());
        var machineEvent = e.EndpointId == UpsEvent.ThisMachine;
        var s = machineEvent ? null : states.Get(e.EndpointId);
        static double? N(string? v) => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
        return new ActionMessage
        {
            Kind = e.Kind.ToString(),
            Title = machineEvent ? kind.Title : $"{kind.Title}: {e.DisplayName}",
            Message = e.Message,
            Urgency = kind.Urgency,
            IsProblem = e.IsProblem,
            Time = e.Time,
            Machine = Environment.MachineName,
            UpsId = machineEvent ? null : e.EndpointId,
            UpsName = machineEvent ? null : e.DisplayName,
            Host = s?.Host,
            Status = e.Status?.Raw ?? s?.Status?.Raw,
            Charge = N(s?.Var("battery.charge")),
            RuntimeSeconds = N(s?.Var("battery.runtime")),
            Load = N(s?.Var("ups.load")),
            TestMode = shutdownTestMode,
        };
    }

    /// <summary>The message an action's Test button sends.</summary>
    public static ActionMessage Test() => new()
    {
        Kind = "Test",
        Title = "WALLNut test",
        Message = $"This is a test from WALLNut on {Environment.MachineName}. If you can see it, this alert works.",
        Urgency = NotificationUrgency.Normal,
        IsProblem = false,
        Time = DateTimeOffset.UtcNow,
        Machine = Environment.MachineName,
        UpsName = "Test UPS",
        UpsId = "test",
        Status = "OL",
        Charge = 100,
        RuntimeSeconds = 1800,
        Load = 25,
    };
}

/// <summary>What happened when an action ran.</summary>
public sealed record ActionResult(bool Ok, string Message, bool Retry = false)
{
    public static ActionResult Success(string message) => new(true, message);
    public static ActionResult Failed(string message, bool retry) => new(false, message, retry);
}

/// <summary>Delivers one kind of action (ntfy, webhook, script, email).</summary>
public interface IActionSender
{
    string Type { get; }
    Task<ActionResult> SendAsync(Configuration.ActionOptions action, ActionMessage message, CancellationToken ct);
}
