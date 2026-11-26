using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using RadikoUWP.Models;
using RadikoUWP.Services;
using Windows.Media.Playback;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace RadikoUWP
{
    public sealed partial class MainPage : Page
    {
        // ガイドラインのブレークポイント
        private const double WideBreakpoint = 720;
        private const double LargeBreakpoint = 1024;

        private enum DetailKind { None, Live, Timefree }

        private readonly RadikoAuthService _authService;
        private readonly RadikoStationService _stationService;
        private readonly RadikoPlayerService _playerService;

        private readonly ObservableCollection<Station> _stations = new ObservableCollection<Station>();
        private readonly ObservableCollection<TimefreeProgram> _programs = new ObservableCollection<TimefreeProgram>();
        private readonly Dictionary<string, Dictionary<string, List<TimefreeProgram>>> _weeklyCache =
            new Dictionary<string, Dictionary<string, List<TimefreeProgram>>>();

        private readonly DispatcherTimer _programTimer = new DispatcherTimer();
        private readonly DispatcherTimer _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        private readonly DispatcherTimer _seekTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };

        // 詳細に表示中の項目
        private DetailKind _detailKind = DetailKind.None;
        private Station _selectedStation;
        private TimefreeProgram _selectedProgram;

        // 再生中の項目 (_playingProgram が null ならライブ)
        private Station _playingStation;
        private TimefreeProgram _playingProgram;

        private bool _showDetailInNarrow;   // 狭い画面で詳細を表示しているか
        private bool _isLoading;
        private bool _isStarting;           // 再生開始処理中
        private bool _timefreeInitialized;
        private bool _suppressTimefreeFilter;
        private bool _updatingTimeline;
        private double _pendingSeekSeconds = -1;

        public MainPage()
        {
            this.InitializeComponent();
            NavigationCacheMode = NavigationCacheMode.Required;

            _authService = new RadikoAuthService();
            _stationService = new RadikoStationService();
            _playerService = new RadikoPlayerService(_stationService);
            _playerService.PlaybackFailed += PlayerService_PlaybackFailed;
            _playerService.PlaybackEnded += PlayerService_PlaybackEnded;
            _playerService.StateChanged += PlayerService_StateChanged;

            StationListView.ItemsSource = _stations;
            ProgramListView.ItemsSource = _programs;
            TimefreeStationCombo.ItemsSource = _stations;
            PlayerElement.SetMediaPlayer(_playerService.MediaPlayer);

            _programTimer.Tick += ProgramTimer_Tick;
            _positionTimer.Tick += (s, e) => UpdateTimeline();
            _seekTimer.Tick += SeekTimer_Tick;

            SizeChanged += (s, e) => UpdateMasterDetailLayout();
            MainPivot.Loaded += (s, e) => UpdatePivotHeaderAlignment();
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

        private bool IsTimefreePivot => MainPivot.SelectedItem == TimefreePivotItem;

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
                _playerService.SetAuthToken(_authService.AuthToken, _authService.AreaId);
                AreaText.Text = $"{_authService.AreaName} ({_authService.AreaId})";

                var previousId = _selectedStation?.Id;
                var previousTimefreeId = (TimefreeStationCombo.SelectedItem as Station)?.Id;
                var stations = await _stationService.GetStationsAsync(_authService.AreaId);

                _suppressTimefreeFilter = true;
                _stations.Clear();
                foreach (var station in stations)
                    _stations.Add(station);
                _suppressTimefreeFilter = false;
                _weeklyCache.Clear();

                if (_stations.Count == 0)
                    ShowEmptyState("このエリアの放送局が見つかりませんでした。");

                // 選択状態を復元
                var restored = _stations.FirstOrDefault(s => s.Id == previousId);
                if (_detailKind != DetailKind.Timefree)
                    SelectStation(restored, false);

                await RefreshProgramsAsync();

                if (_timefreeInitialized)
                {
                    _timefreeInitialized = false;
                    await InitializeTimefreeAsync(previousTimefreeId);
                }
                else if (IsTimefreePivot)
                {
                    await InitializeTimefreeAsync(null);
                }
            }
            catch (Exception ex)
            {
                AreaText.Text = "エリア: 不明";
                _stations.Clear();
                _programs.Clear();
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
            if (_playingStation != null && _playingProgram == null)
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

        #region Pivot

        private async void MainPivot_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (IsTimefreePivot && !_timefreeInitialized && _authService.IsAuthenticated)
                await InitializeTimefreeAsync(_selectedStation?.Id);
        }

        /// <summary>
        /// Pivot ヘッダーの揃え位置: 720 epx 未満は中央揃え、以上は左揃え (タブとピボットのガイドライン)。
        /// SDK 15063 の Pivot には揃え位置のプロパティが無いため、テンプレート内のヘッダー パネルを直接設定する。
        /// </summary>
        private void UpdatePivotHeaderAlignment()
        {
            var alignment = IsNarrow ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            foreach (var panel in FindDescendants<PivotHeaderPanel>(MainPivot))
                panel.HorizontalAlignment = alignment;
        }

        private static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                var match = child as T;
                if (match != null)
                    yield return match;
                foreach (var descendant in FindDescendants<T>(child))
                    yield return descendant;
            }
        }

        #endregion

        #region タイムフリー番組表

        private async Task InitializeTimefreeAsync(string preferredStationId)
        {
            _timefreeInitialized = true;

            _suppressTimefreeFilter = true;
            var days = RadikoStationService.GetTimefreeDays();
            var previousDate = (TimefreeDateCombo.SelectedItem as BroadcastDay)?.Date;
            TimefreeDateCombo.ItemsSource = days;
            TimefreeDateCombo.SelectedItem = days.FirstOrDefault(d => d.Date == previousDate) ?? days.FirstOrDefault();

            TimefreeStationCombo.SelectedItem =
                _stations.FirstOrDefault(s => s.Id == preferredStationId) ?? _stations.FirstOrDefault();
            _suppressTimefreeFilter = false;

            await LoadTimefreeProgramsAsync();
        }

        private async void TimefreeFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressTimefreeFilter || !_timefreeInitialized)
                return;
            await LoadTimefreeProgramsAsync();
        }

        private async Task LoadTimefreeProgramsAsync()
        {
            var station = TimefreeStationCombo.SelectedItem as Station;
            var day = TimefreeDateCombo.SelectedItem as BroadcastDay;
            _programs.Clear();
            TimefreeEmptyText.Visibility = Visibility.Collapsed;
            if (station == null || day == null)
                return;

            Dictionary<string, List<TimefreeProgram>> weekly;
            if (!_weeklyCache.TryGetValue(station.Id, out weekly))
            {
                TimefreeLoadingBar.Visibility = Visibility.Visible;
                try
                {
                    weekly = await _stationService.GetWeeklyProgramsAsync(station);
                    _weeklyCache[station.Id] = weekly;
                }
                catch (Exception ex)
                {
                    ShowTimefreeEmpty("番組表を取得できませんでした。\n" + ex.Message);
                    return;
                }
                finally
                {
                    TimefreeLoadingBar.Visibility = Visibility.Collapsed;
                }

                // 取得中に選択が変わった場合は最新の選択で表示し直す
                if (TimefreeStationCombo.SelectedItem != station || TimefreeDateCombo.SelectedItem != day)
                {
                    await LoadTimefreeProgramsAsync();
                    return;
                }
            }

            List<TimefreeProgram> list;
            if (!weekly.TryGetValue(day.Date, out list) || list.Count == 0)
            {
                ShowTimefreeEmpty("この日の番組はありません。");
                return;
            }

            foreach (var program in list)
            {
                program.IsPlaying = _playingProgram != null && IsSameProgram(program, _playingProgram);
                _programs.Add(program);
            }

            // 最も新しい聴ける番組までスクロール
            var latest = list.LastOrDefault(p => p.IsAvailable);
            if (latest != null)
                ProgramListView.ScrollIntoView(latest);
            if (_detailKind == DetailKind.Timefree && _selectedProgram != null)
            {
                var same = list.FirstOrDefault(p => IsSameProgram(p, _selectedProgram));
                if (same != null)
                    ProgramListView.SelectedItem = same;
            }
        }

        private void ShowTimefreeEmpty(string message)
        {
            TimefreeEmptyText.Text = message;
            TimefreeEmptyText.Visibility = Visibility.Visible;
        }

        private static bool IsSameProgram(TimefreeProgram a, TimefreeProgram b)
        {
            return a.Station.Id == b.Station.Id && a.Info.Start == b.Info.Start;
        }

        private void ProgramListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            var program = e.ClickedItem as TimefreeProgram;
            if (program == null)
                return;

            if (!program.IsAvailable)
            {
                ShowError($"「{program.Title}」は聴けません ({program.StatusText})。");
                return;
            }

            HideError();
            _selectedProgram = program;
            _detailKind = DetailKind.Timefree;
            if (IsNarrow)
                _showDetailInNarrow = true;

            UpdateDetail();
            UpdateMasterDetailLayout();
            UpdatePlaybackUi();
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
            if (station != null && (station != _selectedStation || _detailKind != DetailKind.Live))
            {
                _selectedStation = station;
                _detailKind = DetailKind.Live;
                UpdateDetail();
                UpdatePlaybackUi();
            }
        }

        private void SelectStation(Station station, bool openDetail)
        {
            _selectedStation = station;
            _detailKind = station != null ? DetailKind.Live : DetailKind.None;
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
                MasterColumn.Width = new GridLength(ActualWidth >= LargeBreakpoint ? 400 : 360);
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

            UpdatePivotHeaderAlignment();
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
                if (IsTimefreePivot)
                    ProgramListView.Focus(FocusState.Programmatic);
                else
                    StationListView.Focus(FocusState.Programmatic);
            }
        }

        private void MiniPlayer_Click(object sender, RoutedEventArgs e)
        {
            if (_playingProgram != null)
            {
                _selectedProgram = _playingProgram;
                _detailKind = DetailKind.Timefree;
                _showDetailInNarrow = true;
                UpdateDetail();
                UpdateMasterDetailLayout();
                UpdatePlaybackUi();
            }
            else if (_playingStation != null)
            {
                SelectStation(_playingStation, true);
            }
        }

        private void UpdateDetail()
        {
            if (_detailKind == DetailKind.None ||
                (_detailKind == DetailKind.Live && _selectedStation == null) ||
                (_detailKind == DetailKind.Timefree && _selectedProgram == null))
            {
                DetailContent.Visibility = Visibility.Collapsed;
                NoSelectionText.Visibility = Visibility.Visible;
                return;
            }

            NoSelectionText.Visibility = Visibility.Collapsed;
            DetailContent.Visibility = Visibility.Visible;

            if (_detailKind == DetailKind.Live)
            {
                var station = _selectedStation;
                DetailLogo.Source = station.LargeLogo;
                DetailStationName.Text = station.Name;

                var program = station.Program;
                DetailProgramTitle.Text = program?.Title ?? "番組情報はありません";
                SetPerformer(program?.Performer);
                ProgramProgressArea.Visibility = program != null ? Visibility.Visible : Visibility.Collapsed;
                TimelineArea.Visibility = Visibility.Collapsed;
                UpdateProgramProgress();
            }
            else
            {
                var program = _selectedProgram;
                DetailLogo.Source = program.Station.LargeLogo;
                DetailStationName.Text = program.Station.Name;
                DetailProgramTitle.Text = program.Title;
                SetPerformer(program.Performer);

                // 放送日時はテキストで表示し、タイムラインで再生位置を示す
                ProgramProgressArea.Visibility = Visibility.Visible;
                ProgramProgress.Visibility = Visibility.Collapsed;
                DetailTime.Text = "タイムフリー · " + program.DateTimeText;
                TimelineArea.Visibility = Visibility.Visible;
                UpdateTimeline();
            }
        }

        private void SetPerformer(string performer)
        {
            DetailPerformer.Text = performer ?? "";
            DetailPerformer.Visibility = string.IsNullOrEmpty(performer) ? Visibility.Collapsed : Visibility.Visible;
        }

        private void UpdateProgramProgress()
        {
            if (_detailKind != DetailKind.Live)
                return;
            var program = _selectedStation?.Program;
            if (program == null)
                return;

            ProgramProgress.Visibility = Visibility.Visible;
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
            MiniStationName.Text = _playingProgram != null
                ? $"{_playingStation.Name} · タイムフリー"
                : _playingStation.Name;
            MiniProgramTitle.Text = PlaybackStatusLabel()
                ?? (_playingProgram != null ? _playingProgram.Title : _playingStation.ProgramTitle);
        }

        #endregion

        #region 再生

        private bool SelectedIsActive =>
            _playerService.IsPlaying &&
            ((_detailKind == DetailKind.Live && _playingProgram == null && _selectedStation != null && _playingStation == _selectedStation) ||
             (_detailKind == DetailKind.Timefree && _playingProgram != null && _selectedProgram != null && IsSameProgram(_playingProgram, _selectedProgram)));

        private async void PlayStopButton_Click(object sender, RoutedEventArgs e)
        {
            HideError();

            if (SelectedIsActive || (_isStarting && _detailKind == DetailKind.Live))
            {
                if (_playerService.IsTimefree)
                {
                    // タイムフリー: 一時停止 / 再開
                    if (_playerService.IsPaused)
                        _playerService.Resume();
                    else
                        _playerService.Pause();
                }
                else if (_playerService.IsPaused)
                {
                    // ライブ: システムのメディアコントロールで一時停止されている場合は再開
                    _playerService.Resume();
                }
                else
                {
                    StopPlayback();
                }
                return;
            }

            if (_detailKind == DetailKind.Timefree && _selectedProgram != null)
                await StartTimefreeAsync(_selectedProgram, TimeSpan.Zero);
            else if (_detailKind == DetailKind.Live && _selectedStation != null)
                await StartLiveAsync(_selectedStation);
            else
                ShowError("放送局または番組を選んでください。");
        }

        private async Task StartLiveAsync(Station station)
        {
            if (!EnsureAuthenticated())
                return;

            StopPlayback();
            _isStarting = true;
            _playingStation = station;
            station.IsPlaying = true;
            UpdatePlaybackUi();

            try
            {
                await _playerService.PlayStationAsync(station.Id, station.Name, station.ProgramTitle);
                if (!_playerService.IsPlaying && _playingStation == station)
                    ClearPlayingState();
            }
            catch (Exception ex)
            {
                if (_playingStation == station)
                    ClearPlayingState();
                ShowError("再生できませんでした: " + ex.Message);
            }
            finally
            {
                _isStarting = false;
                UpdatePlaybackUi();
            }
        }

        private async Task StartTimefreeAsync(TimefreeProgram program, TimeSpan offset)
        {
            if (!EnsureAuthenticated())
                return;

            StopPlayback();
            _isStarting = true;
            _playingStation = program.Station;
            _playingProgram = program;
            program.IsPlaying = true;
            UpdatePlaybackUi();

            try
            {
                await _playerService.PlayTimefreeAsync(program.Station.Id, program.Station.Name, program.Info, offset);
                if (!_playerService.IsPlaying && _playingProgram == program)
                    ClearPlayingState();
                else
                    _positionTimer.Start();
            }
            catch (Exception ex)
            {
                if (_playingProgram == program)
                    ClearPlayingState();
                ShowError("タイムフリーを再生できませんでした: " + ex.Message);
            }
            finally
            {
                _isStarting = false;
                UpdatePlaybackUi();
            }
        }

        private bool EnsureAuthenticated()
        {
            if (_authService.IsAuthenticated)
                return true;
            ShowError("radiko に接続されていません。「更新」を押してください。");
            return false;
        }

        private void StopPlayback()
        {
            _playerService.Stop();
            ClearPlayingState();
            UpdatePlaybackUi();
        }

        private void ClearPlayingState()
        {
            _positionTimer.Stop();
            _seekTimer.Stop();
            _pendingSeekSeconds = -1;
            if (_playingStation != null)
                _playingStation.IsPlaying = false;
            if (_playingProgram != null)
            {
                _playingProgram.IsPlaying = false;
                foreach (var p in _programs.Where(p => IsSameProgram(p, _playingProgram)))
                    p.IsPlaying = false;
            }
            _playingStation = null;
            _playingProgram = null;
            UpdateTimeline();
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
            bool active = SelectedIsActive || (_isStarting && IsSelectedStarting());
            bool paused = active && !_isStarting && _playerService.IsPaused;
            bool timefree = _detailKind == DetailKind.Timefree;

            string label;
            Symbol symbol;
            if (!active || paused)
            {
                label = "再生";
                symbol = Symbol.Play;
            }
            else if (timefree)
            {
                label = "一時停止";
                symbol = Symbol.Pause;
            }
            else
            {
                label = "停止";
                symbol = Symbol.Stop;
            }

            bool hasSelection = (_detailKind == DetailKind.Live && _selectedStation != null) ||
                                (_detailKind == DetailKind.Timefree && _selectedProgram != null);

            PlayStopIcon.Symbol = symbol;
            PlayStopText.Text = label;
            AutomationProperties.SetName(PlayStopButton, label);
            PlayStopButton.IsEnabled = hasSelection && _authService.IsAuthenticated;

            PlayStopAppBarButton.Icon = new SymbolIcon(symbol);
            PlayStopAppBarButton.Label = label;
            ToolTipService.SetToolTip(PlayStopAppBarButton, label);
            PlayStopAppBarButton.IsEnabled = PlayStopButton.IsEnabled;

            string status = active ? PlaybackStatusLabel() : null;
            bool buffering = status == "接続しています…";
            BufferingRing.IsActive = buffering;
            BufferingRing.Visibility = buffering ? Visibility.Visible : Visibility.Collapsed;
            PlaybackStatusText.Text = status ?? (active ? "再生中" : "");

            UpdateTimeline();
            UpdateMiniPlayer();
        }

        private bool IsSelectedStarting()
        {
            if (_detailKind == DetailKind.Live)
                return _playingProgram == null && _playingStation != null && _playingStation == _selectedStation;
            if (_detailKind == DetailKind.Timefree)
                return _playingProgram != null && _selectedProgram != null && IsSameProgram(_playingProgram, _selectedProgram);
            return false;
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

        // タイムフリー番組を最後まで再生した
        private async void PlayerService_PlaybackEnded(object sender, EventArgs e)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, StopPlayback);
        }

        private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_playerService != null)
                _playerService.MediaPlayer.Volume = e.NewValue / 100.0;
        }

        #endregion

        #region タイムフリーのタイムライン (シーク)

        private void UpdateTimeline()
        {
            if (_detailKind != DetailKind.Timefree || _selectedProgram == null)
                return;

            var duration = _selectedProgram.Duration;
            bool active = SelectedIsActive;
            TimeSpan position = active ? _playerService.Position : TimeSpan.Zero;
            if (_pendingSeekSeconds >= 0)
                position = TimeSpan.FromSeconds(_pendingSeekSeconds);
            if (position > duration)
                position = duration;

            _updatingTimeline = true;
            TimelineSlider.Maximum = Math.Max(1, duration.TotalSeconds);
            if (_pendingSeekSeconds < 0)
                TimelineSlider.Value = position.TotalSeconds;
            TimelineSlider.IsEnabled = active && !_isStarting;
            _updatingTimeline = false;

            TimelinePositionText.Text = FormatTime(position);
            TimelineDurationText.Text = FormatTime(duration);
            AutomationProperties.SetName(TimelineSlider, $"再生位置 {FormatTime(position)} / {FormatTime(duration)}");
        }

        private void TimelineSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_updatingTimeline || !SelectedIsActive)
                return;

            // ドラッグ中に何度もシークしないよう、操作が止まってから確定する
            _pendingSeekSeconds = e.NewValue;
            TimelinePositionText.Text = FormatTime(TimeSpan.FromSeconds(e.NewValue));
            _seekTimer.Stop();
            _seekTimer.Start();
        }

        private void SeekTimer_Tick(object sender, object e)
        {
            _seekTimer.Stop();
            if (_pendingSeekSeconds >= 0 && SelectedIsActive)
                _playerService.Seek(TimeSpan.FromSeconds(_pendingSeekSeconds));
            _pendingSeekSeconds = -1;
        }

        private static string FormatTime(TimeSpan time)
        {
            if (time < TimeSpan.Zero)
                time = TimeSpan.Zero;
            return time.TotalHours >= 1
                ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
                : $"{time.Minutes}:{time.Seconds:00}";
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
                    Text = "radiko のライブ放送とタイムフリーを聴くための非公式クライアントです。\n\n" +
                           "・接続元の IP アドレスで判定されたエリアの放送局を聴くことができます。\n" +
                           "・タイムフリーは過去 7 日間の番組を、番組ごとに聴き始めてから 24 時間以内・合計 3 時間まで聴けます。\n" +
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
}
