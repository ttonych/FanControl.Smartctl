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
            _sensors.Clear();
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

            var added = 0;
            foreach (var dev in devices)
            {
                if (dev.Type != null && dev.Type.Contains("nvme", StringComparison.OrdinalIgnoreCase))
                    continue;

                bool looksGood = dev.Type != null && (dev.Type.Contains("sat", StringComparison.OrdinalIgnoreCase)
                                                      || dev.Type.Contains("scsi", StringComparison.OrdinalIgnoreCase)
                                                      || dev.Type.Contains("ata", StringComparison.OrdinalIgnoreCase));
                if (!looksGood) continue;

                var deviceId = dev.Name ?? dev.Open_Device ?? dev.Info_Name ?? Guid.NewGuid().ToString("N");
                var devicePath = SelectDevicePath(dev.Open_Device, dev.Name, dev.Info_Name);
                if (string.IsNullOrWhiteSpace(devicePath))
                {
                    var fallbackPath = NormalizeDevicePath(deviceId);
                    if (!string.IsNullOrWhiteSpace(fallbackPath)) devicePath = fallbackPath;
                }

                if (string.IsNullOrWhiteSpace(devicePath) || !LooksLikeDeviceToken(devicePath))
                {
                    _log?.Log($"[Smartctl] skipping {deviceId}: unable to resolve device path");
                    continue;
                }
                var sensor = new SmartctlTempSensor(
                    device: deviceId,
                    devicePath: devicePath,
                    devTypeArg: dev.Type ?? "auto",
                    displayName: BuildNiceName(dev),
                    logger: _log,
                    smartctlPath: _smartctlPath
                );

                if (RegisterSensor(container, sensor))
                {
                    _sensors.Add(sensor);
                    added++;
                }
            }

            _log?.Log($"[Smartctl] added sensors: {added}");
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

        private bool RegisterSensor(IPluginSensorsContainer container, IPluginSensor sensor)
        {
            try
            {
                var containerType = container.GetType();

                foreach (var propertyName in new[] { "TempSensors", "TemperatureSensors", "Temperatures" })
                {
                    var property = containerType.GetProperty(propertyName);
                    if (property == null) continue;
                    if (TryAddToCollection(property.GetValue(container), sensor)) return true;
                }

                if (TryInvoke(container, "AddTempSensor", sensor)) return true;
                if (TryInvoke(container, "AddTemperatureSensor", sensor)) return true;
                if (TryInvoke(container, "AddSensor", sensor)) return true;
                if (TryInvoke(container, "RegisterSensor", sensor)) return true;

                _log?.Log("[Smartctl] unable to register smartctl sensor: unsupported container API");
            }
            catch (Exception ex)
            {
                _log?.Log($"[Smartctl] failed to register smartctl sensor: {ex}");
            }
            return false;

            static bool TryInvoke(IPluginSensorsContainer target, string methodName, IPluginSensor sensor)
            {
                var methods = target.GetType().GetMethods();
                foreach (var method in methods)
                {
                    if (!string.Equals(method.Name, methodName, StringComparison.Ordinal)) continue;
                    var parameters = method.GetParameters();
                    if (parameters.Length != 1) continue;
                    var parameterType = parameters[0].ParameterType;
                    if (!parameterType.IsInstanceOfType(sensor) && !parameterType.IsAssignableFrom(sensor.GetType()) && parameterType != typeof(object))
                        continue;

                    method.Invoke(target, new object?[] { sensor });
                    return true;
                }

                return false;
            }

            static bool TryAddToCollection(object? target, IPluginSensor sensor)
            {
                if (target is null) return false;

                if (target is IList list)
                {
                    list.Add(sensor);
                    return true;
                }

                var targetType = target.GetType();
                foreach (var method in targetType.GetMethods())
                {
                    if (!string.Equals(method.Name, "Add", StringComparison.Ordinal)) continue;
                    var parameters = method.GetParameters();

                    if (parameters.Length == 1)
                    {
                        var parameterType = parameters[0].ParameterType;
                        if (!parameterType.IsInstanceOfType(sensor) && !parameterType.IsAssignableFrom(sensor.GetType()) && parameterType != typeof(object))
                            continue;

                        method.Invoke(target, new object?[] { sensor });
                        return true;
                    }

                    if (parameters.Length == 2 && parameters[0].ParameterType == typeof(string))
                    {
                        var valueType = parameters[1].ParameterType;
                        if (!valueType.IsInstanceOfType(sensor) && !valueType.IsAssignableFrom(sensor.GetType()) && valueType != typeof(object))
                            continue;

                        var key = sensor.Id ?? sensor.Name ?? Guid.NewGuid().ToString("N");
                        method.Invoke(target, new object?[] { key, sensor });
                        return true;
                    }
                }

                return false;
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
            var src = SelectDevicePath(d.Name, d.Open_Device, d.Info_Name);
            if (string.IsNullOrWhiteSpace(src)) src = "Disk";
            var m = Regex.Match(src, @"PhysicalDrive(\d+)", RegexOptions.IgnoreCase);
            var pd = m.Success ? $"PD{m.Groups[1].Value}" : src;
            var type = d.Type ?? "auto";
            return $"Disk {pd} ({type})";
        }

        private static string SelectDevicePath(params string?[] candidates)
        {
            foreach (var candidate in candidates)
            {
                var normalized = NormalizeDevicePath(candidate);
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    return normalized;
                }
            }

            return string.Empty;
        }

        private static string NormalizeDevicePath(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;

            var trimmed = value.Trim().Trim('"');

            var commentIndex = trimmed.IndexOf('#');
            if (commentIndex >= 0)
            {
                trimmed = trimmed.Substring(0, commentIndex).TrimEnd();
            }

            trimmed = Regex.Replace(trimmed, @"\s*\[[^\]]+\]\s*$", string.Empty, RegexOptions.CultureInvariant);

            var tokens = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length > 1)
            {
                foreach (var token in tokens)
                {
                    if (token.StartsWith("#", StringComparison.Ordinal)) break;
                    if (LooksLikeDeviceToken(token))
                    {
                        trimmed = token;
                        break;
                    }
                }
            }

            trimmed = trimmed.Trim();

            return LooksLikeDeviceToken(trimmed) ? trimmed : string.Empty;
        }

        private static bool LooksLikeDeviceToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return false;

            if (token.IndexOf('/') >= 0 || token.IndexOf('\\') >= 0) return true;
            if (token.IndexOf('@') >= 0) return true;
            if (token.Contains("PhysicalDrive", StringComparison.OrdinalIgnoreCase)) return true;
            if (token.StartsWith("PD", StringComparison.OrdinalIgnoreCase) && token.Length > 2 && char.IsDigit(token[2])) return true;

            return false;
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
            private readonly string _devicePath;
            private readonly string _type;

            public string Name { get; }
            public string Identifier { get; }
            public string Id => Identifier;
            public float? Value { get; private set; }

            public SmartctlTempSensor(string device, string devicePath, string devTypeArg, string displayName, IPluginLogger? logger, string smartctlPath)
            {
                var path = string.IsNullOrWhiteSpace(devicePath) ? device : devicePath;
                var normalizedPath = NormalizeDevicePath(path);
                _devicePath = string.IsNullOrWhiteSpace(normalizedPath) ? path : normalizedPath;
                _type = devTypeArg;
                _log = logger;
                _smartctlPath = smartctlPath;
                Name = displayName;
                Identifier = $"smartctl://{device}|{devTypeArg}";
            }

            public void Update() { /* no-op because we refresh with RefreshOnce() */ }

            public void RefreshOnce()
            {
                var args = $"-A -j -n standby,0 -d {_type} \"{_devicePath}\"";
                var (ec, so, se) = RunSmartctl(args, TimeSpan.FromSeconds(4));

                if (ec == 2 || string.IsNullOrWhiteSpace(so))
                {
                    _log?.Log($"[Smartctl] {Name} read failed (device {_devicePath}): {se}");
                    return;
                }

                using var doc = JsonDocument.Parse(so);
                float? temp = TryParseAtaAttribute(doc, 194) ?? TryParseAtaAttribute(doc, 190);

                if (temp is null)
                {
                    var argsSct = $"-l scttempsts -j -n standby,0 -d {_type} \"{_devicePath}\"";
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
                        _log?.Log($"[Smartctl] {Name} scttempsts failed (device {_devicePath}): {se2}");
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
