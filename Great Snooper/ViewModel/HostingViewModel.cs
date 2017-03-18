﻿namespace GreatSnooper.ViewModel
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Text.RegularExpressions;
    using System.Threading.Tasks;
    using System.Windows.Input;
    using System.Windows.Threading;
    using GalaSoft.MvvmLight;
    using GalaSoft.MvvmLight.Command;
    using GreatSnooper.Helpers;
    using GreatSnooper.IRC;
    using GreatSnooper.Services;

    class HostingViewModel : ViewModelBase
    {
        private static Regex PassRegex = new Regex(@"^[a-z]*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private string cc;
        private ChannelViewModel channel;
        private Dispatcher dispatcher;
        private Process gameProcess;
        private string serverAddress;
        private bool _loading;

        public HostingViewModel(MainViewModel mvm, string serverAddress, ChannelViewModel channel, string cc)
        {
            this.serverAddress = serverAddress;
            this.channel = channel;
            this.cc = cc;
            this.MVM = mvm;

            this.GameName = Properties.Settings.Default.HostGameName;
            this.UsingWormNat2 = Properties.Settings.Default.HostUseWormnat;
            this.InfoToChannel = Properties.Settings.Default.HostInfoToChannel;
            this.SelectedWaExe = Properties.Settings.Default.SelectedWaExe;
            this.GamePassword = string.Empty;
            this.ExitSnooper = false;

            this.dispatcher = Dispatcher.CurrentDispatcher;
        }

        private enum HosterErrors
        {
            NoError, WormNatError, WormNatInitError, FailedToGetLocalIP, CreateGameFailed, NoGameID, FailedToStartTheGame, Unkown, WormNatClientError
        }

        public ICommand CloseCommand
        {
            get
            {
                return new RelayCommand(Close);
            }
        }

        public ICommand CreateGameCommand
        {
            get
            {
                return new RelayCommand(CreateGame);
            }
        }

        public IMetroDialogService DialogService
        {
            get;
            set;
        }

        public bool? ExitSnooper
        {
            get;
            set;
        }

        public string GameName
        {
            get;
            set;
        }

        public string GamePassword
        {
            get;
            set;
        }

        public bool? InfoToChannel
        {
            get;
            set;
        }

        public bool Loading
        {
            get
            {
                return _loading;
            }
            private set
            {
                if (_loading != value)
                {
                    _loading = value;
                    RaisePropertyChanged("Loading");
                }
            }
        }

        public MainViewModel MVM
        {
            get;
            private set;
        }

        public int SelectedWaExe
        {
            get;
            set;
        }

        public bool? UsingWormNat2
        {
            get;
            set;
        }

        private void Close()
        {
            this.DialogService.CloseRequest();
        }

        private void CreateGame()
        {
            if (!PassRegex.IsMatch(GamePassword))
            {
                this.DialogService.ShowDialog(Localizations.GSLocalization.Instance.InvalidValueText, Localizations.GSLocalization.Instance.GamePassBadText);
                return;
            }

            this.Loading = true;

            Task.Factory.StartNew<string>(() =>
            {
                string wormnat = (UsingWormNat2.HasValue && UsingWormNat2.Value) ? "1" : "0";

                // Save settings
                string validGameName = WormNetCharTable.Instance.RemoveNonGameChars(GameName.Trim());
                Properties.Settings.Default.HostGameName = validGameName;
                Properties.Settings.Default.HostUseWormnat = UsingWormNat2.HasValue && UsingWormNat2.Value;
                Properties.Settings.Default.HostInfoToChannel = InfoToChannel.HasValue && InfoToChannel.Value;
                Properties.Settings.Default.SelectedWaExe = this.SelectedWaExe;
                Properties.Settings.Default.Save();

                string encodedGameName = WormNetCharTable.Instance.EncodeGameUrl(validGameName);
                string highPriority = Properties.Settings.Default.WAHighPriority ? "1" : "0";
                string waExe = (this.SelectedWaExe == 0)
                    ? Properties.Settings.Default.WaExe
                    : Properties.Settings.Default.WaExe2;

                string arguments = string.Format("\"{0}\" \"{1}\" \"{2}\" \"{3}\" \"{4}\" \"{5}\" \"{6}\" \"{7}\" \"{8}\" \"{9}\" \"{10}\" \"{11}\"",
                                                 serverAddress,
                                                 waExe,
                                                 channel.Server.User.Name,
                                                 encodedGameName,
                                                 GamePassword,
                                                 channel.Name.Substring(1),
                                                 channel.Scheme,
                                                 channel.Server.User.Country.ID.ToString(),
                                                 cc,
                                                 wormnat,
                                                 highPriority,
                                                 GlobalManager.SettingsPath);

                string success = TryHostGame(arguments);

                using (gameProcess.StandardInput)
                {
                    gameProcess.StandardInput.WriteLine("1");
                }

                return success;
            })
            .ContinueWith((t) =>
            {
                this.Loading = false;
                HosterErrors result;

                if (t.IsFaulted || Enum.TryParse(t.Result, out result) == false || result == HosterErrors.Unkown)
                {
                    this.DialogService.ShowDialog(Localizations.GSLocalization.Instance.ErrorText, Localizations.GSLocalization.Instance.HosterUnknownFail);
                    return;
                }
                switch (result)
                {
                    case HosterErrors.CreateGameFailed:
                        this.DialogService.ShowDialog(Localizations.GSLocalization.Instance.ErrorText, Localizations.GSLocalization.Instance.HosterCreateGameFail);
                        return;

                    case HosterErrors.FailedToStartTheGame:
                        this.DialogService.ShowDialog(Localizations.GSLocalization.Instance.ErrorText, Localizations.GSLocalization.Instance.HosterStartGameFail);
                        return;

                    case HosterErrors.NoGameID:
                        this.DialogService.ShowDialog(Localizations.GSLocalization.Instance.ErrorText, Localizations.GSLocalization.Instance.HosterNoGameIDError);
                        return;

                    case HosterErrors.FailedToGetLocalIP:
                        this.DialogService.ShowDialog(Localizations.GSLocalization.Instance.ErrorText, Localizations.GSLocalization.Instance.HosterFailedToGetLocalIP);
                        return;

                    case HosterErrors.WormNatClientError:
                    case HosterErrors.WormNatError:
                    case HosterErrors.WormNatInitError:
                        this.DialogService.ShowDialog(Localizations.GSLocalization.Instance.ErrorText, Localizations.GSLocalization.Instance.HosterWormNatError);
                        return;
                }

                this.dispatcher.BeginInvoke(new Action(() =>
                {
                    if (Properties.Settings.Default.HostInfoToChannel)
                        this.channel.SendActionMessage("is hosting a game: " + Properties.Settings.Default.HostGameName);

                    if (this.ExitSnooper.HasValue && this.ExitSnooper.Value)
                    {
                        this.gameProcess.Dispose();
                        this.gameProcess = null;
                        this.MVM.CloseCommand.Execute(null);
                        return;
                    }

                    this.MVM.GameProcess = this.gameProcess;
                    this.MVM.StartedGameType = MainViewModel.StartedGameTypes.Host;

                    if (Properties.Settings.Default.MarkAway)
                        this.MVM.SetAway();
                }));

                this.CloseCommand.Execute(null);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private string TryHostGame(string arguments)
        {
            gameProcess = new Process();
            gameProcess.StartInfo.UseShellExecute = false;
            gameProcess.StartInfo.CreateNoWindow = true;
            gameProcess.StartInfo.RedirectStandardOutput = true;
            gameProcess.StartInfo.RedirectStandardInput = true;
            gameProcess.StartInfo.FileName = Path.GetFullPath("Hoster.exe");
            gameProcess.StartInfo.Arguments = arguments;
            Debug.WriteLine("HOSTER: " + arguments);
            if (gameProcess.Start())
            {
                using (gameProcess.StandardOutput)
                {
                    return gameProcess.StandardOutput.ReadLine();
                }
            }
            else
            {
                gameProcess.Dispose();
                gameProcess = null;
            }
            return string.Empty;
        }
    }
}
