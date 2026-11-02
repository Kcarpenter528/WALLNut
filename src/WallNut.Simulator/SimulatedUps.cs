using System.Globalization;

namespace WallNut.Simulator;

/// <summary>Result of a SET VAR or INSTCMD, mapped to NUT replies by the server.</summary>
public enum SimResult { Ok, UnknownVariable, Readonly, InvalidValue, UnknownCommand, InvalidArgument }

/// <summary>
/// One simulated UPS. Holds its variables and a simple physical model: the battery drains
/// on battery power (faster at higher load), recharges on mains, and the status flags, timers
/// and readings follow from that. Time only moves when <see cref="Advance"/> is called, so
/// tests can fast-forward exactly. All members are thread-safe.
/// </summary>
public sealed class SimulatedUps
{
    private enum PendingOff { None, Return, StayOff, Reboot }

    private readonly object _gate = new();
    private readonly SortedDictionary<string, string> _vars = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _decimals = new(StringComparer.Ordinal);
    private readonly Random _rng;
    private readonly SimulationSettings _sim;

    // Physical state.
    private bool _mains = true;
    private string _outageReason = "input voltage out of range";
    private double _inputVoltage;
    private double _charge = 100;
    private double _load;
    private bool _outputOn = true;
    private bool _waitingForMains;     // output is off and comes back when power returns
    private bool _replaceBattery;
    private bool _bypass;
    private bool _calibrating;
    private bool _fsd;
    private bool _stale;
    private double _secondsSinceFull = double.MaxValue;
    private DateTime _clock;
    private double _uptime;

    // Timers (seconds), null when not running.
    private double? _offIn, _onIn, _testIn;
    private PendingOff _pendingOff;
    private readonly Dictionary<int, bool> _outletOn = [];
    private readonly Dictionary<string, double> _ambientBase = new(StringComparer.Ordinal);

    public string Name { get; }
    public UpsProfile Profile { get; }
    public string Description => Profile.Description;

    /// <summary>Random wobble on readings (voltage, load, temperature). Off makes values exact for tests.</summary>
    public bool Jitter { get; set; } = true;

    public SimulatedUps(string name, UpsProfile profile, DateTime? startTime = null, int? seed = null)
    {
        Name = name;
        Profile = profile;
        _sim = profile.Simulation;
        _rng = new Random(seed ?? StringComparer.Ordinal.GetHashCode(name));
        _clock = startTime ?? DateTime.Now;

        foreach (var (k, v) in profile.Vars)
        {
            _vars[k] = v;
            var dot = v.IndexOf('.');
            _decimals[k] = dot >= 0 && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
                ? v.Length - dot - 1 : 0;
        }
        foreach (var k in _vars.Keys.Where(k => k.StartsWith("ambient.", StringComparison.Ordinal)
                     && (k.EndsWith(".temperature", StringComparison.Ordinal) || k.EndsWith(".humidity", StringComparison.Ordinal))))
        {
            _ambientBase[k] = Num(k, 0);
        }
        foreach (var k in _vars.Keys.Where(k => k.StartsWith("outlet.", StringComparison.Ordinal) && k.EndsWith(".id", StringComparison.Ordinal)))
        {
            if (int.TryParse(_vars[k], out var n) && n > 0) _outletOn[n] = true;
        }

        _inputVoltage = Num("input.voltage.nominal", _sim.Phases == 3 ? 400 : 120);
        _load = _sim.LoadPercent;
        lock (_gate) Recalculate();
    }

    // ---- Reading ------------------------------------------------------------------------

    public bool IsStale { get { lock (_gate) return _stale; } }
    public bool IsOnBattery { get { lock (_gate) return OnBattery; } }
    public double Charge { get { lock (_gate) return _charge; } }
    public double LoadPercent { get { lock (_gate) return _load; } }

    public string? Get(string name)
    {
        lock (_gate) return _vars.TryGetValue(name, out var v) ? v : null;
    }

    public IReadOnlyList<KeyValuePair<string, string>> Snapshot()
    {
        lock (_gate) return _vars.ToList();
    }

    public bool IsWritable(string name) => Profile.Rw.ContainsKey(name);
    public IReadOnlyList<string> Commands => Profile.Commands;

    // ---- Writing (SET VAR / INSTCMD) ----------------------------------------------------

