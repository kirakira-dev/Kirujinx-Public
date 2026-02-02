#nullable enable
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using System;

namespace Ryujinx.Ava.UI.Windows
{
    public partial class KiraKiraPrankWindow : Window
    {
        private readonly DispatcherTimer _flashTimer;
        private bool _isWhite;
        private readonly Grid _flashGrid;
        private readonly TextBlock _prankText;

        public IRelayCommand CloseCommand { get; }

        public KiraKiraPrankWindow()
        {
            InitializeComponent();

            DataContext = this;
            CloseCommand = new RelayCommand(CloseWindow);

            _flashGrid = this.FindControl<Grid>("FlashGrid")!;
            _prankText = this.FindControl<TextBlock>("PrankText")!;

            _flashTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(50)
            };
            _flashTimer.Tick += FlashTimer_Tick;

            Opened += OnOpened;
            Closing += OnClosing;
        }

        private void OnOpened(object? sender, EventArgs e)
        {
            _flashTimer.Start();
            Focus();
        }

        private void OnClosing(object? sender, WindowClosingEventArgs e)
        {
            _flashTimer.Stop();
        }

        private void FlashTimer_Tick(object? sender, EventArgs e)
        {
            _isWhite = !_isWhite;

            if (_isWhite)
            {
                _flashGrid.Background = Brushes.White;
                _prankText.Foreground = Brushes.Black;
            }
            else
            {
                _flashGrid.Background = Brushes.Black;
                _prankText.Foreground = Brushes.White;
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            bool isMacQuit = e.KeyModifiers.HasFlag(KeyModifiers.Meta) && e.Key == Key.Q;
            bool isCtrlQ = e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.Q;
            bool isAltF4 = e.KeyModifiers.HasFlag(KeyModifiers.Alt) && e.Key == Key.F4;

            if (isMacQuit || isCtrlQ || isAltF4)
            {
                base.OnKeyDown(e);
                return;
            }

            e.Handled = true;
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            e.Handled = true;
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            e.Handled = true;
        }

        private void CloseWindow()
        {
            _flashTimer.Stop();
            Close();
        }
    }
}
