using CommunityToolkit.Mvvm.ComponentModel;
using Ryujinx.Ava.UI.ViewModels;
using Ryujinx.Common.Configuration.Hid;
using Ryujinx.Common.Configuration.Hid.Keyboard;

namespace Ryujinx.Ava.UI.Models.Input
{
    public partial class KeyboardInputConfig : BaseModel
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public ControllerType ControllerType { get; set; }
        public PlayerIndex PlayerIndex { get; set; }

        [ObservableProperty]
        public partial Key LeftStickUp { get; set; }

        [ObservableProperty]
        public partial Key LeftStickDown { get; set; }

        [ObservableProperty]
        public partial Key LeftStickLeft { get; set; }

        [ObservableProperty]
        public partial Key LeftStickRight { get; set; }

        [ObservableProperty]
        public partial Key LeftStickButton { get; set; }

        [ObservableProperty]
        public partial Key RightStickUp { get; set; }

        [ObservableProperty]
        public partial Key RightStickDown { get; set; }

        [ObservableProperty]
        public partial Key RightStickLeft { get; set; }

        [ObservableProperty]
        public partial Key RightStickRight { get; set; }

        [ObservableProperty]
        public partial Key RightStickButton { get; set; }

        [ObservableProperty]
        public partial Key DpadUp { get; set; }

        [ObservableProperty]
        public partial Key DpadDown { get; set; }

        [ObservableProperty]
        public partial Key DpadLeft { get; set; }

        [ObservableProperty]
        public partial Key DpadRight { get; set; }

        [ObservableProperty]
        public partial Key ButtonMinus { get; set; }

        [ObservableProperty]
        public partial Key ButtonPlus { get; set; }

        [ObservableProperty]
        public partial Key ButtonA { get; set; }

        [ObservableProperty]
        public partial Key ButtonB { get; set; }

        [ObservableProperty]
        public partial Key ButtonX { get; set; }

        [ObservableProperty]
        public partial Key ButtonY { get; set; }

        [ObservableProperty]
        public partial Key ButtonL { get; set; }

        [ObservableProperty]
        public partial Key ButtonR { get; set; }

        [ObservableProperty]
        public partial Key ButtonZl { get; set; }

        [ObservableProperty]
        public partial Key ButtonZr { get; set; }

        [ObservableProperty]
        public partial Key LeftButtonSl { get; set; }

        [ObservableProperty]
        public partial Key LeftButtonSr { get; set; }

        [ObservableProperty]
        public partial Key RightButtonSl { get; set; }

        [ObservableProperty]
        public partial Key RightButtonSr { get; set; }

        public KeyboardInputConfig(InputConfig config)
        {
            if (config != null)
            {
                Id = config.Id;
                Name = config.Name;
                ControllerType = config.ControllerType;
                PlayerIndex = config.PlayerIndex;

                if (config is not StandardKeyboardInputConfig keyboardConfig)
                {
                    return;
                }

                LeftStickUp = keyboardConfig.LeftJoyconStick.StickUp;
                LeftStickDown = keyboardConfig.LeftJoyconStick.StickDown;
                LeftStickLeft = keyboardConfig.LeftJoyconStick.StickLeft;
                LeftStickRight = keyboardConfig.LeftJoyconStick.StickRight;
                LeftStickButton = keyboardConfig.LeftJoyconStick.StickButton;

                RightStickUp = keyboardConfig.RightJoyconStick.StickUp;
                RightStickDown = keyboardConfig.RightJoyconStick.StickDown;
                RightStickLeft = keyboardConfig.RightJoyconStick.StickLeft;
                RightStickRight = keyboardConfig.RightJoyconStick.StickRight;
                RightStickButton = keyboardConfig.RightJoyconStick.StickButton;

                DpadUp = keyboardConfig.LeftJoycon.DpadUp;
                DpadDown = keyboardConfig.LeftJoycon.DpadDown;
                DpadLeft = keyboardConfig.LeftJoycon.DpadLeft;
                DpadRight = keyboardConfig.LeftJoycon.DpadRight;
                ButtonL = keyboardConfig.LeftJoycon.ButtonL;
                ButtonMinus = keyboardConfig.LeftJoycon.ButtonMinus;
                LeftButtonSl = keyboardConfig.LeftJoycon.ButtonSl;
                LeftButtonSr = keyboardConfig.LeftJoycon.ButtonSr;
                ButtonZl = keyboardConfig.LeftJoycon.ButtonZl;

                ButtonA = keyboardConfig.RightJoycon.ButtonA;
                ButtonB = keyboardConfig.RightJoycon.ButtonB;
                ButtonX = keyboardConfig.RightJoycon.ButtonX;
                ButtonY = keyboardConfig.RightJoycon.ButtonY;
                ButtonR = keyboardConfig.RightJoycon.ButtonR;
                ButtonPlus = keyboardConfig.RightJoycon.ButtonPlus;
                RightButtonSl = keyboardConfig.RightJoycon.ButtonSl;
                RightButtonSr = keyboardConfig.RightJoycon.ButtonSr;
                ButtonZr = keyboardConfig.RightJoycon.ButtonZr;
            }
        }

        public InputConfig GetConfig()
        {
            StandardKeyboardInputConfig config = new()
            {
                Id = Id,
                Name = Name,
                Backend = InputBackendType.WindowKeyboard,
                PlayerIndex = PlayerIndex,
                ControllerType = ControllerType,
                LeftJoycon = new LeftJoyconCommonConfig<Key>
                {
                    DpadUp = DpadUp,
                    DpadDown = DpadDown,
                    DpadLeft = DpadLeft,
                    DpadRight = DpadRight,
                    ButtonL = ButtonL,
                    ButtonMinus = ButtonMinus,
                    ButtonZl = ButtonZl,
                    ButtonSl = LeftButtonSl,
                    ButtonSr = LeftButtonSr,
                },
                RightJoycon = new RightJoyconCommonConfig<Key>
                {
                    ButtonA = ButtonA,
                    ButtonB = ButtonB,
                    ButtonX = ButtonX,
                    ButtonY = ButtonY,
                    ButtonPlus = ButtonPlus,
                    ButtonSl = RightButtonSl,
                    ButtonSr = RightButtonSr,
                    ButtonR = ButtonR,
                    ButtonZr = ButtonZr,
                },
                LeftJoyconStick = new JoyconConfigKeyboardStick<Key>
                {
                    StickUp = LeftStickUp,
                    StickDown = LeftStickDown,
                    StickRight = LeftStickRight,
                    StickLeft = LeftStickLeft,
                    StickButton = LeftStickButton,
                },
                RightJoyconStick = new JoyconConfigKeyboardStick<Key>
                {
                    StickUp = RightStickUp,
                    StickDown = RightStickDown,
                    StickLeft = RightStickLeft,
                    StickRight = RightStickRight,
                    StickButton = RightStickButton,
                },
                Version = InputConfig.CurrentVersion,
            };

            return config;
        }
    }
}