    public SimResult Set(string name, string value)
    {
        lock (_gate)
        {
            if (!_vars.ContainsKey(name)) return SimResult.UnknownVariable;
            if (!Profile.Rw.TryGetValue(name, out var spec)) return SimResult.Readonly;
            if (!spec.Accepts(value)) return SimResult.InvalidValue;
            _vars[name] = value;
            if (name == "input.voltage.nominal") _inputVoltage = Num(name, _inputVoltage);
            Recalculate();
            return SimResult.Ok;
        }
    }

    public SimResult RunCommand(string command, string? param = null)
    {
        lock (_gate)
        {
            if (!Profile.Commands.Contains(command)) return SimResult.UnknownCommand;
            double? seconds = null;
            if (param is not null)
            {
                if (!double.TryParse(param, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) || p < 0)
                    return SimResult.InvalidArgument;
                seconds = p;
            }

            var parts = command.Split('.');
            if (parts[0] == "outlet" && parts.Length >= 3 && int.TryParse(parts[1], out var outlet))
            {
                var action = string.Join('.', parts[2..]);
                if (action is "load.off") _outletOn[outlet] = false;
                else if (action is "load.on" or "load.cycle" or "shutdown.return") _outletOn[outlet] = true;
                Recalculate();
                return SimResult.Ok;
            }

            switch (command)
            {
                case "beeper.enable" or "beeper.on": Write("ups.beeper.status", "enabled"); break;
                case "beeper.disable" or "beeper.off": Write("ups.beeper.status", "disabled"); break;
                case "beeper.mute": Write("ups.beeper.status", "muted"); break;
                case "beeper.toggle":
                    Write("ups.beeper.status", Get("ups.beeper.status") == "enabled" ? "disabled" : "enabled"); break;

                case "load.off": TurnOutputOff(waitForMains: false); break;
                case "load.on": TurnOutputOn(); break;
                case "load.cycle": TurnOutputOff(false); _onIn = seconds ?? Num("ups.delay.start", 10); break;
                case "load.off.delay": ScheduleOff(PendingOff.StayOff, seconds ?? Num("ups.delay.shutdown", 20)); break;
                case "load.on.delay": _onIn = seconds ?? Num("ups.delay.start", 30); break;

                case "shutdown.return" or "shutdown.default" or "driver.killpower":
                    ScheduleOff(PendingOff.Return, command == "driver.killpower" ? 0 : seconds ?? Num("ups.delay.shutdown", 20)); break;
                case "shutdown.stayoff": ScheduleOff(PendingOff.StayOff, seconds ?? Num("ups.delay.shutdown", 20)); break;
                case "shutdown.reboot": ScheduleOff(PendingOff.Reboot, seconds ?? 0); break;
                case "shutdown.reboot.graceful": ScheduleOff(PendingOff.Reboot, seconds ?? Num("ups.delay.shutdown", 20)); break;
                case "shutdown.stop": _offIn = null; _pendingOff = PendingOff.None; break;

                case "test.battery.start" or "test.system.start": StartTest(seconds ?? 30); break;
                case "test.battery.start.quick": StartTest(seconds ?? 10); break;
                case "test.battery.start.low": StartTest(seconds ?? 60); break;
                case "test.battery.start.deep": StartTest(seconds ?? 180); break;
                case "test.battery.stop": _testIn = null; Write("ups.test.result", "Aborted"); break;
                case "test.failure.start": PowerFailLocked("self test"); break;
                case "test.failure.stop": PowerRestoreLocked(); break;

                case "calibrate.start": _calibrating = true; break;
                case "calibrate.stop": _calibrating = false; break;
                case "bypass.start": _bypass = true; break;
                case "bypass.stop": _bypass = false; break;

                case "experimental.ecomode.start" or "experimental.bypass.ecomode.start": Write("ups.mode", "eco"); break;
                case "experimental.essmode.start": Write("ups.mode", "ess"); break;
                case "experimental.ecomode.stop" or "experimental.bypass.ecomode.stop" or "experimental.essmode.stop":
                    Write("ups.mode", "online"); break;

                case "reset.input.minmax":
                    Write("input.voltage.minimum", Fmt("input.voltage.minimum", _mains ? _inputVoltage : 0));
                    Write("input.voltage.maximum", Fmt("input.voltage.maximum", _mains ? _inputVoltage : 0));
                    break;

                default: break; // Acknowledged with no visible effect (panel test, watchdog, driver.*, outlet groups...).
            }
            Recalculate();
            return SimResult.Ok;
        }
    }

