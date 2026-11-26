using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using RadikoUWP.Models;
using RadikoUWP.Services;
using Windows.Media.Playback;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace RadikoUWP
{
    public sealed partial class MainPage : Page
    {
        // ガイドラインのブレークポイント
        private const double WideBreakpoint = 720;
        private const double LargeBreakpoint = 1024;

        private readonly RadikoAuthService _authService;
        private readonly RadikoStationService _stationService;
        private readonly RadikoPlayerService _playerService;

        private readonly ObservableCollection<Station> _stations = new ObservableCollection<Station>();
        private readonly DispatcherTimer _programTimer = new DispatcherTimer();

        private Station _selectedStation;   // 詳細に表示中の局
        private Station _playingStation;    // 再生中の局
        private bool _showDetailInNarrow;   // 狭い画面で詳細を表示しているか
        private bool _isLoading;
        private bool _isStarting;           // 再生開始処理中

        public MainPage()
        {
            this.InitializeComponent();
            NavigationCacheMode = NavigationCacheMode.Required;

            _authService = new RadikoAuthService();
            _stationService = new RadikoStationService();
            _playerService = new RadikoPlayerService(_stationService);
            _playerService.PlaybackFailed += PlayerService_PlaybackFailed;
            _playerService.StateChanged += PlayerService_StateChanged;

            StationListView.ItemsSource = _stations;
            PlayerElement.SetMediaPlayer(_playerService.MediaPlayer);

            _programTimer.Tick += ProgramTimer_Tick;

            SizeChanged += (s, e) => UpdateMasterDetailLayout();
            Loaded += MainPage_Loaded;
        }

        private async void MainPage_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= MainPage_Loaded;
            UpdateMasterDetailLayout();
            UpdatePlaybackUi();

            // 起動時に自動で認証・一覧取得 (ボタン操作を不要にする)
            await LoadAsync();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            SystemNavigationManager.GetForCurrentView().BackRequested += OnBackRequested;
            UpdateBackButton();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            SystemNavigationManager.GetForCurrentView().BackRequested -= OnBackRequested;
        }

        #region 認証・一覧取得

        private async Task LoadAsync()
        {
            if (_isLoading)
                return;
            _isLoading = true;

            HideError();
            SetLoading(true);
            EmptyState.Visibility = Visibility.Collapsed;
            AreaText.Text = "エリアを確認しています…";

            try
            {
                // 再認証時は旧トークンでの再生を止める
                if (_playerService.IsPlaying)
                    StopPlayback();

                await _authService.AuthenticateAsync();
                _playerService.SetAuthToken(_authService.AuthToken);
                AreaText.Text = $"{_authService.AreaName} ({_authService.AreaId})";

                var previousId = _selectedStation?.Id;
                var stations = await _stationService.GetStationsAsync(_authService.AreaId);

                _stations.Clear();
                foreach (var station in stations)
                    _stations.Add(station);

                if (_stations.Count == 0)
                {
                    ShowEmptyState("このエリアの放送局が見つかりませんでした。");
                }

                // 選択状態を復元
                var restored = _stations.FirstOrDefault(s => s.Id == previousId);
                SelectStation(restored, false);

                await RefreshProgramsAsync();
            }
            catch (Exception ex)
            {
                AreaText.Text = "エリア: 不明";
                _stations.Clear();
                SelectStation(null, false);
                ShowEmptyState("radiko に接続できませんでした。\n" + ex.Message);
            }
            finally
            {
                SetLoading(false);
                _isLoading = false;
                UpdatePlaybackUi();
            }
        }

        private async Task RefreshProgramsAsync()
        {
            _programTimer.Stop();
            if (!_authService.IsAuthenticated)
                return;

            var programs = await _stationService.GetNowProgramsAsync(_authService.AreaId);
            foreach (var station in _stations)
            {
                ProgramInfo info;
                station.Program = programs.TryGetValue(station.Id, out info) ? info : null;
            }

            UpdateDetail();
            UpdateMiniPlayer();
            if (_playingStation != null)
                _playerService.UpdateDisplay(_playingStation.Name, _playingStation.ProgramTitle);

            // 番組の経過表示は 30 秒ごと、番組情報は番組が切り替わったら再取得
            _programTimer.Interval = TimeSpan.FromSeconds(30);
            _programTimer.Start();
        }

        private async void ProgramTimer_Tick(object sender, object e)
        {
            var now = DateTimeOffset.Now;
            bool ended = _stations.Any(s => s.Program != null && s.Program.End <= now);
            if (ended && !_isLoading)
                await RefreshProgramsAsync();
            else
                UpdateProgramProgress();
        }

        private void SetLoading(bool loading)
        {
            LoadingBar.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
            RefreshAppBarButton.IsEnabled = !loading;
            RetryButton.IsEnabled = !loading;
        }

        private void ShowEmptyState(string message)
        {
            EmptyStateText.Text = message;
            EmptyState.Visibility = Visibility.Visible;
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await LoadAsync();
        }

        #endregion

        #region マスター/詳細

        private void StationListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            var station = e.ClickedItem as Station;
            if (station == null)
                return;

            SelectStation(station, true);
        }

        private void StationListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // キーボード操作 (方向キー) による選択にも追従する。狭い画面では詳細へは移動しない
            var station = StationListView.SelectedItem as Station;
            if (station != null && station != _selectedStation)
            {
                _selectedStation = station;
                UpdateDetail();
                UpdatePlaybackUi();
            }
        }

        private void SelectStation(Station station, bool openDetail)
        {
            _selectedStation = station;
            if (StationListView.SelectedItem != station)
                StationListView.SelectedItem = station;

            if (openDetail && station != null && IsNarrow)
                _showDetailInNarrow = true;
            if (station == null)
                _showDetailInNarrow = false;

            UpdateDetail();
            UpdateMasterDetailLayout();
            UpdatePlaybackUi();
        }

        private bool IsNarrow => ActualWidth > 0 && ActualWidth < WideBreakpoint;

        /// <summary>
        /// 720 epx 未満はスタック表示 (一覧か詳細のどちらか)、以上は左右に並べる表示。
        /// </summary>
        private void UpdateMasterDetailLayout()
        {
            if (ActualWidth <= 0)
                return;

            if (!IsNarrow)
            {
                _showDetailInNarrow = false;
                MasterColumn.Width = new GridLength(ActualWidth >= LargeBreakpoint ? 400 : 320);
                DetailColumn.Width = new GridLength(1, GridUnitType.Star);
                MasterPane.Visibility = Visibility.Visible;
                DetailPane.Visibility = Visibility.Visible;
                DetailPane.BorderThickness = new Thickness(1, 0, 0, 0);
            }
            else if (_showDetailInNarrow)
            {
                MasterColumn.Width = new GridLength(0);
                DetailColumn.Width = new GridLength(1, GridUnitType.Star);
                MasterPane.Visibility = Visibility.Collapsed;
                DetailPane.Visibility = Visibility.Visible;
                DetailPane.BorderThickness = new Thickness(0);
            }
            else
            {
                MasterColumn.Width = new GridLength(1, GridUnitType.Star);
                DetailColumn.Width = new GridLength(0);
                MasterPane.Visibility = Visibility.Visible;
                DetailPane.Visibility = Visibility.Collapsed;
            }

            UpdateMiniPlayer();
            UpdateBackButton();
        }

        private void UpdateBackButton()
        {
            // デスクトップ モードではタイトル バーの戻るボタンを表示 (電話はハードウェア/ソフトウェア ボタン)
            SystemNavigationManager.GetForCurrentView().AppViewBackButtonVisibility =
                (IsNarrow && _showDetailInNarrow) ? AppViewBackButtonVisibility.Visible : AppViewBackButtonVisibility.Collapsed;
        }

        private void OnBackRequested(object sender, BackRequestedEventArgs e)
        {
            if (e.Handled)
                return;

            // 狭い画面で詳細を表示中なら一覧に戻る。それ以外は既定動作 (アプリを離れる)
            if (IsNarrow && _showDetailInNarrow)
            {
                e.Handled = true;
                _showDetailInNarrow = false;
                UpdateMasterDetailLayout();
                StationListView.Focus(FocusState.Programmatic);
            }
        }

        private void MiniPlayer_Click(object sender, RoutedEventArgs e)
        {
            if (_playingStation != null)
                SelectStation(_playingStation, true);
        }

        private void UpdateDetail()
        {
            var station = _selectedStation;
            if (station == null)
            {
                DetailContent.Visibility = Visibility.Collapsed;
                NoSelectionText.Visibility = Visibility.Visible;
                return;
            }

            NoSelectionText.Visibility = Visibility.Collapsed;
            DetailContent.Visibility = Visibility.Visible;

            DetailLogo.Source = station.LargeLogo;
            DetailStationName.Text = station.Name;

            var program = station.Program;
            DetailProgramTitle.Text = program?.Title ?? "番組情報はありません";
            DetailPerformer.Text = program?.Performer ?? "";
            DetailPerformer.Visibility = string.IsNullOrEmpty(DetailPerformer.Text) ? Visibility.Collapsed : Visibility.Visible;
            ProgramProgressArea.Visibility = program != null ? Visibility.Visible : Visibility.Collapsed;
            UpdateProgramProgress();
        }

        private void UpdateProgramProgress()
        {
            var program = _selectedStation?.Program;
            if (program == null)
                return;

            ProgramProgress.Value = program.ProgressPercent(DateTimeOffset.Now);
            DetailTime.Text = program.TimeText;
        }

        private void UpdateMiniPlayer()
        {
            bool show = IsNarrow && !_showDetailInNarrow && _playingStation != null;
            MiniPlayer.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show)
                return;

            MiniLogo.Source = _playingStation.Logo;
            MiniStationName.Text = _playingStation.Name;
            MiniProgramTitle.Text = PlaybackStatusLabel() ?? _playingStation.ProgramTitle;
        }

        #endregion

        #region 再生

        private async void PlayStopButton_Click(object sender, RoutedEventArgs e)
        {
            HideError();

            var station = _selectedStation;
            bool selectedIsPlaying = station != null && _playingStation == station && _playerService.IsPlaying;

            if (selectedIsPlaying)
            {
                // システムのメディアコントロールで一時停止されている場合は再開、それ以外は停止
                if (_playerService.IsPaused)
                    _playerService.Resume();
                else
                    StopPlayback();
                return;
            }

            if (station == null)
            {
                ShowError("放送局を選んでください。");
                return;
            }

            await StartPlaybackAsync(station);
        }

        private async Task StartPlaybackAsync(Station station)
        {
            if (!_authService.IsAuthenticated)
            {
                ShowError("radiko に接続されていません。「更新」を押してください。");
                return;
            }

            StopPlayback();
            _isStarting = true;
            _playingStation = station;
            station.IsPlaying = true;
            UpdatePlaybackUi();

            try
            {
                await _playerService.PlayStationAsync(station.Id, station.Name, station.ProgramTitle);
                if (!_playerService.IsPlaying && _playingStation == station)
                {
                    // 開始前に停止された
                    station.IsPlaying = false;
                    _playingStation = null;
                }
            }
            catch (Exception ex)
            {
                station.IsPlaying = false;
                if (_playingStation == station)
                    _playingStation = null;
                ShowError("再生できませんでした: " + ex.Message);
            }
            finally
            {
                _isStarting = false;
                UpdatePlaybackUi();
            }
        }

        private void StopPlayback()
        {
            _playerService.Stop();
            if (_playingStation != null)
                _playingStation.IsPlaying = false;
            _playingStation = null;
            UpdatePlaybackUi();
        }

        private string PlaybackStatusLabel()
        {
            if (_playingStation == null)
                return null;
            if (_isStarting)
                return "接続しています…";

            if (_playerService.IsPaused)
                return "一時停止中";

            switch (_playerService.PlaybackState)
            {
                case MediaPlaybackState.Playing:
                    return null;
                default:
                    // Opening / Buffering / 再生開始直後
                    return "接続しています…";
            }
        }

        /// <summary>再生状態に合わせてボタン・状態表示を更新する。</summary>
        private void UpdatePlaybackUi()
        {
            var station = _selectedStation;
            bool selectedIsActive = station != null && station == _playingStation;
            bool paused = selectedIsActive && !_isStarting && _playerService.IsPaused;
            bool showStop = selectedIsActive && !paused;

            var label = showStop ? "停止" : "再生";
            var symbol = showStop ? Symbol.Stop : Symbol.Play;

            PlayStopIcon.Symbol = symbol;
            PlayStopText.Text = label;
            AutomationPropertiesHelper.SetName(PlayStopButton, label);
            PlayStopButton.IsEnabled = station != null && _authService.IsAuthenticated;

            PlayStopAppBarButton.Icon = new SymbolIcon(symbol);
            PlayStopAppBarButton.Label = label;
            ToolTipService.SetToolTip(PlayStopAppBarButton, label);
            PlayStopAppBarButton.IsEnabled = PlayStopButton.IsEnabled;

            string status = selectedIsActive ? PlaybackStatusLabel() : null;
            bool buffering = status == "接続しています…";
            BufferingRing.IsActive = buffering;
            BufferingRing.Visibility = buffering ? Visibility.Visible : Visibility.Collapsed;
            PlaybackStatusText.Text = status ?? (selectedIsActive ? "再生中" : "");

            UpdateMiniPlayer();
        }

        private async void PlayerService_StateChanged(object sender, EventArgs e)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, UpdatePlaybackUi);
        }

        // 再生中のエラー (バックグラウンドスレッドから通知される)
        private async void PlayerService_PlaybackFailed(object sender, string message)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                StopPlayback();
                ShowError(message + " 認証の期限切れの場合は「更新」を押してください。");
            });
        }

        private void VolumeSlider_ValueChanged(object sender, Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_playerService != null)
                _playerService.MediaPlayer.Volume = e.NewValue / 100.0;
        }

        #endregion

        #region その他

        private async void AboutButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new ContentDialog
            {
                Title = "このアプリについて",
                Content = new TextBlock
                {
                    Text = "radiko のライブ放送を聴くための非公式クライアントです。\n\n" +
                           "・接続元の IP アドレスで判定されたエリアの放送局を聴くことができます。\n" +
                           "・radiko の利用規約に従ってご利用ください。\n" +
                           "・radiko のサービス変更により動作しなくなる場合があります。",
                    TextWrapping = TextWrapping.Wrap,
                    Style = (Style)Application.Current.Resources["BodyTextBlockStyle"]
                },
                PrimaryButtonText = "閉じる"
            };
            await dialog.ShowAsync();
        }

        private void DismissErrorButton_Click(object sender, RoutedEventArgs e)
        {
            HideError();
        }

        private void ShowError(string message)
        {
            ErrorText.Text = message;
            ErrorBar.Visibility = Visibility.Visible;
        }

        private void HideError()
        {
            ErrorText.Text = "";
            ErrorBar.Visibility = Visibility.Collapsed;
        }

        #endregion
    }

    internal static class AutomationPropertiesHelper
    {
        public static void SetName(DependencyObject element, string name)
        {
            Windows.UI.Xaml.Automation.AutomationProperties.SetName(element, name);
        }
    }
}
