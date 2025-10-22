using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
#if WINDOWS
using System.Management;
using System.Runtime.InteropServices;
#endif
using FanControl.Plugins;

namespace FanControl.Smartctl
{
    /// <summary>
    /// Plugin that adds HDD/SSD temperature sensors via smartctl.
    /// </summary>
    public sealed class SmartctlPlugin : IPlugin3
    {
        private readonly IPluginLogger? _log;
        private readonly IPluginDialog? _dialog;
        private readonly List<SmartctlTempSensor> _sensors = new();
        private DateTime _lastPoll = DateTime.MinValue;
        private SmartctlPluginOptions _options = SmartctlPluginOptions.CreateDefault();
        private string? _configPath;

        public SmartctlPlugin(IPluginLogger? logger = null, IPluginDialog? dialog = null)
        {
            _log = logger;
            _dialog = dialog;
        }

        public string Name => "Smartctl Disk Temperatures";

        public event Action? RefreshRequested;

        public void Initialize()
        {
            _sensors.Clear();
            _lastPoll = DateTime.MinValue;

            try
            {
                var dllDir = Path.GetDirectoryName(typeof(SmartctlPlugin).Assembly.Location)!;
                _configPath = Path.Combine(dllDir, "FanControl.Smartctl.json");
                _options = LoadOptions(_configPath);
            }
            catch (Exception ex)
            {
                _log?.Log($"[Smartctl] config load failed: {ex.Message}");
                _options = SmartctlPluginOptions.CreateDefault();
            }
        }