    // ---- Scenarios ----------------------------------------------------------------------

    /// <summary>Mains power is lost.</summary>
    public void PowerFail(string reason = "input voltage out of range") { lock (_gate) { PowerFailLocked(reason); Recalculate(); } }

    /// <summary>Mains power comes back.</summary>
    public void PowerRestore() { lock (_gate) { PowerRestoreLocked(); Recalculate(); } }

    /// <summary>Changes the mains voltage (line-to-line on three-phase). Outside the transfer limits, the UPS goes to battery.</summary>
    public void SetInputVoltage(double volts) { lock (_gate) { _inputVoltage = volts; Recalculate(); } }

    public void SetLoad(double percent) { lock (_gate) { _load = Math.Clamp(percent, 0, 200); Recalculate(); } }

    public void SetCharge(double percent) { lock (_gate) { _charge = Math.Clamp(percent, 0, 100); Recalculate(); } }

    public void SetReplaceBattery(bool needed) { lock (_gate) { _replaceBattery = needed; Recalculate(); } }

    /// <summary>The driver loses contact with the UPS: the server answers ERR DATA-STALE.</summary>
    public void SetStale(bool stale) { lock (_gate) _stale = stale; }

    /// <summary>Sets the forced-shutdown flag (what a NUT primary does before powering off). It stays set, like in upsd.</summary>
    public void SetForcedShutdown() { lock (_gate) { _fsd = true; Recalculate(); } }

    // ---- Time ---------------------------------------------------------------------------

