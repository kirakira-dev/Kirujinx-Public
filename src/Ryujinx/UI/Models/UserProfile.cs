using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Ryujinx.Ava.UI.Controls;
using Ryujinx.Ava.UI.ViewModels;
using Ryujinx.Ava.UI.Views.User;
using Ryujinx.HLE.HOS.Services.Account.Acc;
using Profile = Ryujinx.HLE.HOS.Services.Account.Acc.UserProfile;

namespace Ryujinx.Ava.UI.Models
{
    public partial class UserProfile : BaseModel
    {
        private readonly Profile _profile;
        private readonly NavigationDialogHost _owner;
        [ObservableProperty]
        public partial byte[] Image { get; set; }

        [ObservableProperty]
        public partial string Name { get; set; }

        [ObservableProperty]
        public partial UserId UserId { get; set; }

        [ObservableProperty]
        public partial bool IsPointerOver { get; set; }

        [ObservableProperty]
        public partial IBrush BackgroundColor { get; set; }

        public UserProfile(Profile profile, NavigationDialogHost owner)
        {
            _profile = profile;
            _owner = owner;

            UpdateBackground();

            Image = profile.Image;
            Name = profile.Name;
            UserId = profile.UserId;
        }

        public void UpdateState()
        {
            UpdateBackground();
            OnPropertyChanged(nameof(Name));
        }

        private void UpdateBackground()
        {
            Application currentApplication = Application.Current;
            currentApplication.Styles.TryGetResource("ControlFillColorSecondary", currentApplication.ActualThemeVariant, out object color);

            if (color is not null)
            {
                BackgroundColor = _profile.AccountState == AccountState.Open ? new SolidColorBrush((Color)color) : Brushes.Transparent;
            }
        }

        public void Recover(UserProfile userProfile)
        {
            _owner.Navigate(typeof(UserEditorView), (_owner, userProfile, true));
        }
    }
}