        public void Load(IPluginSensorsContainer container)
        {
            _sensors.Clear();
            RegisterSettingsControl(container);
            MaybeShowSettingsDialog();
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

#if WINDOWS
            var windowsDisks = WindowsDiskEnumerator.TryCollect(_log);
#endif
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
#if WINDOWS
                var metadata = CreateDeviceMetadata(dev, deviceId, devicePath, windowsDisks);
#else
                var metadata = CreateDeviceMetadata(dev, deviceId, devicePath);
#endif

                if (ShouldExcludeDevice(metadata))
                {
                    _log?.Log($"[Smartctl] skipping {metadata.DeviceToken}: excluded by config");
                    continue;
                }

                var sensor = new SmartctlTempSensor(
                    device: metadata.DeviceToken,
                    devicePath: metadata.DevicePath,
                    devTypeArg: metadata.TypeArgument,
                    displayName: BuildDisplayName(metadata),
                    logger: _log,
                    smartctlPath: _options.SmartctlPath
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
            if ((DateTime.UtcNow - _lastPoll) < _options.PollInterval) return;
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

        private SmartctlPluginOptions LoadOptions(string configPath)
        {
            if (!File.Exists(configPath))
            {
                return SmartctlPluginOptions.CreateDefault();
            }

            try
            {
                var json = File.ReadAllText(configPath);
                var config = JsonSerializer.Deserialize<PluginConfig>(json);
                if (config != null)
                {
                    var options = SmartctlPluginOptions.CreateDefault();

                    if (!string.IsNullOrWhiteSpace(config.SmartctlPath))
                    {
                        options.SmartctlPath = config.SmartctlPath!;
                    }

                    if (config.PollSeconds is > 0)
                    {
                        options.PollIntervalSeconds = config.PollSeconds.Value;
                    }

                    if (config.DisplayName != null)
                    {
                        if (!string.IsNullOrWhiteSpace(config.DisplayName.Mode))
                        {
                            if (Enum.TryParse(config.DisplayName.Mode, true, out DisplayNameMode parsed))
                            {
                                options.DisplayNameMode = parsed;
                            }
                            else
                            {
                                _log?.Log($"[Smartctl] unknown displayName.mode '{config.DisplayName.Mode}' - using {options.DisplayNameMode}");
                            }
                        }

                        options.DisplayNameFormat = config.DisplayName.Format;
                        options.DisplayNamePrefix = config.DisplayName.Prefix;
                        options.DisplayNameSuffix = config.DisplayName.Suffix;
                    }

                    if (config.ExcludeDevices != null)
                    {
                        options.ExcludedTokens = config.ExcludeDevices
                            .Where(token => !string.IsNullOrWhiteSpace(token))
                            .Select(token => token!.Trim())
                            .ToList();
                    }

                    options.HasShownSettingsHint = config.SettingsHintShown ?? false;

                    options.Normalize();
                    return options;
                }
            }
            catch (Exception ex)
            {
                _log?.Log($"[Smartctl] failed to parse config: {ex.Message}");
            }

            return SmartctlPluginOptions.CreateDefault();
        }

        private void ApplyOptions(SmartctlPluginOptions options, bool persist, bool requestRefresh)
        {
            _options = options.Clone();
            _options.Normalize();
            _lastPoll = DateTime.MinValue;

            if (persist)
            {
                SaveOptions();
            }

            if (requestRefresh)
            {
                try
                {
                    RefreshRequested?.Invoke();
                }
                catch (Exception ex)
                {
                    _log?.Log($"[Smartctl] failed to request plugin refresh: {ex.Message}");
                }
            }
        }

        private void SaveOptions()
        {
            if (string.IsNullOrWhiteSpace(_configPath)) return;

            try
            {
                var json = JsonSerializer.Serialize(PluginConfig.FromOptions(_options), new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                File.WriteAllText(_configPath, json);
            }
            catch (Exception ex)
            {
                _log?.Log($"[Smartctl] failed to save config: {ex.Message}");
            }
        }

        private SettingsDialogResult OpenSettingsDialog()
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null)
            {
                _log?.Log("[Smartctl] unable to open settings UI: no dispatcher available.");
                return SettingsDialogResult.NotShown;
            }

            SmartctlPluginOptions? updatedOptions = null;
            var windowShown = false;

            void ShowDialog()
            {
                var window = new SmartctlSettingsWindow(_options);
                if (Application.Current?.MainWindow != null && window.Owner == null)
                {
                    window.Owner = Application.Current.MainWindow;
                }

                windowShown = true;

                if (window.ShowDialog() == true)
                {
                    updatedOptions = window.ResultOptions;
                }
            }

            if (dispatcher.CheckAccess())
            {
                ShowDialog();
            }
            else
            {
                dispatcher.Invoke(ShowDialog);
            }

            if (!windowShown)
            {
                return SettingsDialogResult.NotShown;
            }

            if (updatedOptions is null)
            {
                if (!_options.HasShownSettingsHint)
                {
                    _options.HasShownSettingsHint = true;
                    SaveOptions();
                }

                return SettingsDialogResult.ShownNoSave;
            }

            updatedOptions.HasShownSettingsHint = true;
            ApplyOptions(updatedOptions, persist: true, requestRefresh: true);
            _log?.Log("[Smartctl] settings updated via GUI.");
            return SettingsDialogResult.Applied;
        }

        private enum SettingsDialogResult
        {
            NotShown,
            ShownNoSave,
            Applied
        }

        private void RegisterSettingsControl(IPluginSensorsContainer container)
        {
            try
            {
                var control = new SmartctlSettingsControlSensor(OpenSettingsDialog);
                if (!RegisterSensor(container, control))
                {
                    _log?.Log("[Smartctl] unable to register settings control sensor: unsupported container API");
                }
            }
            catch (Exception ex)
            {
                _log?.Log($"[Smartctl] failed to register settings control sensor: {ex.Message}");
            }
        }

        private void MaybeShowSettingsDialog()
        {
            if (_options.HasShownSettingsHint)
            {
                return;
            }

            var result = OpenSettingsDialog();
            if (result == SettingsDialogResult.NotShown)
            {
                if (!_options.HasShownSettingsHint)
                {
                    _options.HasShownSettingsHint = true;
                    SaveOptions();
                }

                TryShowDialogMessage(_dialog,
                    "Add the \"Smartctl Settings\" control in FanControl and move it above 50% (for example, set it to 100%) to reopen the configuration window.");
            }
        }

#if WINDOWS
        private DeviceMetadata CreateDeviceMetadata(SmartctlScanOpenResult.Device device, string deviceToken, string devicePath, IReadOnlyList<WindowsDiskInfo> windowsDisks)
#else
        private DeviceMetadata CreateDeviceMetadata(SmartctlScanOpenResult.Device device, string deviceToken, string devicePath)
#endif
        {
            var metadata = new DeviceMetadata
            {
                DeviceToken = deviceToken,
                DevicePath = devicePath,
                TypeArgument = string.IsNullOrWhiteSpace(device.Type) ? "auto" : device.Type!.Trim(),
                Name = device.Name,
                InfoName = device.Info_Name,
                OpenDevice = device.Open_Device
            };

            PopulateFromSmartctlInfo(metadata);

#if WINDOWS
            if (windowsDisks.Count > 0)
            {
                TryAttachWindowsInfo(metadata, windowsDisks);
            }
#endif

            return metadata;
        }

        private void PopulateFromSmartctlInfo(DeviceMetadata metadata)
        {
            try
            {
                var args = $"-i -j -d {metadata.TypeArgument} \"{metadata.DevicePath}\"";
                var (exitCode, stdOut, stdErr) = RunSmartctl(args, TimeSpan.FromSeconds(4));

                if (!string.IsNullOrWhiteSpace(stdOut))
                {
                    using var doc = JsonDocument.Parse(stdOut);
                    var root = doc.RootElement;
                    metadata.Model = TryGetInfoString(root, "model_name", "device_model", "model_family", "product", "nvme_model_number", "scsi_product") ?? metadata.Model;
                    metadata.SerialNumber = TryGetInfoString(root, "serial_number", "ata_serial_number", "nvme_serial_number", "scsi_serial_number") ?? metadata.SerialNumber;
                    metadata.Firmware = TryGetInfoString(root, "firmware_version", "firmware_revision", "nvme_firmware_version", "scsi_firmware_version") ?? metadata.Firmware;
                }
                else if (exitCode != 0)
                {
                    _log?.Log($"[Smartctl] info query failed for {metadata.DevicePath}: {stdErr}");
                }
            }
            catch (Exception ex)
            {
                _log?.Log($"[Smartctl] failed to inspect {metadata.DevicePath}: {ex.Message}");
            }
        }

        private static string? TryGetInfoString(JsonElement root, params string[] propertyNames)
        {
            foreach (var property in propertyNames)
            {
                if (!root.TryGetProperty(property, out var value)) continue;
                if (value.ValueKind != JsonValueKind.String) continue;
                var str = value.GetString();
                if (!string.IsNullOrWhiteSpace(str)) return str;
            }

            return null;
        }

#if WINDOWS
        private static void TryAttachWindowsInfo(DeviceMetadata metadata, IReadOnlyList<WindowsDiskInfo> windowsDisks)
        {
            WindowsDiskInfo? match = null;

            if (!string.IsNullOrWhiteSpace(metadata.SerialNumber))
            {
                var serial = NormalizeSerial(metadata.SerialNumber);
                match = windowsDisks.FirstOrDefault(d => !string.IsNullOrWhiteSpace(d.Serial) && NormalizeSerial(d.Serial!) == serial);
            }

            if (match == null)
            {
                var candidate = NormalizeWindowsDeviceId(metadata.DevicePath);
                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    match = windowsDisks.FirstOrDefault(d => string.Equals(NormalizeWindowsDeviceId(d.DeviceId), candidate, StringComparison.OrdinalIgnoreCase));
                }
            }

            if (match == null && !string.IsNullOrWhiteSpace(metadata.OpenDevice))
            {
                var candidate = NormalizeWindowsDeviceId(metadata.OpenDevice);
                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    match = windowsDisks.FirstOrDefault(d => string.Equals(NormalizeWindowsDeviceId(d.DeviceId), candidate, StringComparison.OrdinalIgnoreCase));
                }
            }