    /// <summary>Moves simulated time forward.</summary>
    public void Advance(TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero) return;
        lock (_gate)
        {
            // Step in chunks so thresholds and timers fire at about the right moment.
            var remaining = elapsed.TotalSeconds;
            while (remaining > 0)
            {
                var step = Math.Min(remaining, 1.0);
                Step(step);
                remaining -= step;
            }
            Recalculate();
        }
    }

    private void Step(double s)
    {
        _clock = _clock.AddSeconds(s);
        _uptime += s;

        if (_offIn is { } off && (_offIn = off - s) <= 0)
        {
            _offIn = null;
            var kind = _pendingOff;
            _pendingOff = PendingOff.None;
            TurnOutputOff(waitForMains: kind == PendingOff.Return);
            if (kind == PendingOff.Reboot) _onIn = Num("ups.delay.start", 10);
            if (kind == PendingOff.Return && _mains && !OutOfRange) _onIn = Num("ups.delay.start", 0);
        }
        if (_onIn is { } on && (_onIn = on - s) <= 0) { _onIn = null; TurnOutputOn(); }
        if (_testIn is { } t && (_testIn = t - s) <= 0)
        {
            _testIn = null;
            Write("ups.test.result", _replaceBattery ? "Done and error" : "Done and passed");
            Write("ups.test.date", _clock.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        bool drawing = _outputOn && !_bypass && (OnBattery || _calibrating);
        if (drawing && _load > 0)
        {
            _charge -= 100.0 / RuntimeAtLoad(_load) * s;
            if (_charge <= 0)
            {
                _charge = 0;
                _calibrating = false;
                TurnOutputOff(waitForMains: true); // battery exhausted: the UPS drops the load
            }
            if (_calibrating && _charge <= 25) _calibrating = false;
            _secondsSinceFull = double.MaxValue;
        }
        else if (_mains && !OutOfRange)
        {
            var before = _charge;
            _charge = Math.Min(100, _charge + 100.0 / _sim.RechargeSec * s);
            if (_charge >= 100) _secondsSinceFull = before < 100 ? 0 : _secondsSinceFull + s;
        }
    }

    // ---- Model --------------------------------------------------------------------------

    private bool OutOfRange =>
        _vars.ContainsKey("input.transfer.low") && _inputVoltage < Num("input.transfer.low", 0)
        || _vars.ContainsKey("input.transfer.high") && _inputVoltage > Num("input.transfer.high", double.MaxValue);

    private bool OnBattery => !_bypass && (!_mains || OutOfRange);

    private double RuntimeAtLoad(double load) =>
        // Batteries last disproportionately longer at light load (Peukert-style curve), capped at 8 hours.
        Math.Min(8 * 3600, _sim.RuntimeAtFullLoadSec * Math.Pow(100.0 / Math.Max(load, 1), 1.2));

    private void PowerFailLocked(string reason)
    {
        _mains = false;
        _outageReason = reason;
        if (_bypass) TurnOutputOff(waitForMains: true); // nothing behind the bypass
    }

    private void PowerRestoreLocked()
    {
        _mains = true;
        if (_waitingForMains && !_outputOn && Get("ups.start.auto") != "no")
            _onIn = Num("ups.delay.start", 0);
    }

    private void ScheduleOff(PendingOff kind, double seconds) { _pendingOff = kind; _offIn = seconds; }

    private void StartTest(double seconds) { _testIn = seconds; Write("ups.test.result", "In progress"); }

    private void TurnOutputOff(bool waitForMains) { _outputOn = false; _waitingForMains = waitForMains; }

    private void TurnOutputOn()
    {
        if (!_mains && _charge <= 0) return; // nothing to run on
        _outputOn = true;
        _waitingForMains = false;
    }

    /// <summary>Rewrites every live variable from the current state.</summary>
    private void Recalculate()
    {
        bool onBatt = OnBattery;
        bool mainsOk = _mains && !OutOfRange;
        double load = _outputOn ? Math.Max(0, _load + Wobble(0.5)) : 0;
        double runtime = _charge / 100 * RuntimeAtLoad(Math.Max(load, 1));
        bool low = onBatt && (_charge <= Num("battery.charge.low", 10) || runtime <= Num("battery.runtime.low", 0));
        bool over = load > Num("ups.load.high", 100);
        bool charging = mainsOk && _charge < 100 && !_calibrating;
        bool discharging = _outputOn && !_bypass && (onBatt || _calibrating);
        bool lineInteractive = Get("ups.type") is not "online";
        double vin = _mains ? Math.Max(0, _inputVoltage + Wobble(1.2)) : 0;
        bool trim = lineInteractive && mainsOk && _vars.ContainsKey("input.transfer.trim.low") && vin >= Num("input.transfer.trim.low", double.MaxValue);
        bool boost = lineInteractive && mainsOk && _vars.ContainsKey("input.transfer.boost.high") && vin > 0 && vin <= Num("input.transfer.boost.high", 0);

        // Status and alarms.
        var alarms = new List<string>();
        if (_replaceBattery) alarms.Add("Replace battery!");
        if (over) alarms.Add("Overload!");
        if (onBatt && _bypass) alarms.Add("Input power lost while on bypass!");
        bool alarm = alarms.Count > 0 && _vars.ContainsKey("ups.alarm");

        var status = new List<string>();
        if (alarm) status.Add("ALARM");
        if (!_outputOn) status.Add("OFF");
        status.Add(onBatt ? "OB" : "OL");
        if (_bypass) status.Add("BYPASS");
        if (_calibrating) status.Add("CAL");
        if (trim) status.Add("TRIM");
        if (boost) status.Add("BOOST");
        if (charging) status.Add("CHRG");
        if (discharging) status.Add("DISCHRG");
        if (low) status.Add("LB");
        if (_replaceBattery) status.Add("RB");
        if (over) status.Add("OVER");
        if (_fsd) status.Add("FSD");
        Write("ups.status", string.Join(' ', status));
        Write("ups.alarm", alarm ? string.Join(' ', alarms) : "");

        // Power flow.
        double pf = _sim.PowerFactor;
        double eff = Math.Clamp(Num("ups.efficiency", 92), 50, 100) / 100;
        double realpower = load / 100 * Num("ups.realpower.nominal", Num("ups.power.nominal", 1000) * pf);
        double apparent = realpower / pf;
        double vnom = Num("output.voltage.nominal", Num("input.voltage.nominal", 120));
        double vout = _outputOn ? (onBatt || !lineInteractive || _bypass ? vnom + Wobble(0.3) : vin * (trim ? 0.88 : boost ? 1.12 : 1)) : 0;
        double fnom = Num("output.frequency.nominal", Num("input.frequency.nominal", 60));
        double fin = mainsOk || (_mains && _bypass) ? fnom + Wobble(0.05) : 0;
        double inReal = mainsOk ? realpower / eff + (charging ? 0.02 * Num("ups.realpower.nominal", 1000) : 0) : 0;

        WriteNum("ups.load", load);
        WriteNum("ups.realpower", realpower);
        WriteNum("ups.power", apparent);
        WriteNum("ups.temperature", 28 + load * 0.12 + (onBatt ? 3 : 0) + Wobble(0.2));
        WriteNum("ups.timer.shutdown", _offIn is { } o ? Math.Ceiling(o) : -1);
        WriteNum("ups.timer.start", _onIn is { } startIn ? Math.Ceiling(startIn) : -1);
        WriteNum("ups.timer.reboot", _pendingOff == PendingOff.Reboot && _offIn is { } r ? Math.Ceiling(r) : -1);
        Write("ups.time", _clock.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
        Write("ups.date", _clock.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        WriteNum("device.uptime", Math.Floor(_uptime));

        if (_sim.Phases == 3) WritePhases(vin, vout, inReal, realpower, apparent, pf, load);
        else
        {
            WriteNum("input.voltage", vin);
            WriteNum("input.current", vin > 0 ? inReal / pf / vin : 0);
            WriteNum("output.voltage", vout);
            WriteNum("output.current", vout > 0 ? apparent / vout : 0);
        }
        WriteNum("input.frequency", fin);
        WriteNum("input.realpower", inReal);
        WriteNum("input.power", inReal / pf);
        WriteNum("input.load", mainsOk ? load / eff : 0);
        WriteNum("output.frequency", _outputOn ? fnom + Wobble(0.02) : 0);
        WriteNum("output.realpower", realpower);
        WriteNum("output.power", apparent);
        WriteNum("input.bypass.voltage", _mains ? _inputVoltage + Wobble(1) : 0);
        WriteNum("input.bypass.frequency", _mains ? fnom + Wobble(0.05) : 0);
        Write("input.transfer.reason", onBatt ? (_mains ? "input voltage out of range" : _outageReason) : Get("input.transfer.reason") ?? "");
        Write("input.voltage.status", ThresholdStatus("input.voltage", vin, _mains));
        Write("input.frequency.status", !_mains ? "out-of-range" : "good");
        if (mainsOk)
        {
            if (vin < Num("input.voltage.minimum", double.MaxValue)) WriteNum("input.voltage.minimum", vin);
            if (vin > Num("input.voltage.maximum", 0)) WriteNum("input.voltage.maximum", vin);
        }

        // Battery.
        double bnom = Num("battery.voltage.nominal", 12);
        double bv = discharging
            ? bnom * (0.875 + 0.15 * _charge / 100) - load * 0.0004 * bnom
            : bnom * (charging ? 1.125 : 1.12);
        bv += Wobble(bnom * 0.002);
        double cells = Math.Max(1, bnom / 2);
        double bcur = discharging ? realpower / Math.Max(bv, 1) : charging ? 0.1 * Num("battery.capacity", 9) : 0.05;
        WriteNum("battery.charge", _charge);
        WriteNum("battery.charge.approx", Math.Round(_charge / 5) * 5);
        WriteNum("battery.runtime", Math.Floor(runtime));
        WriteNum("battery.voltage", bv);
        WriteNum("battery.voltage.cell.max", bv / cells + 0.015);
        WriteNum("battery.voltage.cell.min", bv / cells - 0.015);
        WriteNum("battery.current", bcur);
        WriteNum("battery.current.total", bcur * Math.Max(1, Num("battery.packs", 1) + Num("battery.packs.external", 0)));
        double btemp = 25 + load * 0.05 + (charging ? 1.5 : 0) + (discharging ? 2.5 : 0) + Wobble(0.1);
        WriteNum("battery.temperature", btemp);
        WriteNum("battery.temperature.cell.max", btemp + 0.8);
        WriteNum("battery.temperature.cell.min", btemp - 0.8);
        Write("battery.charger.status", discharging ? "discharging" : charging ? "charging"
            : _secondsSinceFull < 600 ? "floating" : "resting");
        Write("battery.status", _replaceBattery ? "replace" : "ok");
        WriteNum("battery.packs.bad", _replaceBattery ? 1 : 0);

        // Outlets.
        Write("outlet.status", _outputOn ? "on" : "off");
        Write("outlet.switch", _outputOn ? "on" : "off");
        int liveOutlets = Math.Max(1, _outletOn.Count(kv => kv.Value));
        foreach (var (n, isOn) in _outletOn)
        {
            bool live = _outputOn && isOn;
            string p = $"outlet.{n}.";
            Write(p + "status", live ? "on" : "off");
            Write(p + "switch", isOn ? "on" : "off");
            WriteNum(p + "voltage", live ? vout : 0);
            WriteNum(p + "current", live && vout > 0 ? apparent / liveOutlets / vout : 0);
            WriteNum(p + "power", live ? apparent / liveOutlets : 0);
            WriteNum(p + "realpower", live ? realpower / liveOutlets : 0);
        }

        // Environment probes wobble a little around their profile values.
        foreach (var (key, baseValue) in _ambientBase) WriteNum(key, baseValue + Wobble(0.3));
    }

    private void WritePhases(double vinLL, double vout, double inReal, double realpower, double apparent, double pf, double load)
    {
        double sqrt3 = Math.Sqrt(3);
        double outNomLL = Num("output.voltage.nominal", 400);
        double[] vinLN = new double[4], voutLN = new double[4];
        for (int p = 1; p <= 3; p++)
        {
            vinLN[p] = vinLL > 0 ? vinLL / sqrt3 + Wobble(0.6) : 0;
            voutLN[p] = vout > 0 ? outNomLL / sqrt3 + Wobble(0.2) : 0;
            WriteNum($"input.L{p}-N.voltage", vinLN[p]);
            WriteNum($"input.bypass.L{p}-N.voltage", vinLL > 0 ? vinLL / sqrt3 + Wobble(0.5) : 0);
            WriteNum($"output.L{p}-N.voltage", voutLN[p]);
            WriteNum($"input.L{p}.realpower", inReal / 3);
            WriteNum($"input.L{p}.current", vinLN[p] > 0 ? inReal / 3 / pf / vinLN[p] : 0);
            WriteNum($"output.L{p}.realpower", realpower / 3);
            WriteNum($"output.L{p}.power", apparent / 3);
            WriteNum($"output.L{p}.current", voutLN[p] > 0 ? apparent / 3 / voutLN[p] : 0);
            WriteNum($"output.L{p}.power.percent", load);
        }
        for (int p = 1; p <= 3; p++)
        {
            int q = p % 3 + 1;
            WriteNum($"input.L{p}-L{q}.voltage", (vinLN[p] + vinLN[q]) / 2 * sqrt3);
            WriteNum($"output.L{p}-L{q}.voltage", (voutLN[p] + voutLN[q]) / 2 * sqrt3);
        }
        WriteNum("input.N.current", inReal > 0 ? 0.6 + Wobble(0.05) : 0);
        WriteNum("output.N.current", apparent > 0 ? 0.5 + Wobble(0.05) : 0);
    }

    private string ThresholdStatus(string prefix, double value, bool present)
    {
        if (!present) return "critical-low";
        if (value <= Num(prefix + ".low.critical", double.MinValue)) return "critical-low";
        if (value <= Num(prefix + ".low.warning", double.MinValue)) return "warning-low";
        if (value >= Num(prefix + ".high.critical", double.MaxValue)) return "critical-high";
        if (value >= Num(prefix + ".high.warning", double.MaxValue)) return "warning-high";
        return "good";
    }

    // ---- Helpers ------------------------------------------------------------------------

    private double Wobble(double amount) => Jitter ? (_rng.NextDouble() * 2 - 1) * amount : 0;

    private double Num(string name, double fallback) =>
        _vars.TryGetValue(name, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d : fallback;

    /// <summary>Only variables the profile declares are ever written, so sparse devices stay sparse.</summary>
    private void Write(string name, string value)
    {
        if (_vars.ContainsKey(name)) _vars[name] = value;
    }

    private void WriteNum(string name, double value)
    {
        if (_vars.ContainsKey(name)) _vars[name] = Fmt(name, value);
    }

    private string Fmt(string name, double value) =>
        value.ToString("F" + _decimals.GetValueOrDefault(name, 1), CultureInfo.InvariantCulture);
}
