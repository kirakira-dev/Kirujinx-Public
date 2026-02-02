using Avalonia.Svg.Skia;
using CommunityToolkit.Mvvm.ComponentModel;
using Ryujinx.Ava.UI.Models.Input;
using Ryujinx.Ava.UI.Views.Input;
using Ryujinx.Common.Utilities;
using Ryujinx.UI.Views.Input;

namespace Ryujinx.Ava.UI.ViewModels.Input
{
    public partial class ControllerInputViewModel : BaseModel
    {
        public GamepadInputConfig Config
        {
            get;
            set
            {
                field = value;

                OnPropertyChanged();
            }
        }

        public StickVisualizer Visualizer
        {
            get;
            set
            {
                field = value;

                OnPropertyChanged();
            }
        }

        public bool IsLeft
        {
            get;
            set
            {
                field = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasSides));
            }
        }

        public bool IsRight
        {
            get;
            set
            {
                field = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasSides));
            }
        }

        public bool HasSides => IsLeft ^ IsRight;

        [ObservableProperty]
        public partial SvgImage Image { get; set; }
        public InputViewModel ParentModel { get; }

        public ControllerInputViewModel(InputViewModel model, GamepadInputConfig config, StickVisualizer visualizer)
        {
            ParentModel = model;
            Visualizer = visualizer;
            model.NotifyChangesEvent += OnParentModelChanged;
            OnParentModelChanged();
            config.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is nameof(Config.UseRainbowLed))
                {
                    if (Config is { UseRainbowLed: true, TurnOffLed: false, EnableLedChanging: true })
                        Rainbow.Updated += (ref color) => ParentModel.SelectedGamepad.SetLed((uint)color.ToArgb());
                    else
                    {
                        Rainbow.Reset();

                        if (Config.TurnOffLed)
                            ParentModel.SelectedGamepad.ClearLed();
                        else
                            ParentModel.SelectedGamepad.SetLed(Config.LedColor.ToUInt32());
                    }
                }
            };
            Config = config;
        }

        public async void ShowMotionConfig()
        {
            await MotionInputView.Show(this);
            ParentModel.IsModified = true;
        }

        public async void ShowRumbleConfig()
        {
            await RumbleInputView.Show(this);
            ParentModel.IsModified = true;
        }

        public async void ShowLedConfig()
        {
            await LedInputView.Show(this);
            ParentModel.IsModified = true;
        }

        public void OnParentModelChanged()
        {
            IsLeft = ParentModel.IsLeft;
            IsRight = ParentModel.IsRight;
            Image = ParentModel.Image;
        }
    }
}