            if (match == null && !string.IsNullOrWhiteSpace(metadata.InfoName))
            {
                var candidate = NormalizeWindowsDeviceId(metadata.InfoName);
                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    match = windowsDisks.FirstOrDefault(d => string.Equals(NormalizeWindowsDeviceId(d.DeviceId), candidate, StringComparison.OrdinalIgnoreCase));
                }
            }

            if (match == null) return;

            metadata.WindowsDeviceId = match.DeviceId;
            metadata.WindowsFriendlyName = match.FriendlyName;

            if (string.IsNullOrWhiteSpace(metadata.Model) && !string.IsNullOrWhiteSpace(match.Model))
            {
                metadata.Model = match.Model;
            }

            if (string.IsNullOrWhiteSpace(metadata.SerialNumber) && !string.IsNullOrWhiteSpace(match.Serial))
            {
                metadata.SerialNumber = match.Serial;
            }

            foreach (var letter in match.DriveLetters)
            {
                if (string.IsNullOrWhiteSpace(letter)) continue;
                if (!metadata.DriveLetters.Any(l => string.Equals(l, letter, StringComparison.OrdinalIgnoreCase)))
                {
                    metadata.DriveLetters.Add(letter);
                }
            }

            if (metadata.DriveLetters.Count > 1)
            {
                metadata.DriveLetters.Sort(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static string NormalizeWindowsDeviceId(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var trimmed = value.Trim();
            trimmed = trimmed.Replace('/', '\\');

            if (trimmed.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed;
            }

            var physical = Regex.Match(trimmed, @"PhysicalDrive(\d+)", RegexOptions.IgnoreCase);
            if (physical.Success)
            {
                return @$"\\.\PHYSICALDRIVE{physical.Groups[1].Value}";
            }

            var pd = Regex.Match(trimmed, @"PD(\d+)", RegexOptions.IgnoreCase);
            if (pd.Success)
            {
                return @$"\\.\PHYSICALDRIVE{pd.Groups[1].Value}";
            }

            return trimmed;
        }

        private static string NormalizeSerial(string value)
        {
            var cleaned = Regex.Replace(value.Trim(), @"\s+", string.Empty);
            return cleaned.ToUpperInvariant();
        }
#endif

        private bool ShouldExcludeDevice(DeviceMetadata metadata)
        {
            var tokens = _options.ExcludedTokens;
            if (tokens.Count == 0) return false;

            foreach (var token in tokens)
            {
                if (Matches(metadata, token)) return true;
            }

            return false;
        }

        private static bool Matches(DeviceMetadata metadata, string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return false;

            bool Match(string? value) => !string.IsNullOrWhiteSpace(value) && value.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;

            if (Match(metadata.DeviceToken)) return true;
            if (Match(metadata.DevicePath)) return true;
            if (Match(metadata.SerialNumber)) return true;
            if (Match(metadata.Model)) return true;
            if (Match(metadata.Name)) return true;
            if (Match(metadata.InfoName)) return true;
            if (Match(metadata.OpenDevice)) return true;
            if (Match(metadata.WindowsDeviceId)) return true;
            if (Match(metadata.WindowsFriendlyName)) return true;
            if (metadata.DriveLetters.Any(Match)) return true;

            return false;
        }

        private string BuildDisplayName(DeviceMetadata metadata)
        {
            string? result = null;

            var format = _options.DisplayNameFormat;
            if (!string.IsNullOrWhiteSpace(format))
            {
                result = ApplyDisplayNameFormat(format!, metadata);
            }

            if (string.IsNullOrWhiteSpace(result))
            {
                var mode = _options.DisplayNameMode;
                result = mode switch
                {
                    DisplayNameMode.Device => ChooseDeviceToken(metadata),
                    DisplayNameMode.DeviceAndType => $"{ChooseDeviceToken(metadata)} ({metadata.TypeArgument})",
                    DisplayNameMode.Model => metadata.Model ?? metadata.WindowsFriendlyName ?? ChooseDeviceToken(metadata),
                    DisplayNameMode.Serial => metadata.SerialNumber ?? ChooseDeviceToken(metadata),
                    DisplayNameMode.ModelAndSerial => CombineNonEmpty(metadata.Model ?? metadata.WindowsFriendlyName, metadata.SerialNumber)
                                                        ?? CombineNonEmpty(metadata.Model ?? metadata.WindowsFriendlyName, ChooseDeviceToken(metadata)),
                    DisplayNameMode.DriveLetters => metadata.DriveLetters.Count > 0 ? $"Disk {string.Join(", ", metadata.DriveLetters)}" : null,
                    DisplayNameMode.ModelAndDriveLetters => BuildModelWithLetters(metadata),
                    _ => null
                };
            }

            if (string.IsNullOrWhiteSpace(result))
            {
                result = BuildDefaultDisplayName(metadata);
            }

            var prefix = _options.DisplayNamePrefix;
            if (!string.IsNullOrWhiteSpace(prefix))
            {
                result = prefix + result;
            }

            var suffix = _options.DisplayNameSuffix;
            if (!string.IsNullOrWhiteSpace(suffix))
            {
                result += suffix;
            }

            return result;
        }

        private static string ApplyDisplayNameFormat(string format, DeviceMetadata metadata)
        {
            var replacements = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["device"] = metadata.DeviceToken,
                ["devicePath"] = metadata.DevicePath,
                ["type"] = metadata.TypeArgument,
                ["model"] = metadata.Model ?? metadata.WindowsFriendlyName,
                ["serial"] = metadata.SerialNumber,
                ["letters"] = metadata.DriveLetters.Count > 0 ? string.Join(", ", metadata.DriveLetters) : null,
                ["name"] = metadata.Name,
                ["info"] = metadata.InfoName,
                ["openDevice"] = metadata.OpenDevice,
                ["windowsDeviceId"] = metadata.WindowsDeviceId,
                ["friendly"] = metadata.WindowsFriendlyName,
                ["firmware"] = metadata.Firmware
            };

            var sb = new StringBuilder();
            for (var i = 0; i < format.Length;)
            {
                var open = format.IndexOf('{', i);
                if (open < 0)
                {
                    sb.Append(format, i, format.Length - i);
                    break;
                }

                sb.Append(format, i, open - i);
                var close = format.IndexOf('}', open + 1);
                if (close < 0)
                {
                    sb.Append(format, open, format.Length - open);
                    break;
                }

                var token = format.Substring(open + 1, close - open - 1).Trim();
                if (token.Length > 0 && replacements.TryGetValue(token, out var value) && !string.IsNullOrWhiteSpace(value))
                {
                    sb.Append(value);
                }

                i = close + 1;
            }

            return sb.ToString().Trim();
        }

        private static string? BuildModelWithLetters(DeviceMetadata metadata)
        {
            if (metadata.DriveLetters.Count > 0 && !string.IsNullOrWhiteSpace(metadata.Model ?? metadata.WindowsFriendlyName))
            {
                var label = metadata.Model ?? metadata.WindowsFriendlyName;
                return $"{label} [{string.Join(", ", metadata.DriveLetters)}]";
            }

            if (metadata.DriveLetters.Count > 0)
            {
                return $"Disk {string.Join(", ", metadata.DriveLetters)}";
            }

            return metadata.Model ?? metadata.WindowsFriendlyName;
        }

        private static string BuildDefaultDisplayName(DeviceMetadata metadata)
        {
            var token = ChooseDeviceToken(metadata);
            var m = Regex.Match(token, @"PhysicalDrive(\d+)", RegexOptions.IgnoreCase);
            var pd = m.Success ? $"PD{m.Groups[1].Value}" : token;
            var type = string.IsNullOrWhiteSpace(metadata.TypeArgument) ? "auto" : metadata.TypeArgument;
            return $"Disk {pd} ({type})";
        }

        private static string ChooseDeviceToken(DeviceMetadata metadata)
        {
            if (!string.IsNullOrWhiteSpace(metadata.DevicePath)) return metadata.DevicePath;
            if (!string.IsNullOrWhiteSpace(metadata.DeviceToken)) return metadata.DeviceToken;
            if (!string.IsNullOrWhiteSpace(metadata.OpenDevice)) return metadata.OpenDevice!;
            if (!string.IsNullOrWhiteSpace(metadata.InfoName)) return metadata.InfoName!;
            if (!string.IsNullOrWhiteSpace(metadata.Name)) return metadata.Name!;
            return "Disk";
        }

        private static string? CombineNonEmpty(params string?[] values)
        {
            var parts = values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim()).ToArray();
            return parts.Length == 0 ? null : string.Join(" - ", parts);
        }


