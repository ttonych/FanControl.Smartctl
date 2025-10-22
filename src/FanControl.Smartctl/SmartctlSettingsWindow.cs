using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace FanControl.Smartctl
{
    internal sealed class SmartctlSettingsWindow : Window
    {
        private readonly TextBox _smartctlPathBox = new() { MinWidth = 260 };
        private readonly TextBox _pollIntervalBox = new() { MinWidth = 80 };
        private readonly ComboBox _displayNameModeBox = new()
        {
            ItemsSource = Enum.GetValues(typeof(DisplayNameMode)),
            SelectedIndex = 0,
            MinWidth = 160
        };
        private readonly TextBox _displayNameFormatBox = new();
        private readonly TextBox _displayNamePrefixBox = new();
        private readonly TextBox _displayNameSuffixBox = new();
        private readonly TextBox _excludedTokensBox = new()
        {
            AcceptsReturn = true,
            AcceptsTab = false,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            TextWrapping = TextWrapping.NoWrap
        };

        public SmartctlPluginOptions ResultOptions { get; private set; }

        public SmartctlSettingsWindow(SmartctlPluginOptions options)
        {
            if (options is null) throw new ArgumentNullException(nameof(options));

            ResultOptions = options.Clone();

            Title = "Smartctl Plugin Settings";
            Width = 520;
            Height = 520;
            MinWidth = 460;
            MinHeight = 420;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.CanResize;

            Content = BuildContent();
            PopulateFields(ResultOptions);
        }

        private UIElement BuildContent()
        {
            var root = new DockPanel { Margin = new Thickness(16) };

            var buttonsPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var cancelButton = new Button
            {
                Content = "Cancel",
                MinWidth = 80,
                IsCancel = true
            };

            var saveButton = new Button
            {
                Content = "Save",
                MinWidth = 80,
                Margin = new Thickness(8, 0, 0, 0),
                IsDefault = true
            };
            saveButton.Click += (_, _) => Save();

            buttonsPanel.Children.Add(cancelButton);
            buttonsPanel.Children.Add(saveButton);
            DockPanel.SetDock(buttonsPanel, Dock.Bottom);
            root.Children.Add(buttonsPanel);

            var grid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < 6; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            AddLabel(grid, "smartctl path:", 0);
            var pathPanel = new DockPanel { LastChildFill = true };
            var browseButton = new Button { Content = "Browse...", Margin = new Thickness(8, 0, 0, 0) };
            browseButton.Click += OnBrowseClicked;
            DockPanel.SetDock(browseButton, Dock.Right);
            pathPanel.Children.Add(browseButton);
            pathPanel.Children.Add(_smartctlPathBox);
            AddControl(grid, pathPanel, 0);

            AddLabel(grid, "Poll interval (s):", 1);
            AddControl(grid, _pollIntervalBox, 1);

            AddLabel(grid, "Display name mode:", 2);
            AddControl(grid, _displayNameModeBox, 2);

            AddLabel(grid, "Display name format:", 3);
            AddControl(grid, _displayNameFormatBox, 3);

            AddLabel(grid, "Display name prefix:", 4);
            AddControl(grid, _displayNamePrefixBox, 4);

            AddLabel(grid, "Display name suffix:", 5);
            AddControl(grid, _displayNameSuffixBox, 5);

            var excludedLabel = new TextBlock
            {
                Text = "Excluded device tokens (one per line):",
                Margin = new Thickness(0, 8, 12, 4),
                VerticalAlignment = VerticalAlignment.Top
            };
            Grid.SetRow(excludedLabel, 6);
            grid.Children.Add(excludedLabel);

            Grid.SetRow(_excludedTokensBox, 6);
            Grid.SetColumn(_excludedTokensBox, 1);
            grid.Children.Add(_excludedTokensBox);

            var instructions = new TextBlock
            {
                Text = "Add the \"Smartctl Settings\" control in FanControl (Controls tab) and move it above 50% (for example, set it to 100%) to reopen this window.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 12, 12),
                FontStyle = FontStyles.Italic
            };

            var contentPanel = new StackPanel();
            contentPanel.Children.Add(instructions);
            contentPanel.Children.Add(grid);

            root.Children.Add(contentPanel);
            return root;
        }

        private static void AddLabel(Grid grid, string text, int row)
        {
            var label = new TextBlock
            {
                Text = text,
                Margin = new Thickness(0, row == 0 ? 0 : 8, 12, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetRow(label, row);
            grid.Children.Add(label);
        }

        private static void AddControl(Grid grid, UIElement element, int row)
        {
            if (row > 0)
            {
                if (element is FrameworkElement fe)
                {
                    var margin = fe.Margin;
                    margin.Top = Math.Max(margin.Top, 8);
                    fe.Margin = margin;
                }
            }
            Grid.SetRow(element, row);
            Grid.SetColumn(element, 1);
            grid.Children.Add(element);
        }

        private void PopulateFields(SmartctlPluginOptions options)
        {
            _smartctlPathBox.Text = options.SmartctlPath;
            _pollIntervalBox.Text = options.PollIntervalSeconds.ToString(CultureInfo.InvariantCulture);
            _displayNameModeBox.SelectedItem = options.DisplayNameMode;
            _displayNameFormatBox.Text = options.DisplayNameFormat ?? string.Empty;
            _displayNamePrefixBox.Text = options.DisplayNamePrefix ?? string.Empty;
            _displayNameSuffixBox.Text = options.DisplayNameSuffix ?? string.Empty;
            _excludedTokensBox.Text = string.Join(Environment.NewLine, options.ExcludedTokens);
        }

        private void OnBrowseClicked(object? sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Executable files (*.exe)|*.exe|All files (*.*)|*.*",
                Title = "Select smartctl executable"
            };

            if (dialog.ShowDialog(this) == true)
            {
                _smartctlPathBox.Text = dialog.FileName;
            }
        }

        private void Save()
        {
            if (!double.TryParse(_pollIntervalBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var pollSeconds) || pollSeconds <= 0)
            {
                MessageBox.Show(this, "Poll interval must be a positive number.", "Smartctl Plugin", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var mode = _displayNameModeBox.SelectedItem is DisplayNameMode selectedMode
                ? selectedMode
                : DisplayNameMode.Auto;

            var excluded = new List<string>();
            foreach (var token in _excludedTokensBox.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = token.Trim();
                if (!string.IsNullOrWhiteSpace(trimmed))
                {
                    excluded.Add(trimmed);
                }
            }

            var result = ResultOptions;
            result.SmartctlPath = _smartctlPathBox.Text?.Trim() ?? string.Empty;
            result.PollIntervalSeconds = pollSeconds;
            result.DisplayNameMode = mode;
            result.DisplayNameFormat = string.IsNullOrWhiteSpace(_displayNameFormatBox.Text) ? null : _displayNameFormatBox.Text.Trim();
            result.DisplayNamePrefix = string.IsNullOrWhiteSpace(_displayNamePrefixBox.Text) ? null : _displayNamePrefixBox.Text.Trim();
            result.DisplayNameSuffix = string.IsNullOrWhiteSpace(_displayNameSuffixBox.Text) ? null : _displayNameSuffixBox.Text.Trim();
            result.ExcludedTokens = excluded;

            ResultOptions = result;

            DialogResult = true;
        }
    }
}
