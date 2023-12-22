﻿using NLog;
using NLog.Config;
using Octokit;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Application = System.Windows.Application;
using Label = System.Windows.Controls.Label;
using MessageBox = System.Windows.MessageBox;
using TextBox = System.Windows.Controls.TextBox;

namespace SCVRPatcher {

    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window {
        // test
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

        internal static Utils.AssemblyAttributes AssemblyAttributes { get; } = new();
        internal static Uri availableConfigsUrl = new Uri(AppSettings.Default.availableConfigsUrl);
        internal static FileInfo availableConfigsFile = new FileInfo(AppSettings.Default.availableConfigsFile);
        internal static Resolution mainScreenResolution = Utils.GetMainScreenResolution();

        internal static ConfigDataBase configDatabase { get; private set; }
        internal static EAC eac { get; private set; } = new();
        internal static Game game { get; private set; } = new();
        internal static VorpX vorpx { get; private set; } = new();
        internal static Hmdq hmdq { get; private set; } = new();

        public static void SetupLogging() {
            var stream = typeof(MainWindow).Assembly.GetManifestResourceStream("SCVRPatcher.NLog.config");
            string xml;
            using (var reader = new StreamReader(stream)) {
                xml = reader.ReadToEnd();
            }
            LogManager.Configuration = XmlLoggingConfiguration.CreateFromXmlString(xml);
            //Logger = LogManager.GetCurrentClassLogger();
        }

        public MainWindow() {

            var assembly = typeof(MainWindow).Assembly;
            var attributes = assembly.GetCustomAttributes(false);
            var title = attributes.OfType<System.Reflection.AssemblyTitleAttribute>().FirstOrDefault();
            var version = attributes.OfType<System.Reflection.AssemblyFileVersionAttribute>().FirstOrDefault();
            var repositoryUrl = attributes.OfType<System.Reflection.AssemblyMetadataAttribute>().FirstOrDefault(x => x.Key == "RepositoryUrl");


            SetupLogging();
            Logger.Info($"Started {Application.Current.MainWindow.Title}");
            var args = Environment.GetCommandLineArgs();
            Logger.Info($"Command line arguments: {string.Join(" ", args)}");
            var parser = new CommandLineParser(args);
            var configsArg = parser.GetStringArgument("config");
            if (configsArg != null) {
                Logger.Info($"--config argument: {configsArg}");
                if (File.Exists(configsArg)) {
                    Logger.Info($"--config argument is a file: {configsArg}");
                    availableConfigsFile = new FileInfo(configsArg);
                } else if (Uri.TryCreate(configsArg, UriKind.Absolute, out var uriResult)) {
                    Logger.Info($"--config argument is a url: {configsArg}");
                    availableConfigsUrl = uriResult;
                } else Logger.Error($"Invalid --config argument: \"{configsArg}\"");
            }
            if (parser.GetSwitchArgument("console", 'c')) {
                AllocConsole();
                Logger.Info("Console ready!");
            }
            var pageFiles = Utils.GetPageFileSizes();
            foreach (var pageFile in pageFiles) {
                Logger.Info($"PageFile: {pageFile}");
            }
            hmdq.Initialize();
            hmdq.RunHmdq();
            if (hmdq.Data.IsEmpty) {
                Logger.Error("Failed to get HMD info through HMDQ!");
                var _ = MessageBox.Show("Failed to get HMD info from HMDQ!", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            } else {
                var hmdmanufacturer = hmdq.Data.Openvr.Properties.The0.PropManufacturerNameString;
                var hmdmodel = hmdq.Data.Openvr.Properties.The0.PropModelNumberString;
                var width = hmdq.Data.Openvr.Geometry.RecRts[0];
                var height = hmdq.Data.Openvr.Geometry.RecRts[1];
                var verticalFov = hmdq.Data.Openvr.Geometry.FovTot.FovVer;
                Logger.Info($"Manufacturer: {hmdmanufacturer} Model: {hmdmodel} {width}x{height} (fov: {verticalFov})");
            }


            //game.Initialize();
            InitializeComponent();
            configDatabase = new();
            LoadAvailableConfigs(availableConfigsUrl, availableConfigsFile);

            FillHmds(configDatabase);
            stackpanel_config.Children.Clear(); vorpx = new();
            VREnableButton.IsEnabled = true;
            // VRDisableButton.IsEnabled = true;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AllocConsole();

        public void LoadAvailableConfigs(Uri availableConfigsUrl, FileInfo availableConfigsFile) {
            //Logger.Info($"Loading config from Url: {availableConfigsUrl}");
            var onlineConfigs = ConfigDataBase.FromUrl(availableConfigsUrl);
            var offlineConfigs = ConfigDataBase.FromFile(availableConfigsFile);
            if (onlineConfigs != null && onlineConfigs != offlineConfigs && MessageBox.Show("Online and offline configs differ, overwrite?", "New configs available", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) {
                onlineConfigs.ToFile(availableConfigsFile);
                Logger.Info($"Saved config from {availableConfigsUrl} to {availableConfigsFile.Quote()}");
                configDatabase = onlineConfigs;
            } else if (offlineConfigs != null) {
                Logger.Info($"Loaded config from {availableConfigsFile.Quote()}");
                configDatabase = offlineConfigs;
            }

            //if (configDatabase.IsEmptyOrMissing) {
            //    Logger.Error($"Failed to load config from Url!");
            //    Logger.Info($"Loading config from File: {availableConfigsFile.Quote()}");
            //    configDatabase = GetAvailableConfigsFromFile(availableConfigsFile);
            //}
            if (configDatabase.IsEmptyOrMissing) {
                Logger.Error("No configs available!");
                var result = System.Windows.MessageBox.Show("No configs available!", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Application.Current.Shutdown();
                return;
            }
            Logger.Info($"Loaded {configDatabase.Brands.Count} brands.");
            //foreach (var brand in configDatabase.Brands) {
            //    foreach (var model in brand.Value) {
            //        foreach (var config in model.Value) {
            //            config.Value.Brand = brand.Key;
            //            config.Value.Model = model.Key;
            //            config.Value.Name = config.Key;
            //        }
            //    }
            //}
        }

        public void FillHmds(ConfigDataBase db) {
            tree_hmds.Items.Clear();
            if (db.IsEmptyOrMissing) {
                Logger.Error("No configs available!"); return;
            }
            foreach (var brand in db.Brands) {
                var brandItem = new TreeViewItem() { Header = brand.Key };
                foreach (var model in brand.Value) {
                    var modelItem = new TreeViewItem() { Header = model.Key };
                    foreach (var config in model.Value) {
                        var configItem = new TreeViewItem() { Header = config.Key };
                        modelItem.Items.Add(configItem);
                    }
                    brandItem.Items.Add(modelItem);
                }
                tree_hmds.Items.Add(brandItem);
            }
        }

        public void FillConfigList(ConfigDataBase db, HmdConfig config) {
            list_configs.Items.Clear();
            var redBrush = new SolidColorBrush(Colors.Red);
            foreach (var res in config.CustomResolutions) {
                var item = new ListViewItem() { Content = res.ToString(), Tag = res };
                if (res > mainScreenResolution) {
                    item.Foreground = redBrush;
                    item.ToolTip = $"Resolution is higher than your main screen's resolution! ({mainScreenResolution.Width} x {mainScreenResolution.Height})";
                }
                list_configs.Items.Add(item);
            }
            // sort list_configs
            list_configs.Items.SortDescriptions.Add(new System.ComponentModel.SortDescription("Tag.Width", System.ComponentModel.ListSortDirection.Ascending));
        }

        private void FillConfig(HmdConfig config) {
            if (config is null) return;
            stackpanel_config.Children.Clear();
            Logger.Info($"Filling config: {config}");
            stackpanel_config.Children.Add(new Label() { Content = string.Join(" ", configDatabase.GetPath(config)) });
            Type t = config.GetType();
            Dictionary<string, object> Items = new();
            foreach (var item in t.GetProperties()) {
                var value = item.GetValue(config);
                if (value is null) continue;
                //if (new string[] { "Brand", "Model", "Name" }.Contains(item.Name)) continue;
                Items.Add(item.Name, value);
            }
            foreach (var item in t.GetFields()) {
                var value = item.GetValue(config);
                if (value is null) continue;
                Items.Add(item.Name, value);
            }
            foreach (var item in Items) {
                var value = item.Value;
                if (value is null) continue;
                if (value is System.Collections.IList) continue;
                var label = new Label();
                label.Content = item.Key + ":";
                label.VerticalAlignment = VerticalAlignment.Center;
                //label.HorizontalAlignment = HorizontalAlignment.Right;
                label.Margin = new Thickness(0, 0, 5, 0);
                var textbox = new TextBox();
                textbox.Text = value.ToString();
                textbox.VerticalAlignment = VerticalAlignment.Center;
                //textbox.HorizontalAlignment = HorizontalAlignment.Stretch;
                textbox.Margin = new Thickness(0, 0, 5, 0);
                // add label and textbox to stackpanel_config
                stackpanel_config.Children.Add(label);
                stackpanel_config.Children.Add(textbox);
            }
        }

        private void VREnableButton_Click(object sender, RoutedEventArgs e) {
            var selectedConfig = GetSelectedConfig();
            if (selectedConfig is null) {
                MessageBox.Show("No HMD selected!", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            var selectedResolution = GetSelectedResolution();
            if (selectedResolution is null) {
                MessageBox.Show("No config selected!", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            Logger.Info("Patching VR");
            eac.Patch();
            vorpx.Patch(selectedConfig, selectedResolution);
            // Logger.Info(vorpx.ToJson(true));
            // foreach (var excludedItem in vorpx.vorpControlConfig.Data.Exclude) {
            //     Logger.Info($"Excluding {excludedItem.Quote()} from VorpX");
            // }
            game.Patch(selectedConfig, selectedResolution);
        }

        private void VRDisableButton_Click(object sender, RoutedEventArgs e) {
            MessageBox.Show("Not implemented yet, silly :)", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            eac.UnPatch();
            vorpx.UnPatch();
        }

        private HmdConfig? GetSelectedConfig() {
            var selectedItem = (TreeViewItem)tree_hmds.SelectedItem;
            var hasParent = selectedItem.Parent is not null;
            var hasChildren = selectedItem.Items.Count > 0;
            if (!hasParent || hasChildren) return null;
            var configName = selectedItem.Header.ToString();
            var hmdName = ((TreeViewItem)selectedItem.Parent).Header.ToString();
            var brandItem = (TreeViewItem)selectedItem.Parent;
            var brandName = (((TreeViewItem)brandItem.Parent).Header).ToString();
            return configDatabase.GetConfig(brandName, hmdName, configName);
        }

        private Resolution? GetSelectedResolution() {
            var selectedItem = (ListViewItem)list_configs.SelectedItem;
            if (selectedItem is null) return null;
            return (Resolution)selectedItem.Tag;
        }

        private void tree_hmds_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e) {
            var selectedConfig = GetSelectedConfig();
            if (selectedConfig is null) return;
            FillConfigList(configDatabase, selectedConfig);
        }

        private void list_configs_SelectionChanged(object sender, SelectionChangedEventArgs e) {
            var selectedConfig = GetSelectedConfig();
            if (selectedConfig is null) return;
            FillConfig(selectedConfig);
        }

        private void onAboutButtonClicked(object sender, RoutedEventArgs e) {
            AssemblyAttributes.RepositoryUrl.OpenInDefaultBrowser();
        }

        private void onCheckForUpdatesClicked(object sender, RoutedEventArgs e) {
            var githubUrl = AssemblyAttributes.RepositoryUrl;
            var githubUser = githubUrl.Segments[1].TrimEnd('/');
            var githubRepo = githubUrl.Segments[2].TrimEnd('/');
            Logger.Debug($"Getting self from Github release: {githubUser}/{githubRepo}");
            var client = new GitHubClient(new ProductHeaderValue("SCVRPatcher"));
            var release = client.Repository.Release.GetLatest(githubUser, githubRepo).Result;
            Logger.Debug($"Installed version: {AssemblyAttributes.Version}");
            Logger.Debug($"Found latest release {release.Name} ({release.TagName})");
            if (release.TagName == AssemblyAttributes.Version) {
                var msg = $"Version {release.TagName} is already latest, nothing to update!";
                Logger.Info(msg);
                MessageBox.Show(msg, "No update available", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var asset = release.Assets.First(a => a.Name.EndsWith(".zip"));
            var downloadUrl = asset.BrowserDownloadUrl;
            Logger.Debug($"Downloading {downloadUrl}");
            var tempPath = Utils.GetTempFile().WithExtension("zip");
            using (var client2 = new WebClient()) {
                client2.DownloadFile(downloadUrl, tempPath.FullName);
            }
            Logger.Debug($"Downloaded to {tempPath}");
            var currentPath = new FileInfo(System.Reflection.Assembly.GetExecutingAssembly().Location);
            currentPath.Directory.OpenInExplorer();
            tempPath.OpenInExplorer();
        }

        private void onDiscordButtonClicked(object sender, RoutedEventArgs e) {
            new Uri(AppSettings.Default.DiscordInviteUrl).OpenInDefaultBrowser();
        }

        private void onOpenFileButtonClicked(object sender, RoutedEventArgs e) {
            var buttonText = ((MenuItem)sender).Header.ToString();
            MessageBox.Show(buttonText, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private void onOpenDirectoryButtonClicked(object sender, RoutedEventArgs e) {
            var buttonText = ((MenuItem)sender).Header.ToString();
            MessageBox.Show(buttonText, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private void onSettingsButtonClicked(object sender, RoutedEventArgs e) {
            var settingsWindow = new UI.Settings();
            settingsWindow.Show();
        }

        private void onExitButtonClicked(object sender, RoutedEventArgs e) {
            Application.Current.Shutdown();
        }
    }
}