        private bool RegisterSensor(IPluginSensorsContainer container, IPluginSensor sensor)
        {
            try
            {
                var containerType = container.GetType();
                var isControl = sensor is IPluginControlSensor;

                var propertyCandidates = isControl
                    ? new[] { "ControlSensors", "Controls" }
                    : new[] { "TempSensors", "TemperatureSensors", "Temperatures" };

                foreach (var propertyName in propertyCandidates)
                {
                    var property = containerType.GetProperty(propertyName);
                    if (property == null) continue;
                    if (TryAddToCollection(property.GetValue(container), sensor)) return true;
                }

                var methodCandidates = isControl
                    ? new[] { "AddControlSensor", "AddControl", "AddSensor", "RegisterSensor" }
                    : new[] { "AddTempSensor", "AddTemperatureSensor", "AddSensor", "RegisterSensor" };

                foreach (var methodName in methodCandidates)
                {
                    if (TryInvoke(container, methodName, sensor)) return true;
                }

                _log?.Log($"[Smartctl] unable to register {sensor.GetType().Name}: unsupported container API");
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

        private sealed class SmartctlSettingsControlSensor : IPluginControlSensor, IPluginSensor
        {
            private readonly Func<SettingsDialogResult> _openSettings;
            private bool _isOpening;
            private float? _value = 0f;

            public SmartctlSettingsControlSensor(Func<SettingsDialogResult> openSettings)
            {
                _openSettings = openSettings;
            }

            public string Name => "Smartctl Settings";
            public string Identifier => "smartctl://settings";
            public string Id => Identifier;
            public float? Value => _value;

            public void Update()
            {
                // no polling required
            }

            public void Set(float val)
            {
                if (_isOpening) return;
                if (val < 50)
                {
                    Reset();
                    return;
                }

                _isOpening = true;
                try
                {
                    _openSettings();
                }
                finally
                {
                    _isOpening = false;
                    Reset();
                }
            }

            public void Reset()
            {
                _value = 0;
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
                FileName = _options.SmartctlPath,
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

        private sealed class DeviceMetadata
        {
            public string DeviceToken { get; set; } = string.Empty;
            public string DevicePath { get; set; } = string.Empty;
            public string TypeArgument { get; set; } = "auto";
            public string? Name { get; set; }
            public string? InfoName { get; set; }
            public string? OpenDevice { get; set; }
            public string? Model { get; set; }
            public string? SerialNumber { get; set; }
            public string? Firmware { get; set; }
            public string? WindowsDeviceId { get; set; }
            public string? WindowsFriendlyName { get; set; }
            public List<string> DriveLetters { get; } = new();
        }

#if WINDOWS
        private sealed class WindowsDiskInfo
        {
            public string DeviceId { get; init; } = string.Empty;
            public string? Serial { get; init; }
            public string? Model { get; init; }
            public string? FriendlyName { get; init; }
            public List<string> DriveLetters { get; } = new();
        }

        private static class WindowsDiskEnumerator
        {
            private static readonly string[] s_diskQueries =
            {
                "SELECT DeviceID, SerialNumber, Model, FriendlyName FROM Win32_DiskDrive",
                "SELECT DeviceID, SerialNumber, Model, Caption FROM Win32_DiskDrive"
            };

            public static IReadOnlyList<WindowsDiskInfo> TryCollect(IPluginLogger? log)
            {
                if (!OperatingSystem.IsWindows()) return new List<WindowsDiskInfo>();

                foreach (var query in s_diskQueries)
                {
                    try
                    {
                        var collected = CollectWithQuery(query, log);
                        if (collected.Count > 0 || query == s_diskQueries[^1])
                        {
                            return collected;
                        }
                    }
                    catch (ManagementException mex) when (mex.ErrorCode == ManagementStatus.InvalidQuery)
                    {
                        log?.Log($"[Smartctl] disk query not supported: {mex.Message}. Retrying without FriendlyName.");
                    }
                    catch (COMException cex)
                    {
                        log?.Log($"[Smartctl] failed to enumerate Windows disks: {cex.Message}");
                        return new List<WindowsDiskInfo>();
                    }
                    catch (Exception ex)
                    {
                        log?.Log($"[Smartctl] failed to enumerate Windows disks: {ex.Message}");
                        return new List<WindowsDiskInfo>();
                    }
                }

                return new List<WindowsDiskInfo>();
            }

            private static List<WindowsDiskInfo> CollectWithQuery(string query, IPluginLogger? log)
            {
                var result = new List<WindowsDiskInfo>();

                using var searcher = new ManagementObjectSearcher(query);
                using var disks = searcher.Get();
                foreach (ManagementObject disk in disks)
                {
                    using (disk)
                    {
                        var info = new WindowsDiskInfo
                        {
                            DeviceId = TryGetPropertyString(disk, "DeviceID") ?? string.Empty,
                            Serial = TryGetPropertyString(disk, "SerialNumber"),
                            Model = TryGetPropertyString(disk, "Model"),
                            FriendlyName = TryGetPropertyString(disk, "FriendlyName") ?? TryGetPropertyString(disk, "Caption")
                        };

                        AppendDriveLetters(disk, info, log);

                        if (info.DriveLetters.Count > 1)
                        {
                            info.DriveLetters.Sort(StringComparer.OrdinalIgnoreCase);
                        }

                        result.Add(info);
                    }
                }

                return result;
            }

            private static void AppendDriveLetters(ManagementObject disk, WindowsDiskInfo info, IPluginLogger? log)
            {
                try
                {
                    foreach (ManagementObject partition in disk.GetRelated("Win32_DiskPartition"))
                    {
                        using (partition)
                        {
                            foreach (ManagementObject logical in partition.GetRelated("Win32_LogicalDisk"))
                            {
                                using (logical)
                                {
                                    var letter = TryGetPropertyString(logical, "DeviceID");
                                    if (string.IsNullOrWhiteSpace(letter)) continue;
                                    if (!info.DriveLetters.Any(l => string.Equals(l, letter, StringComparison.OrdinalIgnoreCase)))
                                    {
                                        info.DriveLetters.Add(letter);
                                    }
                                }
                            }
                        }
                    }
                }
                catch (ManagementException mex)
                {
                    log?.Log($"[Smartctl] failed to enumerate logical disks for {info.DeviceId}: {mex.Message}");
                }
                catch (COMException cex)
                {
                    log?.Log($"[Smartctl] failed to enumerate logical disks for {info.DeviceId}: {cex.Message}");
                }
                catch (Exception ex)
                {
                    log?.Log($"[Smartctl] unexpected error while enumerating logical disks for {info.DeviceId}: {ex.Message}");
                }
            }

            private static string? TryGetPropertyString(ManagementBaseObject obj, string propertyName)
            {
                try
                {
                    return obj.GetPropertyValue(propertyName)?.ToString();
                }
                catch (ManagementException)
                {
                    return null;
                }
                catch (COMException)
                {
                    return null;
                }
            }
        }
#endif

        private sealed class PluginConfig
        {
            [JsonPropertyName("smartctlPath")] public string? SmartctlPath { get; set; }
            [JsonPropertyName("pollSeconds")] public int? PollSeconds { get; set; }
            [JsonPropertyName("displayName")] public DisplayNameConfig? DisplayName { get; set; }
            [JsonPropertyName("excludeDevices")] public List<string>? ExcludeDevices { get; set; }
            [JsonPropertyName("settingsHintShown")] public bool? SettingsHintShown { get; set; }

            public static PluginConfig FromOptions(SmartctlPluginOptions options)
            {
                var config = new PluginConfig
                {
                    SmartctlPath = string.IsNullOrWhiteSpace(options.SmartctlPath) ? null : options.SmartctlPath,
                    PollSeconds = (int)Math.Round(Math.Clamp(options.PollIntervalSeconds, 1, 3600)),
                    DisplayName = new DisplayNameConfig
                    {
                        Mode = options.DisplayNameMode != DisplayNameMode.Auto ? options.DisplayNameMode.ToString() : null,
                        Format = options.DisplayNameFormat,
                        Prefix = options.DisplayNamePrefix,
                        Suffix = options.DisplayNameSuffix
                    },
                    ExcludeDevices = options.ExcludedTokens.Count > 0 ? new List<string>(options.ExcludedTokens) : null,
                    SettingsHintShown = options.HasShownSettingsHint ? true : null
                };

                if (config.PollSeconds <= 0)
                {
                    config.PollSeconds = null;
                }

                if (config.DisplayName is { Mode: null, Format: null, Prefix: null, Suffix: null })
                {
                    config.DisplayName = null;
                }

                if (string.IsNullOrWhiteSpace(config.SmartctlPath))
                {
                    config.SmartctlPath = null;
                }

                return config;
            }
        }

        private sealed class DisplayNameConfig
        {
            [JsonPropertyName("mode")] public string? Mode { get; set; }
            [JsonPropertyName("format")] public string? Format { get; set; }
            [JsonPropertyName("prefix")] public string? Prefix { get; set; }
            [JsonPropertyName("suffix")] public string? Suffix { get; set; }
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
