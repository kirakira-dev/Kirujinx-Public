using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Gommon;
using LibHac.Common;
using LibHac.Ns;
using Ryujinx.Ava.Common.Locale;
using Ryujinx.Ava.Systems.AppLibrary;
using Ryujinx.Ava.Systems.Configuration;
using Ryujinx.Ava.UI.Controls;
using Ryujinx.Ava.UI.Helpers;
using Ryujinx.Ava.UI.ViewModels;
using Ryujinx.Ava.UI.Views.Dialog;
using Ryujinx.Ava.UI.Windows;
using Ryujinx.Ava.Utilities;
using Ryujinx.Common;
using Ryujinx.Common.Helper;
using Ryujinx.Common.Utilities;
using Ryujinx.HLE.HOS.Services.Nfc.AmiiboDecryption;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Ryujinx.Ava.UI.Views.Main
{
    public partial class MainMenuBarView : RyujinxControl<MainWindowViewModel>
    {
        public MainWindow Window { get; private set; }

        public MainMenuBarView()
        {
            InitializeComponent();

            ToggleFileTypesMenuItem.ItemsSource = GenerateToggleFileTypeItems();
            ChangeLanguageMenuItem.ItemsSource = GenerateLanguageMenuItems();

            MiiAppletMenuItem.Command = Commands.Create(OpenMiiApplet);
            CloseRyujinxMenuItem.Command = Commands.Create(() => Window?.Close());
            OpenSettingsMenuItem.Command = Commands.Create(OpenSettings);
            PauseEmulationMenuItem.Command = Commands.Create(() => ViewModel.AppHost?.Pause());
            ResumeEmulationMenuItem.Command = Commands.Create(() => ViewModel.AppHost?.Resume());
            StopEmulationMenuItem.Command = Commands.Create(() => ViewModel.AppHost?.ShowExitPrompt().OrCompleted());
            CheatManagerMenuItem.Command = Commands.CreateSilentFail(OpenCheatManagerForCurrentApp);
            InstallFileTypesMenuItem.Command = Commands.Create(InstallFileTypes);
            UninstallFileTypesMenuItem.Command = Commands.Create(UninstallFileTypes);
            XciTrimmerMenuItem.Command = Commands.Create(XciTrimmerView.Show);
            AboutWindowMenuItem.Command = Commands.Create(AboutView.Show);
            CompatibilityListMenuItem.Command = Commands.Create(() => CompatibilityListWindow.Show());
            LdnGameListMenuItem.Command = Commands.Create(() => LdnGamesListWindow.Show());

            UpdateMenuItem.Command = MainWindowViewModel.UpdateCommand;

            FaqMenuItem.Command =
                SetupGuideMenuItem.Command =
                    LdnGuideMenuItem.Command = Commands.Create<string>(OpenHelper.OpenUrl);

            WindowSize720PMenuItem.Command =
                WindowSize1080PMenuItem.Command =
                    WindowSize1440PMenuItem.Command =
                        WindowSize2160PMenuItem.Command = Commands.Create<string>(ChangeWindowSize);

            LocaleManager.Instance.LocaleChanged += OnLocaleChanged;
        }

        private void OnLocaleChanged()
        {
            ChangeLanguageMenuItem.ItemsSource = GenerateLanguageMenuItems();
            Menu.Close();
        }

        private IEnumerable<CheckBox> GenerateToggleFileTypeItems() =>
            Enum.GetValues<FileTypes>()
                .Select(it => (FileName: Enum.GetName(it)!, FileType: it))
                .Select(it =>
                    new CheckBox
                    {
                        Content = $".{it.FileName}",
                        IsChecked = it.FileType.GetConfigValue(ConfigurationState.Instance.UI.ShownFileTypes),
                        Command = Commands.Create(() => Window.ToggleFileType(it.FileName))
                    }
                );

        private static IEnumerable<MenuItem> GenerateLanguageMenuItems()
        {
            const string LanguagesPath = "Ryujinx/Assets/Languages.json";

            string languageJson = EmbeddedResources.ReadAllText(LanguagesPath);
            string currentLanguageCode = LocaleManager.Instance.CurrentLanguageCode;

            LanguagesJson languages = JsonHelper.Deserialize(languageJson, LanguagesJsonContext.Default.LanguagesJson);

            foreach ((string code, string language) in languages.Languages)
            {
                string languageName = string.IsNullOrEmpty(language) ? code : language;

                MenuItem menuItem = new()
                {
                    Padding = new Thickness(15, 0, 0, 0),
                    Margin = new Thickness(3, 0, 3, 0),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Header = code == currentLanguageCode ? $"{languageName}  ✔" : languageName,
                    Command = Commands.Create(() => MainWindowViewModel.ChangeLanguage(code))
                };

                yield return menuItem;
            }
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);

            if (VisualRoot is MainWindow window)
            {
                Window = window;
                DataContext = ViewModel = window.ViewModel;
            }
        }

        public async Task OpenSettings()
        {
            Window.SettingsWindow = new(Window.VirtualFileSystem, Window.ContentManager);

            Rainbow.Enable();

            if (ViewModel.SelectedApplication is null) // Checks if game data exists
            {
                await StyleableAppWindow.ShowAsync(Window.SettingsWindow);
            }
            else
            {
                bool customConfigExists = File.Exists(Program.GetDirGameUserConfig(ViewModel.SelectedApplication.IdString));

                if (!ViewModel.IsGameRunning || !customConfigExists)
                {
                    await Window.SettingsWindow.ShowDialog(Window); // The game is not running, or if the user configuration does not exist
                }
                else
                {
                    // If there is a custom configuration in the folder
                    await StyleableAppWindow.ShowAsync(new GameSpecificSettingsWindow(ViewModel, customConfigExists));
                }
            }

            Rainbow.Disable();
            Rainbow.Reset();

            Window.SettingsWindow = null;

            ViewModel.LoadConfigurableHotKeys();
        }

        public AppletMetadata MiiApplet => new(ViewModel.ContentManager, "miiEdit", 0x0100000000001009);

        public async Task OpenMiiApplet()
        {
            if (!MiiApplet.CanStart(out ApplicationData appData, out BlitStruct<ApplicationControlProperty> nacpData))
                return;

            await ViewModel.LoadApplication(appData, ViewModel.IsFullScreen || ViewModel.StartGamesInFullscreen, nacpData);
        }

        public async Task OpenCheatManagerForCurrentApp()
        {
            if (!ViewModel.IsGameRunning)
                return;

            string name = ViewModel.AppHost.Device.Processes.ActiveApplication.ApplicationControlProperties.Title[(int)ViewModel.AppHost.Device.System.State.DesiredTitleLanguage].NameString.ToString();

            await StyleableAppWindow.ShowAsync(
                new CheatWindow(
                    Window.VirtualFileSystem,
                    ViewModel.AppHost.Device.Processes.ActiveApplication.ProgramIdText,
                    name,
                    ViewModel.SelectedApplication.Path)
            );

            ViewModel.AppHost.Device.EnableCheats();
        }

        private void ScanAmiiboMenuItem_AttachedToVisualTree(object sender, VisualTreeAttachmentEventArgs e)
        {
            if (sender is MenuItem)
                ViewModel.IsAmiiboRequested = ViewModel.AppHost.Device.System.SearchingForAmiibo(out _);
        }

        private void ScanBinAmiiboMenuItem_AttachedToVisualTree(object sender, VisualTreeAttachmentEventArgs e)
        {
            if (sender is MenuItem)
                ViewModel.IsAmiiboBinRequested = ViewModel.IsAmiiboRequested && AmiiboBinReader.HasAmiiboKeyFile;
        }

        private async Task InstallFileTypes()
        {
            ViewModel.AreMimeTypesRegistered = FileAssociationHelper.Install();
            if (ViewModel.AreMimeTypesRegistered)
                await ContentDialogHelper.CreateInfoDialog(LocaleManager.Instance[LocaleKeys.DialogInstallFileTypesSuccessMessage], string.Empty, LocaleManager.Instance[LocaleKeys.InputDialogOk], string.Empty, string.Empty);
            else
                await ContentDialogHelper.CreateErrorDialog(LocaleManager.Instance[LocaleKeys.DialogInstallFileTypesErrorMessage]);
        }

        private async Task UninstallFileTypes()
        {
            ViewModel.AreMimeTypesRegistered = !FileAssociationHelper.Uninstall();
            if (!ViewModel.AreMimeTypesRegistered)
                await ContentDialogHelper.CreateInfoDialog(LocaleManager.Instance[LocaleKeys.DialogUninstallFileTypesSuccessMessage], string.Empty, LocaleManager.Instance[LocaleKeys.InputDialogOk], string.Empty, string.Empty);
            else
                await ContentDialogHelper.CreateErrorDialog(LocaleManager.Instance[LocaleKeys.DialogUninstallFileTypesErrorMessage]);
        }

        private void ChangeWindowSize(string resolution)
        {
            (int resolutionWidth, int resolutionHeight) = resolution.Split(' ', 2)
                .Into(parts =>
                    (int.Parse(parts[0]), int.Parse(parts[1]))
                );

            // Correctly size window when 'TitleBar' is enabled (Nov. 14, 2024)
            double barsHeight = ((Window.StatusBarHeight + Window.MenuBarHeight) +
                (ConfigurationState.Instance.ShowOldUI ? (int)Window.TitleBar.Height : 0));

            double windowWidthScaled = (resolutionWidth * Program.WindowScaleFactor);
            double windowHeightScaled = ((resolutionHeight + barsHeight) * Program.WindowScaleFactor);

            Dispatcher.UIThread.Post(() =>
            {
                ViewModel.WindowState = WindowState.Normal;

                Window.Arrange(new Rect(Window.Position.X, Window.Position.Y, windowWidthScaled, windowHeightScaled));
            });
        }
    }
}
