﻿using System;
using System.ComponentModel;
using System.Net;
using System.Text.RegularExpressions;
using System.Windows;

namespace MySnooper
{
    public class WormageddonWebComm
    {
        private Window MainWindow;
        private string ServerAddress;

        // Regex
        // <SCHEME=Pf,Be>
        private Regex SchemeRegex = new Regex(@"^<SCHEME=([^>]+)>$", RegexOptions.IgnoreCase);
        // <GAME GameName Hoster HosterAddress CountryID 1 PasswordNeeded GameID HEXCC>
        private Regex GameRegex = new Regex(@"^<GAME\s(\S*)\s(\S+)\s(\S+)\s(\S+)\s1\s(\S+)\s(\S+)\s([^>]+)>$", RegexOptions.IgnoreCase);

        // Buffers for LoadHostedGames thread
        private byte[] RecvBuffer; // stores the bytes arrived from WormNet server. These bytes will be decoding into RecvMessage or into RecvHTML
        private System.Text.StringBuilder RecvHTML; // stores the encoded messages from the server which will be proceed by the IRC thread

        // Buffers for UI thread
        private byte[] RecvBufferUI = new byte[100];
        private System.Text.StringBuilder RecvHTMLUI;

        // Update game list
        public bool Stop { get; set; } // With this variable it is guaranteed that BGWorker won't start again


        public WormageddonWebComm(Window MainWindow, string ServerAddress)
        {
            this.ServerAddress = ServerAddress;
            this.MainWindow = MainWindow;
            Stop = false;

            RecvBuffer = new byte[1024]; // 1kB
            RecvHTML = new System.Text.StringBuilder(RecvBuffer.Length);

            RecvBufferUI = new byte[100];
            RecvHTMLUI = new System.Text.StringBuilder(RecvBufferUI.Length);
        }


