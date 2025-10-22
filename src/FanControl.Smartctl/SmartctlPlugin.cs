using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using FanControl.Plugins;

namespace FanControl.Smartctl
{
    /// <summary>
    /// Plugin that adds HDD/SSD temperature sensors via smartctl.
    /// </summary>
    public sealed class SmartctlPlugin : IPlugin2
    {
        private readonly IPluginLogger? _log;
        private readonly IPluginDialog? _dialog;
        private readonly List<SmartctlTempSensor> _sensors = new();
        private DateTime _lastPoll = DateTime.MinValue;

        // Configuration
        private string _smartctlPath = "smartctl";
        private TimeSpan _pollInterval = TimeSpan.FromSeconds(10);

        public SmartctlPlugin(IPluginLogger? logger = null, IPluginDialog? dialog = null)
        {
            _log = logger;
            _dialog = dialog;
        }

        public string Name => "Smartctl Disk Temperatures";

        public void Initialize()
        {
            try
            {
                var dllDir = Path.GetDirectoryName(typeof(SmartctlPlugin).Assembly.Location)!;
                var cfg = Path.Combine(dllDir, "FanControl.Smartctl.json");
                if (File.Exists(cfg))
                {
                    var json = JsonSerializer.Deserialize<PluginConfig>(File.ReadAllText(cfg));
                    if (json != null)
                    {
                        if (!string.IsNullOrWhiteSpace(json.SmartctlPath)) _smartctlPath = json.SmartctlPath!;
                        if (json.PollSeconds is > 0) _pollInterval = TimeSpan.FromSeconds(json.PollSeconds.Value);
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Log($"[Smartctl] config load failed: {ex}");
            }
        }

        public void Load(IPluginSensorsContainer container)
        {
            var scan = RunSmartctl("--scan-open -j", TimeSpan.FromSeconds(4));
            if (scan.ExitCode != 0)
            {
                _log?.Log($"[Smartctl] smartctl scan failed: {scan.StdErr} {scan.StdOut}");
                TryShowDialogMessage(_dialog, "Smartctl scan failed. See log.");
                return;
            }

            SmartctlScanOpenResult? scanObj;
            try
            {
                scanObj = JsonSerializer.Deserialize<SmartctlScanOpenResult>(scan.StdOut);
            }
            catch (Exception ex)
            {
                _log?.Log($"[Smartctl] scan JSON parse failed: {ex}");
                return;
            }

            var devices = scanObj?.Devices;
            if (devices == null || devices.Count == 0)
            {
                _log?.Log("[Smartctl] no devices found by smartctl --scan-open");
                return;
            }

            foreach (var dev in devices)
            {
                if (dev.Type != null && dev.Type.Contains("nvme", StringComparison.OrdinalIgnoreCase))
                    continue;

                bool looksGood = dev.Type != null && (dev.Type.Contains("sat", StringComparison.OrdinalIgnoreCase)
                                                      || dev.Type.Contains("scsi", StringComparison.OrdinalIgnoreCase)
                                                      || dev.Type.Contains("ata", StringComparison.OrdinalIgnoreCase));
                if (!looksGood) continue;

                var deviceId = dev.Name ?? dev.Open_Device ?? dev.Info_Name ?? Guid.NewGuid().ToString("N");
                var openDevice = dev.Open_Device ?? dev.Info_Name ?? dev.Name ?? deviceId;
                var sensor = new SmartctlTempSensor(
                    device: deviceId,
                    openDevice: openDevice,
                    devTypeArg: dev.Type ?? "auto",
                    displayName: BuildNiceName(dev),
                    logger: _log,
                    smartctlPath: _smartctlPath
                );

                _sensors.Add(sensor);
                RegisterSensor(container, sensor);
            }

            _log?.Log($"[Smartctl] added sensors: {_sensors.Count}");
        }

        public void Update()
        {
            if ((DateTime.UtcNow - _lastPoll) < _pollInterval) return;
            _lastPoll = DateTime.UtcNow;

            foreach (var s in _sensors)
            {
                try { s.RefreshOnce(); }
                catch (Exception ex)
                {
                    _log?.Log($"[Smartctl] update failed for {s.Identifier}: {ex.Message}");
                }
            }
        }

        public void Close() { }

        private void RegisterSensor(IPluginSensorsContainer container, IPluginSensor sensor)
        {
            try
            {
                var containerType = container.GetType();

                var tempsProp = containerType.GetProperty("Temperatures");
                if (tempsProp?.GetValue(container) is IList list)
                {
                    list.Add(sensor);
                    return;
                }

                static bool TryInvoke(IPluginSensorsContainer target, string methodName, IPluginSensor sensor)
                {
                    var method = target.GetType().GetMethod(methodName, new[] { typeof(IPluginSensor) });
                    if (method == null) return false;
                    method.Invoke(target, new object?[] { sensor });
                    return true;
                }

                if (TryInvoke(container, "AddTemperatureSensor", sensor)) return;
                if (TryInvoke(container, "AddSensor", sensor)) return;
                if (TryInvoke(container, "RegisterSensor", sensor)) return;

                _log?.Log("[Smartctl] unable to register smartctl sensor: unsupported container API");
            }
            catch (Exception ex)
            {
                _log?.Log($"[Smartctl] failed to register smartctl sensor: {ex}");
            }
        }

        private static void TryShowDialogMessage(IPluginDialog? dialog, string message)
        {
            if (dialog is null) return;

            try
            {
                var type = dialog.GetType();

                if (TryInvoke(type, dialog, "ShowMessageBox", new object?[] { message })) return;
                if (TryInvoke(type, dialog, "ShowMessage", new object?[] { message })) return;
                if (TryInvoke(type, dialog, "ShowErrorMessage", new object?[] { message })) return;
                if (TryInvoke(type, dialog, "ShowError", new object?[] { message })) return;

                if (TryInvoke(type, dialog, "ShowMessage", new object?[] { "Smartctl", message })) return;
                if (TryInvoke(type, dialog, "ShowMessage", new object?[] { message, "Smartctl" })) return;
                if (TryInvoke(type, dialog, "ShowMessageBox", new object?[] { "Smartctl", message })) return;
                if (TryInvoke(type, dialog, "ShowMessageBox", new object?[] { message, "Smartctl" })) return;
                if (TryInvoke(type, dialog, "ShowMessageDialog", new object?[] { "Smartctl", message })) return;
                if (TryInvoke(type, dialog, "ShowMessageDialog", new object?[] { message, "Smartctl" })) return;
            }
            catch (Exception)
            {
                // fall back to silent failure; message already logged by caller
            }

            static bool TryInvoke(Type type, object target, string methodName, object?[] args)
            {
                try
                {
                    var argTypes = Array.ConvertAll(args, a => a?.GetType() ?? typeof(object));
                    var method = type.GetMethod(methodName, argTypes);
                    if (method == null)
                    {
                        foreach (var m in type.GetMethods())
                        {
                            if (!string.Equals(m.Name, methodName, StringComparison.Ordinal)) continue;
                            var parameters = m.GetParameters();
                            if (parameters.Length != args.Length) continue;
                            method = m;
                            break;
                        }
                    }

                    if (method == null) return false;
                    method.Invoke(target, args);
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        private static string BuildNiceName(SmartctlScanOpenResult.Device d)
        {
            var src = d.Open_Device ?? d.Info_Name ?? d.Name ?? "Disk";
            var m = Regex.Match(src, @"PhysicalDrive(\d+)", RegexOptions.IgnoreCase);
            var pd = m.Success ? $"PD{m.Groups[1].Value}" : src;
            var type = d.Type ?? "auto";
            return $"Disk {pd} ({type})";
        }

        private (int ExitCode, string StdOut, string StdErr) RunSmartctl(string args, TimeSpan timeout)
        {
            var psi = new ProcessStartInfo
            {
                FileName = _smartctlPath,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            using var p = Process.Start(psi);
            if (p == null) return (-1, "", "failed to start smartctl");

            using var cts = new CancellationTokenSource(timeout);
            string so = "", se = "";
            try
            {
                so = p.StandardOutput.ReadToEndAsync(cts.Token).GetAwaiter().GetResult();
                se = p.StandardError.ReadToEndAsync(cts.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                try { if (!p.HasExited) p.Kill(true); } catch { }
                return (-2, so, "smartctl timeout");
            }

            p.WaitForExit();
            return (p.ExitCode, so, se);
        }

        private sealed class PluginConfig
        {
            [JsonPropertyName("smartctlPath")] public string? SmartctlPath { get; set; }
            [JsonPropertyName("pollSeconds")] public int? PollSeconds { get; set; }
        }

        private sealed class SmartctlScanOpenResult
        {
            [JsonPropertyName("devices")] public List<Device> Devices { get; set; } = new();

            public sealed class Device
            {
                [JsonPropertyName("name")] public string? Name { get; set; }
                [JsonPropertyName("info_name")] public string? Info_Name { get; set; }
                [JsonPropertyName("open_device")] public string? Open_Device { get; set; }
                [JsonPropertyName("type")] public string? Type { get; set; }
            }
        }

        private sealed class SmartctlTempSensor : IPluginSensor
        {
            private readonly IPluginLogger? _log;
            private readonly string _smartctlPath;
            private readonly string _dev;
            private readonly string _type;

            public string Name { get; }
            public string Identifier { get; }
            public string Id => Identifier;
            public float? Value { get; private set; }

            public SmartctlTempSensor(string device, string openDevice, string devTypeArg, string displayName, IPluginLogger? logger, string smartctlPath)
            {
                _dev = openDevice;
                _type = devTypeArg;
                _log = logger;
                _smartctlPath = smartctlPath;
                Name = displayName;
                Identifier = $"smartctl://{device}|{devTypeArg}";
            }

            public void Update() { /* no-op because we refresh with RefreshOnce() */ }

            public void RefreshOnce()
            {
                var args = $"-A -j -n standby,0 -d {_type} \"{_dev}\"";
                var (ec, so, se) = RunSmartctl(args, TimeSpan.FromSeconds(4));

                if (ec == 2 || string.IsNullOrWhiteSpace(so))
                {
                    _log?.Log($"[Smartctl] {_dev} read failed: {se}");
                    return;
                }

                using var doc = JsonDocument.Parse(so);
                float? temp = TryParseAtaAttribute(doc, 194) ?? TryParseAtaAttribute(doc, 190);

                if (temp is null)
                {
                    var argsSct = $"-l scttempsts -j -n standby,0 -d {_type} \"{_dev}\"";
                    var (ec2, so2, se2) = RunSmartctl(argsSct, TimeSpan.FromSeconds(3));
                    if (ec2 == 0 && !string.IsNullOrWhiteSpace(so2))
                    {
                        using var d2 = JsonDocument.Parse(so2);
                        if (d2.RootElement.TryGetProperty("temperature", out var tnode) &&
                            tnode.TryGetProperty("current", out var cur) &&
                            cur.TryGetInt32(out var c1))
                        {
                            temp = c1;
                        }
                    }
                    else
                    {
                        _log?.Log($"[Smartctl] {_dev} scttempsts failed: {se2}");
                    }
                }

                if (temp is not null && temp > -50 && temp < 150)
                {
                    Value = temp.Value;
                }
            }

            private static float? TryParseAtaAttribute(JsonDocument doc, int id)
            {
                try
                {
                    if (!doc.RootElement.TryGetProperty("ata_smart_attributes", out var ata)) return null;
                    if (!ata.TryGetProperty("table", out var table)) return null;
                    foreach (var item in table.EnumerateArray())
                    {
                        if (!item.TryGetProperty("id", out var pid)) continue;
                        if (pid.GetInt32() != id) continue;
                        if (item.TryGetProperty("raw", out var raw))
                        {
                            if (raw.TryGetProperty("value", out var v) && v.TryGetInt32(out var iv))
                                return iv;
                            if (raw.TryGetProperty("string", out var s))
                            {
                                var m = Regex.Match(s.GetString() ?? "", @"(-?\d+)");
                                if (m.Success && int.TryParse(m.Groups[1].Value, out var iv2)) return iv2;
                            }
                        }
                    }
                }
                catch { /* ignore */ }
                return null;
            }

            private (int ExitCode, string StdOut, string StdErr) RunSmartctl(string args, TimeSpan timeout)
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _smartctlPath,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };
                using var p = Process.Start(psi);
                if (p == null) return (-1, "", "failed to start smartctl");
                using var cts = new CancellationTokenSource(timeout);
                string so = "", se = "";
                try
                {
                    so = p.StandardOutput.ReadToEndAsync(cts.Token).GetAwaiter().GetResult();
                    se = p.StandardError.ReadToEndAsync(cts.Token).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    try { if (!p.HasExited) p.Kill(true); } catch { }
                    return (-2, so, "smartctl timeout");
                }
                p.WaitForExit();
                return (p.ExitCode, so, se);
            }
        }
    }
}
