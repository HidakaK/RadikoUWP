using System;
using System.Collections.ObjectModel;
using RadikoUWP.Models;
using RadikoUWP.Services;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace RadikoUWP
{
    public sealed partial class MainPage : Page
    {
        private readonly RadikoAuthService _authService;
        private readonly RadikoStationService _stationService;
        private readonly RadikoPlayerService _playerService;

        private readonly ObservableCollection<Station> _stations;
        private Station _selectedStation;

        public MainPage()
        {
            this.InitializeComponent();

            _authService = new RadikoAuthService();
            _stationService = new RadikoStationService();
            _playerService = new RadikoPlayerService(_stationService);
            _playerService.PlaybackFailed += PlayerService_PlaybackFailed;

            _stations = new ObservableCollection<Station>();
            StationListView.ItemsSource = _stations;

            // MediaPlayerElement に MediaPlayer を関連付け
            PlayerElement.SetMediaPlayer(_playerService.MediaPlayer);
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            _playerService.PlaybackFailed -= PlayerService_PlaybackFailed;
            _playerService.Dispose();
        }

        // 認証ボタンクリック
        private async void AuthButton_Click(object sender, RoutedEventArgs e)
        {
            AuthButton.IsEnabled = false;
            AuthStatusText.Text = "認証中...";
            AreaText.Text = "エリア: 取得中...";
            HideError();
            _stations.Clear();

            try
            {
                // 再認証時は旧トークンでの再生を止める
                _playerService.Stop();
                PlayStopButton.Content = "▶ 再生";
                PlayStopButton.IsEnabled = false;

                await _authService.AuthenticateAsync();

                AuthStatusText.Text = "認証済み";
                AreaText.Text = $"エリア: {_authService.AreaName} ({_authService.AreaId})";
                AuthButton.Content = "再認証";

                // 認証トークンをプレーヤーに設定
                _playerService.SetAuthToken(_authService.AuthToken);

                // 放送局一覧を取得
                var stations = await _stationService.GetStationsAsync(_authService.AreaId);
                foreach (var station in stations)
                {
                    _stations.Add(station);
                }
            }
            catch (Exception ex)
            {
                AuthStatusText.Text = "認証失敗";
                AreaText.Text = "エリア: -";
                ShowError($"認証エラー: {ex.Message}");
            }
            finally
            {
                AuthButton.IsEnabled = true;
            }
        }

        // 放送局リスト選択変更
        private void StationListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (StationListView.SelectedItem is Station station)
            {
                _selectedStation = station;
                CurrentStationText.Text = station.Name;
                PlayStopButton.IsEnabled = _authService.IsAuthenticated;
            }
        }

        // 放送局リストアイテムクリック
        private void StationListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is Station station)
            {
                _selectedStation = station;
                CurrentStationText.Text = station.Name;
                PlayStopButton.IsEnabled = _authService.IsAuthenticated;
            }
        }

        // 再生/停止ボタンクリック
        private async void PlayStopButton_Click(object sender, RoutedEventArgs e)
        {
            HideError();

            if (_playerService.IsPlaying)
            {
                _playerService.Stop();
                PlayStopButton.Content = "▶ 再生";
            }
            else
            {
                if (_selectedStation == null)
                {
                    ShowError("放送局を選択してください。");
                    return;
                }

                PlayStopButton.IsEnabled = false;
                PlayStopButton.Content = "読込中...";

                try
                {
                    await _playerService.PlayStationAsync(_selectedStation.Id, _selectedStation.Name);
                    PlayStopButton.Content = _playerService.IsPlaying ? "■ 停止" : "▶ 再生";
                }
                catch (Exception ex)
                {
                    PlayStopButton.Content = "▶ 再生";
                    ShowError($"再生エラー: {ex.Message}");
                }
                finally
                {
                    PlayStopButton.IsEnabled = true;
                }
            }
        }

        // 再生中のエラー (バックグラウンドスレッドから通知される)
        private async void PlayerService_PlaybackFailed(object sender, string message)
        {
            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                _playerService.Stop();
                PlayStopButton.Content = "▶ 再生";
                PlayStopButton.IsEnabled = _selectedStation != null && _authService.IsAuthenticated;
                ShowError(message + " (トークン期限切れの場合は「再認証」してください)");
            });
        }

        private void ShowError(string message)
        {
            ErrorText.Text = message;
            ErrorText.Visibility = Visibility.Visible;
        }

        private void HideError()
        {
            ErrorText.Text = "";
            ErrorText.Visibility = Visibility.Collapsed;
        }
    }
}
