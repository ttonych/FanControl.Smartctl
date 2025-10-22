using System;
using System.Collections.Generic;
using System.Linq;

namespace FanControl.Smartctl
{
    internal enum DisplayNameMode
    {
        Auto,
        Device,
        DeviceAndType,
        Model,
        Serial,
        ModelAndSerial,
        DriveLetters,
        ModelAndDriveLetters
    }

    internal sealed class SmartctlPluginOptions
    {
        public string SmartctlPath { get; set; } = "smartctl";
        public double PollIntervalSeconds { get; set; } = 10;
        public DisplayNameMode DisplayNameMode { get; set; } = DisplayNameMode.Auto;
        public string? DisplayNameFormat { get; set; }
        public string? DisplayNamePrefix { get; set; }
        public string? DisplayNameSuffix { get; set; }
        public List<string> ExcludedTokens { get; set; } = new();
        public bool HasShownSettingsHint { get; set; }

        public TimeSpan PollInterval => TimeSpan.FromSeconds(Math.Clamp(PollIntervalSeconds, 1, 3600));

        public SmartctlPluginOptions Clone()
        {
            return new SmartctlPluginOptions
            {
                SmartctlPath = SmartctlPath,
                PollIntervalSeconds = PollIntervalSeconds,
                DisplayNameMode = DisplayNameMode,
                DisplayNameFormat = DisplayNameFormat,
                DisplayNamePrefix = DisplayNamePrefix,
                DisplayNameSuffix = DisplayNameSuffix,
                ExcludedTokens = new List<string>(ExcludedTokens),
                HasShownSettingsHint = HasShownSettingsHint
            };
        }

        public void Normalize()
        {
            SmartctlPath = string.IsNullOrWhiteSpace(SmartctlPath) ? "smartctl" : SmartctlPath.Trim();

            if (!double.IsFinite(PollIntervalSeconds) || PollIntervalSeconds <= 0)
            {
                PollIntervalSeconds = 10;
            }

            DisplayNameFormat = string.IsNullOrWhiteSpace(DisplayNameFormat) ? null : DisplayNameFormat.Trim();
            DisplayNamePrefix = string.IsNullOrWhiteSpace(DisplayNamePrefix) ? null : DisplayNamePrefix.Trim();
            DisplayNameSuffix = string.IsNullOrWhiteSpace(DisplayNameSuffix) ? null : DisplayNameSuffix.Trim();

            var cleaned = new List<string>();
            foreach (var token in ExcludedTokens)
            {
                if (string.IsNullOrWhiteSpace(token)) continue;
                var trimmed = token.Trim();
                if (trimmed.Length == 0) continue;
                if (!cleaned.Any(t => string.Equals(t, trimmed, StringComparison.OrdinalIgnoreCase)))
                {
                    cleaned.Add(trimmed);
                }
            }

            ExcludedTokens = cleaned;
        }

        public static SmartctlPluginOptions CreateDefault() => new();
    }
}