        // Set the scheme of the channel
        public void SetChannelScheme(Channel channel)
        {
            try
            {
                HttpWebRequest myHttpWebRequest = (HttpWebRequest)WebRequest.Create("http://" + ServerAddress + "/wormageddonweb/RequestChannelScheme.asp?Channel=" + channel.Name.Substring(1));
                myHttpWebRequest.UserAgent = "T17Client/1.2";
                myHttpWebRequest.Proxy = null;
                myHttpWebRequest.AllowAutoRedirect = false;
                using (WebResponse myHttpWebResponse = myHttpWebRequest.GetResponse())
                using (System.IO.Stream stream = myHttpWebResponse.GetResponseStream())
                {
                    int bytes;
                    RecvHTMLUI.Clear();
                    while ((bytes = stream.Read(RecvBufferUI, 0, RecvBufferUI.Length)) > 0)
                    {
                        for (int j = 0; j < bytes; j++)
                        {
                            RecvHTMLUI.Append(WormNetCharTable.Decode[RecvBufferUI[j]]);
                        }
                    }

                    // <SCHEME=Pf,Be>
                    Match m = SchemeRegex.Match(RecvHTMLUI.ToString());
                    if (m.Success)
                        channel.Scheme = m.Groups[1].Value;
                    else
                        MessageBox.Show("Failed to load the scheme of the channel!", "Fail", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception e)
            {
                ErrorLog.log(e);
                MessageBox.Show("Failed to load the scheme of the channel!", "Fail", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }


        public bool GetGamesOfChannel(Channel channel)
        {
            if (!Stop && !LoadHostedGames.IsBusy)
            {
                LoadHostedGames.RunWorkerAsync(channel);
                return true;
            }
            return false;
        }


        // LoadGames.DoWork
        private void LoadGamesDoWork(object sender, DoWorkEventArgs e)
        {
            Channel ch = (Channel)e.Argument;
            e.Result = ch;

            try
            {
                HttpWebRequest myHttpWebRequest = (HttpWebRequest)WebRequest.Create("http://" + ServerAddress + ":80/wormageddonweb/GameList.asp?Channel=" + ch.Name.Substring(1));
                myHttpWebRequest.UserAgent = "T17Client/1.2";
                myHttpWebRequest.Proxy = null;
                myHttpWebRequest.AllowAutoRedirect = false;
                using (WebResponse myHttpWebResponse = myHttpWebRequest.GetResponse())
                using (System.IO.Stream stream = myHttpWebResponse.GetResponseStream())
                {
                    int bytes;
                    RecvHTML.Clear();
                    while ((bytes = stream.Read(RecvBuffer, 0, RecvBuffer.Length)) > 0)
                    {
                        for (int j = 0; j < bytes; j++)
                        {
                            RecvHTML.Append(WormNetCharTable.Decode[RecvBuffer[j]]);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ErrorLog.log(ex);
            }

            if (LoadHostedGames.CancellationPending)
                e.Cancel = true;
        }


        // LoadGames.RunWorkerCompleted
        private void LoadGamesCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            if (e.Cancelled)
            {
                MainWindow.Close();
                return;
            }

            try
            {
                Channel ch = (Channel)e.Result;
                if (!ch.Joined) // we already left the channel
                    return;

                string start = "<GAMELISTSTART>";
                string end = "<GAMELISTEND>";

                // Preprocessing.. is it a gamelist answer?
                string RecvHTMLstr = RecvHTML.ToString();
                if (!RecvHTMLstr.Contains(start) || !RecvHTMLstr.Contains(end))
                    return;

                RecvHTML.Replace("\n", "");
                RecvHTML.Replace(start, "");
                RecvHTML.Replace(end, "");

                string[] games = RecvHTML.ToString().Trim().Split(new string[] { "<BR>" }, StringSplitOptions.RemoveEmptyEntries);

                // Set all the games we have in !isAlive state (we will know if the game is not active anymore)
                for (int i = 0; i < ch.GameList.Count; i++)
                    ch.GameList[i].isAlive = false;

                for (int i = 0; i < games.Length; i++)
                {
                    // <GAME GameName Hoster HosterAddress CountryID 1 PasswordNeeded GameID HEXCC><BR>
                    Match m = GameRegex.Match(games[i].Trim());
                    if (m.Success)
                    {
                        string name = m.Groups[1].Value.Replace('\b', ' ').Replace("#039", "\x12");

                        // Encode the name to decode it with GameDecode
                        if (name.Length > RecvBufferUI.Length)
                            continue;
                        int bytes = 0;
                        byte b;
                        for (int j = 0; j < name.Length; j++)
                        {
                            if (WormNetCharTable.Encode.TryGetValue(name[j], out b))
                            {
                                RecvBufferUI[bytes++] = b;
                            }
                        }
                        RecvHTMLUI.Clear();
                        for (int j = 0; j < bytes; j++)
                            RecvHTMLUI.Append(WormNetCharTable.DecodeGame[RecvBufferUI[j]]);
                        name = RecvHTMLUI.ToString();

                        string hoster = m.Groups[2].Value;
                        string address = m.Groups[3].Value;

                        int countryID;
                        if (!int.TryParse(m.Groups[4].Value, out countryID))
                            continue;

                        bool password = m.Groups[5].Value == "1";

                        uint gameID;
                        if (!uint.TryParse(m.Groups[6].Value, out gameID))
                            continue;

                        string hexcc = m.Groups[7].Value;


                        // Get the country of the hoster
                        CountryClass country;
                        if (hexcc.Length < 9)
                        {
                            country = CountriesClass.GetCountryByID(countryID);
                        }
                        else
                        {
                            string hexstr = uint.Parse(hexcc).ToString("X");
                            if (hexstr.Length == 8 && hexstr.Substring(0, 4) == "6487")
                            {
                                char c1 = WormNetCharTable.Decode[byte.Parse(hexstr.Substring(6), System.Globalization.NumberStyles.HexNumber)];
                                char c2 = WormNetCharTable.Decode[byte.Parse(hexstr.Substring(4, 2), System.Globalization.NumberStyles.HexNumber)];
                                country = CountriesClass.GetCountryByCC(c1.ToString() + c2.ToString());
                            }
                            else
                            {
                                country = CountriesClass.GetCountryByID(49);
                            }
                        }

                        // Add the game to the list or set its isAlive state true if it is already in the list
                        Game game = null;
                        for (int j = 0; j < ch.GameList.Count; j++)
                        {
                            if (ch.GameList[j].ID == gameID)
                            {
                                game = ch.GameList[j];
                                game.isAlive = true;
                                break;
                            }
                        }
                        if (game == null)
                            ch.GameList.Add(new Game(gameID, name, address, country, hoster, password));
                    }
                }

                // Delete inactive games from the list
                for (int i = 0; i < ch.GameList.Count; i++)
                {
                    if (!ch.GameList[i].isAlive)
                    {
                        ch.GameList.RemoveAt(i);
                        i--;
                    }
                }
            }
            catch (Exception ex)
            {
                ErrorLog.log(ex);
            }
        }
    }
}
