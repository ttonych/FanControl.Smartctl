> **Disclaimer**
> This plugin was assembled for personal use by **Codex (OpenAI)** — not by a professional programmer. It may contain mistakes or rough edges. **Use at your own risk.**

# FanControl.Smartctl Plugin

FanControl.Smartctl is a  plugin for [FanControl](https://github.com/Rem0o/FanControl) that exposes the temperature of HDDs and SATA / SAS SSDs by polling the `smartctl` utility. Once installed, FanControl can use these disk temperatures to drive custom curves and triggers alongside your existing sensors.

## Why this plugin exists

With HBA/RAID controllers, FanControl may not see drive S.M.A.R.T. temperatures; compatibility varies. For example, the Broadcom 9400-16i doesn’t expose temps to FanControl. This plugin bridges the gap by leveraging the open-source [smartmontools](https://www.smartmontools.org/) project. If you already rely on FanControl to manage fans, pumps, or LEDs, the plugin lets you react to disk temperatures without switching tools.

## What you need

- FanControl or later on Windows. (The plugin targets the official plugin interface.)
- The `smartctl` executable from smartmontools. Install smartmontools from the official site or your package manager and make sure `smartctl.exe` is reachable by the account that runs FanControl.
- Administrator rights the first time you copy the plugin DLL into FanControl's `Plugins` folder.

## Installation

1. Download the latest `FanControl.Smartctl.dll` release from the project's GitHub releases page (or build it yourself with `dotnet build`).
2. Locate your FanControl installation folder and open the `Plugins` subfolder. Typical path: `C:\Program Files\FanControl\Plugins`.
3. Copy `FanControl.Smartctl.dll` into the `Plugins` folder.
4. (Optional) Copy the sample configuration file from `src/FanControl.Smartctl/FanControl.Smartctl.sample.json` into the same folder and rename it to `FanControl.Smartctl.json`.
5. Launch FanControl. The plugin will appear as **Smartctl Disk Temperatures** under *Sensors*.

## Configuration

The plugin automatically discovers SATA and SAS drives by running `smartctl --scan-open -j`. Configuration is optional, but you can fine tune behavior by editing `FanControl.Smartctl.json` alongside the plugin DLL.

Key settings:

- `smartctlPath` – Absolute path to `smartctl.exe` if it is not in the `PATH`.
- `pollSeconds` – How often temperatures are refreshed (defaults to `10`).
- `displayName` – Controls how sensors are named. Choose a preset `mode` or supply a custom `format`, `prefix`, and `suffix`.
- `excludeDevices` – List tokens (model, serial, device path, drive letter, etc.) to ignore specific disks.
- `cages` – Group disks into virtual "cages". Each cage exposes minimum, maximum, and average temperature sensors.
- `cases` – Group cages into higher-level virtual "cases" with their own aggregate sensors.

### Minimal example

```json
{
  "pollSeconds": 15,
  "displayName": {
    "mode": "ModelAndDriveLetters"
  },
  "excludeDevices": [
    "USB"
  ]
}
```

### Full example

The repository ships with a commented sample (`FanControl.Smartctl.sample.json`) that showcases every option, including cages and cases.

## Using the sensors in FanControl

1. Open FanControl and go to the *Sensors* tab.
2. Look for entries that start with the plugin name, for example `Smartctl Disk Temperatures / Samsung SSD 870 QVO [C, D]`.
3. Drag a temperature sensor into a control curve or trigger just like any other FanControl sensor.
4. If you defined cages or cases, their aggregate temperatures will be listed under the same plugin section.

## Troubleshooting

- **No disks found:** Ensure `smartctl.exe` is installed, runs without UAC prompts, and supports your controller. NVMe devices are currently skipped.
- **Access denied:** Run FanControl with administrator privileges or configure appropriate permissions for the account that owns the disks.
- **Wrong names:** Adjust the `displayName` section or add `prefix` / `suffix` values to match your preferences.

## Building from source

This repository uses the .NET SDK. To compile the plugin locally:

```bash
dotnet build src/FanControl.Smartctl/FanControl.Smartctl.csproj -c Release
```

The build output will be placed in `src/FanControl.Smartctl/bin/Release/netstandard2.0/`.

## License

See the [LICENSE](LICENSE) file if available in the repository or the project page for licensing details.
